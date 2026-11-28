"""Generates Docs/ModApiPolicy.tsv, the cluster policy of every mod API member (SE-1840).

Usage, from the repository root (see "Member policy table" in Docs/ModCompat.md):

    dotnet run --project Test/ModApiPolicy -c Release -- "$DEDICATED64" --facts DECOMPILED > facts.tsv
    python3 Test/ModApiPolicy/generate.py facts.tsv

The facts come from the game assemblies and their decompiled code; the Workshop counts from
Test/ModApiPolicy/CorpusUsage.tsv. Every decision lives in this file: per-member and per-type rules for the
ModAPI surface first, then signature-specific overrides, then rules for the concrete game members that
Workshop mods call. The output depends only on the inputs, so the same inputs give the same table.
"""

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
HEADER = ("member", "policy", "breakage", "shim", "channel", "on", "mods", "reason")

FACTS = []
for line in open(sys.argv[1], encoding="utf-8"):
    fields = line.rstrip("\n").split("\t")
    if len(fields) != 12:
        sys.exit(f"not a facts line: {line!r}")
    FACTS.append(fields)
used = {}
for line in open(ROOT / "Test/ModApiPolicy/CorpusUsage.tsv", encoding="utf-8"):
    key, count = line.rstrip("\n").split("\t")
    used[key] = int(count)
KIND = {"Interface": "I", "Enum": "E", "Delegate": "D", "Struct": "S", "Class": "C"}
kinds = {}
for key, t, m, kind, tk, assembly, api, static, readonly, decl, impl, faces in FACTS:
    if api == "1":
        kinds[t + "." + m] = KIND[tk]


# ---- policy shorthands: (policy, breakage, shim, op, reason) ----
def P(policy, br="-", shim="-", op="-", why="", on="any"):
    """on: "write" when the breakage needs a write or mutation through the member; a read is the same everywhere."""
    return (policy, br, shim, op, why, on)


OWN = P(
    "local",
    why="acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there",
)
DATA = P("local", why="value, parser or helper; no world state")
DEFS = P(
    "local",
    why="definitions, mods and session settings are the same on every node (cluster join hash)",
)
CLIENT = P(
    "client", why="client UI, input, camera, audio or HUD; nothing to do on a server"
)
PROC = P(
    "local",
    why="process-wide registration or service; every node runs the mod and registers its own",
)
UI = P(
    "local",
    why="terminal UI definitions, process-wide and identical on every node; the UI itself runs on the client",
)
ACCESSOR = P(
    "local",
    why="accessor of a ModAPI service; the policy is on the members of the returned interface",
)

# B-classes per plan: node-scoped B1/B3/B5, per-node B2/B6/B7, affinity B8
SCAN = P(
    "node-scoped",
    "B1",
    "no",
    "broadcast / request",
    "enumerates or queries this node's entities only (MyEntities); entities on other nodes and in offline partitions are missing",
)
BYID = P(
    "node-scoped",
    "B3",
    "no",
    "owner-of / to-owner",
    "resolves the entity on this node only (MyEntities); null for entities on other nodes or concealed",
)
BYNAME = P(
    "node-scoped",
    "B3",
    "no",
    "owner-of / to-owner",
    "looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node",
)
TOCLIENTS = P(
    "node-scoped",
    "B5",
    "yes",
    "player-nodes + to-node",
    "server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node",
)
PLAYERBYID = P(
    "node-scoped",
    "B3",
    "no",
    "player-nodes + to-node",
    "resolves the player through its identity's Character (GetIdentityFromPlayerId); a player attached to another node has no character here",
)
SPAWN = P(
    "affinity",
    "B8",
    "no",
    "to-owner / request",
    "creates the entity on this node whatever the position; a spawn near another node's partition merges over there, and the WA must never spawn (cluster: SpawnDirectorService.cs:15)",
)
SESSIONEVT = P(
    "per-node",
    "B6",
    "no",
    "role (act on WA only)",
    "fires once per node process (each node and the WA run the mod), so a world-wide action in the handler runs once per node",
)
WORLDFILE_W = P(
    "per-node",
    "B7",
    "yes",
    "request (WA)",
    "writes this node's own <world>/Storage/<mod> file; only the WA copy is kept (cluster: ModCompatService.cs:493)",
)
MACHINEFILE_W = P(
    "per-node",
    "B7",
    "no",
    "request (WA)",
    "writes <UserDataPath>/Storage on this machine, as on a single server; each node has its own folder, so a runtime write is not seen on the other nodes. Mostly logs and client settings: counted, but not a reason for class C (Docs/ModCompat.md)",
)
MACHINEFILE_R = P(
    "per-node",
    "B7",
    "no",
    "request (WA)",
    "reads <UserDataPath>/Storage on this machine; files other nodes write at runtime are not there, files shipped or placed by an admin read the same everywhere",
)
MOVE = P(
    "affinity",
    "B3,B4",
    "no",
    "owner-of / to-owner",
    "moves the entity to any position, also into a partition another node hosts; this node places it without seeing that node's entities, then hands it over, so references held here stop resolving and its logic components are recreated there",
)
JUMP = P(
    "affinity",
    "B3,B4",
    "no",
    "owner-of / to-owner",
    "the jump can land in a partition another node hosts; the cluster keeps the arrival off that node's grids (cluster: JumpObstacleService.cs:19, SE-1871) and hands the grid over, so references held here stop resolving and its logic components are recreated there",
)
SESSION_STATE = P(
    "per-node",
    "B7",
    "yes",
    "broadcast",
    "sets a MySession property that the checkpoint saves; every node keeps its own value, only the WA copy is saved, and clients get the value of the node they join",
)
BUS = P(
    "local",
    why="in-process mod bus (m_registeredListeners); every node runs every mod, so the sender and its listener share a process as on a single server",
)
WORLDFILE_R = P(
    "per-node",
    "B7",
    "yes",
    "request (WA)",
    "reads this node's own copy; consistent only while no node writes it at runtime (cluster: ModCompatService.cs:493)",
)
SHIPPED_R = P(
    "local", why="reads files shipped with the mod or the game; identical on every node"
)
REPL_FACTION = P(
    "replicated",
    why="factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)",
)
REPL_GPS = P(
    "replicated",
    why="GPS collection is WA-replicated; requests are executed on the WA (cluster: Patches/MyGpsCollectionPatch.cs:15)",
)
REPL_BANK = P(
    "replicated",
    why="banking is executed on the WA (cluster: Cluster/Patches/MyBankingSystemPatch.cs:15)",
)
REPL_IDENT = P(
    "replicated",
    why="identity and player bindings come from the WA binding view (cluster: Cluster/Patches/MyPlayerCollectionPatch.cs:398)",
)
REPL_WEATHER = P(
    "replicated",
    why="weather is decided on the WA; a node's change is forwarded and published (cluster: WeatherAuthorityService.cs:24)",
)
REPL_CLOCK = P(
    "replicated",
    why="session clock is slewed to the WA clock (cluster: Patches/MySessionPatch.cs:24)",
)
REPL_PROMO = P(
    "replicated",
    why="promote levels are replicated from the WA (cluster: Patches/MySessionPatch.cs:17)",
)
CONTRACT = P(
    "node-scoped",
    "B2",
    "no",
    "request (WA)",
    "UNCERTAIN: contract timers and payouts run on the WA only (cluster: Patches/MySessionComponentContractSystemPatch.cs:16); whether a node's contract add/remove reaches the WA was not verified",
)
VOXEL_EDIT = P(
    "per-node",
    "B7",
    "yes",
    "broadcast",
    "edits this node's voxel storage; the cluster voxel journal hooks only RequestVoxelCutoutSphere (cluster: Patches/MyVoxelBasePatch.cs:18), so other nodes keep the old shape",
)
TIMERS = P(
    "per-node",
    "B2",
    "no",
    "request (WA)",
    "state in a static or session component of this process; every node keeps its own copy",
)
LIFECYCLE = P(
    "per-node",
    "B6",
    "no",
    "role (act on WA only)",
    "acts on this node process only (save, unload, quit)",
)
UNCERTAIN_NODE = lambda why: P(
    "node-scoped", "B2", "no", "request (WA)", "UNCERTAIN: " + why
)

