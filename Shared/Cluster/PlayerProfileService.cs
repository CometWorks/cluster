using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Sandbox.Game.Entities;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ObjectBuilders.Private;
using VRage;
using VRageMath;

namespace ClusterPlugin;

/// <summary>
/// The per-PLAYER settings of record: toolbar, build colour palette, selected colour slot, build armor
/// skin and the creative-tools flag. WorldAuthority.md §5.5 covers the per-CHARACTER settings
/// (<see cref="CharacterRegistryService"/>); these belong to the player, not to the body, and a single
/// server keeps them for as long as the world exists - across relogs and across any offline period.
/// </summary>
/// <remarks>
/// On a single dedicated server all of this lives in ONE session: <c>MySession.Static.Toolbars</c> keyed by
/// <c>MyPlayer.PlayerId</c>, the palette on <c>MyPlayer</c> and in <c>MyCubeBuilder.AllPlayersColors</c>, and
/// all of it is written into <c>checkpoint.AllPlayersData</c> - which is also the checkpoint a joining client
/// receives, so the client initialises its toolbar from the server's copy
/// (<c>MySession.LoadWorld</c> -> <c>MyToolbarComponent.InitCharacterToolbar</c>).
///
/// On the cluster every node has its own session, so all of it was node-local: a player who joined a node
/// that had never held them got the scenario's DEFAULT toolbar and palette, and every edit they made was
/// stored on whichever node happened to host them and lost with it. Measured on w23, 2026-08-23: four marked
/// toolbar slots and a marked palette slot survived a live node-to-node move (the client keeps its own copy
/// through a handover) and came back as the scenario default after a relog
/// (findings/player-settings-are-node-local-and-lost-on-rejoin.md).
///
/// The shape is the one PlayerStateService and CharacterRegistryService already use: nodes report the players
/// they host once a second, the World Authority merges and keeps the record sticky for players nobody hosts,
/// and the merged view replicates back. Two things then use the record:
///
///   * <see cref="Reconcile"/> seeds a node's own session the first time it sees a player, so the toolbar the
///     player edits exists here (vanilla drops a slot change for a <c>PlayerId</c> with no toolbar) and so
///     this node's next report carries the player's real settings rather than the defaults it would otherwise
///     have invented.
///   * <see cref="AugmentSavedPlayers"/> writes the record into every checkpoint this node builds, which is
///     both how a joining client gets its toolbar back and how the World Authority's global save persists it.
/// </remarks>
public static class PlayerProfileService
{
	private const string Subsystem = "player-profiles";
	private const int MaxEntries = PlayerProfileCodec.MaxEntries;
	private const int MaxToolbarBytes = PlayerProfileCodec.MaxToolbarBytes;
	private const double ReportExpiryMilliseconds = 30000;
	private const int ColorSlotCount = PlayerProfileCodec.ColorSlotCount;
	private const int MaxCameras = PlayerProfileCodec.MaxCameras;
	private const AdminSettingsEnum AdminSettingsMask = (AdminSettingsEnum)PlayerProfileCodec.AdminSettingsMask;

	private static readonly object Sync = new object();
	private static readonly string Node = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID") ?? string.Empty;

	// World Authority state: the sticky record, plus the live reports it is merged from.
	private static readonly Dictionary<string, StampedReport> ReportsByOrigin = new(StringComparer.Ordinal);
	// Keyed the way PlayerProfileCodec.Merge wants it, so the merge folds reports into this dictionary
	// directly instead of copying the whole record on every report.
	private static readonly Dictionary<(ulong SteamId, int SerialId), PlayerProfile> Record = new();

	// Node state.
	private static Dictionary<Key, PlayerProfile> m_cluster = new();
	private static bool m_clusterReady;
	private static string m_snapshot = PlayerProfileCodec.WriteSnapshot(Array.Empty<PlayerProfile>());
	private static string m_pending;
	private static double m_nextReport;
	private static string m_lastReport;
	private static double m_nextForcedReport;
	private static readonly HashSet<Key> Reconciled = new();
	// Serializing a toolbar is the one expensive part of a report, and a toolbar changes when a player
	// edits it - about never. The cache holds the last XML per player and the subscription below is what
	// invalidates it, so the once-a-second report costs a dictionary lookup instead of an XML pass.
	private static readonly Dictionary<Key, string> ToolbarXmlCache = new();
	private static readonly Dictionary<Key, MyToolbar> ToolbarWatched = new();
	private static readonly HashSet<Key> ToolbarDirty = new();

