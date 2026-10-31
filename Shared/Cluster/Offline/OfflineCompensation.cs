using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using VRage;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Game.Entity;
using VRage.Game.ObjectBuilders.ComponentSystem;
using VRage.Game.ObjectBuilders.Components.Beacon;
using VRage.ObjectBuilders;
using VRage.ObjectBuilders.Private;
using VRage.Utils;

namespace ClusterPlugin;

internal static partial class OfflineCompensation
{
	private static readonly Dictionary<long, OfflineResult> Results = new();
	internal static long Frame => checked((long)Math.Floor(ClusterRuntime.AuthorityClockMilliseconds / 16));
	internal const long FramesPerAuthorityHour = 3600000 / 16;
	internal static long MaxFrames => checked((long)ClusterConfig.GetInt("offline.compensateMaxHours") * FramesPerAuthorityHour);

	/// <summary>Runs once, at the shared recovery seam, before any entity is instantiated. The source
	/// save is immutable: a failed wake re-reads it; an ordinary new-generation Final drops profiles.</summary>
	internal static void Apply(IDictionary<long, MyObjectBuilder_EntityBase> entities,
		IEnumerable<(OfflineProfile Profile, HashSet<long> Members)> profiles)
	{
		if (!ClusterConfig.GetBool("offline.compensate")) return;
		long wake = Frame;
		foreach (var entry in profiles)
		{
			var profile = entry.Profile;
			try
			{
				if (profile.Members.Any(id => !entry.Members.Contains(id) || !entities.ContainsKey(id)))
					throw new InvalidDataException("incomplete logical group in recovery cover");
				if (profile.ContentHash != ContentHash(profile.Definitions)) throw new InvalidDataException("content mismatch");
				// A clone keeps diagnostics and dry-run repeatable, even if application validation refuses.
				var model = JsonSerializer.Deserialize<OfflineProfile>(JsonSerializer.Serialize(profile, OfflineProfile.Json), OfflineProfile.Json);
				// Match capture's double arithmetic: float intermediate rounding is not a settings change.
				double period = MySession.Static.Settings.EnableSunRotation ? MySession.Static.Settings.SunRotationIntervalMinutes * 60000.0 / 16 : 0;
				if (model.SunPeriodFrames != period)
				{
					model.SunPeriodFrames = period;
					if (model.Confidence == "Exact") model.Confidence = "Approximate";
				}
				OfflineResult result = OfflineModel.Simulate(model, wake, MaxFrames);
				Commit(profile, model, entities);
				Results[profile.Group] = result;
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] offline compensation " + JsonSerializer.Serialize(result, OfflineProfile.Json));
			}
			catch (Exception e)
			{
				Results[profile.Group] = new() { Group = profile.Group, FromFrame = profile.SimulatedThrough, Skipped = e.Message, Confidence = "Frozen" };
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] offline compensation skipped group=" + profile.Group + ": " + SecretRedactor.Redact(e.Message));
			}
		}
	}

	internal static object Status() => new { enabled = ClusterConfig.GetBool("offline.compensate"), results = Results.Values.ToArray(), profiling = ProfilingStatus(), cache = OfflineRateCache.Status() };

	internal static OfflineResult[] DryRun(PartitionSaveFile.Parsed save, double hours)
	{
		if (!double.IsFinite(hours) || hours < 0 || hours > 168) throw new ArgumentException("hours must be between 0 and 168");
		if (save.Profiles == null) return [];
		return (JsonSerializer.Deserialize<OfflineProfile[]>(save.Profiles, OfflineProfile.Json) ?? []).Select(p =>
		{
			if (p.ContentHash != ContentHash(p.Definitions)) return new OfflineResult { Group = p.Group, Skipped = "content mismatch", Confidence = "Frozen" };
			return OfflineModel.Simulate(p, checked(p.SimulatedThrough + (long)(hours * FramesPerAuthorityHour)), MaxFrames);
		}).ToArray();
	}

	private static MySession m_contentSession;
	private static Task<string> m_modContent;
	internal static bool ContentReady
	{
		get
		{
			if (!ReferenceEquals(m_contentSession, MySession.Static) || m_modContent == null)
			{
				m_contentSession = MySession.Static;
				string[] paths = MySession.Static.Mods.Select(m => m.GetPath()).ToArray();
				m_modContent = paths.Length == 0 ? Task.FromResult(Convert.ToHexString(SHA256.HashData(Array.Empty<byte>()))) : Task.Run(() =>
				{
					using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
					foreach (string root in paths)
					{
						string[] files = File.Exists(root) ? new[] { root } : Directory.Exists(root) ? Directory.GetFiles(root, "*", SearchOption.AllDirectories).OrderBy(f => Path.GetRelativePath(root, f), StringComparer.Ordinal).ToArray()
							: throw new FileNotFoundException("Mod content is unavailable", root);
						foreach (string file in files)
						{
							hash.AppendData(Encoding.UTF8.GetBytes(File.Exists(root) ? Path.GetFileName(root) : Path.GetRelativePath(root, file).Replace('\\', '/')));
							using var input = File.OpenRead(file);
							hash.AppendData(SHA256.HashData(input));
						}
					}
					return Convert.ToHexString(hash.GetHashAndReset());
				});
			}
			return m_modContent.IsCompleted;
		}
	}

	internal static string ContentHash(IEnumerable<string> definitions)
	{
		using var stream = new MemoryStream();
		void Append(string text)
		{
			byte[] bytes = Encoding.UTF8.GetBytes(text ?? string.Empty);
			stream.Write(BitConverter.GetBytes(bytes.Length)); stream.Write(bytes);
		}
		if (!ContentReady) throw new InvalidOperationException("Mod content hash is still being prepared");
		Append(m_modContent.GetAwaiter().GetResult());
		Append(MyFinalBuildConstants.APP_VERSION_STRING.ToString());
		Append(MySector.SunProperties.BaseSunDirectionNormalized.ToString());
		Append(MySector.SunProperties.SunRotationAxis.ToString());
		Append(MySession.Static.Settings.EnableSunRotation.ToString());
		var session = MySession.Static;
		Append(string.Join("|", session.Mods.Select(m => m.PublishedFileId + ":" + m.PublishedServiceName + ":" + m.Name)));
		Append(string.Join("|", session.RefinerySpeedMultiplier.ToString("R", CultureInfo.InvariantCulture),
			session.AssemblerSpeedMultiplier.ToString("R", CultureInfo.InvariantCulture), session.AssemblerEfficiencyMultiplier.ToString("R", CultureInfo.InvariantCulture),
			session.Settings.BlocksInventorySizeMultiplier.ToString("R", CultureInfo.InvariantCulture), session.CreativeMode, session.SimplifiedSimulation));
		foreach (string id in definitions.OrderBy(x => x, StringComparer.Ordinal))
		{
			var definition = MyDefinitionManager.Static.GetDefinition(MyDefinitionId.Parse(id)) ?? throw new InvalidDataException("Missing definition " + id);
			Append(id);
			Append(DefinitionFingerprint(definition));
		}
		return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
	}

	// Definition builders do not all serialize their runtime fields (notably fuel/blueprint data).
	// Walk public data deterministically; skip callbacks and break reference cycles.
	private static string DefinitionFingerprint(object value)
	{
		var text = new StringBuilder();
		var path = new HashSet<object>(System.Collections.Generic.ReferenceEqualityComparer.Instance);
		void Visit(object item, int depth)
		{
			if (item == null) { text.Append("null;"); return; }
			Type type = item.GetType();
			text.Append(type.FullName).Append(':');
			if (item is string str)
			{
				string content = VRage.FileSystem.MyFileSystem.ContentPath;
				if (!string.IsNullOrEmpty(content) && str.StartsWith(content, StringComparison.Ordinal)) str = "$CONTENT/" + str.Substring(content.Length).Replace('\\', '/');
				foreach (var mod in MySession.Static.Mods)
				{
					string root = mod.GetPath();
					if (!string.IsNullOrEmpty(root) && str.StartsWith(root, StringComparison.Ordinal)) { str = "$MOD/" + mod.PublishedFileId + "/" + str.Substring(root.Length).Replace('\\', '/'); break; }
				}
				text.Append(str.Length).Append(':').Append(str); return;
			}
			if (type.IsPrimitive || type.IsEnum || item is decimal || item is MyDefinitionId || item is MyFixedPoint)
			{ text.Append(Convert.ToString(item, CultureInfo.InvariantCulture)).Append(';'); return; }
			if (depth > 12 || item is Delegate || !type.IsValueType && !path.Add(item)) { text.Append("cycle;"); return; }
			if (item is System.Collections.IDictionary dictionary)
			{
				foreach (var key in dictionary.Keys.Cast<object>().OrderBy(k => k.ToString(), StringComparer.Ordinal)) { Visit(key, depth + 1); Visit(dictionary[key], depth + 1); }
			}
			else if (item is System.Collections.IEnumerable sequence) foreach (var child in sequence) Visit(child, depth + 1);
			else
			{
				foreach (var member in type.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public).Where(f => f.Name is not ("Context" or "ModContext")).OrderBy(f => f.Name, StringComparer.Ordinal))
				{ text.Append(member.Name).Append('='); Visit(member.GetValue(item), depth + 1); }
				foreach (var property in type.GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
					.Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && (p.PropertyType.IsPrimitive || p.PropertyType.IsEnum || p.PropertyType == typeof(string)))
					.OrderBy(p => p.Name, StringComparer.Ordinal))
				{ text.Append(property.Name).Append('='); Visit(property.GetValue(item), depth + 1); }
			}
			if (!type.IsValueType) path.Remove(item);
		}
		Visit(value, 0);
		return text.ToString();
	}

	private static IEnumerable<MyObjectBuilder_Inventory> Inventories(MyObjectBuilder_CubeBlock block)
	{
		IEnumerable<MyObjectBuilder_Inventory> Flatten(MyObjectBuilder_InventoryBase inventory)
		{
			if (inventory is MyObjectBuilder_Inventory single) yield return single;
			if (inventory is MyObjectBuilder_InventoryAggregate aggregate)
				foreach (var child in aggregate.Inventories ?? []) foreach (var nested in Flatten(child)) yield return nested;
		}
		var components = block.ComponentContainer?.Components;
		var found = (components ?? []).Where(c => c.Component is MyObjectBuilder_InventoryBase)
			.SelectMany(c => Flatten((MyObjectBuilder_InventoryBase)c.Component)).ToArray();
		if (found.Length != 0) return found;
		// Older saves used the block's legacy fields; the component container is authoritative when present.
		if (block is MyObjectBuilder_ProductionBlock production) return new[] { production.InputInventory, production.OutputInventory }.Where(i => i != null);
		if (block is MyObjectBuilder_Reactor reactor && reactor.Inventory != null) return new[] { reactor.Inventory };
		if (block is MyObjectBuilder_OxygenGenerator generator && generator.Inventory != null) return new[] { generator.Inventory };
		if (block is MyObjectBuilder_GasTank tank && tank.Inventory != null) return new[] { tank.Inventory };
		return [];
	}

	/// <summary>Validate and prepare every write before touching any builder. IDs, stacks and all
	/// item metadata that the model does not own survive; new products receive fresh inventory IDs.</summary>
	private static void Commit(OfflineProfile before, OfflineProfile after, IDictionary<long, MyObjectBuilder_EntityBase> entities)
	{
		var blocks = before.Members.SelectMany(id => ((MyObjectBuilder_CubeGrid)entities[id]).CubeBlocks).Where(b => b.EntityId != 0).ToDictionary(b => b.EntityId);
		var writes = new List<Action>();
		for (int i = 0; i < after.Inventories.Count; i++)
		{
			var original = before.Inventories[i]; var modeled = after.Inventories[i];
			if (!blocks.TryGetValue(modeled.Block, out var block)) throw new InvalidDataException("Inventory block absent");
			var candidates = Inventories(block).ToArray();
			var inventory = candidates.FirstOrDefault(x => x.InventoryId == original.InventoryId)
				?? (original.Index < candidates.Length ? candidates[original.Index] : null)
				?? throw new InvalidDataException("Inventory absent from saved builder");
			var actual = inventory.Items.GroupBy(x => x.PhysicalContent.GetId().ToString()).ToDictionary(g => g.Key, g => g.Sum(x => (decimal)x.Amount));
			var expected = original.Stacks.GroupBy(x => x.Item).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));
			if (actual.Count != expected.Count || actual.Any(x => expected.GetValueOrDefault(x.Key) != x.Value)) throw new InvalidDataException("Frozen inventory differs from profile");
			var amounts = modeled.Stacks.GroupBy(x => x.Item).ToDictionary(g => g.Key, g => decimal.Floor(g.Sum(x => x.Amount) * 1000000) / 1000000);
			decimal volume = amounts.Sum(x => x.Value * after.Items[x.Key].Volume), mass = amounts.Sum(x => x.Value * after.Items[x.Key].Mass);
			if (volume > Math.Max(modeled.MaxVolume, original.Stacks.Sum(x => x.Amount * before.Items[x.Item].Volume)) + 0.000001m
				|| mass > Math.Max(modeled.MaxMass, original.Stacks.Sum(x => x.Amount * before.Items[x.Item].Mass)) + 0.000001m)
				throw new InvalidDataException("Compensated inventory exceeds its limit");
			var items = new List<MyObjectBuilder_InventoryItem>();
			foreach (var item in inventory.Items)
			{
				string id = item.PhysicalContent.GetId().ToString();
				decimal keep = Math.Min((decimal)item.Amount, amounts.GetValueOrDefault(id));
				if (keep > 0) items.Add(new() { Amount = (MyFixedPoint)keep, PhysicalContent = item.PhysicalContent, ItemId = item.ItemId, Scale = item.Scale });
				amounts[id] -= keep;
			}
			uint next = inventory.nextItemId;
			var used = inventory.Items.Select(x => x.ItemId).ToHashSet();
			foreach (var amount in amounts.Where(x => x.Value > 0))
			{
				while (used.Contains(next)) next = checked(next + 1);
				items.Add(new() { Amount = (MyFixedPoint)amount.Value, PhysicalContent = (MyObjectBuilder_PhysicalObject)MyObjectBuilderSerializerKeen.CreateNewObject(MyDefinitionId.Parse(amount.Key)), ItemId = next });
				used.Add(next); next = checked(next + 1);
			}
			uint nextId = next;
			writes.Add(() => { inventory.Items = items; inventory.nextItemId = nextId; });
		}
		foreach (var store in after.Storage)
		{
			MyObjectBuilder_CubeBlock block = blocks[store.Block];
			switch (store.Kind)
			{
				case "Battery": var battery = (MyObjectBuilder_BatteryBlock)block; writes.Add(() => battery.CurrentStoredPower = (float)store.Amount); break;
				case "Tank": var tank = (MyObjectBuilder_GasTank)block; writes.Add(() => tank.FilledRatio = store.Capacity > 0 ? (float)(store.Amount / store.Capacity) : 0); break;
				case "Engine": var engine = (MyObjectBuilder_FueledPowerProducer)block; writes.Add(() => engine.Capacity = (float)store.Amount); break;
				case "Jump": var jump = (MyObjectBuilder_JumpDrive)block; writes.Add(() => jump.StoredPower = (float)store.Amount); break;
			}
		}
		foreach (var source in after.Sources.Where(s => s.FuelTickFrames > 0))
		{
			var block = blocks[source.Block];
			var timer = block.ComponentContainer?.Components.Select(c => c.Component).OfType<MyObjectBuilder_TimerComponent>().SingleOrDefault()
				?? throw new InvalidDataException("Reactor fuel timer missing");
			uint counter = checked((uint)(Math.Floor(source.FuelElapsedFrames / 100) * 100));
			uint tick = checked((uint)source.FuelTickFrames);
			var grid = before.Members.Select(id => (MyObjectBuilder_CubeGrid)entities[id]).Single(g => g.CubeBlocks.Contains(block));
			writes.Add(() =>
			{
				timer.FramesFromLastTrigger = counter; timer.TimerTickInFrames = tick;
				if (source.FuelTier2AfterFrames < 0 && source.FuelTickFrames == source.FuelTier2TickFrames)
					grid.PlayerPresenceTier = MyUpdateTiersPlayerPresence.Tier2;
			});
		}
		foreach (var production in after.Production.Where(p => !p.Refinery))
		{
			var block = (MyObjectBuilder_Assembler)blocks[production.Block];
			uint next = block.NextItemId;
			var queue = production.Queue.Select(q => new MyObjectBuilder_ProductionBlock.QueueItem { Id = MyDefinitionId.Parse(q.Recipe), Amount = (MyFixedPoint)q.Amount, ItemId = next++ }).ToArray();
			writes.Add(() => { block.Queue = queue; block.NextItemId = next; block.CurrentProgress = (float)production.Progress; });
		}
		foreach (var zone in after.Upkeep)
		{
			var block = (MyObjectBuilder_FunctionalBlock)blocks[zone.Block];
			var component = block.ComponentContainer?.Components.Select(c => c.Component).OfType<MyObjectBuilder_SafeZoneComponent>().FirstOrDefault()
				?? throw new InvalidDataException("Safe zone component missing");
			// Prepare every parse/serialization before committing any builder writes. Removing a
			// bubble retains its settings/rights and the functional block's Enabled switch, like
			// vanilla power loss/chip exhaustion; paid credit can reactivate after power returns.
			string carried = BlockBuilderEntry.Read(block, BlockSafeZones.Key);
			var bubble = carried == null ? null : BlockSafeZones.Parse(carried);
			var settings = component.SafeZoneOb ?? bubble;
			var retained = settings?.Clone() as MyObjectBuilder_SafeZone;
			if (retained != null) retained.Enabled = false;
			bool exists = zone.Enabled && zone.Exists;
			// An initially power-paused zone has no carried live entry. If its modeled
			// supply recovered, restore the remembered entity through the same carried-zone
			// path, with its original id/settings and the already-accounted activation clock.
			if (exists && bubble == null)
			{
				if (retained == null || retained.EntityId == 0 || retained.SafeZoneBlockId != block.EntityId || entities.ContainsKey(retained.EntityId))
					throw new InvalidDataException("Paused safe zone settings missing or conflicting");
				bubble = (MyObjectBuilder_SafeZone)retained.Clone();
			}
			if (bubble != null) bubble.Enabled = exists && zone.ActivationRemainingFrames <= 0;
			string nextEntry = exists && bubble != null ? BlockSafeZones.Serialize(bubble) : null;
			long nextZoneId = exists ? bubble.EntityId : 0;
			writes.Add(() =>
			{
				component.UpkeepTime = zone.RemainingFrames * (1000.0 / 60);
				component.Activating = exists && zone.ActivationRemainingFrames > 0;
				component.ActivationTime = checked((long)Math.Ceiling(zone.ActivationRemainingFrames * (1000.0 / 60)));
				component.SafeZoneOb = retained;
				((ObjectBuilders.SafeZone.MyObjectBuilder_SafeZoneBlock)block).SafeZoneId = nextZoneId;
				BlockBuilderEntry.Write(block, BlockSafeZones.Key, nextEntry);
			});
		}
		foreach (var write in writes) write();
	}
}