T = {}  # full type name -> rule
M = {}  # full member name -> rule


def rule(types, r):
    for t in types.split():
        T[t] = r


def mem(prefix, names, r, only_existing=False):
    for n in names.split():
        if only_existing and prefix + "." + n not in kinds:
            continue
        M[prefix + "." + n] = r


SM, VGM, VM, SGM = (
    "Sandbox.ModAPI",
    "VRage.Game.ModAPI",
    "VRage.ModAPI",
    "SpaceEngineers.Game.ModAPI",
)

# ---------------- MyAPIGateway ----------------
rule(SM + ".MyAPIGateway", ACCESSOR)
mem(SM + ".MyAPIGateway", "GetMessageBoxPointer GuiControlCreated", CLIENT)
mem(SM + ".MyAPIGateway", "Clean", PROC)

# ---------------- IMyUtilities / MyAPIUtilities ----------------
U = VGM + ".IMyUtilities"
rule(U, DATA)
mem(
    U,
    "ShowNotification CreateNotification ShowMessage ShowMissionScreen MessageEntered MessageEnteredSender MessageRecieved GetObjectiveLine",
    CLIENT,
)
mem(U, "RegisterMessageHandler UnregisterMessageHandler SendModMessage", BUS)
mem(U, "InvokeOnGameThread IsDedicated GamePaths ConfigDedicated", PROC)
mem(
    U,
    "SendMessage",
    P(
        "node-scoped",
        "B5",
        "yes",
        "player-nodes + to-node",
        "UNCERTAIN: MyMultiplayer.SendChatMessage from the server; player chat is accepted on the WA (cluster: Cluster/Patches/ChatAuthorityPatch.cs:58), server-sent chat was not traced",
    ),
)
mem(
    U,
    "GetVariable SetVariable RemoveVariable",
    P(
        "per-node",
        "B7",
        "no",
        "request (WA)",
        "MyAPIUtilities.Variables lives in this node's checkpoint (ScriptManagerData); every node has its own and only the WA copy is saved",
    ),
)
mem(
    U,
    "WriteFileInWorldStorage WriteBinaryFileInWorldStorage DeleteFileInWorldStorage",
    WORLDFILE_W,
)
mem(
    U,
    "ReadFileInWorldStorage ReadBinaryFileInWorldStorage FileExistsInWorldStorage",
    WORLDFILE_R,
)
for tier in ("Local", "Global"):
    mem(
        U,
        f"WriteFileIn{tier}Storage WriteBinaryFileIn{tier}Storage DeleteFileIn{tier}Storage",
        MACHINEFILE_W,
    )
    mem(
        U,
        f"ReadFileIn{tier}Storage ReadBinaryFileIn{tier}Storage FileExistsIn{tier}Storage",
        MACHINEFILE_R,
    )
mem(
    U,
    "ReadFileInModLocation ReadBinaryFileInModLocation FileExistsInModLocation ReadFileInGameContent ReadBinaryFileInGameContent FileExistsInGameContent",
    SHIPPED_R,
)
A = SM + ".MyAPIUtilities"
rule(A, PROC)
mem(
    A,
    "EnterMessage EnterMessageSender MessageEntered MessageEnteredSender MessageRecieved RecieveMessage",
    CLIENT,
)
mem(A, "RegisterMessageHandler UnregisterMessageHandler SendModMessage", BUS)
mem(A, "Variables", M[U + ".SetVariable"])

# ---------------- Multiplayer ----------------
for MP in (VGM + ".IMyMultiplayer", SM + ".MyModAPIHelper.MyMultiplayer"):
    rule(MP, PROC)
    mem(
        MP,
        "IsServer MultiplayerActive ServerId MyId MyName IsServerPlayer",
        P(
            "local",
            why="every node is the server of its own clients; true and correct per process",
        ),
    )
    mem(MP, "JoinServer", CLIENT)
    mem(MP, "Players", ACCESSOR)
    mem(MP, "SendEntitiesCreated", P("local", why="empty method"))
    mem(
        MP,
        "SendMessageToServer",
        P(
            "affinity",
            "B8",
            "no",
            "request (WA) for shared state",
            "raises ModMessageServer* to the node the client is attached to; after a node switch it is another node and another mod instance (cluster delivers it there: Cluster/Patches/ModMessageToServerPatch.cs:58)",
        ),
    )
    mem(
        MP,
        "SendMessageToOthers",
        P(
            "node-scoped",
            "B5",
            "yes",
            "player-nodes + to-node / broadcast",
            "on a server raises ModMessageBroadcast*FromServer ([Broadcast]); reaches only clients attached to this node",
        ),
    )
    mem(
        MP,
        "SendMessageTo",
        P(
            "node-scoped",
            "B5",
            "yes",
            "player-nodes + to-node",
            "raises ModMessageClient* to EndpointId(recipient); undeliverable when the client is attached to another node",
        ),
    )
M[SM + ".MyModAPIHelper.MyMultiplayer.Static"] = ACCESSOR

# ---------------- Entities ----------------
E = VM + ".IMyEntities"
rule(E, OWN)
mem(
    E,
    "GetEntities GetEntitiesInAABB GetEntitiesInSphere GetElementsInBox GetTopMostEntitiesInBox GetTopMostEntitiesInSphere GetIntersectionWithSphere IsSpherePenetrating FindFreePlace IsRaycastBlocked GetInflatedPlayerBoundingBox",
    SCAN,
)
mem(
    E,
    "GetEntityById TryGetEntityById EntityExists Exist GetEntity GetEntityByName TryGetEntityByName IsNameExists",
    BYID,
)
mem(
    E,
    "OnEntityAdd OnEntityRemove OnCloseAll",
    P(
        "node-scoped",
        "B2",
        "no",
        "handover-hooks",
        "raised by this node's MyEntities; a grid arriving or leaving by handover looks like a create or delete, and other nodes' entities never show up",
    ),
)
mem(
    E,
    "AddEntity CreateFromObjectBuilder CreateFromObjectBuilderAndAdd CreateFromObjectBuilderNoinit CreateFromObjectBuilderParallel",
    SPAWN,
)
mem(
    E,
    "IsTypeHidden IsVisible SetTypeHidden UnhideAllTypes EnableEntityBoundingBoxDraw RegisterForDraw UnregisterForDraw",
    CLIENT,
)
mem(
    E,
    "IsWorldLimited WorldHalfExtent WorldSafeHalfExtent IsInsideWorld IsInsideVoxel RemapObjectBuilder RemapObjectBuilderCollection",
    DATA,
)