	private readonly record struct Key(ulong SteamId, int SerialId);

	private sealed record StampedReport(double Received, PlayerProfile[] Entries);


	internal static void Install()
	{
		GlobalStateChannel.RegisterAuthorityHandler(Subsystem, ReceiveReport);
		GlobalStateChannel.RegisterReplicaHandler(Subsystem, ReceiveSnapshot);
		GlobalStateChannel.RegisterSnapshot(Subsystem, () => { lock (Sync) return m_snapshot; }, ReceiveSnapshot);
		MySessionSeams.ClusterAfterSavePlayers = AugmentSavedPlayers;
	}

	public static void Update()
	{
		if (!ClusterRuntime.IsClustered || MySession.Static?.Ready != true) return;
		// Reconcile BEFORE reporting, for the reason CharacterRegistryService settles before reporting: a
		// player this node has just taken carries whatever defaults vanilla invented for them, and a report
		// captured first would overwrite the record with those defaults.
		ApplyPending();
		if (ClusterRuntime.LocalMilliseconds < m_nextReport) return;
		m_nextReport = ClusterRuntime.LocalMilliseconds + 1000;
		string payload = PlayerProfileCodec.WriteReport(new PlayerProfileReport(Node, CaptureLocal()));
		// Unlike the liveness-carrying reports next door, an unchanged report here says nothing: the
		// record is sticky, so a node that stops reporting loses nothing. Sending only changes keeps a
		// full fleet's toolbars off the control plane and off the game thread.
		// ...but resend a stale-identical report every 30 s anyway, so a World Authority that came up
		// without a record (a replacement WA, a generation change) relearns it from the nodes instead of
		// waiting for someone to edit a toolbar.
		if (payload == m_lastReport && ClusterRuntime.LocalMilliseconds < m_nextForcedReport) return;
		m_lastReport = payload;
		m_nextForcedReport = ClusterRuntime.LocalMilliseconds + 30000;
		if (ClusterRuntime.IsWorldAuthority) ReceiveReport(Node, payload);
		else GlobalStateChannel.SendRequest(Subsystem, payload);
	}

	// -- node half ---------------------------------------------------------

	/// <summary>Every real player attached to THIS node, as their settings currently stand here.</summary>
	private static PlayerProfile[] CaptureLocal()
	{
		MySession session = MySession.Static;
		var profiles = new List<PlayerProfile>();
		foreach (MyPlayer player in session.Players.GetOnlinePlayers())
		{
			if (player?.Identity == null || !player.IsRealPlayer) continue;
			var key = new Key(player.Id.SteamId, player.Id.SerialId);
			// Never report a player this node has not reconciled yet: until then its toolbar and palette
			// are whatever vanilla defaulted them to, and the merge would take that for the truth.
			lock (Sync)
				if (!Reconciled.Contains(key)) continue;
			profiles.Add(Capture(player));
			if (profiles.Count >= MaxEntries) break;
		}
		return profiles.OrderBy(profile => profile.SteamId).ThenBy(profile => profile.SerialId).ToArray();
	}

	private static PlayerProfile Capture(MyPlayer player)
	{
		var key = new Key(player.Id.SteamId, player.Id.SerialId);
		MyToolbar toolbar = MySession.Static.Toolbars.TryGetPlayerToolbar(player.Id);
		lock (Sync)
			if (ToolbarDirty.Remove(key) || !ToolbarXmlCache.ContainsKey(key))
				ToolbarXmlCache[key] = toolbar == null ? null : WriteToolbar(toolbar.GetObjectBuilder());
		MySession.Static.RemoteAdminSettings.TryGetValue(player.Id.SteamId, out AdminSettingsEnum admin);
		return new PlayerProfile(player.Id.SteamId, player.Id.SerialId, player.Identity.IdentityId,
			ToolbarXmlCache[key],
			player.BuildColorSlots?.Select(color => new[] { color.X, color.Y, color.Z }).SelectMany(x => x).ToArray(),
			player.SelectedBuildColorSlot, player.BuildArmorSkin ?? string.Empty,
			// CreativeTools rather than CreativeToolsEnabled: the latter also asks HasPlayerCreativeRights,
			// so a survival world would report the flag as false and the merge would erase what the player set.
			MySession.Static.CreativeTools.Contains(player.Id.SteamId),
			(int)(admin & AdminSettingsMask), CaptureCameras(player.Id));
	}

