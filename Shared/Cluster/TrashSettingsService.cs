using System;
using System.Text.Json;
using System.Threading.Tasks;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using VRage.Game;

namespace ClusterPlugin;

/// <summary>
/// SE-1596: the Trash Removal settings of the Alt-F10 admin menu are World Authority state.
/// <para>
/// Vanilla's <c>UploadSettingsToServer</c> writes them into the session settings of the server the admin is on. In a
/// cluster that changed one node, and the node's next restart loaded the world file again. Now the World Authority's
/// settings are the cluster's: the world file's until an admin changes them, then the admin's, kept in a section of the
/// global save (every role loads its session settings from the world file, the World Authority too). It publishes them
/// on the global state channel with a version that grows with each change. A node forwards an admin's upload to the
/// World Authority and writes the newest published set into its session settings, so the admin menu of every node
/// shows the cluster's settings and the node's sweep runs with them. The vanilla upload still runs first on the
/// admin's node, so the change shows there at once.
/// </para>
/// </summary>
public static class TrashSettingsService
{
    private const string Subsystem = "trash-settings";
    private const int CommandTimeoutMilliseconds = 10000;

    internal sealed record Settings(
        int Flags,
        bool Enable,
        int BlockCount,
        float PlayerDistance,
        int GridCount,
        float PlayerInactivity,
        int CharacterRemovalThreshold,
        int StopGridsPeriod,
        int RemoveOldIdentities,
        bool VoxelEnable,
        float VoxelDistanceFromPlayer,
        float VoxelDistanceFromGrid,
        int VoxelAge,
        short CargoBagsMaxLiveTime,
        short MaxCargoBags
    );

    internal sealed record Set(long Generation, long Version, Settings Settings);

    private static readonly object Gate = new object();

    // World Authority: the cluster's settings, null until its session is ready or the global save restored an admin's;
    // s_adminSet once they are an admin's rather than the world file's.
    private static Settings s_current;
    private static long s_version;
    private static bool s_adminSet;
    private static Settings s_appliedOnAuthority;

    // Regular node: the newest set seen, the one applied, and an admin's upload waiting for the World Authority.
    private static Set s_newest;
    private static (long Generation, long Version) s_applied;
    private static Settings s_outgoing;
    private static Task s_flushing = Task.CompletedTask;
    private static double s_nextFlush;

    internal static void Install()
    {
        GlobalStateChannel.RegisterCommandHandler(Subsystem, Handle, journaled: true);
        GlobalStateChannel.RegisterReplicaHandler(Subsystem, Offer);
        GlobalStateChannel.RegisterSnapshot(Subsystem, Snapshot, Offer);
        MyGuiScreenAdminMenuSeams.ClusterTrashSettingsUploaded = Uploaded;
    }

    /// <summary>Game thread, every cluster frame.</summary>
    internal static void Update()
    {
        MySession session = MySession.Static;
        if (!ClusterRuntime.IsClustered || session?.Ready != true)
            return;
        if (ClusterRuntime.IsWorldAuthority)
        {
            UpdateAuthority(session);
            return;
        }
        Set newest;
        lock (Gate)
        {
            // An admin's own upload goes first; applying an older set before it would undo it here for a moment.
            if (s_outgoing != null)
            {
                if (s_flushing.IsCompleted && ClusterRuntime.LocalMilliseconds >= s_nextFlush)
                    s_flushing = Task.Run(Flush);
                return;
            }
            newest = s_newest;
        }
        if (newest == null || (newest.Generation, newest.Version).CompareTo(s_applied) <= 0)
            return;
        s_applied = (newest.Generation, newest.Version);
        if (newest.Settings == Read(session.Settings))
            return;
        Apply(session, newest.Settings);
        Console.WriteLine(
            "[ClusterPlugin] trash settings applied generation="
                + newest.Generation
                + " version="
                + newest.Version
        );
    }