# ---------------- Players ----------------
PC = VGM + ".IMyPlayerCollection"
rule(PC, OWN)
mem(
    PC,
    "GetPlayers Count",
    P(
        "node-scoped",
        "B1",
        "no",
        "player-nodes",
        "enumerates m_players (MyPlayerCollection.cs:3150), which holds only clients attached to this node; players on other nodes are mirrored to clients only (cluster: RemotePlayerMirrorService.cs:29)",
    ),
)
mem(PC, "GetAllIdentites TryGetIdentityId TryGetSteamId", REPL_IDENT)
mem(PC, "RequestChangeBalance", REPL_BANK)
mem(
    PC,
    "ExtendControl ReduceControl TryExtendControl TryReduceControl RemoveControlledEntity SetControlledEntity",
    P(
        "affinity",
        "B8",
        "no",
        "to-node (player's node)",
        "needs the player and the entity on this node; for a player on another node act there",
    ),
)
mem(PC, "GetPlayerControllingEntity HasExtendedControl ItemConsumed", OWN)
PL = VGM + ".IMyPlayer"
rule(
    PL,
    P(
        "local",
        why="an IMyPlayer is only reachable for players attached to this node, so its character and controller are here",
    ),
)
mem(PL, "RequestChangeBalance TryGetBalanceInfo GetBalanceShortString", REPL_BANK)
mem(PL, "GetRelationTo", REPL_FACTION)
mem(
    PL,
    "Grids AddGrid RemoveGrid",
    UNCERTAIN_NODE(
        "MyPlayer.Grids is a per-process list; not traced whether the cluster keeps it per player across nodes"
    ),
)
rule(VGM + ".IMyIdentity", REPL_IDENT)

# ---------------- Session ----------------
S = VGM + ".IMySession"
rule(S, DEFS)
mem(
    S,
    "Camera CameraController ControlledObject LocalHumanPlayer Player CameraTargetDistance IsCameraAwaitingEntity IsCameraControlledObject IsCameraUserControlledSpectator SetCameraController HasCreativeRights HasAdminPrivileges PromoteLevel Draw",
    CLIENT,
)
mem(S, "IsServer", M[VGM + ".IMyMultiplayer.IsServer"])
mem(
    S,
    "Factions GPS DamageSystem VoxelMaps WeatherEffects OxygenProviderSystem Config GetComponentByInterfaceType TryGetComponentByInterfaceType",
    ACCESSOR,
)
mem(S, "ElapsedPlayTime GameDateTime", REPL_CLOCK)
mem(
    S,
    "GameplayFrameCounter",
    P(
        "node-scoped",
        "B2",
        "no",
        "-",
        "frame counter of this process; differs between nodes, so a stored value is meaningless after a handover",
    ),
)
mem(S, "OnSessionReady OnSessionLoading", SESSIONEVT)
mem(
    S,
    "GetCheckpoint GetWorld GetSector GetVoxelMapsArray",
    P(
        "node-scoped",
        "B1",
        "no",
        "request (WA)",
        "snapshot of this node's session only (partial world)",
    ),
)
mem(
    S,
    "Save Unload UnloadDataComponents UnloadMultiplayer SetAsNotReady GameOver",
    LIFECYCLE,
)
mem(
    S,
    "Update UpdateComponents BeforeStartComponents RegisterComponent UnregisterComponent SetComponentUpdateOrder",
    PROC,
)
mem(S, "IsUserAdmin GetUserPromoteLevel IsUserPromoted", REPL_PROMO)
mem(
    S,
    "IsUserInvulnerable IsUserShowAllPlayers IsUserUseAllTerminals IsUserUntargetable IsUserKeepOriginalOwnershipOnPaste IsUserIgnoreSafeZones IsUserIgnorePCULimit TryGetAdminSettings",
    UNCERTAIN_NODE(
        "admin settings live in MySession.RemoteAdminSettings of this process; set from the admin's client on its node, replication not verified"
    ),
)
mem(S, "ElapsedPlayTime", REPL_CLOCK)

# ---------------- Factions, GPS ----------------
rule(VGM + ".IMyFactionCollection", REPL_FACTION)
mem(
    VGM + ".IMyFactionCollection",
    "FactionCreated FactionEdited FactionStateChanged FactionAutoAcceptChanged ReputationChanged",
    P(
        "per-node",
        "B6",
        "no",
        "role (act on WA only)",
        "raised on every node when the WA state is applied there, so a handler with world-wide side effects runs once per node",
    ),
)
rule(VGM + ".IMyFaction", REPL_FACTION)
mem(
    VGM + ".IMyFaction",
    "RequestChangeBalance TryGetBalanceInfo GetBalanceShortString",
    REPL_BANK,
)
rule(VGM + ".IMyFactionStation", REPL_FACTION)
rule(VGM + ".IMyGpsCollection", REPL_GPS)
mem(VGM + ".IMyGpsCollection", "AddLocalGps RemoveLocalGps", CLIENT)
rule(VGM + ".IMyGps", DATA)

# ---------------- Physics, gravity, voxels, weather ----------------
PH = VGM + ".IMyPhysics"
rule(PH, DATA)
mem(
    PH,
    "CastRay CastLongRay CastRayParallel",
    P(
        "node-scoped",
        "B1",
        "no",
        "to-owner",
        "casts in this node's Havok worlds; complete within sync distance, misses grids on other nodes beyond it",
    ),
)
mem(
    PH,
    "CalculateArtificialGravityAt",
    P(
        "node-scoped",
        "B1",
        "no",
        "-",
        "sums this node's gravity generators; generators are short range, so complete within sync distance",
    ),
)
mem(PH, "CalculateNaturalGravityAt", P("local", why="planets are loaded on every node"))
mem(
    PH,
    "StepsLastSecond SimulationRatio ServerSimulationRatio",
    P("node-scoped", "B2", "no", "-", "this node's simulation speed"),
)
G = VGM + ".IMyGravityProviderSystem"
rule(G, PROC)
mem(
    G,
    "CalculateNaturalGravityInPoint CalculateHighestNaturalGravityMultiplierInPoint DoesTrajectoryIntersectNaturalGravity GetStrongestNaturalGravityWell IsPositionInNaturalGravity",
    P("local", why="planets are loaded on every node"),
)
mem(
    G,
    "CalculateArtificialGravityInPoint CalculateTotalGravityInPoint DoesEntityHaveGravityProvider",
    M[PH + ".CalculateArtificialGravityAt"],
)
rule(VGM + ".IMyOxygenProviderSystem", PROC)
mem(
    VGM + ".IMyOxygenProviderSystem",
    "GetOxygenInPoint",
    P(
        "local",
        why="planet atmospheres are on every node; registered generators are this node's own",
    ),
)
VX = VGM + ".IMyVoxelMaps"
rule(VX, DATA)
mem(
    VX,
    "GetInstances GetOverlappingWithSphere GetVoxelMapWhoseBoundingBoxIntersectsBox Exist",
    SCAN,
)
mem(VX, "CutOutShape FillInShape PaintInShape RevertShape MakeCrater", VOXEL_EDIT)
mem(
    VX,
    "CreateVoxelMap CreateVoxelMapFromStorageName CreatePredefinedVoxelMap CreateProceduralVoxelMap SpawnPlanet",
    SPAWN,
)
mem(VX, "Clear", LIFECYCLE)
W = VGM + ".IMyWeatherEffects"
rule(W, REPL_WEATHER)
mem(
    W,
    "CreateLightning",
    P(
        "local",
        why="lightning runs on the node that hosts the target (cluster: WeatherAuthorityService.cs:7)",
    ),
)
mem(
    W,
    "FogAtmoOverride FogColorOverride FogDensityOverride FogMultiplierOverride FogSkyboxOverride ParticleDirectionOverride ParticleVelocityOverride SunIntensityOverride",
    CLIENT,
)