	/// <summary>
	/// This node's camera settings for the player. They are per ENTITY, and a node holds a fraction of the
	/// cluster's entities - <c>SaveCameraCollection</c> even drops the ones whose entity is not here - so the
	/// World Authority unions them rather than taking a report as the whole truth.
	/// </summary>
	private static PlayerCameraSetting[] CaptureCameras(MyPlayer.PlayerId playerId)
	{
		IReadOnlyDictionary<long, MyEntityCameraSettings> settings = MySession.Static.Cameras?.ClusterCameraSettings(playerId);
		if (settings == null) return Array.Empty<PlayerCameraSetting>();
		return settings.Where(entry => entry.Value != null).Take(MaxCameras)
			.Select(entry => new PlayerCameraSetting(entry.Key, entry.Value.Distance,
				entry.Value.HeadAngle?.X ?? 0f, entry.Value.HeadAngle?.Y ?? 0f, entry.Value.IsFirstPerson))
			.OrderBy(camera => camera.EntityId).ToArray();
	}

	private static void ApplyPending()
	{
		string payload;
		lock (Sync)
		{
			payload = m_pending;
			m_pending = null;
		}
		if (payload != null)
		{
			try
			{
				PlayerProfileSnapshot snapshot = PlayerProfileCodec.ReadSnapshot(payload);
				lock (Sync)
				{
					m_cluster = snapshot.Profiles.ToDictionary(profile => new Key(profile.SteamId, profile.SerialId));
					m_clusterReady = true;
				}
			}
			catch (Exception error)
			{
				Console.WriteLine("[ClusterPlugin] player profile snapshot rejected: " + error.Message);
			}
		}
		// The World Authority reconciles against its own record; a node waits for its first snapshot, so a
		// node that has not heard from the WA yet never invents a profile for a player.
		if (!ClusterRuntime.IsWorldAuthority)
			lock (Sync)
				if (!m_clusterReady) return;
		MySession session = MySession.Static;
		var here = new HashSet<Key>();
		SeedAbsentPlayers(session);
		foreach (MyPlayer player in session.Players.GetOnlinePlayers())
		{
			if (player?.Identity == null || !player.IsRealPlayer) continue;
			var key = new Key(player.Id.SteamId, player.Id.SerialId);
			here.Add(key);
			lock (Sync)
				if (!Reconciled.Add(key)) continue;
			Reconcile(player, key);
		}
		lock (Sync)
		{
			foreach (Key gone in Reconciled.Where(key => !here.Contains(key)).ToArray())
			{
				Reconciled.Remove(gone);
				ToolbarXmlCache.Remove(gone);
				Unwatch(gone);
			}
		}
	}

