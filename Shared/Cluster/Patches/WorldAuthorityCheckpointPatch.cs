using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Sandbox.Engine.Networking;
using VRage.Game;

namespace ClusterPlugin;

public static class WorldAuthorityCheckpointPatch
{
	internal static bool Restored { get; private set; }
	private static readonly HttpClient Client = ClusterHttp.Client(TimeSpan.FromSeconds(10));
	public static void Install()
	{
		ClusterRuntime.AuthorizeRegistryClient(Client);
		MyLocalCacheSeams.ClusterRestoreWorldAuthorityCheckpoint = Restore;
	}

	public static void Restore(ref MyObjectBuilder_Checkpoint checkpoint, ref ulong checkpointSizeInBytes)
	{
		if (!ClusterRuntime.IsWorldAuthority) return;
		string gateway = Environment.GetEnvironmentVariable("CLUSTER_GATEWAY_REGISTRY")?.TrimEnd('/')
			?? throw new InvalidOperationException("World Authority registry endpoint is unavailable");
		using HttpResponseMessage response = Client.GetAsync(gateway + "/registry/global-saves/recovery").GetAwaiter().GetResult();
		if (response.StatusCode == HttpStatusCode.NotFound)
		{
			if (ClusterRuntime.AuthorityGeneration > 1)
				throw new InvalidDataException("Replacement World Authority has no catalogued global save");
			if (checkpoint == null) throw new InvalidDataException("World Authority supplied checkpoint is missing");
			ShipStorageService.RestoreRecord(null, firstLoad: true);
			AdminSafeZones.RestoreRecord(null);
			Restored = true;
			Console.WriteLine("[ClusterPlugin] no catalogued global save; loading supplied checkpoint");
			return;
		}
		response.EnsureSuccessStatusCode();
		using JsonDocument registration = JsonDocument.Parse(response.Content.ReadAsStream());
		JsonElement saveId = registration.RootElement.GetProperty("SaveId");
		uint generation = checked((uint)saveId.GetProperty("Generation").GetInt64());
		uint sequence = checked((uint)saveId.GetProperty("Sequence").GetInt64());
		long expectedSize = registration.RootElement.GetProperty("SizeBytes").GetInt64();
		string expectedChecksum = registration.RootElement.GetProperty("Checksum").GetString();
		// A World Authority only trusts locations under its own data root; a catalogued copy on
		// another host is fetched from a holder's control endpoint instead of being read through
		// a shared filesystem. A readable foreign path stays honored as a logged fallback.
		string data = Environment.GetEnvironmentVariable("CLUSTER_DATA_ROOT");
		// T-0340: its own saves directory is the gateway host's store when the registry said so.
		string globalRoot = Path.Combine(ClusterRuntime.SaveRoot(), "GLOBAL");
		string ownPrefix = globalRoot + Path.DirectorySeparatorChar;
		// A global save that was not moved into the store is catalogued in this node's own data dir, which stays its own.
		string legacyPrefix = Path.Combine(ClusterRuntime.LegacySaveRoot(), "GLOBAL") + Path.DirectorySeparatorChar;
		string[] locations = registration.RootElement.GetProperty("Locations").EnumerateArray().Select(x => x.GetString()).ToArray();
		// T-0397: a copy is used only when it verifies (size + header/body checksum). A torn local copy (a write cut by a
		// host crash) used to be read as it was and threw, so the WA did not start although a holder had a good copy.
		string location = VerifiedGlobalCopy(locations.Where(x => Path.IsPathFullyQualified(x)
			&& (Path.GetFullPath(x).StartsWith(ownPrefix, StringComparison.Ordinal) || Path.GetFullPath(x).StartsWith(legacyPrefix, StringComparison.Ordinal))),
			expectedSize, expectedChecksum);
		if (location == null)
		{
			string fallback = VerifiedGlobalCopy(locations.Where(x => Path.IsPathFullyQualified(x)), expectedSize, expectedChecksum);
			location = FetchGlobal(gateway, registration.RootElement, generation, sequence, expectedSize, expectedChecksum, globalRoot, fallback != null);
			if (location == null)
			{
				location = fallback;
				if (location != null)
					Console.WriteLine("[ClusterPlugin] global_save foreign_path_fallback=true path=" + location);
			}
			if (location == null)
				throw new FileNotFoundException("No catalogued World Authority global save is accessible on this host");
		}
		byte[] file = File.ReadAllBytes(location);
		PartitionSaveFile.Parsed parsed = PartitionSaveFile.Read(file);
		if (file.LongLength != expectedSize || !parsed.BodyChecksum.ToString("X16", CultureInfo.InvariantCulture)
			.Equals(expectedChecksum, StringComparison.OrdinalIgnoreCase))
			throw new InvalidDataException("Catalogued World Authority global save metadata does not match its file");
		GlobalSaveFile.Bundle bundle = GlobalSaveFile.ReadBundle(file, generation, sequence);
		MyObjectBuilder_Checkpoint restored = bundle.Checkpoint;
		if (!Sandbox.Game.World.MySession.IsCompatibleVersion(restored)
			|| restored.WorldId != checkpoint.WorldId || !restored.CurrentSector.Equals(checkpoint.CurrentSector))
			throw new InvalidDataException("Catalogued World Authority checkpoint does not match the local world");
		checkpoint = restored;
		GlobalCommandLedger.Restore(bundle.CommandLedger);
		// #1318: the commands answered after this save, replayed once the session is ready (GlobalStateChannel.Update).
		GlobalCommandJournal.LoadForReplay(gateway, GlobalCommandLedger.Order);
		CharacterRegistryService.RestoreRecord(bundle.CharacterRecord);
		// The per-player settings ride in the checkpoint's own player table (PlayerProfileService
		// .AugmentSavedPlayers put them there), so they need no section of their own in the bundle.
		PlayerProfileService.RestoreFromCheckpoint(restored);
		TimeOfDayService.RestoreRecord(bundle.TimeOfDayRecord);
		ShipStorageService.RestoreRecord(bundle.ShipStorageRecord, firstLoad: false);
		AdminSafeZones.RestoreRecord(bundle.AdminSafeZonesRecord);
		ContractLedger.RestoreRecord(bundle.ContractRecord);
		PromotionService.RestoreRecord(bundle.PromotionRecord);
		checkpointSizeInBytes = checked((ulong)parsed.Body.LongLength);
		Restored = true;
		Console.WriteLine("[ClusterPlugin] global save restored generation=" + generation + " sequence=" + sequence);
		WarnIfNewerGlobalSaveOnDisk(globalRoot, generation, sequence);
	}