# ---------------- Other services ----------------
rule(VGM + ".IMyGui", CLIENT)
rule(VGM + ".IMyParallelTask", PROC)
rule(
    VGM + ".IMyDamageSystem",
    P(
        "local",
        why="an interaction happens on exactly one node (proximity rule), so the handler fires once cluster-wide",
    ),
)
rule(VGM + ".IMyPrefabManager", SPAWN)
mem(VGM + ".IMyPrefabManager", "IsPathClear", SCAN)
rule(
    VGM + ".IMyGridGroups",
    P(
        "local",
        why="grid groups are atomic to a partition and never split across nodes",
    ),
)
mem(
    VGM + ".IMyGridGroups",
    "OnGridGroupCreated OnGridGroupDestroyed",
    M[E + ".OnEntityAdd"],
)
rule(
    VGM + ".IMyGridGroupData",
    P(
        "local",
        why="grid groups are atomic to a partition and never split across nodes",
    ),
)
mem(
    VGM + ".IMyGridGroupData",
    "GetVariable SetVariable TryGetVariable RemoveVariable",
    P(
        "per-node",
        "B4",
        "no",
        "handover-hooks",
        "in-memory variables of the group object; the group is rebuilt on the target node after a handover",
    ),
)
rule(VGM + ".IMyContractSystem", CONTRACT)
rule(VGM + ".IMyGamePaths", PROC)
rule(
    VGM + ".IMyDLCs",
    UNCERTAIN_NODE(
        "DLC ownership checks per client; nodes behind the gateway see the client only through Direct Transport (SE-1158), not verified for the mod API"
    ),
)
rule(VGM + ".IMyDLC", DATA)
rule(
    SM + ".IMyProjectiles",
    P(
        "local",
        why="projectile range is capped at sync distance (cluster: Patches/MyProjectilePatch.cs:20)",
    ),
)
rule(
    SM + ".IMyMissiles",
    P(
        "local",
        why="missile range is capped at sync distance (cluster: Patches/MyMissilePatch.cs:22)",
    ),
)
mem(
    SM + ".IMyMissiles",
    "GetAllMissilesInSphere",
    P(
        "node-scoped",
        "B1",
        "no",
        "-",
        "this node's missiles; complete within sync distance",
    ),
)
rule(SM + ".IMyTerminalControls", UI)
rule(SM + ".IMyTerminalActionsHelper", OWN)
rule(VGM + ".IMyReflection", DATA)
rule(VGM + ".ScriptingReflection", DATA)
rule(VGM + ".IMyGeneratedTextures", CLIENT)
rule(VGM + ".IMyGeneratedTexture", CLIENT)
rule(VGM + ".IMyModContext", DEFS)
rule(
    VGM + ".Network.MySync",
    P(
        "local",
        why="entity-bound sync value; set on the entity's node and replicated to that node's clients",
    ),
)
rule(SM + ".IMyStoreBlock", OWN)
rule(VGM + ".IMyStoreItem", DATA)
rule(VGM + ".IMyGridJumpDriveSystem", OWN)
mem(VGM + ".IMyGridJumpDriveSystem", "Jump PerformJump RequestJump", JUMP)
mem(SM + ".IMyJumpDrive", "Jump", JUMP)
mem(VM + ".IMyEntity", "Teleport", MOVE)
rule(
    VGM + ".IMyModelDummy",
    P("local", why="reads model dummy metadata from the loaded model"),
)
rule(SM + ".IMyIngameScripting", PROC)

# ---------------- client-only families ----------------
rule(
    f"{VM}.IMyInput {VM}.IMyCamera {VM}.IMyControl {VGM}.IMyConfig {VGM}.IMyHudNotification {VGM}.IMyHudObjectiveLine {VM}.IMyHudStat "
    f"{SM}.MyModScreen {SM}.MyScreenSpace {SM}.MyScreenSpace.MyScissorScope {SM}.MyScreenSpaceSprite {SM}.MyScreenSpaceText {SM}.MyGuiModHelpers "
    f"{VGM}.IMyCubeBuilder {VGM}.IMySpectatorTools {VGM}.Interfaces.IMyCameraController {VM}.MyAPIGatewayShortcuts "
    f"{VGM}.Interfaces.IMyDecalHandler {VGM}.Interfaces.IMyDecalProxy",
    CLIENT,
)
rule(
    f"{VGM}.IMyToolbar {VGM}.IMyToolbarChangeBuilder {VGM}.IMyToolbarProvider {VGM}.IMyToolbarSlots {VGM}.MyToolbarItemInfo {SM}.MyToolbarApiExtensions",
    CLIENT,
)
rule(
    VGM + ".IMyConfigDedicated",
    P(
        "local",
        why="this node's dedicated server config; the cluster deploys the same settings to every node",
    ),
)