	/// <summary>
	/// Give this node's session the player's settings of record, once, when it first sees the player -
	/// whether they arrived by join, by handover adoption or by recovery. Idempotent by
	/// <see cref="Reconciled"/>: a player who then edits their toolbar here is never overwritten by it.
	/// </summary>
	private static void Reconcile(MyPlayer player, Key key)
	{
		PlayerProfile stored;
		lock (Sync)
			stored = ClusterRuntime.IsWorldAuthority
				? Record.GetValueOrDefault((key.SteamId, key.SerialId))
				: m_cluster.GetValueOrDefault(key);
		if (stored == null)
		{
			// No record yet - this is a first-ever join. Make sure a toolbar EXISTS for the player, or
			// vanilla drops every slot change they make (MyToolBarCollection gates on ContainsToolbar).
			EnsureToolbar(player.Id, null);
			Watch(player.Id);
			Console.WriteLine($"[ClusterPlugin] player_profile_seen player={key.SteamId}:{key.SerialId} "
				+ $"identity={player.Identity.IdentityId} record=false");
			return;
		}
		EnsureToolbar(player.Id, stored.ToolbarXml);
		Watch(player.Id);
		ApplyAdminState(player.Id.SteamId, stored);
		if (stored.Colors != null && stored.Colors.Length == ColorSlotCount * 3)
		{
			var colors = new List<Vector3>(ColorSlotCount);
			for (int slot = 0; slot < ColorSlotCount; slot++)
				colors.Add(new Vector3(stored.Colors[slot * 3], stored.Colors[slot * 3 + 1], stored.Colors[slot * 3 + 2]));
			player.SetBuildColorSlots(colors);
			if (MyCubeBuilder.AllPlayersColors != null) MyCubeBuilder.AllPlayersColors[player.Id] = colors;
			player.SelectedBuildColorSlot = stored.SelectedColor;
		}
		if (!string.IsNullOrEmpty(stored.ArmorSkin)) player.BuildArmorSkin = stored.ArmorSkin;
		foreach (PlayerCameraSetting camera in stored.Cameras ?? Array.Empty<PlayerCameraSetting>())
			MySession.Static.Cameras?.SaveEntityCameraSettings(player.Id, camera.EntityId, camera.FirstPerson,
				camera.Distance, isLocalCharacter: false, camera.HeadX, camera.HeadY, sync: false);
		Console.WriteLine($"[ClusterPlugin] player_profile_restored player={key.SteamId}:{key.SerialId} "
			+ $"identity={player.Identity.IdentityId} toolbar={(stored.ToolbarXml != null).ToString().ToLowerInvariant()} "
			+ $"colors={(stored.Colors != null).ToString().ToLowerInvariant()} "
			+ $"admin=0x{stored.AdminSettings:X} creative={stored.CreativeTools.ToString().ToLowerInvariant()}");
	}

	/// <summary>
	/// Put the record for every player this node is NOT hosting into the process-wide tables vanilla reads
	/// when it creates a player: <c>MyCubeBuilder.AllPlayersColors</c> for the palette, and
	/// <c>MySession.Static.RemoteAdminSettings</c> / <c>CreativeTools</c> for the Alt-F10 flags.
	///
	/// Seeding, rather than only reconciling once the player exists, is what makes the flags survive a node
	/// crossing. <c>MyPlayerCollection.CreateNewPlayer</c> reads all three of these tables to build the
	/// <c>MyObjectBuilder_Player</c> it broadcasts, and it runs the moment the client is adopted - before this
	/// service's next tick. A node that learned the values a second late would have told every client that the
	/// arriving player is not invulnerable, cannot use terminals and has no creative tools.
	/// </summary>
	/// <remarks>
	/// A player this node currently hosts is skipped on purpose: their <c>MyPlayer</c> holds the very list
	/// object in the colour dictionary (<c>MyPlayer.Init</c>, <c>SetBuildColorSlots</c>), so replacing the
	/// entry would leave the live player writing into a list nothing else reads - and their admin flags are
	/// live state this node owns, which a seed would overwrite the instant they toggled a checkbox.
	/// </remarks>
	private static void SeedAbsentPlayers(MySession session)
	{
		PlayerProfile[] profiles;
		lock (Sync)
			profiles = ClusterRuntime.IsWorldAuthority ? Record.Values.ToArray() : m_cluster.Values.ToArray();
		if (profiles.Length == 0) return;
		MyCubeBuilder.AllPlayersColors ??= new Dictionary<MyPlayer.PlayerId, List<Vector3>>();
		HashSet<Key> hosted = session.Players.GetOnlinePlayers().Where(player => player?.Identity != null)
			.Select(player => new Key(player.Id.SteamId, player.Id.SerialId)).ToHashSet();
		HashSet<ulong> hostedClients = hosted.Select(key => key.SteamId).ToHashSet();
		foreach (PlayerProfile profile in profiles)
		{
			var key = new Key(profile.SteamId, profile.SerialId);
			if (hosted.Contains(key)) continue;
			// Admin state is per STEAM id, so it is only safe to seed when no serial of that client is
			// hosted here; the palette below is per (steam, serial) and needs no such care.
			if (!hostedClients.Contains(profile.SteamId)) ApplyAdminState(profile.SteamId, profile);
			if (profile.Colors == null || profile.Colors.Length != ColorSlotCount * 3) continue;
			var playerId = new MyPlayer.PlayerId(profile.SteamId, profile.SerialId);
			var colors = new List<Vector3>(ColorSlotCount);
			for (int slot = 0; slot < ColorSlotCount; slot++)
				colors.Add(new Vector3(profile.Colors[slot * 3], profile.Colors[slot * 3 + 1], profile.Colors[slot * 3 + 2]));
			if (MyCubeBuilder.AllPlayersColors.TryGetValue(playerId, out List<Vector3> present)
				&& present.SequenceEqual(colors)) continue;
			MyCubeBuilder.AllPlayersColors[playerId] = colors;
		}
	}