	/// <summary>
	/// T-0194: the recovery point is the CATALOGUE's newest global save, not the disk's. The gateway
	/// registry marks itself dirty on a mutation and flushes at most once per PersistInterval (60 s
	/// by default), so an unclean gateway stop loses the registrations made since the last flush
	/// while the FILES they name sit complete and checksummed in this host's own GLOBAL directory.
	/// The World Authority then comes back rewound by up to that interval and used to say nothing
	/// about it: P3-SUN-01 on 2026-09-19 restored g1/s2 with g1/s3 and g1/s4 unread beside it, and
	/// an administrator's time-of-day set captured in those two was simply gone.
	///
	/// This deliberately does NOT change the recovery point. The catalogue stays authoritative - a
	/// save the registry never acknowledged may belong to a generation that was fenced, and
	/// preferring the disk would let a stale host resurrect it. It only makes the rewind loud,
	/// which is candidate 1 of the ticket: across 4.5 days the live gateway started 25 times and
	/// every stop was clean, so the population that actually takes this path is our own bench,
	/// which kills uncleanly on purpose.
	/// </summary>
	private static void WarnIfNewerGlobalSaveOnDisk(string globalRoot, uint generation, uint sequence)
	{
		try
		{
			if (!Directory.Exists(globalRoot)) return;
			(uint Generation, uint Sequence, string Path)? newest = null;
			foreach (string file in Directory.EnumerateFiles(globalRoot, "g*-s*.mps"))
			{
				string name = Path.GetFileNameWithoutExtension(file);
				if (name.Length != 19 || name[0] != 'g' || name[9] != '-' || name[10] != 's') continue;
				if (!uint.TryParse(name.Substring(1, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint fileGeneration)
					|| !uint.TryParse(name.Substring(11, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint fileSequence))
					continue;
				if (fileGeneration < generation || (fileGeneration == generation && fileSequence <= sequence)) continue;
				if (newest != null && (fileGeneration < newest.Value.Generation
					|| (fileGeneration == newest.Value.Generation && fileSequence <= newest.Value.Sequence))) continue;
				newest = (fileGeneration, fileSequence, file);
			}
			if (newest == null) return;
			// Only the one candidate that would have been chosen is read, and only far enough to
			// separate "the catalogue lost a finished save" from "that write never finished".
			string verdict;
			try
			{
				PartitionSaveFile.Parsed parsed = PartitionSaveFile.Read(File.ReadAllBytes(newest.Value.Path));
				verdict = "well-formed, body checksum "
					+ parsed.BodyChecksum.ToString("X16", CultureInfo.InvariantCulture);
			}
			catch (Exception error)
			{
				verdict = "NOT readable as a save (" + error.GetType().Name + "), so it was most likely never finished";
			}
			Console.WriteLine("[ClusterPlugin] WARNING: global save rewind - restored generation=" + generation
				+ " sequence=" + sequence + ", but this host's own GLOBAL directory holds a newer save,"
				+ " generation=" + newest.Value.Generation + " sequence=" + newest.Value.Sequence
				+ " (" + verdict + ") at " + newest.Value.Path + ". The gateway registry never catalogued"
				+ " it, which is what an unclean gateway stop inside its flush interval looks like."
				+ " The world has come back without whatever that save held.");
		}
		catch (Exception error)
		{
			Console.WriteLine("[ClusterPlugin] could not check for a newer global save on disk: " + error.Message);
		}
	}

	/// <summary>
	/// #1365: the newest catalogued global save, read by a regular node at load (it takes the World Authority's NPC
	/// factions from it). A verified catalogued copy this host can read, else one fetched from a holder into memory: a
	/// node's GLOBAL store belongs to its PartitionPersistence, so nothing is written. Null when the registry has
	/// catalogued no global save yet, or no copy is reachable.
	/// </summary>
	internal static GlobalSaveFile.Bundle ReadNewest(string gateway, out uint generation, out uint sequence)
	{
		generation = sequence = 0;
		using HttpResponseMessage response = Client.GetAsync(gateway + "/registry/global-saves/recovery").GetAwaiter().GetResult();
		if (response.StatusCode == HttpStatusCode.NotFound) return null;
		response.EnsureSuccessStatusCode();
		using JsonDocument registration = JsonDocument.Parse(response.Content.ReadAsStream());
		JsonElement root = registration.RootElement;
		JsonElement saveId = root.GetProperty("SaveId");
		generation = checked((uint)saveId.GetProperty("Generation").GetInt64());
		sequence = checked((uint)saveId.GetProperty("Sequence").GetInt64());
		long expectedSize = root.GetProperty("SizeBytes").GetInt64();
		string expectedChecksum = root.GetProperty("Checksum").GetString();
		string location = VerifiedGlobalCopy(root.GetProperty("Locations").EnumerateArray().Select(x => x.GetString())
			.Where(x => Path.IsPathFullyQualified(x)), expectedSize, expectedChecksum);
		byte[] file = location != null ? File.ReadAllBytes(location) : null;
		string node = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID") ?? string.Empty;
		foreach (JsonProperty holder in root.GetProperty("HolderEndpoints").EnumerateObject())
		{
			if (file != null) break;
			if (holder.Name == node || holder.Value.ValueKind != JsonValueKind.String) continue;
			try
			{
				byte[] bytes = Client.GetByteArrayAsync(holder.Value.GetString().TrimEnd('/') + "/cluster/global-save-file?generation="
					+ generation.ToString(CultureInfo.InvariantCulture) + "&sequence=" + sequence.ToString(CultureInfo.InvariantCulture))
					.GetAwaiter().GetResult();
				if (bytes.LongLength == expectedSize && PartitionSaveFile.ReadHeader(bytes).BodyChecksum.ToString("X16", CultureInfo.InvariantCulture)
					.Equals(expectedChecksum, StringComparison.OrdinalIgnoreCase)) file = bytes;
				else Console.WriteLine("[ClusterPlugin] global_save fetch_mismatch=true holder=" + holder.Name + " bytes=" + bytes.LongLength);
			}
			catch (Exception exception)
			{
				Console.WriteLine("[ClusterPlugin] global_save fetch_failed=true holder=" + holder.Name
					+ " error=" + SecretRedactor.Redact(exception.Message));
			}
		}
		return file == null ? null : GlobalSaveFile.ReadBundle(file, generation, sequence);
	}

	/// <summary>
	/// T-0397: the first of <paramref name="locations"/> that exists and matches the catalogue (exact size, header and body
	/// checksum, the catalogued body checksum); null when none does. Each copy that exists but does not match is logged
	/// and skipped, never read as the world.
	/// </summary>
	internal static string VerifiedGlobalCopy(IEnumerable<string> locations, long expectedSize, string expectedChecksum)
	{
		foreach (string location in locations)
		{
			if (!File.Exists(location)) continue;
			try
			{
				byte[] file = File.ReadAllBytes(location);
				if (file.LongLength == expectedSize && PartitionSaveFile.ReadHeader(file).BodyChecksum.ToString("X16", CultureInfo.InvariantCulture)
					.Equals(expectedChecksum, StringComparison.OrdinalIgnoreCase)) return location;
				Console.WriteLine("[ClusterPlugin] global_save local_copy_mismatch=true path=" + location + " bytes=" + file.LongLength
					+ " catalogued=" + expectedSize);
			}
			catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
			{
				Console.WriteLine("[ClusterPlugin] global_save local_copy_damaged=true path=" + location + " error=" + exception.Message);
			}
		}
		return null;
	}

	/// <summary>
	/// Fetches the catalogued global save from a holder's control endpoint into this node's own
	/// GLOBAL store, verified against the catalog size and body checksum. Holder endpoints are
	/// refreshed from the registry between attempts because holders may still be re-registering
	/// (concurrent boot, registry rebuild). No usable holder combined with an available readable
	/// fallback short-circuits immediately.
	/// </summary>
	private static string FetchGlobal(string gateway, JsonElement registration, uint generation, uint sequence,
		long expectedSize, string expectedChecksum, string globalRoot, bool canFallback)
	{
		string node = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID") ?? string.Empty;
		string final = Path.Combine(globalRoot,
			string.Format(CultureInfo.InvariantCulture, "g{0:X8}-s{1:X8}.mps", generation, sequence));
		var holders = registration.GetProperty("HolderEndpoints").EnumerateObject()
			.Where(x => x.Name != node && x.Value.ValueKind == JsonValueKind.String)
			.ToDictionary(x => x.Name, x => x.Value.GetString());
		DateTime deadline = DateTime.UtcNow.AddSeconds(45);
		while (DateTime.UtcNow < deadline)
		{
			if (holders.Count == 0 && canFallback)
			{
				Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
					"[ClusterPlugin] global_save holder_unavailable=true generation={0} sequence={1}", generation, sequence));
				return null;
			}
			foreach (var holder in holders)
			{
				try
				{
					byte[] bytes = Client.GetByteArrayAsync(holder.Value.TrimEnd('/') + "/cluster/global-save-file?generation="
						+ generation.ToString(CultureInfo.InvariantCulture) + "&sequence=" + sequence.ToString(CultureInfo.InvariantCulture))
						.GetAwaiter().GetResult();
					PartitionPersistence.WriteVerifiedGlobalSave(bytes, expectedSize, expectedChecksum, final,
						ClusterRuntime.HostStore && node.Length != 0 ? node : null);
					Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
						"[ClusterPlugin] global_save fetched=true generation={0} sequence={1} bytes={2} from={3}",
						generation, sequence, bytes.Length, holder.Key));
					return final;
				}
				catch (Exception exception)
				{
					Console.WriteLine("[ClusterPlugin] global_save fetch_failed=true holder=" + holder.Key
						+ " error=" + SecretRedactor.Redact(exception.Message));
				}
			}
			System.Threading.Thread.Sleep(2000);
			try
			{
				using HttpResponseMessage refresh = Client.GetAsync(gateway + "/registry/global-saves/recovery").GetAwaiter().GetResult();
				if (refresh.IsSuccessStatusCode)
				{
					using JsonDocument document = JsonDocument.Parse(refresh.Content.ReadAsStream());
					JsonElement saveId = document.RootElement.GetProperty("SaveId");
					if (saveId.GetProperty("Generation").GetInt64() == generation && saveId.GetProperty("Sequence").GetInt64() == sequence)
						holders = document.RootElement.GetProperty("HolderEndpoints").EnumerateObject()
							.Where(x => x.Name != node && x.Value.ValueKind == JsonValueKind.String)
							.ToDictionary(x => x.Name, x => x.Value.GetString());
				}
			}
			catch (Exception) { }
		}
		return null;
	}
}