# ---------------- session components and services on blocks ----------------
rule(
    SM + ".IMyNPCGridClaimSessionComponent",
    UNCERTAIN_NODE(
        "NPC grid claims are kept by this node's session component; not traced"
    ),
)
rule(SM + ".IMyNPCSpawnerSessionComponent", SPAWN)
rule(
    SM + ".IMyGlobalEncounterComponent",
    UNCERTAIN_NODE(
        "global encounters are generated on the WA (cluster: Patches/MyGlobalEncountersGeneratorPatch.cs); the component API on a node was not traced"
    ),
)
rule(SM + ".IMyAiRvoSessionComponent", PROC)
rule(SM + ".IMyPathfindingSessionComponent", PROC)
rule(
    SM + ".IMyRespawnComponent",
    UNCERTAIN_NODE(
        "respawn is coordinated cluster-wide (cluster: Patches/MySpaceRespawnComponentPatch.cs); this component's answers on a node were not traced"
    ),
)
rule(
    SM + ".IMyRepairServiceComponent",
    UNCERTAIN_NODE(
        "repair service requests use grids that may be on another node; not traced"
    ),
)
rule(
    SM + ".IMySalvageServiceComponent",
    UNCERTAIN_NODE(
        "salvage service requests use grids that may be on another node; not traced"
    ),
)
rule(f"{SM}.IMyScriptBlacklist {SM}.IMyScriptBlacklistBatch", PROC)
rule(
    SM + ".Ingame.IMyIntergridCommunicationSystem",
    P(
        "replicated",
        why="IGC broadcast and unicast cross nodes through the cluster (cluster: Patches/MyIGCSystemSessionComponentPatch.cs, Cluster/Patches/MyIntergridCommunicationContextPatch.cs:9)",
    ),
)
mem(
    SM + ".Ingame.IMyIntergridCommunicationSystem",
    "IsEndpointReachable",
    UNCERTAIN_NODE("reachability of an endpoint on another node not traced"),
)
rule(SM + ".Ingame.IMyChatBroadcastControllerComponent", OWN)
mem(
    SM + ".Ingame.IMyChatBroadcastControllerComponent",
    "SendMessage SendGps SendRandomMessage",
    P(
        "node-scoped",
        "B5",
        "yes",
        "player-nodes + to-node",
        "UNCERTAIN: MyMultiplayer.RaiseEvent OnTransmitMessage to nearby players; receivers attached to another node were not traced",
    ),
)

# ---------------- entity and block members that leave the entity ----------------
mem(SM + ".IMyRemoteControl", "GetNearestPlayer", SCAN)
mem(SM + ".Ingame.IMyRemoteControl", "GetNearestPlayer", SCAN)
mem(
    SM + ".Ingame.IMyCameraBlock",
    "Raycast",
    P(
        "node-scoped",
        "B1",
        "no",
        "to-owner",
        "casts in this node's Havok world; a camera can scan beyond sync distance and miss grids on other nodes",
    ),
)
for t in (SM + ".IMyLaserAntenna", SM + ".Ingame.IMyLaserAntenna"):
    mem(
        t,
        "Other Connect SetTargetCoords IsInRange TargetCoords",
        P(
            "node-scoped",
            "B3",
            "no",
            "owner-of / to-owner",
            "the partner antenna is usually far away and may be on another node; UNCERTAIN how MyLaserAntennaPatch resolves it",
        ),
        True,
    )
for t in (
    SM + ".Ingame.IMyTransponder",
    SGM + ".Ingame.IMyTransponder",
    SM + ".IMySignalSenderEntityComponent",
):
    mem(
        t,
        "SendSignal",
        UNCERTAIN_NODE("signal receivers in range may be on another node; not traced"),
        True,
    )
mem(VGM + ".IMyCubeGrid", "OnGridSplit OnGridMerge", OWN)
mem(
    VM + ".IMyEntity",
    "GetDistanceBetweenCameraAndBoundingSphere GetDistanceBetweenCameraAndPosition GetLargestDistanceBetweenCameraAndBoundingSphere GetSmallestDistanceBetweenCameraAndBoundingSphere DebugDraw DebugDrawInvalidTriangles NeedsDraw NeedsDrawFromParent LocationForHudMarker",
    CLIENT,
)
mem(
    VGM + ".IMyGunObject",
    "DrawHud UpdateSoundEmitter BeginFailReactionLocal ShootFailReactionLocal",
    CLIENT,
)
mem(
    VGM + ".Interfaces.IMyControllableEntity",
    "DrawHud ShowInventory ShowRemoteAccessMenu",
    CLIENT,
)
mem(
    SM + ".MyProjectileHitInfo",
    "AddDecals AddHitIndicator AddHitParticles AddSZNotification PlayHitSound",
    CLIENT,
)
mem(
    SM + ".Ingame.IMyTextSurface",
    "DrawFrame",
    P(
        "local",
        why="own LCD; the sprites are synced to this node's clients, which are the ones near it",
    ),
)
for t in (VM + ".IMyStorage",):
    mem(t, "WriteRange DeleteRange", VOXEL_EDIT)

# data families by namespace / name
DATA_TYPES = re.compile(
    r"(Utilities\.|\.MyIni|StringSegment|TextPtr|MyCommandLine|MyItemType|MyItemInfo|MyInventoryItem$|MyInventoryItemExtension|MyPhysicalInventoryItemExtensions|MyDetectedEntityInfo|MyShipMass|MyShipVelocities|MyWaypointInfo|MyIGCMessage|MyProductionItem|MyInventoryItemFilter|MyStoreQueryItem|MyStoreItemDataSimple|MyStoreItemData$|TerminalActionParameter|MyProductionQueueItem|MyProjectileInfo|MyProjectileHitInfo|MyHitInfo|IHitInfo|MyDamageInformation|MyAddContractResultWrapper|MyComponentStackInfo|MyLockEntityState|MoveInformation|ModAPIMass|PhysicsSettings|MyTerminalControl(ComboBox|ListBox)Item|IMyTriangleVertexIndices|ModCrashedException|MyGridRetrievalOrder|MyGridStorageItemInfo|IMyVoxelShape|IMyModel$|IMyRemapHelper|MyTerminalInterfaceAttribute|MyVRageIngameScriptingAdapter|MyModAPIHelper$|MyDeviceBase|IVoxelOperator|Contracts\.MyContract|\.IMyContract|MyGridGroupsDefaultEventHandler|MyTerminalControlFactoryHelper|TerminalPropertyExtensions|TerminalBlockExtentions|MyGridProgram$|IMyGridProgram$|IMyGridProgramRuntimeInfo|IMyGridProgramWorldInfo|IUserCustomizableTerminalAction|IMyMessageProvider|IMyBroadcastListener)"
)
UI_TYPES = re.compile(r"(Interfaces\.Terminal\.|Interfaces\.ITerminal)")