	/// <summary>
	/// Put the player's Alt-F10 state into this node's session: the <c>AdminSettingsEnum</c> flags and the
	/// creative-tools toggle. Both are node-local runtime state on a single server
	/// (<c>MyGuiScreenAdminMenu.AdminSettingsChanged</c> and <c>MySession.EnableCreativeTools</c> are
	/// <c>[Server]</c> events that write the hosting session and broadcast), so on the cluster a player who
	/// crossed a node boundary silently lost every server-side effect of those checkboxes - invulnerability,
	/// terminal access, safe-zone and PCU exemptions - while the client kept showing them ticked.
	/// </summary>
	/// <remarks>
	/// Masked by the player's promote level exactly as vanilla's own handler masks it, so a record cannot
	/// grant more than the level allows; <c>PromotionService</c> replicates the levels themselves and seeds
	/// them from the checkpoint at boot, so the mask is meaningful by the time any client is attached.
	/// Written straight into the collections rather than through <c>EnableCreativeTools</c>, whose
	/// <c>HasCreativeRights</c> gate asks about the LOCAL player and whose broadcast would tell every client
	/// that the server itself had toggled the flag.
	/// </remarks>
	private static void ApplyAdminState(ulong steamId, PlayerProfile profile)
	{
		MySession session = MySession.Static;
		if (session == null) return;
		var stored = (AdminSettingsEnum)profile.AdminSettings & AdminSettingsMask;
		AdminSettingsEnum allowed = session.GetUserAllowedAdminSettings(steamId);
		session.RemoteAdminSettings[steamId] = stored & allowed;
		if (profile.CreativeTools && session.HasPlayerCreativeRights(steamId)) session.CreativeTools.Add(steamId);
		else session.CreativeTools.Remove(steamId);
	}

	/// <summary>Follow this player's toolbar so a report only re-serializes one that changed.</summary>
	private static void Watch(MyPlayer.PlayerId playerId)
	{
		var key = new Key(playerId.SteamId, playerId.SerialId);
		MyToolbar toolbar = MySession.Static.Toolbars.TryGetPlayerToolbar(playerId);
		lock (Sync)
		{
			Unwatch(key);
			if (toolbar == null) return;
			ToolbarWatched[key] = toolbar;
			toolbar.ItemChanged += OnToolbarItemChanged;
			ToolbarDirty.Add(key);
		}
	}

	private static void Unwatch(Key key)
	{
		if (!ToolbarWatched.Remove(key, out MyToolbar toolbar)) return;
		toolbar.ItemChanged -= OnToolbarItemChanged;
	}

	private static void OnToolbarItemChanged(MyToolbar toolbar, MyToolbar.IndexArgs index, bool gamepad)
	{
		lock (Sync)
			foreach ((Key key, MyToolbar watched) in ToolbarWatched)
				if (ReferenceEquals(watched, toolbar)) { ToolbarDirty.Add(key); return; }
	}

	/// <summary>
	/// Replace this node's toolbar for the player with the stored one, or create the scenario default when
	/// there is nothing stored. <c>AddPlayerToolbar</c> refuses to overwrite, so an existing one is removed
	/// first - a node that already invented a default for this player must not keep it.
	/// </summary>
	private static void EnsureToolbar(MyPlayer.PlayerId playerId, string toolbarXml)
	{
		MyToolBarCollection toolbars = MySession.Static.Toolbars;
		// ParseToolbar, not the cache: this builder is handed to MyToolbar.Init, and the cached instances
		// are shared between checkpoints on the strict understanding that nothing but serialization sees them.
		MyObjectBuilder_Toolbar builder = toolbarXml == null ? null : ParseToolbar(toolbarXml);
		if (toolbarXml != null && builder == null) return;   // malformed: keep whatever is here
		if (builder == null && toolbars.ContainsToolbar(playerId)) return;
		var toolbar = new MyToolbar(MyToolbarType.Character);
		toolbar.Init(builder ?? MySession.Static.Scenario?.DefaultToolbar, null, skipAssert: true);
		toolbars.RemovePlayerToolbar(playerId);
		toolbars.AddPlayerToolbar(playerId, toolbar);
	}