    private static void UpdateAuthority(MySession session)
    {
        Settings current;
        lock (Gate)
        {
            if (s_current == null)
            {
                Load(session);
                s_appliedOnAuthority = s_current;
                return;
            }
            current = s_current;
        }
        if (current == s_appliedOnAuthority)
            return;
        // Identity cleanup runs here, so the World Authority uses the settings too. An admin's change, or the set the
        // global save restored over the world file's.
        Apply(session, current);
        s_appliedOnAuthority = current;
        ClusterRuntime.RequestGlobalSave();
    }

    // -- regular node -----------------------------------------------------------------------------------------------

    /// <summary>UploadSettingsToServer postfix: vanilla wrote the admin's settings into this node's session.</summary>
    private static void Uploaded()
    {
        if (!ClusterRuntime.IsClustered || ClusterRuntime.IsWorldAuthority)
            return;
        Settings settings = Read(MySession.Static.Settings);
        lock (Gate)
        {
            s_outgoing = settings;
            s_nextFlush = 0;
        }
        Console.WriteLine("[ClusterPlugin] trash settings queued for the World Authority");
    }

    /// <summary>Background: sends the newest upload; the answer is the set right after it.</summary>
    private static void Flush()
    {
        Settings settings;
        lock (Gate)
            settings = s_outgoing;
        if (settings == null)
            return;
        string answer;
        try
        {
            answer = GlobalStateChannel.SendCommand(
                Subsystem,
                JsonSerializer.Serialize(settings),
                CommandTimeoutMilliseconds
            );
        }
        catch (Exception error)
        {
            Console.WriteLine(
                "[ClusterPlugin] trash settings deferred: " + SecretRedactor.Redact(error.Message)
            );
            lock (Gate)
                s_nextFlush = ClusterRuntime.LocalMilliseconds + 5000;
            return;
        }
        lock (Gate)
        {
            Offer(answer);
            // A newer upload that came in meanwhile stays queued.
            if (s_outgoing == settings)
                s_outgoing = null;
        }
    }

    /// <summary>A set from a publish, a snapshot or a command's answer; only the newest is kept.</summary>
    private static void Offer(string payload)
    {
        if (string.IsNullOrEmpty(payload))
            return;
        Set set = JsonSerializer.Deserialize<Set>(payload);
        if (set?.Settings == null)
            throw new InvalidOperationException("Invalid trash settings set");
        lock (Gate)
            if (
                s_newest == null
                || (set.Generation, set.Version).CompareTo((s_newest.Generation, s_newest.Version))
                    > 0
            )
                s_newest = set;
    }

    // -- World Authority --------------------------------------------------------------------------------------------

    private static string Handle(string origin, string payload)
    {
        Settings settings =
            JsonSerializer.Deserialize<Settings>(payload)
            ?? throw new InvalidOperationException("Invalid trash settings");
        bool changed;
        long version;
        lock (Gate)
        {
            if (s_current == null)
            {
                // A command replayed from the journal can come before this side's first frame.
                if (MySession.Static?.Ready != true)
                    throw new InvalidOperationException(
                        "the World Authority has not loaded the trash settings yet"
                    );
                Load(MySession.Static);
            }
            changed = settings != s_current;
            if (changed)
            {
                s_current = settings;
                s_adminSet = true;
                s_version++;
                // Under the lock, so two changes cannot publish their sets in the opposite order.
                GlobalStateChannel.Publish(Subsystem, Snapshot());
            }
            version = s_version;
        }
        if (changed)
            Console.WriteLine(
                "[ClusterPlugin] trash settings from " + origin + " version=" + version
            );
        return Snapshot();
    }

    /// <summary>The world file's settings, until an admin changes them. Under the gate.</summary>
    private static void Load(MySession session)
    {
        s_current = Read(session.Settings);
        s_version++;
        GlobalStateChannel.Publish(Subsystem, Snapshot());
    }

    /// <summary>The global save section: an admin's settings and their version, null while the world file's apply.</summary>
    internal static string PersistedRecord()
    {
        lock (Gate)
            return s_adminSet ? JsonSerializer.Serialize(new Set(0, s_version, s_current)) : null;
    }