# ---------------- VSL ----------------
VSL = "Sandbox.Game.MyVisualScriptLogicProvider"
vsl_rules = [
    (
        r"Local$|^GetLocalPlayerId$|^OpenSteamOverlay$|^TakeScreenshot$|^Is(New)?GameControl|^IsNewKeyPressed$|^GetUserControlKey$|^Get(Game|Music|VoiceChat)Volume$|Analytics$|^UnlockAchievementById$|^SetCampaignLevelOutcome$|^SetMissionOutcome$|^FinishMission$|^AreEssentialGoodbotHintsDone$|^SessionExitGameDialog$|^SessionReloadDialog$|^GetOpened|^GetInteractedEntityInventoryItemIndexAndControl$|^GetToolbarConfigGridItemIndexAndControl$|^GetPlayersInventoryItemIndexAndControl$|^HighlightGuiControl$|^GetControlByName$|^GetTab|^SetCustomLoadingScreen|^SetCustomSkybox$|^ScreenColorFading|^ShowHud$|^SetHudState$|^CreateUIString$|^RemoveUIString$|^SetTooltip$|^SetPage$|^GetQuestlog|^SetDisabledByExperimental$|^SetEnabledByExperimental$|^EnableToolbarConfig$|^ToolbarConfigGroups|^EnableTerminal$|^RequestVicinityCache$",
        CLIENT,
    ),
    (
        r"^(Player(Connected|Disconnected|ConnectRequest|Dropped|RequestsRespawn|RespawnRequest|Spawned)|TeamBalancerPlayerSorted|JoinResult)$",
        P(
            "affinity",
            "B8",
            "no",
            "player-nodes",
            "a player event on the handling process; occurrence during a handover has not been verified",
        ),
    ),
    (r"^(AddGPS|RemoveGPS)", REPL_GPS),
    (r"^(SetGPSHighlight|SetGPSColor)", TOCLIENTS),
    (
        r"^(Show|Add|Remove|Clear)Notification|^SendChatMessage|^Play(Hud)?Sound|^PlaySingleSound|^PlaySoundAmbient|^StopSound$|^Music|^(Start|End|Skip|Next)Cutscene|^Set(Alpha)?Highlight|^EnableHighlight$|^Questlog|^(Add|Remove|Replace|Set)Questlog|^SetAllQuestlog|^Open(Player|Faction)|^DisplayCongratulationScreen$|^(Create|Remove)BoardScreen$|^(Add|Remove)(Column|Row)$|^Set(Cell|ColumnVisibility|RowRanking)$|^Sort|^ShowOrderInColumn$|^SetChat|^SetVolume$|^(Clear|Set|Switch|Reload)Toolbar|^ClearAllToolbarSlots$|^ClearToolbarSlot$|^SetToolbarSlotToItem|^SetToolbarPage$|^SwitchToolbarToSlot$|^ReloadToolbarDefaults$|^SetPlayerInputBlacklistState$|^ToggleAbilityToSprint|^CreateParticleEffect|^CreateSoundEmitter|^RemoveSoundEmitter$",
        TOCLIENTS,
    ),
    (
        r"^(GetOnlinePlayers|GetPlayersCount|GetPlayers|GetAdmins)$",
        P(
            "node-scoped",
            "B1",
            "no",
            "player-nodes",
            "reads MySession.Players.GetOnlinePlayers(), which holds only clients attached to this node",
        ),
    ),
    (
        r"^(Get|Set|Is|Toggle)Player|^(AddTo|RemoveFrom)PlayersInventory$|^SpawnPlayer$|^KickPlayer$|^DisconnectPlayer$|^CockpitInsertPilot$|^CockpitRemovePilot$|^CockpitGetPilotId$|^GetPlayerControlledBlockData$|^GetPlayerRespawnPoints$|^GetOxygenLevelAtPlayersPosition$|^GetSteamId$|^IsPlayerDead$|^PlayerResearch",
        PLAYERBYID,
    ),
    (r"Faction|^SetRelationBetween|^GetRelationBetween", REPL_FACTION),
    (r"^Spawn|^CreateExplosion$|^CreateLocalBlueprint$", SPAWN),
    (r"Contract", CONTRACT),
    (r"^(SetWeather|GetWeather|RemoveWeather)", REPL_WEATHER),
    (r"^Fog", TOCLIENTS),
    (r"^CreateLightning$", M[W + ".CreateLightning"]),
    (
        r"^SunRotation",
        UNCERTAIN_NODE(
            "sun rotation is a session setting of this process; the cluster replicates time of day (cluster: Patches/MySessionPatch.cs:24) but not this setter, not traced"
        ),
    ),
    (
        r"^(SaveSession|SaveSessionAs|SessionClose|StopDedicatedServer|SessionReloadLastCheckpoint|EnableSaving|IsSavingEnabled|IsGameSaving)$",
        LIFECYCLE,
    ),
    (
        r"Timer$|^GetTimers$|^GetGameplayElapsedTime|Match|^ResearchList|^GoToPostMatchState$|^StartStateMachine$|^SetScenario",
        TIMERS,
    ),
    (
        r"^(GetAllEntitiesInRay|GetEntitiesInSphere|FindFreePlace|GetAllCockpits|GetNearestPlanet|IsPlanetNearby)$",
        SCAN,
    ),
    (
        r"^(GetDataFromDefinition|DefinitionIdMatch|GetDefinitionId|GetContentPath|GetModsPath|GetSavesPath|GetCurrentSessionPath|IsCreative|IsSurvival|GetSimulationQuality|IsGameLoaded|GameIsReady|GetPirateId|GetFriendlyName|Init|GetSunDirection|GetTemperatureInPoint|IsOnDarkSide|IsUnderGround|IsFlyable)$",
        DEFS,
    ),
    (
        r"^(GridSpawned|PrefabSpawned|PrefabSpawnedDetailed|RespawnShipSpawned|ItemSpawned)$",
        UNCERTAIN_NODE(
            "raised where the spawn happens; whether a grid imported by handover raises it again on the target node was not traced"
        ),
    ),
    (
        r"^(BlockBuilt|BlockDamaged|BlockDestroyed|BlockFunctionalityChanged|BlockIntegrityChanged|NewItemBuilt|NPCDied|PlayerDied|PlayerEnteredCockpit|PlayerLeftCockpit|PlayerHealthRecharging|PlayerSuitRecharging|PlayerPickedUp|ToolEquipped|LandingGearUnlocked|GridPowerGenerationStateChanged|RoomFullyPressurized|WeaponBlockActivated|ConnectorStateChanged|GridJumped|ShipDrillCollected|RemoteControlChanged|ToolbarItemChanged|TimerBlockTriggered|TimerBlockTriggeredEntityName|ButtonPressedEntityName|ButtonPressedTerminalName|AreaTrigger_.*|ScreenAdded|ScreenRemoved|CutsceneNodeEvent|CutsceneEnded)$",
        P(
            "local",
            why="event raised where it happens; by the proximity rule that is one node, so it fires once cluster-wide",
        ),
    ),
]

mem(
    VSL,
    "SetCustomSkybox SetCustomLoadingScreenImage SetCustomLoadingScreenText",
    SESSION_STATE,
)
mem(
    VSL,
    "ShowHud SetHudState CreateUIString RemoveUIString ScreenColorFadingStart ScreenColorFadingStartSwitch ScreenColorFadingSetColor ScreenColorFadingMinimalizeHUD UnlockAchievementById OpenSteamOverlay",
    TOCLIENTS,
)


def vsl_rule(name):
    for rx, r in vsl_rules:
        if re.search(rx, name):
            return r
    return BYNAME  # entity, block or grid by name/id


# ---------------- type fallback ----------------
CLIENT_NS = re.compile(r"IMyGui$|IMyInput$")


def type_rule(t):
    if t in T:
        return T[t]
    if t == VSL:
        return None
    if DATA_TYPES.search(t):
        return DATA
    if UI_TYPES.search(t):
        return UI
    # entity, block, component, inventory and grid interfaces
    if re.search(r"\.(IMy|MyFarm)", t) and t.split(".")[-1].startswith("IMy"):
        return OWN
    return None


def stage1(t, m, tk):
    """Rule of an API member by type and member name, without location."""
    n = t + "." + m
    if KIND[tk] in "ED":
        return DATA
    if n in M:
        return M[n]
    if t == VSL:
        return vsl_rule(m)
    return type_rule(t)