	// -- checkpoint augmentation -------------------------------------------

	/// <summary>
	/// Write the settings of record into a checkpoint's player table, for every cluster player this node is
	/// not itself hosting. Called after <c>SaveToolbars</c>, so it both CREATES the entries a node has never
	/// seen and REPAIRS the ones a node kept from a player who has since moved on.
	/// </summary>
	/// <remarks>
	/// This is what makes a rejoining player whole: the checkpoint a client receives on join is built by the
	/// node it joined, whose own session knows nothing about a player it has never hosted, and the client
	/// initialises its toolbar and palette from exactly this table
	/// (<c>MySession.LoadWorld</c> -> <c>MyToolbarComponent.InitCharacterToolbar</c>). The same table is what
	/// the World Authority's global save persists, so the record survives a cluster restart.
	///
	/// Players this node hosts are skipped: their live session state is the fresher copy and vanilla has
	/// already written it.
	/// </remarks>
	public static void AugmentSavedPlayers(MyObjectBuilder_Checkpoint checkpoint)
	{
		if (!ClusterRuntime.IsClustered || checkpoint?.AllPlayersData?.Dictionary == null) return;
		PlayerProfile[] profiles;
		lock (Sync)
			profiles = ClusterRuntime.IsWorldAuthority ? Record.Values.ToArray() : m_cluster.Values.ToArray();
		HashSet<Key> hosted = MySession.Static?.Players.GetOnlinePlayers()
			.Where(player => player?.Identity != null)
			.Select(player => new Key(player.Id.SteamId, player.Id.SerialId)).ToHashSet() ?? new HashSet<Key>();
		foreach (PlayerProfile profile in profiles)
		{
			if (hosted.Contains(new Key(profile.SteamId, profile.SerialId))) continue;
			var playerId = new MyObjectBuilder_Checkpoint.PlayerId(profile.SteamId, profile.SerialId);
			if (!checkpoint.AllPlayersData.Dictionary.TryGetValue(playerId, out MyObjectBuilder_Player player)
				|| player == null)
			{
				player = new MyObjectBuilder_Player { Connected = false };
				checkpoint.AllPlayersData.Dictionary[playerId] = player;
			}
			player.IdentityId = profile.IdentityId;
			player.DisplayName ??= MySession.Static?.Players.TryGetIdentity(profile.IdentityId)?.DisplayPlatformName;
			player.BuildColorSlot = profile.SelectedColor;
			player.BuildArmorSkin = profile.ArmorSkin;
			player.CreativeToolsEnabled = profile.CreativeTools;
			player.RemoteAdminSettings = profile.AdminSettings;
			// Not from the profile: PromotionService owns promote levels and keeps them replicated. Read
			// straight out of PromotedUsers rather than through GetUserPromoteLevel, which answers Owner for
			// everybody in OFFLINE mode and would write that into the save.
			if (MySession.Static != null
				&& MySession.Static.PromotedUsers.TryGetValue(profile.SteamId, out MyPromoteLevel level))
				player.PromoteLevel = level;
			player.Toolbar = ReadToolbar(profile.ToolbarXml) ?? player.Toolbar;
			if (profile.Colors != null && profile.Colors.Length == ColorSlotCount * 3)
			{
				player.BuildColorSlots = new List<Vector3>(ColorSlotCount);
				for (int slot = 0; slot < ColorSlotCount; slot++)
					player.BuildColorSlots.Add(new Vector3(profile.Colors[slot * 3],
						profile.Colors[slot * 3 + 1], profile.Colors[slot * 3 + 2]));
				// Both tables, because the client reads the palette from either depending on which of them
				// has the player: MyPlayer.Init takes the builder's list, and MyPlayerCollection.LoadPlayers
				// takes AllPlayersColors for a player it already has.
				if (checkpoint.AllPlayersColors?.Dictionary != null)
					checkpoint.AllPlayersColors.Dictionary[playerId] = player.BuildColorSlots;
			}
			if (profile.Cameras?.Length > 0)
				player.EntityCameraData = profile.Cameras.Select(camera => new CameraControllerSettings
				{
					EntityId = camera.EntityId,
					Distance = camera.Distance,
					IsFirstPerson = camera.FirstPerson,
					HeadAngle = new SerializableVector2(camera.HeadX, camera.HeadY)
				}).ToList();
		}
	}