    /// <summary>World Authority load: an admin's settings from the global save; without them the world file's apply.</summary>
    internal static void RestoreRecord(string record)
    {
        if (string.IsNullOrEmpty(record))
            return;
        Set set = JsonSerializer.Deserialize<Set>(record);
        if (set?.Settings == null)
            throw new InvalidOperationException("Invalid persisted trash settings record");
        lock (Gate)
        {
            s_current = set.Settings;
            s_version = set.Version;
            s_adminSet = true;
            s_appliedOnAuthority = null;
        }
        Console.WriteLine(
            "[ClusterPlugin] trash settings restored from the global save, version " + set.Version
        );
    }

    private static string Snapshot()
    {
        lock (Gate)
            return s_current == null
                ? null
                : JsonSerializer.Serialize(
                    new Set(ClusterRuntime.AuthorityGeneration, s_version, s_current)
                );
    }

    // -- settings ---------------------------------------------------------------------------------------------------

    internal static Settings Read(MyObjectBuilder_SessionSettings s) =>
        new Settings(
            (int)s.TrashFlags,
            s.TrashRemovalEnabled,
            s.BlockCountThreshold,
            s.PlayerDistanceThreshold,
            s.OptimalGridCount,
            s.PlayerInactivityThreshold,
            s.PlayerCharacterRemovalThreshold,
            s.StopGridsPeriodMin,
            s.RemoveOldIdentitiesH,
            s.VoxelTrashRemovalEnabled,
            s.VoxelPlayerDistanceThreshold,
            s.VoxelGridDistanceThreshold,
            s.VoxelAgeThreshold,
            s.TrashCleanerCargoBagsMaxLiveTime,
            s.MaxCargoBags
        );

    internal static void Write(MyObjectBuilder_SessionSettings s, Settings v)
    {
        s.TrashFlags = (MyTrashRemovalFlags)v.Flags;
        s.TrashRemovalEnabled = v.Enable;
        s.BlockCountThreshold = v.BlockCount;
        s.PlayerDistanceThreshold = v.PlayerDistance;
        s.OptimalGridCount = v.GridCount;
        s.PlayerInactivityThreshold = v.PlayerInactivity;
        s.PlayerCharacterRemovalThreshold = v.CharacterRemovalThreshold;
        s.StopGridsPeriodMin = v.StopGridsPeriod;
        s.RemoveOldIdentitiesH = v.RemoveOldIdentities;
        s.VoxelTrashRemovalEnabled = v.VoxelEnable;
        s.VoxelPlayerDistanceThreshold = v.VoxelDistanceFromPlayer;
        s.VoxelGridDistanceThreshold = v.VoxelDistanceFromGrid;
        s.VoxelAgeThreshold = v.VoxelAge;
        s.TrashCleanerCargoBagsMaxLiveTime = v.CargoBagsMaxLiveTime;
        s.MaxCargoBags = v.MaxCargoBags;
    }

    /// <summary>Writes the settings as vanilla's upload does, with its boulder reset when boulder reverting starts or its age changes.</summary>
    private static void Apply(MySession session, Settings settings)
    {
        Settings before = Read(session.Settings);
        Write(session.Settings, settings);
        if (RevertsBouldersAnew(before, settings))
        {
            MySessionComponentTrash trash = session.GetComponent<MySessionComponentTrash>();
            if (trash != null)
                trash.ClearBoulders = true;
        }
    }

    /// <summary>Vanilla's two ClearBoulders triggers in UploadSettingsToServer. Pure, for the self-test.</summary>
    internal static bool RevertsBouldersAnew(Settings before, Settings after)
    {
        const int revert = (int)MyTrashRemovalFlags.RevertBoulders;
        if ((after.Flags & revert) == 0)
            return false;
        return (before.Flags & revert) == 0 || before.VoxelAge != after.VoxelAge;
    }

    public static object Status()
    {
        lock (Gate)
            return new
            {
                authority = ClusterRuntime.IsWorldAuthority,
                version = ClusterRuntime.IsWorldAuthority ? s_version : s_applied.Version,
                pendingUpload = s_outgoing != null,
                settings = MySession.Static == null ? null : Read(MySession.Static.Settings),
            };
    }
}