stale = sorted(m for m in M if m not in kinds)
if stale:
    sys.exit(f"member rules for members the game does not have: {stale}")


# ---------------- signature-specific overrides of API rows ----------------
def node(why, b="B1", shim="no", op="broadcast / request"):
    return P("node-scoped", b, shim, op, why)


def per(why, b="B7", shim="no", op="request (WA)", on="any"):
    return P("per-node", b, shim, op, why, on)


def local(why):
    return P("local", why=why)


def client(why):
    return P("client", why=why)


def api_override(key, t, m, rule):
    if t == VSL and re.fullmatch(r"(Add|Remove)GPS.*ForAll", m):
        return node(
            "enumerates this node’s online players before issuing the per-player GPS request; the WA receives no request for remote players",
            "B1",
            "yes",
            "player-nodes",
        )
    if (
        t.endswith("IMyPlayerCollection")
        and m == "TryGetIdentityId"
        and "(System.Int64)" in key
    ):
        return node(
            "returns TryGetPlayer(identity), which reads the local m_players by player id (MyPlayerCollection.cs:1775)",
            "B3",
            "no",
            "player-nodes + to-node",
        )
    if t.endswith("IMyProjectiles") and m == "GetAllProjectileCount":
        return node(
            "returns this process’s m_projectileCount; projectiles on other nodes are absent"
        )
    if t.endswith("IMyProjectiles") and m in (
        "GetProjectile",
        "MarkProjectileForDestroy",
    ):
        return node(
            "indexes this process’s projectile array; the index belongs to the node that observed it",
            "B3",
            "no",
            "to-node",
        )
    if t.endswith("IMyProjectiles") and m in (
        "OnProjectileAdded",
        "OnProjectileRemoving",
    ):
        return node(
            "raised for projectiles simulated on this node; subscriptions miss events on other nodes",
            "B2",
            "no",
            "broadcast",
        )
    if t == VSL and m in ("PlayerConnected", "PlayerDisconnected"):
        what = (
            "fires on the target when it creates the arriving player"
            if m == "PlayerConnected"
            else "fires on the source when it removes the departing player"
        )
        return per(
            what
            + " during a handover; traversal is visible to the cluster (TraversalAnnouncementPatch.cs:62)",
            "B8",
            "yes",
            "player-nodes",
        )
    if t == VSL and m == "JoinResult":
        return local("returns its enum argument unchanged")
    return rule