	// -- World Authority half ----------------------------------------------

	private static void ReceiveReport(string origin, string payload)
	{
		PlayerProfileReport report = PlayerProfileCodec.ReadReport(payload);
		string snapshot;
		lock (Sync)
		{
			double now = ClusterRuntime.LocalMilliseconds;
			ReportsByOrigin[origin] = new StampedReport(now, report.Entries);
			foreach (string stale in ReportsByOrigin
				.Where(entry => now - entry.Value.Received >= ReportExpiryMilliseconds)
				.Select(entry => entry.Key).ToArray())
				ReportsByOrigin.Remove(stale);
			snapshot = Merge();
			if (snapshot == m_snapshot) return;
			m_snapshot = snapshot;
			m_pending = snapshot;
		}
		GlobalStateChannel.Publish(Subsystem, snapshot);
	}

	/// <summary>
	/// The hosting node speaks for its own players; everyone else's record stays as it was. Nothing is ever
	/// removed - a player who has been offline for a week still has their toolbar when they come back.
	/// </summary>
	private static string Merge()
	{
		// Oldest report first: see PlayerProfileCodec.Merge for why the order is load-bearing.
		PlayerProfileCodec.Merge(Record, ReportsByOrigin.Values
			.OrderBy(report => report.Received).Select(report => report.Entries));
		return PlayerProfileCodec.WriteSnapshot(Record.Values);
	}

	private static void ReceiveSnapshot(string payload)
	{
		if (payload == null || payload.Length > 32 * 1024 * 1024)
			throw new InvalidOperationException("Invalid player profile snapshot");
		lock (Sync) m_pending = payload;
	}

	/// <summary>
	/// Seed the record from a checkpoint the World Authority loaded - the global save carries the profiles in
	/// its player table, written there by <see cref="AugmentSavedPlayers"/>, so a cluster restart keeps them.
	/// </summary>
	public static void RestoreFromCheckpoint(MyObjectBuilder_Checkpoint checkpoint)
	{
		if (checkpoint?.AllPlayersData?.Dictionary == null) return;
		int restored = 0;
		lock (Sync)
			foreach ((MyObjectBuilder_Checkpoint.PlayerId playerId, MyObjectBuilder_Player player)
				in checkpoint.AllPlayersData.Dictionary)
			{
				if (player == null || player.IdentityId <= 0 || player.IsWildlifeAgent) continue;
				var key = new Key(playerId.GetClientId(), playerId.SerialId);
				if (Record.ContainsKey((key.SteamId, key.SerialId))) continue;
				Record[(key.SteamId, key.SerialId)] = new PlayerProfile(key.SteamId, key.SerialId, player.IdentityId,
					player.Toolbar == null ? null : WriteToolbar(player.Toolbar),
					player.BuildColorSlots?.Count == ColorSlotCount
						? player.BuildColorSlots.SelectMany(color => new[] { color.X, color.Y, color.Z }).ToArray()
						: null,
					player.BuildColorSlot, player.BuildArmorSkin ?? string.Empty, player.CreativeToolsEnabled,
					player.RemoteAdminSettings & PlayerProfileCodec.AdminSettingsMask,
					player.EntityCameraData?.Take(MaxCameras).Select(camera => new PlayerCameraSetting(camera.EntityId,
						camera.Distance, camera.HeadAngle?.X ?? 0f, camera.HeadAngle?.Y ?? 0f,
						camera.IsFirstPerson)).ToArray() ?? Array.Empty<PlayerCameraSetting>());
				restored++;
			}
		if (restored > 0) Console.WriteLine("[ClusterPlugin] player profiles restored count=" + restored);
	}

	// -- serialization -----------------------------------------------------

	private static string WriteToolbar(MyObjectBuilder_Toolbar toolbar)
	{
		if (toolbar == null) return null;
		using MemoryStream stream = new MemoryStream();
		if (!MyObjectBuilderSerializerKeen.SerializeXML(stream, toolbar)) return null;
		return stream.Length > MaxToolbarBytes ? null : Encoding.UTF8.GetString(stream.ToArray());
	}

	/// <summary>
	/// Deserialized toolbars, keyed by the XML they came from. Every checkpoint this node builds - each
	/// periodic save, and each joining client's world - would otherwise re-parse every stored toolbar on
	/// the game thread. The cached builders are only ever serialized again, never mutated, and the key is
	/// the payload itself, so a player who edits their toolbar simply misses the cache once.
	/// </summary>
	private static readonly Dictionary<string, MyObjectBuilder_Toolbar> ToolbarBuilderCache = new(StringComparer.Ordinal);

	private static MyObjectBuilder_Toolbar ReadToolbar(string xml)
	{
		if (string.IsNullOrEmpty(xml) || xml.Length > MaxToolbarBytes) return null;
		lock (Sync)
			if (ToolbarBuilderCache.TryGetValue(xml, out MyObjectBuilder_Toolbar cached)) return cached;
		MyObjectBuilder_Toolbar parsed = ParseToolbar(xml);
		if (parsed == null) return null;
		lock (Sync)
		{
			if (ToolbarBuilderCache.Count >= MaxEntries) ToolbarBuilderCache.Clear();
			ToolbarBuilderCache[xml] = parsed;
		}
		return parsed;
	}

	private static MyObjectBuilder_Toolbar ParseToolbar(string xml)
	{
		try
		{
			using MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
			return MyObjectBuilderSerializerKeen.DeserializeXML(stream, out MyObjectBuilder_Toolbar toolbar)
				? toolbar : null;
		}
		catch (Exception error)
		{
			Console.WriteLine("[ClusterPlugin] player profile toolbar rejected: " + error.Message);
			return null;
		}
	}

	// -- test/operator surface ---------------------------------------------

	public static object Status()
	{
		lock (Sync)
		{
			IEnumerable<PlayerProfile> profiles = ClusterRuntime.IsWorldAuthority
				? Record.Values.ToArray() : m_cluster.Values.ToArray();
			return new
			{
				node = Node,
				isAuthority = ClusterRuntime.IsWorldAuthority,
				ready = m_clusterReady || ClusterRuntime.IsWorldAuthority,
				// What THIS node's session would answer right now, which is what every server-side admin
				// check reads (MySession.Static.RemoteAdminSettings). The record above is only a promise
				// until it lands here, and a node that forgot a player answers None without saying so.
				session = SessionAdminState(),
				reconciled = Reconciled.Select(key => key.SteamId + ":" + key.SerialId).OrderBy(x => x).ToArray(),
				profiles = profiles.Select(profile => new
				{
					player = profile.SteamId + ":" + profile.SerialId,
					identity = profile.IdentityId,
					toolbarBytes = profile.ToolbarXml?.Length ?? 0,
					toolbarItems = ToolbarItemCount(profile.ToolbarXml),
					selectedColor = profile.SelectedColor,
					armorSkin = profile.ArmorSkin,
					creativeTools = profile.CreativeTools,
					adminSettings = "0x" + profile.AdminSettings.ToString("X"),
					cameras = profile.Cameras?.Length ?? 0
				}).OrderBy(profile => profile.player, StringComparer.Ordinal).ToArray()
			};
		}
	}

	private static object SessionAdminState()
	{
		MySession session = MySession.Static;
		if (session == null) return Array.Empty<object>();
		return session.RemoteAdminSettings.Select(entry => new
		{
			client = entry.Key,
			adminSettings = "0x" + ((int)entry.Value).ToString("X"),
			creativeTools = session.CreativeTools.Contains(entry.Key),
			promoteLevel = session.PromotedUsers.TryGetValue(entry.Key, out MyPromoteLevel level)
				? level.ToString() : MyPromoteLevel.None.ToString()
		}).OrderBy(entry => entry.client).ToArray();
	}

	private static int ToolbarItemCount(string xml)
	{
		MyObjectBuilder_Toolbar toolbar = ReadToolbar(xml);
		return toolbar?.Slots?.Count ?? 0;
	}
}