# ---------------- concrete game members that Workshop mods call ----------------
def concrete(key, t, m, kind, tk, static, readonly, faces, rows):
    short = t.rsplit(".", 1)[-1]
    if tk in ("Enum", "Delegate"):
        return local(
            "type definition; the policy for an event or callback is on its registration or invocation"
        )
    if t.endswith("MyGamePruningStructure") and m != "Remove":
        return node(
            "queries the local spatial index; entities held on other nodes are absent"
        )
    if t.endswith("MyEntities") and m in (
        "EntityExists",
        "GetEntityById",
        "GetEntityByIdOrDefault",
        "GetEntityByName",
        "TryGetEntityById",
        "TryGetEntityByName",
    ):
        return node(
            "looks up this process’s entity registry; a remote entity cannot be resolved here",
            "B3",
            "no",
            "owner-of / to-owner",
        )
    if t.endswith("MyEntities") and m in (
        "GetEntities",
        "GetEntitiesInAABB",
        "GetEntitiesInSphere",
        "GetIntersectionWithSphere",
        "GetTopMostEntitiesInSphere",
        "IsInsideVoxel",
        "IsRaycastBlocked",
        "FindFreePlace",
        "FindFreePlaceCustom",
    ):
        return node("searches entities or geometry currently loaded on this node")
    if t.endswith("MyEntities") and m in (
        "OnCloseAll",
        "OnEntityAdd",
        "OnEntityCreate",
        "OnEntityRemove",
        "RaiseEntityCreated",
    ):
        return node(
            "entity registry notifications are local, including release and import at handover",
            "B2",
            "no",
            "broadcast",
        )
    if t.endswith("MyEntities") and m in ("Add", "CreateFromObjectBuilderAndAdd"):
        return P(
            "affinity",
            "B8",
            "no",
            "to-owner",
            "adds the entity to the calling node’s simulation",
        )
    interfaces = [face for face in faces if face in rows]
    if interfaces and len({tuple(rows[face][:5]) for face in interfaces}) == 1:
        face = interfaces[0]
        return P(
            *rows[face][:4],
            "implements " + face + "; " + rows[face][-1].partition(" ")[2],
            rows[face][4],
        )
    if m == ".ctor" and (
        short.endswith("ComponentBase") or short == "MyGameLogicComponent"
    ):
        return local(
            "base constructor the compiler calls when the mod constructs its own component on this node"
        )
    if t.endswith("MyGameLogicComponent") and m in ("Close", "Init", "MarkForClose"):
        return per(
            "component lifecycle follows the local entity; release/import can close and initialize it during handover",
            "B4",
            "no",
            "to-owner",
        )
    if t.endswith("MySessionComponentBase") and m in (
        "Init",
        "BeforeStart",
        "LoadData",
        "SaveData",
        "UnloadData",
        "Simulate",
        "UpdateAfterSimulation",
        "UpdateBeforeSimulation",
    ):
        return per(
            "runs in each process that registers the mod session component; world actions can repeat",
            "B6",
            "no",
            "role (act on WA only)",
        )
    if short in (
        "MyLight",
        "MyLights",
        "MyTransparentGeometry",
        "MyBillboard",
        "MyHudBlockInfo",
        "MyRenderComponentBase",
        "MySimpleObjectDraw",
        "MyCubeBuilder",
        "MySpectator",
    ) or t.startswith("Sandbox.Game.GameSystems.TextSurfaceScripts."):
        return client(
            "controls rendering, the local build tool, or screen presentation"
        )
    if short in (
        "MyUtils",
        "MyVoxelCoordSystems",
        "MyValueFormatter",
        "MyStorageData",
        "MyStringId",
        "MyStringHash",
    ):
        return local("computes a value or manipulates caller-supplied data")
    if kind == "Field" and short in (
        "MyDamageType",
        "MyParticleEffectsNameEnum",
        "MyMaterialType",
        "MyFontEnum",
        "MyControlsSpace",
        "MySpaceTexts",
        "MyCommonTexts",
        "CollisionLayers",
    ):
        return local("identifier or constant defined by the game")
    if not static and short in (
        "MyPhysicsComponentBase",
        "MyPhysicsBody",
        "MyPositionComponentBase",
        "MyResourceSinkComponent",
        "MyResourceSourceComponent",
        "MyGunBase",
        "MyCubeBlock",
        "MyGameLogicComponent",
        "MyEntityStat",
        "MyCharacterOxygenComponent",
        "MyCharacterJetpackComponent",
        "MyCharacterStatComponent",
        "MyShipController",
        "MyThrust",
        "MyHierarchyComponentBase",
        "MyResourceSinkComponentBase",
    ):
        return local("acts on the local entity, its components, or its grid group")
    if (
        t
        in (
            "Sandbox.Game.Entities.MyCubeGrid",
            "VRage.Game.Entity.MyEntity",
            "Sandbox.Game.Entities.MyPlanet",
        )
        and not static
        and not re.search(
            "Teleport|IsInVoxel|InsideVoxel|IsUnderGround|MarkForClose|OnClos|OnAddedToScene|OnRemovedFromScene|GetViewMatrix|Camera|MergeGrid|CreateGridGroupLink|PasteBlocks|IsSplit|OnGridSplit|OnGridMerge|OnBlockClosed|WindSpeed|OnClosing|Storage",
            m,
        )
    ):
        return local("operates on this entity or its physical or logical group")
    if short == "MyModStorageComponentBase":
        return local(
            "storage on the host entity; its serialized state follows that entity at handover"
        )
    if t.startswith("VRage.Game.ObjectBuilders.") and kind in ("Field", "Property"):
        return local("field on the serialized object builder")
    if "MyObjectBuilder_SessionSettings" in t and kind in ("Field", "Property"):
        return per(
            "a session setting is read from this process’s settings object; mod writes to that object are not broadcast",
            on="write",
        )
    if "MyObjectBuilder_" in t and (kind in ("Field", "Property") or m == ".ctor"):
        return local(
            "object-builder data carried by this instance; changes take effect only through the code that applies the builder"
        )
    # Calls the compiler adds for a mod's own code (base constructors, conversions, foreach) and the
    # operators they come with act on the values at hand.
    if m == ".ctor" and not static:
        return local(
            "constructs an object in this process; what the mod then does with it has its own row"
        )
    if m.startswith("op_"):
        return local("operator or conversion on the supplied values")
    if m in ("GetEnumerator", "MoveNext", "Current", "Reset") and not static:
        return local("enumerates the collection it is called on")
    if (
        t.startswith("Sandbox.Definitions.")
        or "Definition" in short
        or t.startswith("VRage.Definitions.")
    ):
        if t.endswith("DefinitionManager") and (
            m.startswith(("Get", "TryGet", "Find", "Has", "Is"))
            or m in ("Static", "Characters", "EnvironmentDefinition")
        ):
            return local(
                "reads definitions loaded from the same game and mod content on each node"
            )
        if (
            kind in ("Field", "Property")
            and not static
            and m != "Static"
            and "DefinitionId" not in short
            and "DefinitionId" not in t
            and "MyObjectBuilder" not in t
            and tk == "Class"
        ):
            return per(
                "reads the local loaded definition; changing shared definition data at runtime affects this process",
                on="write",
            )
        if (
            m == ".ctor"
            or "DefinitionId" in t
            or m in ("Id", "SubtypeName", "SubtypeId")
            or readonly
        ):
            return local(
                "definition identifier, constructor, or read-only content value"
            )
        return UNKNOWN(
            "behavior of this definition service on a regular cluster node has not been established"
        )
    if readonly and kind == "Field" and static:
        if (
            short
            in ("MyControlsSpace", "MySpaceTexts", "MyDamageType", "MyCommonTexts")
            or "Constants" in t
            or "Texts" in t
        ):
            return local("static identifier or constant in the game assembly")
        return UNKNOWN("a readonly reference can still hold process-specific state")
    if re.search(
        r"^(ParallelTasks\.|System\.(ArrayExtensions|Collections|Text\.StringBuilderExtensions)|VRage\.(Collections\.|Serialization\.|Library\.Collections|MyFixedPoint|MyTuple|Serializable|MyPositionAndOrientation|MyDiscreteOrientation|MyOrientedBoundingBox|MyTransform|FastResourceLock)|VRage\.Utils\.(MyStringHash|MyStringId)|VRage\.ObjectBuilders\.(Serializable|MyObjectBuilderType))",
        t,
    ):
        return local(
            "operation on supplied data, collection, or synchronization object within this process"
        )
    if short in (
        "MyExplosionInfo",
        "MyHitInfo",
        "MyShipVelocities",
        "MyPlanetAnimalSpawnInfo",
        "MyShipMass",
        "MyAnimationCommand",
        "MyPhysicalInventoryItem",
        "MyInventoryConstraint",
        "MyModContext",
    ):
        return local("describes the supplied value or mod context")
    if (
        t in ("Sandbox.Game.MyInventory", "VRage.Game.Entity.MyInventoryBase")
        and not static
    ):
        if m in (
            "Transfer",
            "TransferItemFrom",
            "PickupItem",
            "TakeFloatingObject",
            "TakeFloatingBag",
        ):
            return P(
                "affinity",
                "B8",
                "no",
                "to-owner",
                "both inventories or the pickup must be on the node performing the transfer",
            )
        return local("operates on the inventory instance of a locally held entity")
    if tk == "Struct" and kind == "Field" and not static:
        return local(
            "field on this value; persistence or replication depends on how its containing value is used"
        )
    if tk == "Struct" and m == ".ctor":
        return local("constructs a value from the supplied arguments")
    return UNKNOWN(
        "the declaration is present in the corpus; its behavior across nodes has not been verified"
    )


def UNKNOWN(why):
    return P("node-scoped", "?", "?", "-", "UNCERTAIN: " + why)


def reason(where, why):
    return (where + " " if where else "") + why


rows = {}
missing = set()
for key, t, m, kind, tk, assembly, api, static, readonly, decl, impl, faces in FACTS:
    if api != "1":
        continue
    rule = stage1(t, m, tk)
    if rule is None:
        missing.add(t)
        continue
    rule = api_override(key, t, m, rule)
    # Enum and delegate types are values; their rows need no location.
    where = "" if key.endswith(".*") else impl or decl
    policy, breakage, shim, op, why, on = rule
    rows[key] = [policy, breakage, shim, op, on, reason(where, why)]
if missing:
    sys.exit("no rule for API types: " + ", ".join(sorted(missing)))
for key, t, m, kind, tk, assembly, api, static, readonly, decl, impl, faces in FACTS:
    if api == "1":
        continue
    rule = concrete(
        key,
        t,
        m,
        kind,
        tk,
        static == "True",
        readonly == "readonly",
        faces.split(),
        rows,
    )
    policy, breakage, shim, op, why, on = rule
    rows[key] = [policy, breakage, shim, op, on, reason(decl, why)]
rows["System.*"] = [
    "local",
    "-",
    "-",
    "-",
    "any",
    "BCL: pure computation and collections; no world state",
]
rows["VRageMath.*"] = ["local", "-", "-", "-", "any", "math types: pure computation"]

unknown = sorted(set(used) - set(rows))
if unknown:
    sys.exit(f"corpus members missing from the facts: {unknown[:20]}")
out = ["\t".join(HEADER)]
for key in sorted(rows):
    policy, breakage, shim, op, on, why = rows[key]
    out.append(
        "\t".join((key, policy, breakage, shim, op, on, str(used.get(key, 0)), why))
    )
(ROOT / "Docs/ModApiPolicy.tsv").write_text("\n".join(out) + "\n", encoding="utf-8")
print(f"rows {len(rows)}", file=sys.stderr)
