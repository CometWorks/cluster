namespace ClusterPlugin;

// SE-1840: cluster policy of every member of the SE1 mod API, one line per member, tab separated:
//   member     full name (namespace, type, member; nested types with a dot, generic types without parameters).
//              "Type.*" covers an enum or delegate type; "System.*" and "VRageMath.*" are the blanket BCL and math rules.
//   policy     local       same result on a cluster: pure, or reads/writes only its own entity or grid group
//              replicated  correct because the World Authority replicates it (factions, GPS, identities, banking, weather)
//              node-scoped works, but sees or reaches only this node (entity scans, lookups by id, messages to clients)
//              per-node    runs or stores once per node instead of once per world (session events, variables, files)
//              affinity    tied to the node the player or entity is on now (client-to-server messages, control, spawns)
//              client      only meaningful on a game client; nothing happens on a server
//   breakage   B1..B8 of clustering-plan/Plan/Modding.md section 4.3, or "-"
//   shim       yes: a cluster-side patch could fix it without a mod change; no: it needs a mod change; "-": nothing to fix
//   channel    the mod channel operation (Docs/ModChannel.md) that replaces it in an adapted mod, or "-"
//   mods       how many of the 250 locally installed scripted Workshop mods use it (2026-11-22 scan); 0 = not seen
//   reason     file:line in the decompiled DS 1.211.018 code (implementation, or the declaration where the
//              implementation is inherited), then why; "cluster:" points into this repository. "UNCERTAIN:" marks a
//              row the code did not settle; those keep node-scoped or a stricter policy.
//
// Test/ModApiPolicy checks that every public member of the game's ModAPI namespaces (Sandbox.ModAPI, VRage.ModAPI,
// VRage.Game.ModAPI, SpaceEngineers.Game.ModAPI and their sub-namespaces) and of MyVisualScriptLogicProvider has a
// row, and that no row names a member the game no longer has. Code, not an embedded file, like the PB policy table
// of the Performance plugin (ScriptPolicyTable.cs), so a loader that compiles the plugin from source keeps it.
internal static class ModApiPolicyTable
{
    public const string Text =
        @"Sandbox.Game.MyVisualScriptLogicProvider.AddAcquisitionContract	node-scoped	B2	no	request (WA)	0	MyVisualScriptLogicProvider.cs:8136 UNCERTAIN: contract timers and payouts run on the WA only (cluster: Patches/MySessionComponentContractSystemPatch.cs:16); whether a node's contract add/remove reaches the WA was not verified
Sandbox.Game.MyVisualScriptLogicProvider.AddBountyContract	node-scoped	B2	no	request (WA)	0	MyVisualScriptLogicProvider.cs:8196 UNCERTAIN: contract timers and payouts run on the WA only (cluster: Patches/MySessionComponentContractSystemPatch.cs:16); whether a node's contract add/remove reaches the WA was not verified
Sandbox.Game.MyVisualScriptLogicProvider.AddColumn	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:8786 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.AddEscortContract	node-scoped	B2	no	request (WA)	0	MyVisualScriptLogicProvider.cs:8156 UNCERTAIN: contract timers and payouts run on the WA only (cluster: Patches/MySessionComponentContractSystemPatch.cs:16); whether a node's contract add/remove reaches the WA was not verified
Sandbox.Game.MyVisualScriptLogicProvider.AddGPS	replicated	-	-	-	4	MyVisualScriptLogicProvider.cs:4759 GPS collection is WA-replicated; requests are executed on the WA (cluster: Patches/MyGpsCollectionPatch.cs:15)
Sandbox.Game.MyVisualScriptLogicProvider.AddGPSForAll	replicated	-	-	-	1	MyVisualScriptLogicProvider.cs:4791 GPS collection is WA-replicated; requests are executed on the WA (cluster: Patches/MyGpsCollectionPatch.cs:15)
Sandbox.Game.MyVisualScriptLogicProvider.AddGPSObjective	replicated	-	-	-	2	MyVisualScriptLogicProvider.cs:4913 GPS collection is WA-replicated; requests are executed on the WA (cluster: Patches/MyGpsCollectionPatch.cs:15)
Sandbox.Game.MyVisualScriptLogicProvider.AddGPSObjectiveForAll	replicated	-	-	-	1	MyVisualScriptLogicProvider.cs:4946 GPS collection is WA-replicated; requests are executed on the WA (cluster: Patches/MyGpsCollectionPatch.cs:15)
Sandbox.Game.MyVisualScriptLogicProvider.AddGPSObjectiveToEntity	replicated	-	-	-	0	MyVisualScriptLogicProvider.cs:4961 GPS collection is WA-replicated; requests are executed on the WA (cluster: Patches/MyGpsCollectionPatch.cs:15)
Sandbox.Game.MyVisualScriptLogicProvider.AddGPSObjectiveToEntityForAll	replicated	-	-	-	0	MyVisualScriptLogicProvider.cs:4989 GPS collection is WA-replicated; requests are executed on the WA (cluster: Patches/MyGpsCollectionPatch.cs:15)
Sandbox.Game.MyVisualScriptLogicProvider.AddGPSToEntity	replicated	-	-	-	2	MyVisualScriptLogicProvider.cs:4836 GPS collection is WA-replicated; requests are executed on the WA (cluster: Patches/MyGpsCollectionPatch.cs:15)
Sandbox.Game.MyVisualScriptLogicProvider.AddGPSToEntityForAll	replicated	-	-	-	0	MyVisualScriptLogicProvider.cs:4864 GPS collection is WA-replicated; requests are executed on the WA (cluster: Patches/MyGpsCollectionPatch.cs:15)
Sandbox.Game.MyVisualScriptLogicProvider.AddGridToTargetList	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:1540 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.AddHaulingContract	node-scoped	B2	no	request (WA)	0	MyVisualScriptLogicProvider.cs:8116 UNCERTAIN: contract timers and payouts run on the WA only (cluster: Patches/MySessionComponentContractSystemPatch.cs:16); whether a node's contract add/remove reaches the WA was not verified
Sandbox.Game.MyVisualScriptLogicProvider.AddMatchStateRemainingDuration	per-node	B2	no	request (WA)	0	MyVisualScriptLogicProvider.cs:9061 state in a static or session component of this process; every node keeps its own copy
Sandbox.Game.MyVisualScriptLogicProvider.AddNotification	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:6361 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.AddNotificationLocal	client	-	-	-	0	MyVisualScriptLogicProvider.cs:6394 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.AddQuestlogDetail	node-scoped	B5	yes	player-nodes + to-node	2	MyVisualScriptLogicProvider.cs:7216 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.AddQuestlogDetailLocal	client	-	-	-	1	MyVisualScriptLogicProvider.cs:7245 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.AddQuestlogObjective	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:7252 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.AddQuestlogObjectiveLocal	client	-	-	-	0	MyVisualScriptLogicProvider.cs:7281 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.AddRepairContract	node-scoped	B2	no	request (WA)	0	MyVisualScriptLogicProvider.cs:8216 UNCERTAIN: contract timers and payouts run on the WA only (cluster: Patches/MySessionComponentContractSystemPatch.cs:16); whether a node's contract add/remove reaches the WA was not verified
Sandbox.Game.MyVisualScriptLogicProvider.AddRow	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:8834 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.AddSearchContract	node-scoped	B2	no	request (WA)	0	MyVisualScriptLogicProvider.cs:8176 UNCERTAIN: contract timers and payouts run on the WA only (cluster: Patches/MySessionComponentContractSystemPatch.cs:16); whether a node's contract add/remove reaches the WA was not verified
Sandbox.Game.MyVisualScriptLogicProvider.AddToInventory	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:3378 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.AddToInventoryFloat	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:3396 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.AddToPlayersInventory	node-scoped	B3	no	player-nodes + to-node	1	MyVisualScriptLogicProvider.cs:7012 resolves the player through its identity's Character (GetIdentityFromPlayerId); a player attached to another node has no character here
Sandbox.Game.MyVisualScriptLogicProvider.AdvanceMatchState	per-node	B2	no	request (WA)	0	MyVisualScriptLogicProvider.cs:9013 state in a static or session component of this process; every node keeps its own copy
Sandbox.Game.MyVisualScriptLogicProvider.AreEssentialGoodbotHintsDone	client	-	-	-	0	MyVisualScriptLogicProvider.cs:7478 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.AreFactionsEnemies	replicated	-	-	-	0	MyVisualScriptLogicProvider.cs:4089 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
Sandbox.Game.MyVisualScriptLogicProvider.AreaTrigger_Entered	local	-	-	-	2	MyVisualScriptLogicProvider.cs:864 event raised where it happens; by the proximity rule that is one node, so it fires once cluster-wide
Sandbox.Game.MyVisualScriptLogicProvider.AreaTrigger_EntityEntered	local	-	-	-	1	MyVisualScriptLogicProvider.cs:858 event raised where it happens; by the proximity rule that is one node, so it fires once cluster-wide
Sandbox.Game.MyVisualScriptLogicProvider.AreaTrigger_EntityLeft	local	-	-	-	1	MyVisualScriptLogicProvider.cs:855 event raised where it happens; by the proximity rule that is one node, so it fires once cluster-wide
Sandbox.Game.MyVisualScriptLogicProvider.AreaTrigger_Left	local	-	-	-	2	MyVisualScriptLogicProvider.cs:861 event raised where it happens; by the proximity rule that is one node, so it fires once cluster-wide
Sandbox.Game.MyVisualScriptLogicProvider.AutopilotActivate	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:1627 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.AutopilotAddWaypoint	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:1594 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.AutopilotClearWaypoints	node-scoped	B3	no	owner-of / to-owner	1	MyVisualScriptLogicProvider.cs:1587 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.AutopilotEnabled	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:1620 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.AutopilotGetCurrentWaypoint	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:1259 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.AutopilotGoToPosition	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:1570 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.AutopilotSetWaypoints	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:1601 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.AutopilotSkipCurrentWaypoint	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:1277 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.BlockBuilt	local	-	-	-	1	MyVisualScriptLogicProvider.cs:882 event raised where it happens; by the proximity rule that is one node, so it fires once cluster-wide
Sandbox.Game.MyVisualScriptLogicProvider.BlockDamaged	local	-	-	-	1	MyVisualScriptLogicProvider.cs:879 event raised where it happens; by the proximity rule that is one node, so it fires once cluster-wide
Sandbox.Game.MyVisualScriptLogicProvider.BlockDestroyed	local	-	-	-	1	MyVisualScriptLogicProvider.cs:873 event raised where it happens; by the proximity rule that is one node, so it fires once cluster-wide
Sandbox.Game.MyVisualScriptLogicProvider.BlockFunctionalityChanged	local	-	-	-	2	MyVisualScriptLogicProvider.cs:894 event raised where it happens; by the proximity rule that is one node, so it fires once cluster-wide
Sandbox.Game.MyVisualScriptLogicProvider.BlockIntegrityChanged	local	-	-	-	1	MyVisualScriptLogicProvider.cs:876 event raised where it happens; by the proximity rule that is one node, so it fires once cluster-wide
Sandbox.Game.MyVisualScriptLogicProvider.ButtonPressedEntityName	local	-	-	-	1	MyVisualScriptLogicProvider.cs:849 event raised where it happens; by the proximity rule that is one node, so it fires once cluster-wide
Sandbox.Game.MyVisualScriptLogicProvider.ButtonPressedTerminalName	local	-	-	-	2	MyVisualScriptLogicProvider.cs:852 event raised where it happens; by the proximity rule that is one node, so it fires once cluster-wide
Sandbox.Game.MyVisualScriptLogicProvider.CancelStoreItem	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:8081 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.ChangeDoorState	node-scoped	B3	no	owner-of / to-owner	1	MyVisualScriptLogicProvider.cs:2559 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.ChangeOwner	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:3506 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.ClearAllToolbarSlots	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:7858 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.ClearAllToolbarSlotsLocal	client	-	-	-	0	MyVisualScriptLogicProvider.cs:7885 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.ClearNotificationLocal	client	-	-	-	0	MyVisualScriptLogicProvider.cs:6466 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.ClearNotifications	node-scoped	B5	yes	player-nodes + to-node	1	MyVisualScriptLogicProvider.cs:6442 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.ClearToolbarSlot	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:7834 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.ClearToolbarSlotLocal	client	-	-	-	1	MyVisualScriptLogicProvider.cs:7851 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.CockpitGetPilotId	node-scoped	B3	no	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:2697 resolves the player through its identity's Character (GetIdentityFromPlayerId); a player attached to another node has no character here
Sandbox.Game.MyVisualScriptLogicProvider.CockpitInsertPilot	node-scoped	B3	no	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:2677 resolves the player through its identity's Character (GetIdentityFromPlayerId); a player attached to another node has no character here
Sandbox.Game.MyVisualScriptLogicProvider.CockpitRemovePilot	node-scoped	B3	no	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:2667 resolves the player through its identity's Character (GetIdentityFromPlayerId); a player attached to another node has no character here
Sandbox.Game.MyVisualScriptLogicProvider.ColorAllGridBlocks	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:5044 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.ColorAndSkinBlock	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:2376 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.ColorBlock	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:2353 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.ConnectorStateChanged	local	-	-	-	1	MyVisualScriptLogicProvider.cs:915 event raised where it happens; by the proximity rule that is one node, so it fires once cluster-wide
Sandbox.Game.MyVisualScriptLogicProvider.ContractAbandoned	node-scoped	B2	no	request (WA)	2	MyVisualScriptLogicProvider.cs:948 UNCERTAIN: contract timers and payouts run on the WA only (cluster: Patches/MySessionComponentContractSystemPatch.cs:16); whether a node's contract add/remove reaches the WA was not verified
Sandbox.Game.MyVisualScriptLogicProvider.ContractAccepted	node-scoped	B2	no	request (WA)	2	MyVisualScriptLogicProvider.cs:939 UNCERTAIN: contract timers and payouts run on the WA only (cluster: Patches/MySessionComponentContractSystemPatch.cs:16); whether a node's contract add/remove reaches the WA was not verified
Sandbox.Game.MyVisualScriptLogicProvider.ContractFailed	node-scoped	B2	no	request (WA)	2	MyVisualScriptLogicProvider.cs:945 UNCERTAIN: contract timers and payouts run on the WA only (cluster: Patches/MySessionComponentContractSystemPatch.cs:16); whether a node's contract add/remove reaches the WA was not verified
Sandbox.Game.MyVisualScriptLogicProvider.ContractFinished	node-scoped	B2	no	request (WA)	2	MyVisualScriptLogicProvider.cs:942 UNCERTAIN: contract timers and payouts run on the WA only (cluster: Patches/MySessionComponentContractSystemPatch.cs:16); whether a node's contract add/remove reaches the WA was not verified
Sandbox.Game.MyVisualScriptLogicProvider.CreateAreaTriggerOnEntity	node-scoped	B3	no	owner-of / to-owner	1	MyVisualScriptLogicProvider.cs:7940 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.CreateAreaTriggerOnPosition	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:7974 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.CreateAreaTriggerRelativeToEntity	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:7957 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.CreateBoardScreen	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:8746 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.CreateExplosion	affinity	B8	no	to-owner / request	2	MyVisualScriptLogicProvider.cs:2899 creates the entity on this node whatever the position; a spawn near another node's partition merges over there, and the WA must never spawn (cluster: SpawnDirectorService.cs:15)
Sandbox.Game.MyVisualScriptLogicProvider.CreateFaction	replicated	-	-	-	0	MyVisualScriptLogicProvider.cs:4082 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
Sandbox.Game.MyVisualScriptLogicProvider.CreateLightning	local	-	-	-	0	MyVisualScriptLogicProvider.cs:3929 lightning runs on the node that hosts the target (cluster: WeatherAuthorityService.cs:7)
Sandbox.Game.MyVisualScriptLogicProvider.CreateLocalBlueprint	affinity	B8	no	to-owner / request	0	MyVisualScriptLogicProvider.cs:5459 creates the entity on this node whatever the position; a spawn near another node's partition merges over there, and the WA must never spawn (cluster: SpawnDirectorService.cs:15)
Sandbox.Game.MyVisualScriptLogicProvider.CreateParticleEffectAtEntity	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:2960 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.CreateParticleEffectAtPosition	node-scoped	B5	yes	player-nodes + to-node	3	MyVisualScriptLogicProvider.cs:2944 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.CreateSoundEmitterAtEntity	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:1877 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.CreateSoundEmitterAtEntityLocal	client	-	-	-	0	MyVisualScriptLogicProvider.cs:1898 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.CreateSoundEmitterAtPosition	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:1905 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.CreateSoundEmitterAtPositionLocal	client	-	-	-	0	MyVisualScriptLogicProvider.cs:1922 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.CreateUIString	client	-	-	-	1	MyVisualScriptLogicProvider.cs:8698 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.CustomRespawnRequest	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:4462 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.CutsceneEnded	local	-	-	-	1	MyVisualScriptLogicProvider.cs:798 event raised where it happens; by the proximity rule that is one node, so it fires once cluster-wide
Sandbox.Game.MyVisualScriptLogicProvider.CutsceneNodeEvent	local	-	-	-	1	MyVisualScriptLogicProvider.cs:795 event raised where it happens; by the proximity rule that is one node, so it fires once cluster-wide
Sandbox.Game.MyVisualScriptLogicProvider.DamageBlock	node-scoped	B3	no	owner-of / to-owner	1	MyVisualScriptLogicProvider.cs:2307 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.DefinitionIdMatch	per-node	B2	no	request (WA)	0	MyVisualScriptLogicProvider.cs:8070 state in a static or session component of this process; every node keeps its own copy
Sandbox.Game.MyVisualScriptLogicProvider.DisableBlock	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:2110 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.DisconnectPlayer	node-scoped	B3	no	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:4420 resolves the player through its identity's Character (GetIdentityFromPlayerId); a player attached to another node has no character here
Sandbox.Game.MyVisualScriptLogicProvider.DisplayCongratulationScreen	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:6473 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.DroneGetCurrentAIBehavior	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:1312 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.DroneGetSpeedLimit	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:1358 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.DroneGetTargetsCount	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:1288 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.DroneGetWaypointCount	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:1247 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.DroneHasAI	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:1300 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.DroneIsInAmbushMode	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:1370 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.DroneIsOperational	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:1382 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.DroneProcessTargets	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:1150 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.DroneSetAmbushMode	node-scoped	B3	no	owner-of / to-owner	1	MyVisualScriptLogicProvider.cs:1335 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.DroneSetCollisionAvoidance	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:1506 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.DroneSetPlayerPriority	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:1420 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.DroneSetPrioritizationStyle	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:1431 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.DroneSetRetreatPosition	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:1518 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.DroneSetRotateToTarget	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:1529 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.DroneSetSpeedLimit	node-scoped	B3	no	owner-of / to-owner	1	MyVisualScriptLogicProvider.cs:1346 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.DroneSetTarget	node-scoped	B3	no	owner-of / to-owner	1	MyVisualScriptLogicProvider.cs:1324 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.DroneTargetAdd	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:1453 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.DroneTargetClear	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:1473 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.DroneTargetLoseCurrent	node-scoped	B3	no	owner-of / to-owner	1	MyVisualScriptLogicProvider.cs:1495 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.DroneTargetRemove	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:1484 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.DroneWaypointAdd	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:1398 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.DroneWaypointClear	node-scoped	B3	no	owner-of / to-owner	1	MyVisualScriptLogicProvider.cs:1442 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.DroneWaypointSetCycling	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:1409 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.EnableBlock	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:2103 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.EnableCustomRespawn	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:4440 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.EnableHighlight	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:7453 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.EnableHighlightLocal	client	-	-	-	0	MyVisualScriptLogicProvider.cs:7471 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.EnableSaving	per-node	B6	no	role (act on WA only)	0	MyVisualScriptLogicProvider.cs:4216 acts on this node process only (save, unload, quit)
Sandbox.Game.MyVisualScriptLogicProvider.EnableSavingLocal	client	-	-	-	0	MyVisualScriptLogicProvider.cs:4230 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.EnableTerminal	client	-	-	-	0	MyVisualScriptLogicProvider.cs:4185 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.EnableToolbarConfig	client	-	-	-	0	MyVisualScriptLogicProvider.cs:5689 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.EndCutscene	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:2824 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.EndCutsceneLocal	client	-	-	-	0	MyVisualScriptLogicProvider.cs:2842 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.EntityExists	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:3753 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.FindFreePlace	node-scoped	B1	no	broadcast / request	0	MyVisualScriptLogicProvider.cs:3799 enumerates or queries this node's entities only (MyEntities); entities on other nodes and in offline partitions are missing
Sandbox.Game.MyVisualScriptLogicProvider.FinishMission	client	-	-	-	0	MyVisualScriptLogicProvider.cs:4390 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.FogSetAll	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:3857 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.FogSetAtmoMultiplier	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:3913 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.FogSetColor	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:3895 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.FogSetDensity	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:3877 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.FogSetMultiplier	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:3886 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.FogSetSkyboxMultiplier	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:3904 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.GameIsReady	local	-	-	-	0	MyVisualScriptLogicProvider.cs:4378 definitions, mods and session settings are the same on every node (cluster join hash)
Sandbox.Game.MyVisualScriptLogicProvider.GetAdmins	node-scoped	B1	no	player-nodes	0	MyVisualScriptLogicProvider.cs:6594 reads MySession.Players.GetOnlinePlayers(), which holds only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.GetAllCockpits	node-scoped	B1	no	broadcast / request	0	MyVisualScriptLogicProvider.cs:2710 enumerates or queries this node's entities only (MyEntities); entities on other nodes and in offline partitions are missing
Sandbox.Game.MyVisualScriptLogicProvider.GetAllEntitiesInRay	node-scoped	B1	no	broadcast / request	0	MyVisualScriptLogicProvider.cs:3367 enumerates or queries this node's entities only (MyEntities); entities on other nodes and in offline partitions are missing
Sandbox.Game.MyVisualScriptLogicProvider.GetBlockAttachedUpgradeModules	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:2317 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.GetBlockHealth	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:2275 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.GetColor	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:6231 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.GetContentPath	local	-	-	-	0	MyVisualScriptLogicProvider.cs:6176 definitions, mods and session settings are the same on every node (cluster join hash)
Sandbox.Game.MyVisualScriptLogicProvider.GetControlByName	client	-	-	-	0	MyVisualScriptLogicProvider.cs:5963 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.GetCountOfSpecificGridBlocks	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:5082 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.GetCurrentSessionPath	local	-	-	-	0	MyVisualScriptLogicProvider.cs:4369 definitions, mods and session settings are the same on every node (cluster join hash)
Sandbox.Game.MyVisualScriptLogicProvider.GetDampenersEnabled	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:3781 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.GetDataFromDefinition	local	-	-	-	0	MyVisualScriptLogicProvider.cs:3636 definitions, mods and session settings are the same on every node (cluster join hash)
Sandbox.Game.MyVisualScriptLogicProvider.GetDefinitionId	local	-	-	-	5	MyVisualScriptLogicProvider.cs:3624 definitions, mods and session settings are the same on every node (cluster join hash)
Sandbox.Game.MyVisualScriptLogicProvider.GetEntitiesInSphere	node-scoped	B1	no	broadcast / request	1	MyVisualScriptLogicProvider.cs:3357 enumerates or queries this node's entities only (MyEntities); entities on other nodes and in offline partitions are missing
Sandbox.Game.MyVisualScriptLogicProvider.GetEntityById	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:3245 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.GetEntityByName	node-scoped	B3	no	owner-of / to-owner	1	MyVisualScriptLogicProvider.cs:3226 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.GetEntityDirection	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:3311 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.GetEntityIdFromEntity	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:3267 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.GetEntityIdFromName	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:3256 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.GetEntityInventoryItemAmount	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:3442 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.GetEntityInventoryItemAmountPrecise	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:3449 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.GetEntityInventoryItemAtSlot	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:3476 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.GetEntityName	node-scoped	B3	no	owner-of / to-owner	1	MyVisualScriptLogicProvider.cs:3601 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.GetEntityPosition	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:3274 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.GetEntitySpeed	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:3612 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.GetEntityVectors	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:3288 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.GetEntityWorldMatrix	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:3281 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.GetFactionMembers	replicated	-	-	-	0	MyVisualScriptLogicProvider.cs:4054 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
Sandbox.Game.MyVisualScriptLogicProvider.GetFriendlyName	local	-	-	-	0	MyVisualScriptLogicProvider.cs:6091 definitions, mods and session settings are the same on every node (cluster join hash)
Sandbox.Game.MyVisualScriptLogicProvider.GetGameVolume	client	-	-	-	0	MyVisualScriptLogicProvider.cs:2070 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.GetGameplayElapsedTime	per-node	B2	no	request (WA)	0	MyVisualScriptLogicProvider.cs:8676 state in a static or session component of this process; every node keeps its own copy
Sandbox.Game.MyVisualScriptLogicProvider.GetGameplayElapsedTimeToString	per-node	B2	no	request (WA)	0	MyVisualScriptLogicProvider.cs:8691 state in a static or session component of this process; every node keeps its own copy
Sandbox.Game.MyVisualScriptLogicProvider.GetGridCockpitId	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:5056 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.GetGridIdOfBlock	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:2264 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.GetGridStatistics	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:5023 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.GetIdListOfSpecificGridBlocks	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:5004 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.GetIdOfFirstSpecificGridBlock	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:5106 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.GetInteractedEntityInventoryItemIndexAndControl	client	-	-	-	0	MyVisualScriptLogicProvider.cs:5859 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.GetLandingGearInformation	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:2460 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.GetLandingGearInformationFromEntity	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:2487 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.GetLocalPlayerId	client	-	-	-	0	MyVisualScriptLogicProvider.cs:3992 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.GetMatchIsRunningState	per-node	B2	no	request (WA)	0	MyVisualScriptLogicProvider.cs:9075 state in a static or session component of this process; every node keeps its own copy
Sandbox.Game.MyVisualScriptLogicProvider.GetMatchState	per-node	B2	no	request (WA)	0	MyVisualScriptLogicProvider.cs:9001 state in a static or session component of this process; every node keeps its own copy
Sandbox.Game.MyVisualScriptLogicProvider.GetMatchStateRemainingDuration	per-node	B2	no	request (WA)	0	MyVisualScriptLogicProvider.cs:9026 state in a static or session component of this process; every node keeps its own copy
Sandbox.Game.MyVisualScriptLogicProvider.GetMergeBlockStatus	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:2388 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.GetModsPath	local	-	-	-	0	MyVisualScriptLogicProvider.cs:6190 definitions, mods and session settings are the same on every node (cluster join hash)
Sandbox.Game.MyVisualScriptLogicProvider.GetMusicVolume	client	-	-	-	0	MyVisualScriptLogicProvider.cs:2081 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.GetNearestPlanet	node-scoped	B1	no	broadcast / request	1	MyVisualScriptLogicProvider.cs:3342 enumerates or queries this node's entities only (MyEntities); entities on other nodes and in offline partitions are missing
Sandbox.Game.MyVisualScriptLogicProvider.GetNumberOfGridBlocks	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:5188 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.GetOnlinePlayers	node-scoped	B1	no	player-nodes	0	MyVisualScriptLogicProvider.cs:6551 reads MySession.Players.GetOnlinePlayers(), which holds only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.GetOpenedTerminal	client	-	-	-	0	MyVisualScriptLogicProvider.cs:6052 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.GetOpenedToolbarConfig	client	-	-	-	0	MyVisualScriptLogicProvider.cs:6077 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.GetOwner	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:3553 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.GetOxygenLevelAtPlayersPosition	node-scoped	B3	no	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:6613 resolves the player through its identity's Character (GetIdentityFromPlayerId); a player attached to another node has no character here
Sandbox.Game.MyVisualScriptLogicProvider.GetPirateId	local	-	-	-	1	MyVisualScriptLogicProvider.cs:3999 definitions, mods and session settings are the same on every node (cluster join hash)
Sandbox.Game.MyVisualScriptLogicProvider.GetPlayerControlledBlockData	node-scoped	B3	no	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:6676 resolves the player through its identity's Character (GetIdentityFromPlayerId); a player attached to another node has no character here
Sandbox.Game.MyVisualScriptLogicProvider.GetPlayerRespawnPoints	node-scoped	B3	no	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:7151 resolves the player through its identity's Character (GetIdentityFromPlayerId); a player attached to another node has no character here
Sandbox.Game.MyVisualScriptLogicProvider.GetPlayers	node-scoped	B1	no	player-nodes	0	MyVisualScriptLogicProvider.cs:6574 reads MySession.Players.GetOnlinePlayers(), which holds only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.GetPlayersCount	node-scoped	B1	no	player-nodes	0	MyVisualScriptLogicProvider.cs:6567 reads MySession.Players.GetOnlinePlayers(), which holds only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.GetPlayersEnergyLevel	node-scoped	B3	no	player-nodes + to-node	3	MyVisualScriptLogicProvider.cs:6897 resolves the player through its identity's Character (GetIdentityFromPlayerId); a player attached to another node has no character here
Sandbox.Game.MyVisualScriptLogicProvider.GetPlayersEntityId	node-scoped	B3	no	player-nodes + to-node	1	MyVisualScriptLogicProvider.cs:6701 resolves the player through its identity's Character (GetIdentityFromPlayerId); a player attached to another node has no character here
Sandbox.Game.MyVisualScriptLogicProvider.GetPlayersEntityName	node-scoped	B3	no	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:6708 resolves the player through its identity's Character (GetIdentityFromPlayerId); a player attached to another node has no character here
Sandbox.Game.MyVisualScriptLogicProvider.GetPlayersFactionName	node-scoped	B3	no	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:4021 resolves the player through its identity's Character (GetIdentityFromPlayerId); a player attached to another node has no character here
Sandbox.Game.MyVisualScriptLogicProvider.GetPlayersFactionTag	node-scoped	B3	no	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:4006 resolves the player through its identity's Character (GetIdentityFromPlayerId); a player attached to another node has no character here
Sandbox.Game.MyVisualScriptLogicProvider.GetPlayersHealth	node-scoped	B3	no	player-nodes + to-node	2	MyVisualScriptLogicProvider.cs:6864 resolves the player through its identity's Character (GetIdentityFromPlayerId); a player attached to another node has no character here
Sandbox.Game.MyVisualScriptLogicProvider.GetPlayersHelmetStatus	node-scoped	B3	no	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:6628 resolves the player through its identity's Character (GetIdentityFromPlayerId); a player attached to another node has no character here
Sandbox.Game.MyVisualScriptLogicProvider.GetPlayersHydrogenLevel	node-scoped	B3	no	player-nodes + to-node	2	MyVisualScriptLogicProvider.cs:6890 resolves the player through its identity's Character (GetIdentityFromPlayerId); a player attached to another node has no character here
Sandbox.Game.MyVisualScriptLogicProvider.GetPlayersInventoryItemAmount	node-scoped	B3	no	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:6993 resolves the player through its identity's Character (GetIdentityFromPlayerId); a player attached to another node has no character here
Sandbox.Game.MyVisualScriptLogicProvider.GetPlayersInventoryItemAmountPrecise	node-scoped	B3	no	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:7000 resolves the player through its identity's Character (GetIdentityFromPlayerId); a player attached to another node has no character here
Sandbox.Game.MyVisualScriptLogicProvider.GetPlayersInventoryItemIndexAndControl	client	-	-	-	0	MyVisualScriptLogicProvider.cs:5837 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.GetPlayersName	node-scoped	B3	no	player-nodes + to-node	1	MyVisualScriptLogicProvider.cs:6852 resolves the player through its identity's Character (GetIdentityFromPlayerId); a player attached to another node has no character here
Sandbox.Game.MyVisualScriptLogicProvider.GetPlayersOxygenLevel	node-scoped	B3	no	player-nodes + to-node	2	MyVisualScriptLogicProvider.cs:6883 resolves the player through its identity's Character (GetIdentityFromPlayerId); a player attached to another node has no character here
Sandbox.Game.MyVisualScriptLogicProvider.GetPlayersPosition	node-scoped	B3	no	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:6981 resolves the player through its identity's Character (GetIdentityFromPlayerId); a player attached to another node has no character here
Sandbox.Game.MyVisualScriptLogicProvider.GetPlayersSpeed	node-scoped	B3	no	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:6720 resolves the player through its identity's Character (GetIdentityFromPlayerId); a player attached to another node has no character here
Sandbox.Game.MyVisualScriptLogicProvider.GetQuestlogMaxPages	client	-	-	-	0	MyVisualScriptLogicProvider.cs:7410 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.GetQuestlogPage	client	-	-	-	0	MyVisualScriptLogicProvider.cs:7403 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.GetQuestlogPageFromMessage	client	-	-	-	0	MyVisualScriptLogicProvider.cs:7446 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.GetRelationBetweenFactions	replicated	-	-	-	0	MyVisualScriptLogicProvider.cs:4106 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
Sandbox.Game.MyVisualScriptLogicProvider.GetRelationBetweenPlayerAndFaction	replicated	-	-	-	1	MyVisualScriptLogicProvider.cs:4119 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
Sandbox.Game.MyVisualScriptLogicProvider.GetSavesPath	local	-	-	-	0	MyVisualScriptLogicProvider.cs:6183 definitions, mods and session settings are the same on every node (cluster join hash)
Sandbox.Game.MyVisualScriptLogicProvider.GetSimulationQuality	local	-	-	-	0	MyVisualScriptLogicProvider.cs:4172 definitions, mods and session settings are the same on every node (cluster join hash)
Sandbox.Game.MyVisualScriptLogicProvider.GetSteamId	node-scoped	B3	no	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:4409 resolves the player through its identity's Character (GetIdentityFromPlayerId); a player attached to another node has no character here
Sandbox.Game.MyVisualScriptLogicProvider.GetSunDirection	local	-	-	-	16	MyVisualScriptLogicProvider.cs:3843 definitions, mods and session settings are the same on every node (cluster join hash)
Sandbox.Game.MyVisualScriptLogicProvider.GetTab	client	-	-	-	0	MyVisualScriptLogicProvider.cs:6059 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.GetTabs	client	-	-	-	0	MyVisualScriptLogicProvider.cs:6066 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.GetTemperatureInPoint	local	-	-	-	3	MyVisualScriptLogicProvider.cs:3922 definitions, mods and session settings are the same on every node (cluster join hash)
Sandbox.Game.MyVisualScriptLogicProvider.GetTimers	per-node	B2	no	request (WA)	0	MyVisualScriptLogicProvider.cs:1030 state in a static or session component of this process; every node keeps its own copy
Sandbox.Game.MyVisualScriptLogicProvider.GetToolbarConfigGridItemIndexAndControl	client	-	-	-	0	MyVisualScriptLogicProvider.cs:5808 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.GetUserControlKey	client	-	-	-	0	MyVisualScriptLogicProvider.cs:6218 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.GetVoiceChatVolume	client	-	-	-	0	MyVisualScriptLogicProvider.cs:2092 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.GetWeather	replicated	-	-	-	3	MyVisualScriptLogicProvider.cs:3950 weather is decided on the WA; a node's change is forwarded and published (cluster: WeatherAuthorityService.cs:24)
Sandbox.Game.MyVisualScriptLogicProvider.GetWeatherIntensity	replicated	-	-	-	0	MyVisualScriptLogicProvider.cs:3957 weather is decided on the WA; a node's change is forwarded and published (cluster: WeatherAuthorityService.cs:24)
Sandbox.Game.MyVisualScriptLogicProvider.GoToPostMatchState	per-node	B2	no	request (WA)	0	MyVisualScriptLogicProvider.cs:9040 state in a static or session component of this process; every node keeps its own copy
Sandbox.Game.MyVisualScriptLogicProvider.GridJumped	local	-	-	-	2	MyVisualScriptLogicProvider.cs:918 event raised where it happens; by the proximity rule that is one node, so it fires once cluster-wide
Sandbox.Game.MyVisualScriptLogicProvider.GridPowerGenerationStateChanged	local	-	-	-	1	MyVisualScriptLogicProvider.cs:903 event raised where it happens; by the proximity rule that is one node, so it fires once cluster-wide
Sandbox.Game.MyVisualScriptLogicProvider.GridSpawned	node-scoped	B2	no	request (WA)	1	MyVisualScriptLogicProvider.cs:891 UNCERTAIN: raised where the spawn happens; whether a grid imported by handover raises it again on the target node was not traced
Sandbox.Game.MyVisualScriptLogicProvider.HasOperationalCockpit	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:5330 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.HasOperationalGyro	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:5304 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.HasPower	node-scoped	B3	no	owner-of / to-owner	1	MyVisualScriptLogicProvider.cs:5256 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.HasThrusterInAllDirections	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:5225 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.HasWorkingRemote	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:5356 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.HighlightGuiControl	client	-	-	-	0	MyVisualScriptLogicProvider.cs:5918 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.Init	local	-	-	-	0	MyVisualScriptLogicProvider.cs:969 definitions, mods and session settings are the same on every node (cluster join hash)
Sandbox.Game.MyVisualScriptLogicProvider.InsertOffer	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:8092 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.InsertOrder	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:8104 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.IsBlockEnabled	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:2232 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.IsBlockFunctional	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:2195 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.IsBlockFunctionalById	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:2206 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.IsBlockPositionAirtight	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:2755 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.IsBlockPowered	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:2217 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.IsBlockWorking	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:2243 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.IsConnectorLocked	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:2513 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.IsConveyorConnected	node-scoped	B3	no	owner-of / to-owner	1	MyVisualScriptLogicProvider.cs:2117 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.IsCreative	local	-	-	-	1	MyVisualScriptLogicProvider.cs:4158 definitions, mods and session settings are the same on every node (cluster join hash)
Sandbox.Game.MyVisualScriptLogicProvider.IsDoorOpen	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:2601 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.IsFlyable	local	-	-	-	0	MyVisualScriptLogicProvider.cs:5378 definitions, mods and session settings are the same on every node (cluster join hash)
Sandbox.Game.MyVisualScriptLogicProvider.IsGameControlPressed	client	-	-	-	0	MyVisualScriptLogicProvider.cs:4499 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.IsGameControlReleased	client	-	-	-	0	MyVisualScriptLogicProvider.cs:4521 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.IsGameLoaded	local	-	-	-	0	MyVisualScriptLogicProvider.cs:4376 definitions, mods and session settings are the same on every node (cluster join hash)
Sandbox.Game.MyVisualScriptLogicProvider.IsGameSaving	per-node	B6	no	role (act on WA only)	0	MyVisualScriptLogicProvider.cs:4243 acts on this node process only (save, unload, quit)
Sandbox.Game.MyVisualScriptLogicProvider.IsGridDestructible	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:5531 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.IsGridEditable	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:5573 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.IsGridImmune	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:5552 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.IsGridLockedWithLandingGear	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:5134 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.IsLandingGearLocked	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:2444 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.IsNewGameControlPressed	client	-	-	-	0	MyVisualScriptLogicProvider.cs:4488 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.IsNewGameControlReleased	client	-	-	-	0	MyVisualScriptLogicProvider.cs:4510 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.IsNewKeyPressed	client	-	-	-	0	MyVisualScriptLogicProvider.cs:6084 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.IsOnDarkSide	local	-	-	-	4	MyVisualScriptLogicProvider.cs:3850 definitions, mods and session settings are the same on every node (cluster join hash)
Sandbox.Game.MyVisualScriptLogicProvider.IsPlanetNearby	node-scoped	B1	no	broadcast / request	0	MyVisualScriptLogicProvider.cs:3331 enumerates or queries this node's entities only (MyEntities); entities on other nodes and in offline partitions are missing
Sandbox.Game.MyVisualScriptLogicProvider.IsPlayerDead	node-scoped	B3	no	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:6845 resolves the player through its identity's Character (GetIdentityFromPlayerId); a player attached to another node has no character here
Sandbox.Game.MyVisualScriptLogicProvider.IsPlayerInCockpit	node-scoped	B3	no	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:6770 resolves the player through its identity's Character (GetIdentityFromPlayerId); a player attached to another node has no character here
Sandbox.Game.MyVisualScriptLogicProvider.IsPlayerInRemote	node-scoped	B3	no	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:6795 resolves the player through its identity's Character (GetIdentityFromPlayerId); a player attached to another node has no character here
Sandbox.Game.MyVisualScriptLogicProvider.IsPlayerInWeapon	node-scoped	B3	no	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:6820 resolves the player through its identity's Character (GetIdentityFromPlayerId); a player attached to another node has no character here
Sandbox.Game.MyVisualScriptLogicProvider.IsPlayersJetpackEnabled	node-scoped	B3	no	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:6871 resolves the player through its identity's Character (GetIdentityFromPlayerId); a player attached to another node has no character here
Sandbox.Game.MyVisualScriptLogicProvider.IsSavingEnabled	per-node	B6	no	role (act on WA only)	0	MyVisualScriptLogicProvider.cs:4236 acts on this node process only (save, unload, quit)
Sandbox.Game.MyVisualScriptLogicProvider.IsSurvival	local	-	-	-	0	MyVisualScriptLogicProvider.cs:4165 definitions, mods and session settings are the same on every node (cluster join hash)
Sandbox.Game.MyVisualScriptLogicProvider.IsUnderGround	local	-	-	-	0	MyVisualScriptLogicProvider.cs:3985 definitions, mods and session settings are the same on every node (cluster join hash)
Sandbox.Game.MyVisualScriptLogicProvider.ItemSpawned	node-scoped	B2	no	request (WA)	1	MyVisualScriptLogicProvider.cs:846 UNCERTAIN: raised where the spawn happens; whether a grid imported by handover raises it again on the target node was not traced
Sandbox.Game.MyVisualScriptLogicProvider.JoinResult	affinity	B8	no	player-nodes	0	MyVisualScriptLogicProvider.cs:8994 raised on the node the client is attached to; a node switch raises it on the new node again
Sandbox.Game.MyVisualScriptLogicProvider.KickPlayer	node-scoped	B3	no	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:4430 resolves the player through its identity's Character (GetIdentityFromPlayerId); a player attached to another node has no character here
Sandbox.Game.MyVisualScriptLogicProvider.KickPlayerFromFaction	replicated	-	-	-	0	MyVisualScriptLogicProvider.cs:4071 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
Sandbox.Game.MyVisualScriptLogicProvider.LandingGearUnlocked	local	-	-	-	1	MyVisualScriptLogicProvider.cs:900 event raised where it happens; by the proximity rule that is one node, so it fires once cluster-wide
Sandbox.Game.MyVisualScriptLogicProvider.LoadEntityBool	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:8351 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.LoadEntityBoolList	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:8461 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.LoadEntityFloat	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:8404 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.LoadEntityFloatList	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:8521 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.LoadEntityInteger	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:8370 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.LoadEntityIntegerList	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:8481 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.LoadEntityLong	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:8389 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.LoadEntityLongList	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:8501 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.LoadEntityString	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:8332 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.LoadEntityStringList	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:8442 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.LoadEntityVector	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:8423 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.LoadEntityVectorList	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:8541 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.MatchStateChanged	per-node	B2	no	request (WA)	1	MyVisualScriptLogicProvider.cs:936 state in a static or session component of this process; every node keeps its own copy
Sandbox.Game.MyVisualScriptLogicProvider.MatchStateEnded	per-node	B2	no	request (WA)	1	MyVisualScriptLogicProvider.cs:933 state in a static or session component of this process; every node keeps its own copy
Sandbox.Game.MyVisualScriptLogicProvider.MatchStateEnding	per-node	B2	no	request (WA)	1	MyVisualScriptLogicProvider.cs:951 state in a static or session component of this process; every node keeps its own copy
Sandbox.Game.MyVisualScriptLogicProvider.MatchStateStarted	per-node	B2	no	request (WA)	1	MyVisualScriptLogicProvider.cs:930 state in a static or session component of this process; every node keeps its own copy
Sandbox.Game.MyVisualScriptLogicProvider.MusicPlayMusicCategory	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:1681 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.MusicPlayMusicCategoryLocal	client	-	-	-	0	MyVisualScriptLogicProvider.cs:1709 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.MusicPlayMusicCue	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:1642 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.MusicPlayMusicCueLocal	client	-	-	-	0	MyVisualScriptLogicProvider.cs:1674 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.MusicSetDynamicMusic	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:1750 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.MusicSetDynamicMusicLocal	client	-	-	-	0	MyVisualScriptLogicProvider.cs:1773 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.MusicSetMusicCategory	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:1716 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.MusicSetMusicCategoryLocal	client	-	-	-	0	MyVisualScriptLogicProvider.cs:1743 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.NPCDied	local	-	-	-	1	MyVisualScriptLogicProvider.cs:825 event raised where it happens; by the proximity rule that is one node, so it fires once cluster-wide
Sandbox.Game.MyVisualScriptLogicProvider.NewItemBuilt	local	-	-	-	1	MyVisualScriptLogicProvider.cs:909 event raised where it happens; by the proximity rule that is one node, so it fires once cluster-wide
Sandbox.Game.MyVisualScriptLogicProvider.NextCutsceneNode	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:2799 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.NextCutsceneNodeLocal	client	-	-	-	0	MyVisualScriptLogicProvider.cs:2817 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.OpenFactionVictoryScreenAll	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:6146 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.OpenFactionVictoryScreenAllBroadcast	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:6155 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.OpenPlayerScreenAll	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:6127 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.OpenPlayerScreenAllBroadcast	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:6136 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.OpenSteamOverlay	client	-	-	-	0	MyVisualScriptLogicProvider.cs:5887 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.OpenSteamOverlayLocal	client	-	-	-	3	MyVisualScriptLogicProvider.cs:5911 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.PlayHudSound	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:1819 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.PlayHudSoundLocal	client	-	-	-	7	MyVisualScriptLogicProvider.cs:1836 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.PlaySingleSoundAtEntity	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:1780 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.PlaySingleSoundAtEntityLocal	client	-	-	-	0	MyVisualScriptLogicProvider.cs:1812 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.PlaySingleSoundAtPosition	node-scoped	B5	yes	player-nodes + to-node	4	MyVisualScriptLogicProvider.cs:1843 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.PlaySingleSoundAtPositionLocal	client	-	-	-	1	MyVisualScriptLogicProvider.cs:1870 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.PlaySound	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:1929 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.PlaySoundAmbient	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:2005 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.PlaySoundAmbientLocal	client	-	-	-	0	MyVisualScriptLogicProvider.cs:2028 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.PlaySoundLocal	client	-	-	-	0	MyVisualScriptLogicProvider.cs:1950 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.PlayerConnectRequest	affinity	B8	no	player-nodes	1	MyVisualScriptLogicProvider.cs:819 raised on the node the client is attached to; a node switch raises it on the new node again
Sandbox.Game.MyVisualScriptLogicProvider.PlayerConnected	affinity	B8	no	player-nodes	10	MyVisualScriptLogicProvider.cs:813 raised on the node the client is attached to; a node switch raises it on the new node again
Sandbox.Game.MyVisualScriptLogicProvider.PlayerDied	local	-	-	-	3	MyVisualScriptLogicProvider.cs:810 event raised where it happens; by the proximity rule that is one node, so it fires once cluster-wide
Sandbox.Game.MyVisualScriptLogicProvider.PlayerDisconnected	affinity	B8	no	player-nodes	13	MyVisualScriptLogicProvider.cs:816 raised on the node the client is attached to; a node switch raises it on the new node again
Sandbox.Game.MyVisualScriptLogicProvider.PlayerDropped	affinity	B8	no	player-nodes	1	MyVisualScriptLogicProvider.cs:843 raised on the node the client is attached to; a node switch raises it on the new node again
Sandbox.Game.MyVisualScriptLogicProvider.PlayerEnteredCockpit	local	-	-	-	4	MyVisualScriptLogicProvider.cs:789 event raised where it happens; by the proximity rule that is one node, so it fires once cluster-wide
Sandbox.Game.MyVisualScriptLogicProvider.PlayerHealthRecharging	local	-	-	-	1	MyVisualScriptLogicProvider.cs:828 event raised where it happens; by the proximity rule that is one node, so it fires once cluster-wide
Sandbox.Game.MyVisualScriptLogicProvider.PlayerLeftCockpit	local	-	-	-	5	MyVisualScriptLogicProvider.cs:786 event raised where it happens; by the proximity rule that is one node, so it fires once cluster-wide
Sandbox.Game.MyVisualScriptLogicProvider.PlayerPickedUp	local	-	-	-	1	MyVisualScriptLogicProvider.cs:840 event raised where it happens; by the proximity rule that is one node, so it fires once cluster-wide
Sandbox.Game.MyVisualScriptLogicProvider.PlayerRequestsRespawn	affinity	B8	no	player-nodes	1	MyVisualScriptLogicProvider.cs:807 raised on the node the client is attached to; a node switch raises it on the new node again
Sandbox.Game.MyVisualScriptLogicProvider.PlayerResearchClear	node-scoped	B3	no	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:5764 resolves the player through its identity's Character (GetIdentityFromPlayerId); a player attached to another node has no character here
Sandbox.Game.MyVisualScriptLogicProvider.PlayerResearchClearAll	node-scoped	B3	no	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:5754 resolves the player through its identity's Character (GetIdentityFromPlayerId); a player attached to another node has no character here
Sandbox.Game.MyVisualScriptLogicProvider.PlayerResearchLock	node-scoped	B3	no	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:5798 resolves the player through its identity's Character (GetIdentityFromPlayerId); a player attached to another node has no character here
Sandbox.Game.MyVisualScriptLogicProvider.PlayerResearchUnlock	node-scoped	B3	no	player-nodes + to-node	1	MyVisualScriptLogicProvider.cs:5788 resolves the player through its identity's Character (GetIdentityFromPlayerId); a player attached to another node has no character here
Sandbox.Game.MyVisualScriptLogicProvider.PlayerRespawnRequest	affinity	B8	no	player-nodes	6	MyVisualScriptLogicProvider.cs:822 raised on the node the client is attached to; a node switch raises it on the new node again
Sandbox.Game.MyVisualScriptLogicProvider.PlayerSpawned	affinity	B8	no	player-nodes	6	MyVisualScriptLogicProvider.cs:801 raised on the node the client is attached to; a node switch raises it on the new node again
Sandbox.Game.MyVisualScriptLogicProvider.PlayerSuitRecharging	local	-	-	-	1	MyVisualScriptLogicProvider.cs:831 event raised where it happens; by the proximity rule that is one node, so it fires once cluster-wide
Sandbox.Game.MyVisualScriptLogicProvider.PrefabSpawned	node-scoped	B2	no	request (WA)	2	MyVisualScriptLogicProvider.cs:885 UNCERTAIN: raised where the spawn happens; whether a grid imported by handover raises it again on the target node was not traced
Sandbox.Game.MyVisualScriptLogicProvider.PrefabSpawnedDetailed	node-scoped	B2	no	request (WA)	5	MyVisualScriptLogicProvider.cs:888 UNCERTAIN: raised where the spawn happens; whether a grid imported by handover raises it again on the target node was not traced
Sandbox.Game.MyVisualScriptLogicProvider.ReloadToolbarDefaults	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:7916 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.ReloadToolbarDefaultsLocal	client	-	-	-	0	MyVisualScriptLogicProvider.cs:7933 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.RemoteControlChanged	local	-	-	-	2	MyVisualScriptLogicProvider.cs:924 event raised where it happens; by the proximity rule that is one node, so it fires once cluster-wide
Sandbox.Game.MyVisualScriptLogicProvider.RemoveAllTriggersFromEntity	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:7993 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.RemoveBoardScreen	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:8766 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.RemoveColumn	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:8814 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.RemoveContract	node-scoped	B2	no	request (WA)	0	MyVisualScriptLogicProvider.cs:8236 UNCERTAIN: contract timers and payouts run on the WA only (cluster: Patches/MySessionComponentContractSystemPatch.cs:16); whether a node's contract add/remove reaches the WA was not verified
Sandbox.Game.MyVisualScriptLogicProvider.RemoveEntities	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:3723 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.RemoveEntity	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:3644 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.RemoveFromEntityInventory	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:3414 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.RemoveFromPlayersInventory	node-scoped	B3	no	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:7030 resolves the player through its identity's Character (GetIdentityFromPlayerId); a player attached to another node has no character here
Sandbox.Game.MyVisualScriptLogicProvider.RemoveGPS	replicated	-	-	-	3	MyVisualScriptLogicProvider.cs:4806 GPS collection is WA-replicated; requests are executed on the WA (cluster: Patches/MyGpsCollectionPatch.cs:15)
Sandbox.Game.MyVisualScriptLogicProvider.RemoveGPSForAll	replicated	-	-	-	2	MyVisualScriptLogicProvider.cs:4821 GPS collection is WA-replicated; requests are executed on the WA (cluster: Patches/MyGpsCollectionPatch.cs:15)
Sandbox.Game.MyVisualScriptLogicProvider.RemoveGPSFromEntity	replicated	-	-	-	2	MyVisualScriptLogicProvider.cs:4879 GPS collection is WA-replicated; requests are executed on the WA (cluster: Patches/MyGpsCollectionPatch.cs:15)
Sandbox.Game.MyVisualScriptLogicProvider.RemoveGPSFromEntityForAll	replicated	-	-	-	0	MyVisualScriptLogicProvider.cs:4898 GPS collection is WA-replicated; requests are executed on the WA (cluster: Patches/MyGpsCollectionPatch.cs:15)
Sandbox.Game.MyVisualScriptLogicProvider.RemoveGridFromTargetList	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:1550 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.RemoveNotification	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:6412 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.RemoveNotificationLocal	client	-	-	-	0	MyVisualScriptLogicProvider.cs:6435 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.RemoveQuestlogDetails	node-scoped	B5	yes	player-nodes + to-node	2	MyVisualScriptLogicProvider.cs:7371 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.RemoveQuestlogDetailsLocal	client	-	-	-	1	MyVisualScriptLogicProvider.cs:7389 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.RemoveRow	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:8854 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.RemoveSoundEmitter	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:1981 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.RemoveSoundEmitterLocal	client	-	-	-	0	MyVisualScriptLogicProvider.cs:1998 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.RemoveTrigger	node-scoped	B3	no	owner-of / to-owner	1	MyVisualScriptLogicProvider.cs:8003 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.RemoveUIString	client	-	-	-	1	MyVisualScriptLogicProvider.cs:8726 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.RemoveWeather	replicated	-	-	-	0	MyVisualScriptLogicProvider.cs:3964 weather is decided on the WA; a node's change is forwarded and published (cluster: WeatherAuthorityService.cs:24)
Sandbox.Game.MyVisualScriptLogicProvider.RenameEntity	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:3575 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.ReplaceQuestlogDetail	node-scoped	B5	yes	player-nodes + to-node	2	MyVisualScriptLogicProvider.cs:7346 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.ReplaceQuestlogDetailLocal	client	-	-	-	1	MyVisualScriptLogicProvider.cs:7364 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.ReportMatchEndToAnalytics	client	-	-	-	0	MyVisualScriptLogicProvider.cs:9047 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.RequestVicinityCache	client	-	-	-	0	MyVisualScriptLogicProvider.cs:2874 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.RequestVicinityCacheLocal	client	-	-	-	0	MyVisualScriptLogicProvider.cs:2892 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.ResearchListAddItem	per-node	B2	no	request (WA)	0	MyVisualScriptLogicProvider.cs:5724 state in a static or session component of this process; every node keeps its own copy
Sandbox.Game.MyVisualScriptLogicProvider.ResearchListClear	per-node	B2	no	request (WA)	0	MyVisualScriptLogicProvider.cs:5744 state in a static or session component of this process; every node keeps its own copy
Sandbox.Game.MyVisualScriptLogicProvider.ResearchListRemoveItem	per-node	B2	no	request (WA)	0	MyVisualScriptLogicProvider.cs:5734 state in a static or session component of this process; every node keeps its own copy
Sandbox.Game.MyVisualScriptLogicProvider.ResearchListWhitelist	per-node	B2	no	request (WA)	0	MyVisualScriptLogicProvider.cs:5778 state in a static or session component of this process; every node keeps its own copy
Sandbox.Game.MyVisualScriptLogicProvider.RespawnShipSpawned	node-scoped	B2	no	request (WA)	6	MyVisualScriptLogicProvider.cs:792 UNCERTAIN: raised where the spawn happens; whether a grid imported by handover raises it again on the target node was not traced
Sandbox.Game.MyVisualScriptLogicProvider.RoomFullyPressurized	local	-	-	-	1	MyVisualScriptLogicProvider.cs:906 event raised where it happens; by the proximity rule that is one node, so it fires once cluster-wide
Sandbox.Game.MyVisualScriptLogicProvider.SaveSession	per-node	B6	no	role (act on WA only)	0	MyVisualScriptLogicProvider.cs:4192 acts on this node process only (save, unload, quit)
Sandbox.Game.MyVisualScriptLogicProvider.SaveSessionAs	per-node	B6	no	role (act on WA only)	0	MyVisualScriptLogicProvider.cs:4204 acts on this node process only (save, unload, quit)
Sandbox.Game.MyVisualScriptLogicProvider.ScreenAdded	local	-	-	-	1	MyVisualScriptLogicProvider.cs:867 event raised where it happens; by the proximity rule that is one node, so it fires once cluster-wide
Sandbox.Game.MyVisualScriptLogicProvider.ScreenColorFadingMinimalizeHUD	client	-	-	-	1	MyVisualScriptLogicProvider.cs:3103 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.ScreenColorFadingSetColor	client	-	-	-	0	MyVisualScriptLogicProvider.cs:3021 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.ScreenColorFadingStart	client	-	-	-	1	MyVisualScriptLogicProvider.cs:2980 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.ScreenColorFadingStartSwitch	client	-	-	-	1	MyVisualScriptLogicProvider.cs:3062 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.ScreenRemoved	local	-	-	-	2	MyVisualScriptLogicProvider.cs:870 event raised where it happens; by the proximity rule that is one node, so it fires once cluster-wide
Sandbox.Game.MyVisualScriptLogicProvider.SendChatMessage	node-scoped	B5	yes	player-nodes + to-node	12	MyVisualScriptLogicProvider.cs:6280 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.SendChatMessageColored	node-scoped	B5	yes	player-nodes + to-node	16	MyVisualScriptLogicProvider.cs:6300 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.SessionClose	per-node	B6	no	role (act on WA only)	0	MyVisualScriptLogicProvider.cs:4267 acts on this node process only (save, unload, quit)
Sandbox.Game.MyVisualScriptLogicProvider.SessionExitGameDialog	client	-	-	-	0	MyVisualScriptLogicProvider.cs:4343 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.SessionReloadDialog	client	-	-	-	0	MyVisualScriptLogicProvider.cs:4250 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.SessionReloadLastCheckpoint	per-node	B6	no	role (act on WA only)	0	MyVisualScriptLogicProvider.cs:4312 acts on this node process only (save, unload, quit)
Sandbox.Game.MyVisualScriptLogicProvider.SetAllQuestlogDetailsCompleted	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:7317 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.SetAllQuestlogDetailsCompletedLocal	client	-	-	-	0	MyVisualScriptLogicProvider.cs:7339 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.SetAlphaHighlight	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:4574 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.SetAlphaHighlightForAll	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:4598 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.SetBlockCustomName	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:2155 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.SetBlockEnabled	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:2148 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.SetBlockGeneralDamageModifier	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:2254 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.SetBlockHealth	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:2290 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.SetBlockShowInInventory	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:2175 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.SetBlockShowInTerminal	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:2165 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.SetBlockShowOnHUD	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:2185 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.SetCampaignLevelOutcome	client	-	-	-	0	MyVisualScriptLogicProvider.cs:4383 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.SetCell	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:8874 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.SetChatMaxMessageCount	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:6327 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.SetChatMessageDuration	node-scoped	B5	yes	player-nodes + to-node	1	MyVisualScriptLogicProvider.cs:6320 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.SetColumnVisibility	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:8974 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.SetCustomLoadingScreenImage	client	-	-	-	0	MyVisualScriptLogicProvider.cs:6197 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.SetCustomLoadingScreenText	client	-	-	-	0	MyVisualScriptLogicProvider.cs:6204 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.SetCustomSkybox	client	-	-	-	0	MyVisualScriptLogicProvider.cs:6211 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.SetDampenersEnabled	node-scoped	B3	no	owner-of / to-owner	1	MyVisualScriptLogicProvider.cs:3788 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.SetDisabledByExperimental	client	-	-	-	0	MyVisualScriptLogicProvider.cs:6040 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.SetDroneBehaviourAdvanced	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:1130 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.SetDroneBehaviourBasic	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:1120 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.SetDroneBehaviourFull	node-scoped	B3	no	owner-of / to-owner	2	MyVisualScriptLogicProvider.cs:1141 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.SetEnabledByExperimental	client	-	-	-	0	MyVisualScriptLogicProvider.cs:6030 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.SetEntityPosition	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:3304 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.SetFactionObjectivePercentageCompleted	replicated	-	-	-	0	MyVisualScriptLogicProvider.cs:6116 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
Sandbox.Game.MyVisualScriptLogicProvider.SetFactionScore	replicated	-	-	-	0	MyVisualScriptLogicProvider.cs:6105 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
Sandbox.Game.MyVisualScriptLogicProvider.SetGPSColor	node-scoped	B5	yes	player-nodes + to-node	1	MyVisualScriptLogicProvider.cs:4748 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.SetGPSHighlight	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:4607 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.SetGPSHighlightForAll	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:4614 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.SetGPSHighlightNoSound	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:4629 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.SetGPSHighlightNoSoundForAll	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:4636 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.SetGridBlocksEnabled	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:5621 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.SetGridBlocksShowInInventory	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:5655 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.SetGridBlocksShowInTerminal	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:5638 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.SetGridBlocksShowOnHUD	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:5672 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.SetGridDestructible	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:5542 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.SetGridEditable	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:5584 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.SetGridGeneralDamageModifier	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:5611 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.SetGridImmune	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:5563 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.SetGridLandingGearsLock	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:5124 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.SetGridPowerState	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:5276 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.SetGridPowerStateByPlayer	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:5292 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.SetGridReactors	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:5149 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.SetGridStatic	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:5594 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.SetGridWeaponStatus	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:5171 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.SetHighlight	node-scoped	B5	yes	player-nodes + to-node	1	MyVisualScriptLogicProvider.cs:4532 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.SetHighlightForAll	node-scoped	B5	yes	player-nodes + to-node	1	MyVisualScriptLogicProvider.cs:4583 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.SetHighlightForProjection	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:5494 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.SetHighlightLocal	client	-	-	-	7	MyVisualScriptLogicProvider.cs:4567 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.SetHudState	client	-	-	-	3	MyVisualScriptLogicProvider.cs:3185 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.SetLandingGearLock	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:2434 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.SetLigtingBlockColor	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:2725 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.SetLigtingBlockIntensity	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:2735 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.SetLigtingBlockRadius	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:2745 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.SetMatchIsRunningState	per-node	B2	no	request (WA)	0	MyVisualScriptLogicProvider.cs:9068 state in a static or session component of this process; every node keeps its own copy
Sandbox.Game.MyVisualScriptLogicProvider.SetMatchStateRemainingDuration	per-node	B2	no	request (WA)	0	MyVisualScriptLogicProvider.cs:9033 state in a static or session component of this process; every node keeps its own copy
Sandbox.Game.MyVisualScriptLogicProvider.SetMissionOutcome	client	-	-	-	0	MyVisualScriptLogicProvider.cs:4398 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.SetName	node-scoped	B3	no	owner-of / to-owner	2	MyVisualScriptLogicProvider.cs:3590 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.SetPage	client	-	-	-	0	MyVisualScriptLogicProvider.cs:6098 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.SetPlayerGeneralDamageModifier	node-scoped	B3	no	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:7049 resolves the player through its identity's Character (GetIdentityFromPlayerId); a player attached to another node has no character here
Sandbox.Game.MyVisualScriptLogicProvider.SetPlayerInputBlacklistState	node-scoped	B5	yes	player-nodes + to-node	13	MyVisualScriptLogicProvider.cs:7112 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.SetPlayersColorInHSV	node-scoped	B3	no	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:6762 resolves the player through its identity's Character (GetIdentityFromPlayerId); a player attached to another node has no character here
Sandbox.Game.MyVisualScriptLogicProvider.SetPlayersColorInRGB	node-scoped	B3	no	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:6754 resolves the player through its identity's Character (GetIdentityFromPlayerId); a player attached to another node has no character here
Sandbox.Game.MyVisualScriptLogicProvider.SetPlayersEnergyLevel	node-scoped	B3	no	player-nodes + to-node	3	MyVisualScriptLogicProvider.cs:6947 resolves the player through its identity's Character (GetIdentityFromPlayerId); a player attached to another node has no character here
Sandbox.Game.MyVisualScriptLogicProvider.SetPlayersFaction	node-scoped	B3	no	player-nodes + to-node	1	MyVisualScriptLogicProvider.cs:4036 resolves the player through its identity's Character (GetIdentityFromPlayerId); a player attached to another node has no character here
Sandbox.Game.MyVisualScriptLogicProvider.SetPlayersHealth	node-scoped	B3	no	player-nodes + to-node	2	MyVisualScriptLogicProvider.cs:6904 resolves the player through its identity's Character (GetIdentityFromPlayerId); a player attached to another node has no character here
Sandbox.Game.MyVisualScriptLogicProvider.SetPlayersHelmetStatus	node-scoped	B3	no	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:6665 resolves the player through its identity's Character (GetIdentityFromPlayerId); a player attached to another node has no character here
Sandbox.Game.MyVisualScriptLogicProvider.SetPlayersHydrogenLevel	node-scoped	B3	no	player-nodes + to-node	2	MyVisualScriptLogicProvider.cs:6935 resolves the player through its identity's Character (GetIdentityFromPlayerId); a player attached to another node has no character here
Sandbox.Game.MyVisualScriptLogicProvider.SetPlayersOxygenLevel	node-scoped	B3	no	player-nodes + to-node	2	MyVisualScriptLogicProvider.cs:6924 resolves the player through its identity's Character (GetIdentityFromPlayerId); a player attached to another node has no character here
Sandbox.Game.MyVisualScriptLogicProvider.SetPlayersPosition	node-scoped	B3	no	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:6958 resolves the player through its identity's Character (GetIdentityFromPlayerId); a player attached to another node has no character here
Sandbox.Game.MyVisualScriptLogicProvider.SetPlayersSpeed	node-scoped	B3	no	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:6732 resolves the player through its identity's Character (GetIdentityFromPlayerId); a player attached to another node has no character here
Sandbox.Game.MyVisualScriptLogicProvider.SetQuestlog	node-scoped	B5	yes	player-nodes + to-node	2	MyVisualScriptLogicProvider.cs:7158 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.SetQuestlogDetailCompleted	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:7288 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.SetQuestlogDetailCompletedLocal	client	-	-	-	0	MyVisualScriptLogicProvider.cs:7310 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.SetQuestlogLocal	client	-	-	-	1	MyVisualScriptLogicProvider.cs:7184 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.SetQuestlogPage	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:7396 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.SetQuestlogTitle	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:7191 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.SetQuestlogTitleLocal	client	-	-	-	0	MyVisualScriptLogicProvider.cs:7209 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.SetQuestlogVisible	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:7417 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.SetQuestlogVisibleLocal	client	-	-	-	1	MyVisualScriptLogicProvider.cs:7439 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.SetRelationBetweenFactions	replicated	-	-	-	0	MyVisualScriptLogicProvider.cs:4133 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
Sandbox.Game.MyVisualScriptLogicProvider.SetRelationBetweenPlayerAndFaction	replicated	-	-	-	1	MyVisualScriptLogicProvider.cs:4145 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
Sandbox.Game.MyVisualScriptLogicProvider.SetRowRanking	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:8894 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.SetScenarioWinTypeForAnalytics	client	-	-	-	0	MyVisualScriptLogicProvider.cs:9054 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.SetTextPanelColors	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:2633 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.SetTextPanelDescription	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:2623 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.SetTextPanelTitle	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:2650 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.SetToolbarPage	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:7892 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.SetToolbarPageLocal	client	-	-	-	0	MyVisualScriptLogicProvider.cs:7909 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.SetToolbarSlotToItem	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:7774 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.SetToolbarSlotToItemLocal	client	-	-	-	1	MyVisualScriptLogicProvider.cs:7799 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.SetToolbarSlotToItemSync	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:7786 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.SetTooltip	client	-	-	-	0	MyVisualScriptLogicProvider.cs:6025 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.SetVolume	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:2035 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.SetVolumeLocal	client	-	-	-	0	MyVisualScriptLogicProvider.cs:2063 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.SetWeatherC	replicated	-	-	-	0	MyVisualScriptLogicProvider.cs:3936 weather is decided on the WA; a node's change is forwarded and published (cluster: WeatherAuthorityService.cs:24)
Sandbox.Game.MyVisualScriptLogicProvider.SetWeatherP	replicated	-	-	-	0	MyVisualScriptLogicProvider.cs:3943 weather is decided on the WA; a node's change is forwarded and published (cluster: WeatherAuthorityService.cs:24)
Sandbox.Game.MyVisualScriptLogicProvider.ShipDrillCollected	local	-	-	-	1	MyVisualScriptLogicProvider.cs:921 event raised where it happens; by the proximity rule that is one node, so it fires once cluster-wide
Sandbox.Game.MyVisualScriptLogicProvider.ShowHud	client	-	-	-	0	MyVisualScriptLogicProvider.cs:3144 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.ShowNotification	node-scoped	B5	yes	player-nodes + to-node	16	MyVisualScriptLogicProvider.cs:6241 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.ShowNotificationLocal	client	-	-	-	0	MyVisualScriptLogicProvider.cs:6342 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.ShowNotificationToAll	node-scoped	B5	yes	player-nodes + to-node	9	MyVisualScriptLogicProvider.cs:6263 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.ShowOrderInColumn	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:8954 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.SkinBlock	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:2365 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.SkipCutscene	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:2849 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.SkipCutsceneLocal	client	-	-	-	0	MyVisualScriptLogicProvider.cs:2867 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.SortByColumn	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:8914 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.SortByRanking	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:8934 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.SpawnBot	affinity	B8	no	to-owner / request	4	MyVisualScriptLogicProvider.cs:7648 creates the entity on this node whatever the position; a spawn near another node's partition merges over there, and the WA must never spawn (cluster: SpawnDirectorService.cs:15)
Sandbox.Game.MyVisualScriptLogicProvider.SpawnGroup	affinity	B8	no	to-owner / request	0	MyVisualScriptLogicProvider.cs:7495 creates the entity on this node whatever the position; a spawn near another node's partition merges over there, and the WA must never spawn (cluster: SpawnDirectorService.cs:15)
Sandbox.Game.MyVisualScriptLogicProvider.SpawnItem	affinity	B8	no	to-owner / request	0	MyVisualScriptLogicProvider.cs:7548 creates the entity on this node whatever the position; a spawn near another node's partition merges over there, and the WA must never spawn (cluster: SpawnDirectorService.cs:15)
Sandbox.Game.MyVisualScriptLogicProvider.SpawnLocalBlueprint	affinity	B8	no	to-owner / request	0	MyVisualScriptLogicProvider.cs:7534 creates the entity on this node whatever the position; a spawn near another node's partition merges over there, and the WA must never spawn (cluster: SpawnDirectorService.cs:15)
Sandbox.Game.MyVisualScriptLogicProvider.SpawnLocalBlueprintInGravity	affinity	B8	no	to-owner / request	0	MyVisualScriptLogicProvider.cs:7541 creates the entity on this node whatever the position; a spawn near another node's partition merges over there, and the WA must never spawn (cluster: SpawnDirectorService.cs:15)
Sandbox.Game.MyVisualScriptLogicProvider.SpawnPlayer	node-scoped	B3	no	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:7072 resolves the player through its identity's Character (GetIdentityFromPlayerId); a player attached to another node has no character here
Sandbox.Game.MyVisualScriptLogicProvider.SpawnPrefab	affinity	B8	no	to-owner / request	3	MyVisualScriptLogicProvider.cs:7697 creates the entity on this node whatever the position; a spawn near another node's partition merges over there, and the WA must never spawn (cluster: SpawnDirectorService.cs:15)
Sandbox.Game.MyVisualScriptLogicProvider.SpawnPrefabInGravity	affinity	B8	no	to-owner / request	2	MyVisualScriptLogicProvider.cs:7714 creates the entity on this node whatever the position; a spawn near another node's partition merges over there, and the WA must never spawn (cluster: SpawnDirectorService.cs:15)
Sandbox.Game.MyVisualScriptLogicProvider.StartCutscene	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:2770 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.StartCutsceneLocal	client	-	-	-	0	MyVisualScriptLogicProvider.cs:2792 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.StartGameplayTimer	per-node	B2	no	request (WA)	0	MyVisualScriptLogicProvider.cs:8645 state in a static or session component of this process; every node keeps its own copy
Sandbox.Game.MyVisualScriptLogicProvider.StartStateMachine	per-node	B2	no	request (WA)	0	MyVisualScriptLogicProvider.cs:7767 state in a static or session component of this process; every node keeps its own copy
Sandbox.Game.MyVisualScriptLogicProvider.StartTimerBlock	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:2424 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.StopDedicatedServer	per-node	B6	no	role (act on WA only)	0	MyVisualScriptLogicProvider.cs:4477 acts on this node process only (save, unload, quit)
Sandbox.Game.MyVisualScriptLogicProvider.StopGameplayTimer	per-node	B2	no	request (WA)	0	MyVisualScriptLogicProvider.cs:8659 state in a static or session component of this process; every node keeps its own copy
Sandbox.Game.MyVisualScriptLogicProvider.StopSound	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:1957 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.StopSoundLocal	client	-	-	-	0	MyVisualScriptLogicProvider.cs:1974 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.StopTimerBlock	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:2529 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.StoreEntityBool	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:8262 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.StoreEntityBoolList	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:8575 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.StoreEntityFloat	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:8304 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.StoreEntityFloatList	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:8617 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.StoreEntityInteger	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:8276 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.StoreEntityIntegerList	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:8589 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.StoreEntityLong	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:8290 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.StoreEntityLongList	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:8603 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.StoreEntityString	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:8248 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.StoreEntityStringList	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:8561 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.StoreEntityVector	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:8318 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.StoreEntityVectorList	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:8631 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.SunRotationEnabled	node-scoped	B2	no	request (WA)	0	MyVisualScriptLogicProvider.cs:3815 UNCERTAIN: sun rotation is a session setting of this process; the cluster replicates time of day (cluster: Patches/MySessionPatch.cs:24) but not this setter, not traced
Sandbox.Game.MyVisualScriptLogicProvider.SunRotationGetCurrentTime	node-scoped	B2	no	request (WA)	0	MyVisualScriptLogicProvider.cs:3836 UNCERTAIN: sun rotation is a session setting of this process; the cluster replicates time of day (cluster: Patches/MySessionPatch.cs:24) but not this setter, not traced
Sandbox.Game.MyVisualScriptLogicProvider.SunRotationGetDayLength	node-scoped	B2	no	request (WA)	3	MyVisualScriptLogicProvider.cs:3829 UNCERTAIN: sun rotation is a session setting of this process; the cluster replicates time of day (cluster: Patches/MySessionPatch.cs:24) but not this setter, not traced
Sandbox.Game.MyVisualScriptLogicProvider.SunRotationSetDayLength	node-scoped	B2	no	request (WA)	0	MyVisualScriptLogicProvider.cs:3822 UNCERTAIN: sun rotation is a session setting of this process; the cluster replicates time of day (cluster: Patches/MySessionPatch.cs:24) but not this setter, not traced
Sandbox.Game.MyVisualScriptLogicProvider.SunRotationSetTime	node-scoped	B2	no	request (WA)	0	MyVisualScriptLogicProvider.cs:3808 UNCERTAIN: sun rotation is a session setting of this process; the cluster replicates time of day (cluster: Patches/MySessionPatch.cs:24) but not this setter, not traced
Sandbox.Game.MyVisualScriptLogicProvider.SwitchToolbarToSlot	node-scoped	B5	yes	player-nodes + to-node	1	MyVisualScriptLogicProvider.cs:7806 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.SwitchToolbarToSlotLocal	client	-	-	-	0	MyVisualScriptLogicProvider.cs:7827 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.TakeScreenshot	client	-	-	-	0	MyVisualScriptLogicProvider.cs:6168 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.TargetingSetWhitelist	node-scoped	B3	no	owner-of / to-owner	1	MyVisualScriptLogicProvider.cs:1560 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.TeamBalancerPlayerSorted	affinity	B8	no	player-nodes	1	MyVisualScriptLogicProvider.cs:804 raised on the node the client is attached to; a node switch raises it on the new node again
Sandbox.Game.MyVisualScriptLogicProvider.TimerBlockTriggered	local	-	-	-	1	MyVisualScriptLogicProvider.cs:834 event raised where it happens; by the proximity rule that is one node, so it fires once cluster-wide
Sandbox.Game.MyVisualScriptLogicProvider.TimerBlockTriggeredEntityName	local	-	-	-	1	MyVisualScriptLogicProvider.cs:837 event raised where it happens; by the proximity rule that is one node, so it fires once cluster-wide
Sandbox.Game.MyVisualScriptLogicProvider.ToggleAbilityToSprint	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:7131 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.ToggleAbilityToSprintSync	node-scoped	B5	yes	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:7140 server-raised event to clients (MyMultiplayer.RaiseStaticEvent); reaches only clients attached to this node
Sandbox.Game.MyVisualScriptLogicProvider.TogglePlayersBroadcastEnabling	node-scoped	B3	no	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:6654 resolves the player through its identity's Character (GetIdentityFromPlayerId); a player attached to another node has no character here
Sandbox.Game.MyVisualScriptLogicProvider.TogglePlayersBroadcasting	node-scoped	B3	no	player-nodes + to-node	0	MyVisualScriptLogicProvider.cs:6643 resolves the player through its identity's Character (GetIdentityFromPlayerId); a player attached to another node has no character here
Sandbox.Game.MyVisualScriptLogicProvider.ToolEquipped	local	-	-	-	1	MyVisualScriptLogicProvider.cs:897 event raised where it happens; by the proximity rule that is one node, so it fires once cluster-wide
Sandbox.Game.MyVisualScriptLogicProvider.ToolbarConfigGroupsDefualtBehavior	client	-	-	-	0	MyVisualScriptLogicProvider.cs:5717 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.ToolbarConfigGroupsHideAll	client	-	-	-	0	MyVisualScriptLogicProvider.cs:5703 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.ToolbarConfigGroupsHideBlockGroups	client	-	-	-	0	MyVisualScriptLogicProvider.cs:5710 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.ToolbarConfigGroupsHideEmpty	client	-	-	-	0	MyVisualScriptLogicProvider.cs:5696 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.ToolbarItemChanged	local	-	-	-	2	MyVisualScriptLogicProvider.cs:927 event raised where it happens; by the proximity rule that is one node, so it fires once cluster-wide
Sandbox.Game.MyVisualScriptLogicProvider.TriggerTimerBlock	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:2539 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.TriggerTimerBlockForNonFriendlyPlayer	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:2549 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.UnlockAchievementById	client	-	-	-	0	MyVisualScriptLogicProvider.cs:8026 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.Game.MyVisualScriptLogicProvider.WeaponBlockActivated	local	-	-	-	1	MyVisualScriptLogicProvider.cs:912 event raised where it happens; by the proximity rule that is one node, so it fires once cluster-wide
Sandbox.Game.MyVisualScriptLogicProvider.WeaponSetShooting	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:2414 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.Game.MyVisualScriptLogicProvider.WeaponShootOnce	node-scoped	B3	no	owner-of / to-owner	0	MyVisualScriptLogicProvider.cs:2404 looks the entity up by name or id on this node (MyEntities.TryGetEntityByName / GetEntityById); no effect when it is on another node
Sandbox.ModAPI.BuildCheckResult.*	local	-	-	-	0	value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractAcquisition.Collateral	local	-	-	-	0	MyContractAcquisition.cs:31 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractAcquisition.ContractCondition	local	-	-	-	0	MyContractAcquisition.cs:43 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractAcquisition.Duration	local	-	-	-	0	MyContractAcquisition.cs:34 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractAcquisition.EndBlockId	local	-	-	-	0	MyContractAcquisition.cs:13 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractAcquisition.FailReputationPrice	local	-	-	-	0	MyContractAcquisition.cs:40 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractAcquisition.Id	local	-	-	-	0	MyContractAcquisition.cs:22 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractAcquisition.ItemAmount	local	-	-	-	0	MyContractAcquisition.cs:19 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractAcquisition.ItemTypeId	local	-	-	-	0	MyContractAcquisition.cs:16 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractAcquisition.MoneyReward	local	-	-	-	0	MyContractAcquisition.cs:28 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractAcquisition.OnContractAcquired	local	-	-	-	0	MyContractAcquisition.cs:46 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractAcquisition.OnContractFailed	local	-	-	-	0	MyContractAcquisition.cs:52 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractAcquisition.OnContractSucceeded	local	-	-	-	0	MyContractAcquisition.cs:49 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractAcquisition.RewardReputation	local	-	-	-	0	MyContractAcquisition.cs:37 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractAcquisition.StartBlockId	local	-	-	-	0	MyContractAcquisition.cs:25 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractBounty.Collateral	local	-	-	-	0	MyContractBounty.cs:24 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractBounty.ContractCondition	local	-	-	-	0	MyContractBounty.cs:36 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractBounty.Duration	local	-	-	-	0	MyContractBounty.cs:27 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractBounty.FailReputationPrice	local	-	-	-	0	MyContractBounty.cs:33 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractBounty.Id	local	-	-	-	0	MyContractBounty.cs:15 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractBounty.MoneyReward	local	-	-	-	0	MyContractBounty.cs:21 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractBounty.OnContractAcquired	local	-	-	-	0	MyContractBounty.cs:39 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractBounty.OnContractFailed	local	-	-	-	0	MyContractBounty.cs:45 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractBounty.OnContractSucceeded	local	-	-	-	0	MyContractBounty.cs:42 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractBounty.RewardReputation	local	-	-	-	0	MyContractBounty.cs:30 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractBounty.StartBlockId	local	-	-	-	0	MyContractBounty.cs:18 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractBounty.TargetIdentityId	local	-	-	-	0	MyContractBounty.cs:12 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractCustom.Collateral	local	-	-	-	0	MyContractCustom.cs:46 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractCustom.ContractCondition	local	-	-	-	0	MyContractCustom.cs:61 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractCustom.DefinitionId	local	-	-	-	0	MyContractCustom.cs:14 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractCustom.Description	local	-	-	-	0	MyContractCustom.cs:25 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractCustom.Duration	local	-	-	-	0	MyContractCustom.cs:49 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractCustom.EndBlockId	local	-	-	-	0	MyContractCustom.cs:17 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractCustom.FailReputationPrice	local	-	-	-	0	MyContractCustom.cs:31 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractCustom.Id	local	-	-	-	0	MyContractCustom.cs:37 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractCustom.MoneyReward	local	-	-	-	0	MyContractCustom.cs:43 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractCustom.Name	local	-	-	-	0	MyContractCustom.cs:22 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractCustom.OnContractAcquired	local	-	-	-	1	MyContractCustom.cs:52 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractCustom.OnContractFailed	local	-	-	-	1	MyContractCustom.cs:58 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractCustom.OnContractSucceeded	local	-	-	-	1	MyContractCustom.cs:55 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractCustom.ReputationReward	local	-	-	-	0	MyContractCustom.cs:28 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractCustom.RewardReputation	local	-	-	-	0	MyContractCustom.cs:34 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractCustom.StartBlockId	local	-	-	-	0	MyContractCustom.cs:40 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractEscort.Collateral	local	-	-	-	0	MyContractEscort.cs:31 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractEscort.ContractCondition	local	-	-	-	0	MyContractEscort.cs:43 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractEscort.Duration	local	-	-	-	0	MyContractEscort.cs:34 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractEscort.End	local	-	-	-	0	MyContractEscort.cs:16 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractEscort.FailReputationPrice	local	-	-	-	0	MyContractEscort.cs:40 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractEscort.Id	local	-	-	-	0	MyContractEscort.cs:22 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractEscort.MoneyReward	local	-	-	-	0	MyContractEscort.cs:28 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractEscort.OnContractAcquired	local	-	-	-	0	MyContractEscort.cs:46 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractEscort.OnContractFailed	local	-	-	-	0	MyContractEscort.cs:52 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractEscort.OnContractSucceeded	local	-	-	-	0	MyContractEscort.cs:49 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractEscort.OwnerIdentityId	local	-	-	-	0	MyContractEscort.cs:19 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractEscort.RewardReputation	local	-	-	-	0	MyContractEscort.cs:37 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractEscort.Start	local	-	-	-	0	MyContractEscort.cs:13 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractEscort.StartBlockId	local	-	-	-	0	MyContractEscort.cs:25 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractGridHauling.Collateral	local	-	-	-	0	MyContractGridHauling.cs:37 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractGridHauling.ContractCondition	local	-	-	-	0	MyContractGridHauling.cs:49 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractGridHauling.Duration	local	-	-	-	0	MyContractGridHauling.cs:40 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractGridHauling.FailReputationPrice	local	-	-	-	0	MyContractGridHauling.cs:46 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractGridHauling.GridPosition	local	-	-	-	0	MyContractGridHauling.cs:44 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractGridHauling.Id	local	-	-	-	0	MyContractGridHauling.cs:28 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractGridHauling.MoneyReward	local	-	-	-	0	MyContractGridHauling.cs:34 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractGridHauling.OnContractAcquired	local	-	-	-	0	MyContractGridHauling.cs:52 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractGridHauling.OnContractFailed	local	-	-	-	0	MyContractGridHauling.cs:58 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractGridHauling.OnContractSucceeded	local	-	-	-	0	MyContractGridHauling.cs:55 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractGridHauling.PrefabName	local	-	-	-	0	MyContractGridHauling.cs:22 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractGridHauling.RewardReputation	local	-	-	-	0	MyContractGridHauling.cs:43 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractGridHauling.RiskLevel	local	-	-	-	0	MyContractGridHauling.cs:46 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractGridHauling.StartBlockId	local	-	-	-	0	MyContractGridHauling.cs:31 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractGridHauling.TargetGridId	local	-	-	-	0	MyContractGridHauling.cs:16 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractHauling.Collateral	local	-	-	-	0	MyContractHauling.cs:27 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractHauling.ContractCondition	local	-	-	-	0	MyContractHauling.cs:39 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractHauling.Duration	local	-	-	-	0	MyContractHauling.cs:30 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractHauling.EndBlockId	local	-	-	-	0	MyContractHauling.cs:12 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractHauling.EndFactionStationId	local	-	-	-	0	MyContractHauling.cs:15 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractHauling.FailReputationPrice	local	-	-	-	0	MyContractHauling.cs:36 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractHauling.Id	local	-	-	-	0	MyContractHauling.cs:18 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractHauling.MoneyReward	local	-	-	-	0	MyContractHauling.cs:24 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractHauling.OnContractAcquired	local	-	-	-	0	MyContractHauling.cs:42 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractHauling.OnContractFailed	local	-	-	-	0	MyContractHauling.cs:48 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractHauling.OnContractSucceeded	local	-	-	-	0	MyContractHauling.cs:45 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractHauling.RewardReputation	local	-	-	-	0	MyContractHauling.cs:33 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractHauling.StartBlockId	local	-	-	-	0	MyContractHauling.cs:21 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractPvEBounty.Collateral	local	-	-	-	0	MyContractPvEBounty.cs:33 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractPvEBounty.ContractCondition	local	-	-	-	0	MyContractPvEBounty.cs:45 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractPvEBounty.Duration	local	-	-	-	0	MyContractPvEBounty.cs:36 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractPvEBounty.FailReputationPrice	local	-	-	-	0	MyContractPvEBounty.cs:42 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractPvEBounty.Id	local	-	-	-	0	MyContractPvEBounty.cs:24 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractPvEBounty.MoneyReward	local	-	-	-	0	MyContractPvEBounty.cs:30 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractPvEBounty.OnContractAcquired	local	-	-	-	0	MyContractPvEBounty.cs:48 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractPvEBounty.OnContractFailed	local	-	-	-	0	MyContractPvEBounty.cs:54 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractPvEBounty.OnContractSucceeded	local	-	-	-	0	MyContractPvEBounty.cs:51 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractPvEBounty.RewardReputation	local	-	-	-	0	MyContractPvEBounty.cs:39 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractPvEBounty.StartBlockId	local	-	-	-	0	MyContractPvEBounty.cs:27 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractPvEBounty.TargetFactionId	local	-	-	-	0	MyContractPvEBounty.cs:66 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractPvEBounty.TargetPosition	local	-	-	-	0	MyContractPvEBounty.cs:68 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractPvEBounty.TargetSpawnGroup	local	-	-	-	0	MyContractPvEBounty.cs:21 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractRepair.Collateral	local	-	-	-	0	MyContractRepair.cs:24 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractRepair.ContractCondition	local	-	-	-	0	MyContractRepair.cs:36 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractRepair.Duration	local	-	-	-	0	MyContractRepair.cs:27 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractRepair.FailReputationPrice	local	-	-	-	0	MyContractRepair.cs:33 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractRepair.GridId	local	-	-	-	0	MyContractRepair.cs:32 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractRepair.Id	local	-	-	-	0	MyContractRepair.cs:15 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractRepair.MoneyReward	local	-	-	-	0	MyContractRepair.cs:21 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractRepair.OnContractAcquired	local	-	-	-	0	MyContractRepair.cs:39 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractRepair.OnContractFailed	local	-	-	-	0	MyContractRepair.cs:45 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractRepair.OnContractSucceeded	local	-	-	-	0	MyContractRepair.cs:42 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractRepair.RewardReputation	local	-	-	-	0	MyContractRepair.cs:30 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractRepair.StartBlockId	local	-	-	-	0	MyContractRepair.cs:18 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractSalvage.Collateral	local	-	-	-	0	MyContractSalvage.cs:32 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractSalvage.ContractCondition	local	-	-	-	0	MyContractSalvage.cs:44 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractSalvage.Duration	local	-	-	-	0	MyContractSalvage.cs:35 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractSalvage.FailReputationPrice	local	-	-	-	0	MyContractSalvage.cs:41 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractSalvage.GridPosition	local	-	-	-	0	MyContractSalvage.cs:61 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractSalvage.Id	local	-	-	-	0	MyContractSalvage.cs:23 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractSalvage.MoneyReward	local	-	-	-	0	MyContractSalvage.cs:29 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractSalvage.OnContractAcquired	local	-	-	-	0	MyContractSalvage.cs:47 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractSalvage.OnContractFailed	local	-	-	-	0	MyContractSalvage.cs:53 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractSalvage.OnContractSucceeded	local	-	-	-	0	MyContractSalvage.cs:50 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractSalvage.PrefabName	local	-	-	-	0	MyContractSalvage.cs:63 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractSalvage.RewardReputation	local	-	-	-	0	MyContractSalvage.cs:38 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractSalvage.StartBlockId	local	-	-	-	0	MyContractSalvage.cs:26 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractSalvage.TargetGridId	local	-	-	-	0	MyContractSalvage.cs:20 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractSearch.Collateral	local	-	-	-	0	MyContractSearch.cs:27 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractSearch.ContractCondition	local	-	-	-	0	MyContractSearch.cs:33 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractSearch.Duration	local	-	-	-	0	MyContractSearch.cs:30 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractSearch.FailReputationPrice	local	-	-	-	0	MyContractSearch.cs:39 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractSearch.Id	local	-	-	-	0	MyContractSearch.cs:18 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractSearch.MoneyReward	local	-	-	-	0	MyContractSearch.cs:24 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractSearch.OnContractAcquired	local	-	-	-	0	MyContractSearch.cs:42 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractSearch.OnContractFailed	local	-	-	-	0	MyContractSearch.cs:48 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractSearch.OnContractSucceeded	local	-	-	-	0	MyContractSearch.cs:45 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractSearch.RewardReputation	local	-	-	-	0	MyContractSearch.cs:36 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractSearch.SearchRadius	local	-	-	-	0	MyContractSearch.cs:15 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractSearch.StartBlockId	local	-	-	-	0	MyContractSearch.cs:21 value, parser or helper; no world state
Sandbox.ModAPI.Contracts.MyContractSearch.TargetGridId	local	-	-	-	0	MyContractSearch.cs:12 value, parser or helper; no world state
Sandbox.ModAPI.CustomActionGetDelegate.*	local	-	-	-	0	value, parser or helper; no world state
Sandbox.ModAPI.CustomControlGetDelegate.*	local	-	-	-	0	value, parser or helper; no world state
Sandbox.ModAPI.HitInterceptor.*	local	-	-	-	0	value, parser or helper; no world state
Sandbox.ModAPI.IMyAccessAnyoneCanUse.AnyoneCanUse	local	-	-	-	0	MyAccessAnyoneCanUse.cs:41 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyAiBlockComponent.AiBlockType	local	-	-	-	0	MyAiBlockComponent.cs:70 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyAiBlockComponent.Deactivate	local	-	-	-	0	MyAiBlockComponent.cs:129 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyAiBlockComponent.Entity	local	-	-	-	0	IMyAiBlockComponent.cs:25 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyAiBlockComponent.HudErrorStringGetter	local	-	-	-	0	MyAiBlockComponent.cs:68 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyAiBlockComponent.IsActivated	local	-	-	-	0	MyAiBlockComponent.cs:72 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyAiBlockComponent.OnActivatedChanged	local	-	-	-	0	MyAiBlockComponent.cs:86 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyAiRvoSessionComponent.IsCubeGridSimulationComputed	local	-	-	-	0	MyAiRvoComponent.cs:136 process-wide registration or service; every node runs the mod and registers its own
Sandbox.ModAPI.IMyAiRvoSessionComponent.RequestUpdate	local	-	-	-	0	MyAiRvoComponent.cs:146 process-wide registration or service; every node runs the mod and registers its own
Sandbox.ModAPI.IMyAssembler.CurrentModeChanged	local	-	-	-	0	MyAssembler.cs:415 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyAssembler.CurrentProgressChanged	local	-	-	-	0	MyAssembler.cs:391 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyAssembler.CurrentStateChanged	local	-	-	-	0	MyAssembler.cs:403 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyAttachableTopBlock.Base	local	-	-	-	6	MyAttachableTopBlockBase.cs:24 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyAttackPatternComponent.AppendCustomInfo	local	-	-	-	0	MyOffensiveCombatAbstractComponent.cs:483 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyAttackPatternComponent.CreateTerminalInterfaceControls	local	-	-	-	0	MyOffensiveCombatAbstractComponent.cs:491 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyAttackPatternComponent.IsSelected	local	-	-	-	0	MyOffensiveCombatAbstractComponent.cs:148 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyBasicMissionAutopilot.AddWaypoint	local	-	-	-	0	MyBasicMissionAutopilot.cs:1082 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyBasicMissionAutopilot.ClearWaypoints	local	-	-	-	0	MyBasicMissionAutopilot.cs:1293 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyBasicMissionAutopilot.GetLastWaypoint	local	-	-	-	0	MyBasicMissionAutopilot.cs:1255 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyBasicMissionAutopilot.GetWaypoints	local	-	-	-	0	MyBasicMissionAutopilot.cs:1264 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyBasicMissionAutopilot.Mode	local	-	-	-	0	MyBasicMissionAutopilot.cs:191 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyBasicMissionAutopilot.RemoveWaypoint	local	-	-	-	0	MyBasicMissionAutopilot.cs:1280 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyBasicMissionAutopilot.SetMode	local	-	-	-	0	MyBasicMissionAutopilot.cs:1240 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyBasicMissionBlock.TryGetMission	local	-	-	-	0	MyBasicMissionBlock.cs:232 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyBasicMissionBlock.TryGetSelectedMission	local	-	-	-	0	MyBasicMissionBlock.cs:218 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyBasicMissionComponent.CreateTerminalInterfaceControls	local	-	-	-	0	MyBasicMissionAutopilot.cs:1236 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyBasicMissionFollowHome.GoHome	local	-	-	-	0	MyBasicMissionFollowHome.cs:785 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyBasicMissionFollowPlayer.FollowPlayer	local	-	-	-	0	MyBasicMissionFollowPlayer.cs:757 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyBasicMissionFollowPlayer.StopFollowing	local	-	-	-	0	MyBasicMissionFollowPlayer.cs:794 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyBlockGroup.GetBlocks	local	-	-	-	8	MyBlockGroup.cs:54 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyBlockGroup.GetBlocksOfType	local	-	-	-	1	MyBlockGroup.cs:66 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyCameraBlock.IsActiveLocal	local	-	-	-	0	MyCameraBlock.cs:318 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyCockpit.AttachPilot	local	-	-	-	4	MyCockpit.cs:2946 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyCockpit.IsOccupied	local	-	-	-	0	MyCockpit.cs:419 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyCockpit.IsOccupiedChanged	local	-	-	-	0	MyCockpit.cs:652 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyCockpit.OxygenFilledRatio	local	-	-	-	3	MyCockpit.cs:476 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyCockpit.RemovePilot	local	-	-	-	3	MyCockpit.cs:2971 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyDefensiveCombatBlock.DoSearch	local	-	-	-	0	MyDefensiveCombatBlock.cs:1117 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyDefensiveCombatBlock.FleeBeaconEntityId	local	-	-	-	0	MyDefensiveCombatBlock.cs:388 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyDefensiveCombatBlock.FleeGps	local	-	-	-	0	MyDefensiveCombatBlock.cs:419 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyDefensiveCombatBlock.ForceFlee	local	-	-	-	0	MyDefensiveCombatBlock.cs:1878 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyDefensiveCombatBlock.IsFleeingChanged	local	-	-	-	0	MyDefensiveCombatBlock.cs:513 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyDefensiveCombatBlock.SearchEnemyComponent	local	-	-	-	1	MyDefensiveCombatBlock.cs:481 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyDistanceToLockedTarget.DistanceToLockedTarget	local	-	-	-	0	MyTargetLockingBlockComponent.cs:129 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyDistanceToLockedTarget.DistanceToLockedTargetChanged	local	-	-	-	0	MyTargetLockingBlockComponent.cs:142 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyDistanceToLockedTarget.Entity	local	-	-	-	0	IMyDistanceToLockedTarget.cs:19 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyDoor.DoorStateChanged	local	-	-	-	3	MyAdvancedDoor.cs:200 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyDoor.IsFullyClosed	local	-	-	-	4	MyAdvancedDoor.cs:122 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyDoor.OnDoorClosed	local	-	-	-	0	MyAdvancedDoor.cs:204 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyDoor.OnDoorOpened	local	-	-	-	0	MyAdvancedDoor.cs:206 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyDoor.OnDoorStateChanged	local	-	-	-	1	MyAdvancedDoor.cs:202 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyEventComponentWithGui.AddBlocks	local	-	-	-	0	MyEventAngleChanged.cs:248 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyEventComponentWithGui.IsBlockValidForList	local	-	-	-	0	MyEventAngleChanged.cs:243 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyEventComponentWithGui.IsBlocksListUsed	local	-	-	-	1	MyEventAngleChanged.cs:72 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyEventComponentWithGui.IsConditionSelectionUsed	local	-	-	-	2	MyEventAngleChanged.cs:74 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyEventComponentWithGui.IsThresholdUsed	local	-	-	-	2	MyEventAngleChanged.cs:70 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyEventComponentWithGui.NotifyValuesChanged	local	-	-	-	0	MyEventAngleChanged.cs:266 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyEventComponentWithGui.RefreshDetailedInfo	local	-	-	-	0	MyEventAngleChanged.cs:261 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyEventComponentWithGui.RemoveBlocks	local	-	-	-	0	MyEventAngleChanged.cs:256 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyEventControllerBlock.ActionTriggered	local	-	-	-	1	MyEventControllerBlock.cs:214 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyEventControllerBlock.SelectedEvent	local	-	-	-	2	MyEventControllerBlock.cs:172 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyEventControllerBlock.TriggerAction	local	-	-	-	3	MyEventControllerBlock.cs:1010 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyEventControllerEntityComponent.CreateTerminalInterfaceControls	local	-	-	-	0	MyEventAngleChanged.cs:274 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyEventControllerEntityComponent.EventDisplayName	local	-	-	-	2	MyEventAngleChanged.cs:84 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyEventControllerEntityComponent.IsSelected	local	-	-	-	0	MyEventAngleChanged.cs:82 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyEventControllerEntityComponent.UniqueSelectionId	local	-	-	-	0	MyEventAngleChanged.cs:80 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyEventControllerEntityComponent.YesNoToolbarNoDescription	local	-	-	-	0	MyEventAngleChanged.cs:78 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyEventControllerEntityComponent.YesNoToolbarYesDescription	local	-	-	-	0	MyEventAngleChanged.cs:76 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyExhaustBlock.SelectEffect	local	-	-	-	0	MyExhaustBlock.cs:205 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyExhaustBlock.StartEffects	local	-	-	-	0	MyExhaustBlock.cs:243 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyExhaustBlock.StopEffects	local	-	-	-	0	MyExhaustBlock.cs:231 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyFarmPlotLogic.Harvest	local	-	-	-	0	MyFarmPlotLogic.cs:763 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyFarmPlotLogic.PlantSeed	local	-	-	-	0	MyFarmPlotLogic.cs:673 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyFarmPlotLogic.RemovePlant	local	-	-	-	0	MyFarmPlotLogic.cs:892 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyFlightMovementBlock.AddWaypoint	local	-	-	-	0	MyFlightMovementBlock.cs:1601 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyFlightMovementBlock.AditionalDetailInfo	local	-	-	-	0	MyFlightMovementBlock.cs:142 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyFlightMovementBlock.AutopilotSpeedLimitValueChanged	local	-	-	-	0	MyFlightMovementBlock.cs:507 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyFlightMovementBlock.FacingDirection	local	-	-	-	0	MyFlightMovementBlock.cs:368 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyFlightMovementBlock.IsPowered	local	-	-	-	0	MyFlightMovementBlock.cs:169 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyFlightMovementBlock.OnWaypointReached	local	-	-	-	0	MyFlightMovementBlock.cs:509 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyFlightMovementBlock.RemoveWaypoint	local	-	-	-	0	MyFlightMovementBlock.cs:1573 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyFlightMovementBlock.SetCollisionAvoidanceErrorState	local	-	-	-	0	MyFlightMovementBlock.cs:761 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyFunctionalBlock.EnabledChanged	local	-	-	-	21	MyFunctionalBlock.cs:223 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyFunctionalBlock.GetFramesFromLastTrigger	local	-	-	-	0	MyFunctionalBlock.cs:781 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyFunctionalBlock.IsUpdateTimerCreated	local	-	-	-	0	MyFunctionalBlock.cs:205 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyFunctionalBlock.IsUpdateTimerEnabled	local	-	-	-	0	MyFunctionalBlock.cs:207 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyFunctionalBlock.UpdateTimerTriggered	local	-	-	-	0	MyFunctionalBlock.cs:235 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyGasGenerator.PowerConsumptionMultiplier	local	-	-	-	1	MyGasGenerator.cs:241 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyGasGenerator.ProductionCapacityMultiplier	local	-	-	-	2	MyGasGenerator.cs:225 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyGasTank.ChangeFilledRatio	local	-	-	-	3	MyGasTank.cs:304 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyGlobalEncounterComponent.AddGlobalEncounterComponent	node-scoped	B2	no	request (WA)	0	MyGlobalEncounterComponent.cs:88 UNCERTAIN: global encounters are generated on the WA (cluster: Patches/MyGlobalEncountersGeneratorPatch.cs); the component API on a node was not traced
Sandbox.ModAPI.IMyGlobalEncounterComponent.EncounterId	node-scoped	B2	no	request (WA)	0	MyGlobalEncounterComponent.cs:21 UNCERTAIN: global encounters are generated on the WA (cluster: Patches/MyGlobalEncountersGeneratorPatch.cs); the component API on a node was not traced
Sandbox.ModAPI.IMyGlobalEncounterComponent.GpsCreated	node-scoped	B2	no	request (WA)	0	MyGlobalEncounterComponent.cs:25 UNCERTAIN: global encounters are generated on the WA (cluster: Patches/MyGlobalEncountersGeneratorPatch.cs); the component API on a node was not traced
Sandbox.ModAPI.IMyGlobalEncounterComponent.RegisteredAsEncounter	node-scoped	B2	no	request (WA)	0	MyGlobalEncounterComponent.cs:23 UNCERTAIN: global encounters are generated on the WA (cluster: Patches/MyGlobalEncountersGeneratorPatch.cs); the component API on a node was not traced
Sandbox.ModAPI.IMyGlobalEncounterComponent.SpawnGroupName	node-scoped	B2	no	request (WA)	0	MyGlobalEncounterComponent.cs:27 UNCERTAIN: global encounters are generated on the WA (cluster: Patches/MyGlobalEncountersGeneratorPatch.cs); the component API on a node was not traced
Sandbox.ModAPI.IMyGlobalEncounterComponent.UnregisterFromEncounter	node-scoped	B2	no	request (WA)	0	MyGlobalEncounterComponent.cs:63 UNCERTAIN: global encounters are generated on the WA (cluster: Patches/MyGlobalEncountersGeneratorPatch.cs); the component API on a node was not traced
Sandbox.ModAPI.IMyGridProgram.Echo	local	-	-	-	0	MyGridProgram.cs:121 value, parser or helper; no world state
Sandbox.ModAPI.IMyGridProgram.GridTerminalSystem	local	-	-	-	0	MyGridProgram.cs:85 value, parser or helper; no world state
Sandbox.ModAPI.IMyGridProgram.HasMainMethod	local	-	-	-	0	MyGridProgram.cs:157 value, parser or helper; no world state
Sandbox.ModAPI.IMyGridProgram.HasSaveMethod	local	-	-	-	0	MyGridProgram.cs:159 value, parser or helper; no world state
Sandbox.ModAPI.IMyGridProgram.IGC_ContextGetter	local	-	-	-	0	MyGridProgram.cs:72 value, parser or helper; no world state
Sandbox.ModAPI.IMyGridProgram.Main	local	-	-	-	0	MyGridProgram.cs:208 value, parser or helper; no world state
Sandbox.ModAPI.IMyGridProgram.Me	local	-	-	-	0	MyGridProgram.cs:97 value, parser or helper; no world state
Sandbox.ModAPI.IMyGridProgram.Runtime	local	-	-	-	0	MyGridProgram.cs:133 value, parser or helper; no world state
Sandbox.ModAPI.IMyGridProgram.Save	local	-	-	-	0	MyGridProgram.cs:226 value, parser or helper; no world state
Sandbox.ModAPI.IMyGridProgram.Storage	local	-	-	-	0	MyGridProgram.cs:109 value, parser or helper; no world state
Sandbox.ModAPI.IMyGridProgram.World	local	-	-	-	0	MyGridProgram.cs:145 value, parser or helper; no world state
Sandbox.ModAPI.IMyGridTerminalSystem.GetBlockGroupWithName	local	-	-	-	5	MyProgrammableBlock.cs:242 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyGridTerminalSystem.GetBlockGroups	local	-	-	-	3	MyProgrammableBlock.cs:217 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyGridTerminalSystem.GetBlockWithName	local	-	-	-	1	MyProgrammableBlock.cs:237 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyGridTerminalSystem.GetBlocks	local	-	-	-	3	MyProgrammableBlock.cs:212 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyGridTerminalSystem.GetBlocksOfType	local	-	-	-	8	MyProgrammableBlock.cs:222 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyGridTerminalSystem.GroupAdded	local	-	-	-	2	MyGridTerminalSystem.cs:102 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyGridTerminalSystem.GroupRemoved	local	-	-	-	2	MyGridTerminalSystem.cs:114 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyGridTerminalSystem.SearchBlocksOfName	local	-	-	-	3	MyProgrammableBlock.cs:232 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyGyro.GyroStrengthMultiplier	local	-	-	-	8	MyGyro.cs:192 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyGyro.PowerConsumptionMultiplier	local	-	-	-	6	MyGyro.cs:212 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyHazardExposureComponent.AddSource	local	-	-	-	2	MyHazardExposureComponent.cs:126 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyHazardExposureComponent.GetCurrentlyProcessedSources	local	-	-	-	1	MyHazardExposureComponent.cs:145 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyHazardExposureComponent.RemoveSource	local	-	-	-	1	MyHazardExposureComponent.cs:135 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyHazardPointSource.GetPosition	local	-	-	-	0	MyRadiationSourceEntityComponent.cs:49 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyHazardReceiver.Apply	local	-	-	-	0	MyCharacterStatComponent.cs:438 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyHazardReceiver.CanBeAffected	local	-	-	-	0	MyCharacterStatComponent.cs:412 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyHazardReceiver.GetEntity	local	-	-	-	2	MyCharacterStatComponent.cs:477 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyHazardReceiver.GetPosition	local	-	-	-	1	MyCharacterStatComponent.cs:455 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyHazardSource.DamageType	local	-	-	-	0	MyRadiationSourceEntityComponent.cs:22 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyHazardSource.GetCurrentExposure	local	-	-	-	0	MyRadiationSourceEntityComponent.cs:34 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyHazardSource.IgnoresSheltering	local	-	-	-	0	MyRadiationSourceEntityComponent.cs:24 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyHazardSource.IsExposureScalingNeeded	local	-	-	-	0	MyRadiationSourceEntityComponent.cs:26 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyHazardSource.StatId	local	-	-	-	0	MyRadiationSourceEntityComponent.cs:20 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyIngameScripting.Clean	local	-	-	-	0	MyVRageIngameScriptingAdapter.cs:110 process-wide registration or service; every node runs the mod and registers its own
Sandbox.ModAPI.IMyIngameScripting.ScriptBlacklist	local	-	-	-	0	MyIngameScripting.cs:9 process-wide registration or service; every node runs the mod and registers its own
Sandbox.ModAPI.IMyItemProducerComponent.Produce	local	-	-	-	0	MyItemProducerComponent.cs:68 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyItemProducerComponent.UseConveyorSystem	local	-	-	-	0	MyItemProducerComponent.cs:50 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyJumpDrive.CurrentStoredPower	local	-	-	-	9	MyJumpDrive.cs:350 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyJumpDrive.Jump	local	-	-	-	0	MyJumpDrive.cs:471 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyLargeTurretBase.SetTarget	local	-	-	-	0	MyLargeTurretBase.cs:3757 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyLargeTurretBase.Target	local	-	-	-	3	MyLargeTurretBase.cs:1229 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyLargeTurretBase.TrackTarget	local	-	-	-	2	MyLargeTurretBase.cs:3730 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyLaserAntenna.IsInRange	node-scoped	B3	no	owner-of / to-owner	2	MyLaserAntenna.cs:2091 the partner antenna is usually far away and may be on another node; UNCERTAIN how MyLaserAntennaPatch resolves it
Sandbox.ModAPI.IMyLaserAntenna.Other	node-scoped	B3	no	owner-of / to-owner	4	MyLaserAntenna.cs:392 the partner antenna is usually far away and may be on another node; UNCERTAIN how MyLaserAntennaPatch resolves it
Sandbox.ModAPI.IMyLcdSurfaceComponent.InitializedWithBuilder	local	-	-	-	0	MyLcdSurfaceComponent.cs:111 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyLcdSurfaceComponent.OnRemovedFromScene	local	-	-	-	0	MyLcdSurfaceComponent.cs:874 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyLcdSurfaceComponent.SelectedRotationIndex	local	-	-	-	2	MyLcdSurfaceComponent.cs:129 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyLcdSurfaceComponent.SetRenderForAllAreas	local	-	-	-	0	MyLcdSurfaceComponent.cs:854 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyLcdSurfaceComponent.SetSelectedRotationIndex	local	-	-	-	0	MyLcdSurfaceComponent.cs:682 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyLcdSurfaceComponent.UpdateVisibility	local	-	-	-	0	MyLcdSurfaceComponent.cs:137 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyMechanicalConnectionBlock.Attach	local	-	-	-	0	MyMechanicalConnectionBlockBase.cs:1268 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyMechanicalConnectionBlock.OnAttachedChanged	local	-	-	-	1	MyMechanicalConnectionBlockBase.cs:347 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyMechanicalConnectionBlock.Top	local	-	-	-	12	MyMechanicalConnectionBlockBase.cs:339 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyMechanicalConnectionBlock.TopGrid	local	-	-	-	11	MyMechanicalConnectionBlockBase.cs:337 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyMissile.AmmoDefinition	local	-	-	-	1	MyMissile.cs:269 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyMissile.AmmoMagazineDefinition	local	-	-	-	0	MyMissile.cs:271 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyMissile.CollidedEntity	local	-	-	-	3	MyMissile.cs:297 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyMissile.CollisionNormal	local	-	-	-	2	MyMissile.cs:301 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyMissile.CollisionPoint	local	-	-	-	2	MyMissile.cs:299 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyMissile.Destroy	local	-	-	-	0	MyMissile.cs:1487 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyMissile.ExplosionDamage	local	-	-	-	1	MyMissile.cs:303 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyMissile.ExplosionType	local	-	-	-	0	MyMissile.cs:285 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyMissile.HealthPool	local	-	-	-	3	MyMissile.cs:315 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyMissile.IsCharacterIdFriendly	local	-	-	-	1	MyMissile.cs:1467 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyMissile.LauncherId	local	-	-	-	5	MyMissile.cs:231 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyMissile.LinearVelocity	local	-	-	-	2	MyMissile.cs:243 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyMissile.MaxTrajectory	local	-	-	-	2	MyMissile.cs:207 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyMissile.Origin	local	-	-	-	2	MyMissile.cs:255 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyMissile.Owner	local	-	-	-	1	MyMissile.cs:219 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyMissile.ParticleEffect	local	-	-	-	1	MyMissile.cs:273 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyMissile.ShouldExplode	local	-	-	-	0	MyMissile.cs:327 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyMissile.WeaponDefinition	local	-	-	-	1	MyMissile.cs:267 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyMissiles.GetAllMissilesInSphere	node-scoped	B1	no	-	1	MyMissiles.cs:196 this node's missiles; complete within sync distance
Sandbox.ModAPI.IMyMissiles.OnMissileAdded	local	-	-	-	2	MyMissiles.cs:36 missile range is capped at sync distance (cluster: Patches/MyMissilePatch.cs:22)
Sandbox.ModAPI.IMyMissiles.OnMissileCollided	local	-	-	-	3	MyMissiles.cs:42 missile range is capped at sync distance (cluster: Patches/MyMissilePatch.cs:22)
Sandbox.ModAPI.IMyMissiles.OnMissileMoved	local	-	-	-	1	MyMissiles.cs:40 missile range is capped at sync distance (cluster: Patches/MyMissilePatch.cs:22)
Sandbox.ModAPI.IMyMissiles.OnMissileRemoved	local	-	-	-	1	MyMissiles.cs:38 missile range is capped at sync distance (cluster: Patches/MyMissilePatch.cs:22)
Sandbox.ModAPI.IMyMissiles.Remove	local	-	-	-	0	MyMissiles.cs:119 missile range is capped at sync distance (cluster: Patches/MyMissilePatch.cs:22)
Sandbox.ModAPI.IMyMotorBase.Attach	local	-	-	-	2	MyMotorBase.cs:328 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyMotorBase.AttachedEntityChanged	local	-	-	-	0	MyMotorBase.cs:112 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyMotorBase.DummyPosition	local	-	-	-	0	MyMotorBase.cs:48 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyMotorBase.MaxRotorAngularVelocity	local	-	-	-	1	MyMotorBase.cs:106 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyMotorBase.Rotor	local	-	-	-	0	MyMotorBase.cs:110 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyMotorBase.RotorAngularVelocity	local	-	-	-	0	MyMotorBase.cs:92 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyMotorBase.RotorGrid	local	-	-	-	0	MyMotorBase.cs:108 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyMotorRotor.Base	local	-	-	-	4	MyMotorRotor.cs:44 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyMotorRotor.Stator	local	-	-	-	0	MyMotorRotor.cs:42 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyMotorStator.AngleChanged	local	-	-	-	0	MyMotorStator.cs:494 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyMotorStator.LimitReached	local	-	-	-	0	MyMotorStator.cs:498 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyNPCGridClaimSessionComponent.ClaimTimerResumed	node-scoped	B2	no	request (WA)	0	MyNPCGridClaimSessionComponent.cs:39 UNCERTAIN: NPC grid claims are kept by this node's session component; not traced
Sandbox.ModAPI.IMyNPCGridClaimSessionComponent.ClaimTimerStarted	node-scoped	B2	no	request (WA)	0	MyNPCGridClaimSessionComponent.cs:37 UNCERTAIN: NPC grid claims are kept by this node's session component; not traced
Sandbox.ModAPI.IMyNPCGridClaimSessionComponent.GetFramesElapsed	node-scoped	B2	no	request (WA)	0	MyNPCGridClaimSessionComponent.cs:314 UNCERTAIN: NPC grid claims are kept by this node's session component; not traced
Sandbox.ModAPI.IMyNPCGridClaimSessionComponent.GetTimeRemaining	node-scoped	B2	no	request (WA)	0	MyNPCGridClaimSessionComponent.cs:323 UNCERTAIN: NPC grid claims are kept by this node's session component; not traced
Sandbox.ModAPI.IMyNPCGridClaimSessionComponent.GridUnregistered	node-scoped	B2	no	request (WA)	0	MyNPCGridClaimSessionComponent.cs:41 UNCERTAIN: NPC grid claims are kept by this node's session component; not traced
Sandbox.ModAPI.IMyNPCGridClaimSessionComponent.RequestRegisterGrid	node-scoped	B2	no	request (WA)	0	MyNPCGridClaimSessionComponent.cs:79 UNCERTAIN: NPC grid claims are kept by this node's session component; not traced
Sandbox.ModAPI.IMyNPCGridClaimSessionComponent.RequestSetFramesElapsed	node-scoped	B2	no	request (WA)	0	MyNPCGridClaimSessionComponent.cs:195 UNCERTAIN: NPC grid claims are kept by this node's session component; not traced
Sandbox.ModAPI.IMyNPCGridClaimSessionComponent.RequestUnregisterGrid	node-scoped	B2	no	request (WA)	0	MyNPCGridClaimSessionComponent.cs:180 UNCERTAIN: NPC grid claims are kept by this node's session component; not traced
Sandbox.ModAPI.IMyNPCSpawnerSessionComponent.SpawnBot	affinity	B8	no	to-owner / request	0	MyBotAnchorComponent.cs:313 creates the entity on this node whatever the position; a spawn near another node's partition merges over there, and the WA must never spawn (cluster: SpawnDirectorService.cs:15)
Sandbox.ModAPI.IMyOffensiveCombatBlock.DoForceSearchEnemy	local	-	-	-	0	MyOffensiveCombatBlock.cs:343 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyOffensiveCombatBlock.DoSearch	local	-	-	-	0	MyOffensiveCombatBlock.cs:454 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyOffensiveCombatBlock.OnSelectedAttackPatternChanged	local	-	-	-	1	MyOffensiveCombatBlock.cs:238 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyOffensiveCombatBlock.OnTargetChanged	local	-	-	-	1	MyOffensiveCombatBlock.cs:236 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyOffensiveCombatBlock.SearchEnemyComponent	local	-	-	-	2	MyOffensiveCombatBlock.cs:196 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyOffensiveCombatBlock.SetSearchLocked	local	-	-	-	0	MyOffensiveCombatBlock.cs:442 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyOffensiveCombatBlock.TryGetSelectedAttackPattern	local	-	-	-	0	MyOffensiveCombatBlock.cs:504 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyOffensiveCombatCircleOrbit.BlockValidation	local	-	-	-	0	IMyOffensiveCombatCircleOrbit.cs:23 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyOffensiveCombatCircleOrbit.ClearBlocksShootingState	local	-	-	-	0	IMyOffensiveCombatCircleOrbit.cs:62 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyOffensiveCombatCircleOrbit.GetFirstWeaponForwardVector	local	-	-	-	0	IMyOffensiveCombatCircleOrbit.cs:71 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyOffensiveCombatCircleOrbit.GetPredictedLookAt	local	-	-	-	0	IMyOffensiveCombatCircleOrbit.cs:51 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyOffensiveCombatCircleOrbit.SetShooting	local	-	-	-	0	IMyOffensiveCombatCircleOrbit.cs:39 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyOffensiveCombatHitAndRun.BlockValidation	local	-	-	-	0	IMyOffensiveCombatHitAndRun.cs:23 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyOffensiveCombatHitAndRun.ClearBlocksShootingState	local	-	-	-	0	IMyOffensiveCombatHitAndRun.cs:62 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyOffensiveCombatHitAndRun.GetFirstWeaponForwardVector	local	-	-	-	0	IMyOffensiveCombatHitAndRun.cs:71 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyOffensiveCombatHitAndRun.GetPredictedLookAt	local	-	-	-	0	IMyOffensiveCombatHitAndRun.cs:51 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyOffensiveCombatHitAndRun.SetShooting	local	-	-	-	0	IMyOffensiveCombatHitAndRun.cs:39 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyOffensiveCombatHitAndRun.StateChanged	local	-	-	-	0	MyOffensiveCombatHitAndRun.cs:175 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyOffensiveCombatIntercept.OnGuidanceTypeValueChanged	local	-	-	-	0	MyOffensiveCombatIntercept.cs:130 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyOffensiveCombatStayAtRange.BlockValidation	local	-	-	-	0	IMyOffensiveCombatStayAtRange.cs:23 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyOffensiveCombatStayAtRange.ClearBlocksShootingState	local	-	-	-	0	IMyOffensiveCombatStayAtRange.cs:62 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyOffensiveCombatStayAtRange.GetFirstWeaponForwardVector	local	-	-	-	0	IMyOffensiveCombatStayAtRange.cs:71 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyOffensiveCombatStayAtRange.GetPredictedLookAt	local	-	-	-	0	IMyOffensiveCombatStayAtRange.cs:51 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyOffensiveCombatStayAtRange.SetShooting	local	-	-	-	0	IMyOffensiveCombatStayAtRange.cs:39 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyPathRecorderBlock.GetComponent	local	-	-	-	0	MyPathRecorderBlock.cs:82 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyPathRecorderComponent.AddWaypoint	local	-	-	-	0	MyPathRecorderComponent.cs:1695 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyPathRecorderComponent.DeleteAllWaypoints	local	-	-	-	0	MyPathRecorderComponent.cs:1690 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyPathRecorderComponent.GetWaypoints	local	-	-	-	0	MyPathRecorderComponent.cs:1700 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyPathRecorderComponent.IsPlayingChanged	local	-	-	-	0	MyPathRecorderComponent.cs:443 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyPathRecorderComponent.IsRecordingChanged	local	-	-	-	0	MyPathRecorderComponent.cs:445 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyPathRecorderComponent.RemoveWaypoint	local	-	-	-	0	MyPathRecorderComponent.cs:1716 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyPathRecorderComponent.ReverseOrder	local	-	-	-	0	MyPathRecorderComponent.cs:1727 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyPathfindingSessionComponent.FindAvoidPath	local	-	-	-	0	MyAutopilotPathfindingComponent.cs:100 process-wide registration or service; every node runs the mod and registers its own
Sandbox.ModAPI.IMyPistonBase.Attach	local	-	-	-	1	MyPistonBase.cs:1603 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyPistonBase.AttachedEntityChanged	local	-	-	-	0	MyPistonBase.cs:351 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyPistonBase.LimitReached	local	-	-	-	0	MyPistonBase.cs:339 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyPistonBase.NormalizedPositionChanged	local	-	-	-	0	MyPistonBase.cs:325 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyPistonTop.Base	local	-	-	-	3	MyPistonTop.cs:52 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyPistonTop.Piston	local	-	-	-	0	MyPistonTop.cs:54 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyPowerProducer.CurrentOutputRatioChanged	local	-	-	-	0	MyBatteryBlock.cs:317 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyProductionBlock.AddQueueItem	local	-	-	-	5	MyProductionBlock.cs:1066 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyProductionBlock.CanUseBlueprint	local	-	-	-	6	MyProductionBlock.cs:1061 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyProductionBlock.GetQueue	local	-	-	-	6	MyProductionBlock.cs:1076 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyProductionBlock.InputInventory	local	-	-	-	1	MyProductionBlock.cs:279 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyProductionBlock.InsertQueueItem	local	-	-	-	2	MyProductionBlock.cs:1071 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyProductionBlock.OutputInventory	local	-	-	-	4	MyProductionBlock.cs:281 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyProductionBlock.StartedProducing	local	-	-	-	1	MyProductionBlock.cs:289 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyProductionBlock.StoppedProducing	local	-	-	-	1	MyProductionBlock.cs:291 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyProgrammableBlock.HasCompileErrors	local	-	-	-	0	MyProgrammableBlock.cs:521 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyProgrammableBlock.ProgramData	local	-	-	-	3	MyProgrammableBlock.cs:486 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyProgrammableBlock.Recompile	local	-	-	-	3	MyProgrammableBlock.cs:1451 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyProgrammableBlock.Run	local	-	-	-	4	MyProgrammableBlock.cs:1456 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyProgrammableBlock.StorageData	local	-	-	-	0	MyProgrammableBlock.cs:506 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyProgrammableBlock.TryRun	local	-	-	-	0	MyProgrammableBlock.cs:1431 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyProjectileDetector.DetectorAABB	local	-	-	-	0	MySafeZone.cs:213 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyProjectileDetector.GetDetectorIntersectionWithLine	local	-	-	-	0	MySafeZone.cs:1669 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyProjectileDetector.HitEntity	local	-	-	-	0	MySafeZone.cs:211 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyProjectileDetector.IsDetectorEnabled	local	-	-	-	0	MySafeZone.cs:215 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyProjectiles.Add	local	-	-	-	1	MyProjectiles.cs:100 projectile range is capped at sync distance (cluster: Patches/MyProjectilePatch.cs:20)
Sandbox.ModAPI.IMyProjectiles.AddHitDetector	local	-	-	-	0	MyProjectiles.cs:236 projectile range is capped at sync distance (cluster: Patches/MyProjectilePatch.cs:20)
Sandbox.ModAPI.IMyProjectiles.AddOnHitInterceptor	local	-	-	-	1	MyProjectiles.cs:183 projectile range is capped at sync distance (cluster: Patches/MyProjectilePatch.cs:20)
Sandbox.ModAPI.IMyProjectiles.GetAllProjectileCount	local	-	-	-	0	MyProjectiles.cs:213 projectile range is capped at sync distance (cluster: Patches/MyProjectilePatch.cs:20)
Sandbox.ModAPI.IMyProjectiles.GetProjectile	local	-	-	-	0	MyProjectiles.cs:199 projectile range is capped at sync distance (cluster: Patches/MyProjectilePatch.cs:20)
Sandbox.ModAPI.IMyProjectiles.GetSurfaceAndMaterial	local	-	-	-	2	MyProjectiles.cs:255 projectile range is capped at sync distance (cluster: Patches/MyProjectilePatch.cs:20)
Sandbox.ModAPI.IMyProjectiles.MarkProjectileForDestroy	local	-	-	-	1	MyProjectiles.cs:205 projectile range is capped at sync distance (cluster: Patches/MyProjectilePatch.cs:20)
Sandbox.ModAPI.IMyProjectiles.OnProjectileAdded	local	-	-	-	2	MyProjectiles.cs:41 projectile range is capped at sync distance (cluster: Patches/MyProjectilePatch.cs:20)
Sandbox.ModAPI.IMyProjectiles.OnProjectileRemoving	local	-	-	-	1	MyProjectiles.cs:43 projectile range is capped at sync distance (cluster: Patches/MyProjectilePatch.cs:20)
Sandbox.ModAPI.IMyProjectiles.RemoveHitDetector	local	-	-	-	0	MyProjectiles.cs:246 projectile range is capped at sync distance (cluster: Patches/MyProjectilePatch.cs:20)
Sandbox.ModAPI.IMyProjectiles.RemoveOnHitInterceptor	local	-	-	-	1	MyProjectiles.cs:190 projectile range is capped at sync distance (cluster: Patches/MyProjectilePatch.cs:20)
Sandbox.ModAPI.IMyProjector.Build	local	-	-	-	10	MyProjectorBase.cs:2497 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyProjector.CanBuild	local	-	-	-	13	MyProjectorBase.cs:2492 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyProjector.LoadBlueprint	local	-	-	-	1	MyProjectorBase.cs:2519 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyProjector.LoadRandomBlueprint	local	-	-	-	0	MyProjectorBase.cs:2507 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyProjector.ProjectedGrid	local	-	-	-	21	MyProjectorBase.cs:792 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyProjector.SetProjectedGrid	local	-	-	-	2	MyProjectorBase.cs:2465 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyRandomCargoEntityComponent.ContainerType	local	-	-	-	0	MyRandomCargoEntityComponent.cs:16 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyRandomCargoEntityComponent.SpawnRandomCargo	local	-	-	-	0	MyRandomCargoEntityComponent.cs:25 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyReactor.PowerOutputMultiplier	local	-	-	-	2	MyReactor.cs:113 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyRemoteControl.GetFreeDestination	local	-	-	-	0	MyRemoteControl.cs:1024 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyRemoteControl.GetNearestPlayer	node-scoped	B1	no	broadcast / request	0	MyRemoteControl.cs:899 enumerates or queries this node's entities only (MyEntities); entities on other nodes and in offline partitions are missing
Sandbox.ModAPI.IMyRepairServiceComponent.RequestGrids	node-scoped	B2	no	request (WA)	0	MyRepairServiceComponent.cs:231 UNCERTAIN: repair service requests use grids that may be on another node; not traced
Sandbox.ModAPI.IMyRepairServiceComponent.RequestRepair	node-scoped	B2	no	request (WA)	0	MyRepairServiceComponent.cs:486 UNCERTAIN: repair service requests use grids that may be on another node; not traced
Sandbox.ModAPI.IMyRepairServiceComponent.RequestRepairQuote	node-scoped	B2	no	request (WA)	0	MyRepairServiceComponent.cs:335 UNCERTAIN: repair service requests use grids that may be on another node; not traced
Sandbox.ModAPI.IMyResourceStorageComponent.ChangeFilledRatio	local	-	-	-	1	MyResourceStorageComponent.cs:460 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyResourceStorageComponent.FilledRatioChanged	local	-	-	-	0	MyResourceStorageComponent.cs:125 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyRespawnComponent.CanPlayerSpawn	node-scoped	B2	no	request (WA)	0	MyRespawnComponent.cs:111 UNCERTAIN: respawn is coordinated cluster-wide (cluster: Patches/MySpaceRespawnComponentPatch.cs); this component's answers on a node were not traced
Sandbox.ModAPI.IMyRespawnComponent.GetOxygenLevel	node-scoped	B2	no	request (WA)	0	MyRespawnComponent.cs:90 UNCERTAIN: respawn is coordinated cluster-wide (cluster: Patches/MySpaceRespawnComponentPatch.cs); this component's answers on a node were not traced
Sandbox.ModAPI.IMyRespawnComponent.GetSpawnPosition	node-scoped	B2	no	request (WA)	0	MyRespawnComponent.cs:80 UNCERTAIN: respawn is coordinated cluster-wide (cluster: Patches/MySpaceRespawnComponentPatch.cs); this component's answers on a node were not traced
Sandbox.ModAPI.IMyRespawnComponent.SpawnWithoutOxygen	node-scoped	B2	no	request (WA)	0	MyRespawnComponent.cs:26 UNCERTAIN: respawn is coordinated cluster-wide (cluster: Patches/MySpaceRespawnComponentPatch.cs); this component's answers on a node were not traced
Sandbox.ModAPI.IMySalvageServiceComponent.ClearCallbacks	node-scoped	B2	no	request (WA)	0	MySalvageServiceComponent.cs:511 UNCERTAIN: salvage service requests use grids that may be on another node; not traced
Sandbox.ModAPI.IMySalvageServiceComponent.RequestAcceptOffer	node-scoped	B2	no	request (WA)	0	MySalvageServiceComponent.cs:435 UNCERTAIN: salvage service requests use grids that may be on another node; not traced
Sandbox.ModAPI.IMySalvageServiceComponent.RequestGrids	node-scoped	B2	no	request (WA)	0	MySalvageServiceComponent.cs:222 UNCERTAIN: salvage service requests use grids that may be on another node; not traced
Sandbox.ModAPI.IMySalvageServiceComponent.RequestQuote	node-scoped	B2	no	request (WA)	0	MySalvageServiceComponent.cs:326 UNCERTAIN: salvage service requests use grids that may be on another node; not traced
Sandbox.ModAPI.IMyScriptBlacklist.GetBlacklistedIngameEntries	local	-	-	-	0	MyVRageIngameScriptingAdapter.cs:128 process-wide registration or service; every node runs the mod and registers its own
Sandbox.ModAPI.IMyScriptBlacklist.GetWhitelist	local	-	-	-	0	MyVRageIngameScriptingAdapter.cs:119 process-wide registration or service; every node runs the mod and registers its own
Sandbox.ModAPI.IMyScriptBlacklist.OpenIngameBlacklistBatch	local	-	-	-	0	MyVRageIngameScriptingAdapter.cs:137 process-wide registration or service; every node runs the mod and registers its own
Sandbox.ModAPI.IMyScriptBlacklistBatch.AddMembers	local	-	-	-	0	MyVRageIngameScriptingAdapter.cs:65 process-wide registration or service; every node runs the mod and registers its own
Sandbox.ModAPI.IMyScriptBlacklistBatch.AddNamespaceOfTypes	local	-	-	-	0	MyVRageIngameScriptingAdapter.cs:28 process-wide registration or service; every node runs the mod and registers its own
Sandbox.ModAPI.IMyScriptBlacklistBatch.AddTypes	local	-	-	-	0	MyVRageIngameScriptingAdapter.cs:46 process-wide registration or service; every node runs the mod and registers its own
Sandbox.ModAPI.IMyScriptBlacklistBatch.RemoveMembers	local	-	-	-	0	MyVRageIngameScriptingAdapter.cs:75 process-wide registration or service; every node runs the mod and registers its own
Sandbox.ModAPI.IMyScriptBlacklistBatch.RemoveNamespaceOfTypes	local	-	-	-	0	MyVRageIngameScriptingAdapter.cs:37 process-wide registration or service; every node runs the mod and registers its own
Sandbox.ModAPI.IMyScriptBlacklistBatch.RemoveTypes	local	-	-	-	0	MyVRageIngameScriptingAdapter.cs:55 process-wide registration or service; every node runs the mod and registers its own
Sandbox.ModAPI.IMySearchEnemyComponent.ForceSearchRequired	local	-	-	-	0	MySearchEnemyComponent.cs:206 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMySearchEnemyComponent.FoundEnemy	local	-	-	-	2	MySearchEnemyComponent.cs:171 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMySearchEnemyComponent.SearchComplete	local	-	-	-	0	MySearchEnemyComponent.cs:208 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMySearchEnemyComponent.TargetChanged	local	-	-	-	0	MySearchEnemyComponent.cs:220 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMySearchlight.SetAimingRadiusNoCheck	local	-	-	-	0	MySearchlight.cs:1328 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMySensorBlock.FieldMax	local	-	-	-	0	MySensorBlock.cs:781 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMySensorBlock.FieldMin	local	-	-	-	0	MySensorBlock.cs:769 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMySensorBlock.StateChanged	local	-	-	-	0	MySensorBlock.cs:799 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyShipConnector.AttachFinished	local	-	-	-	1	MyShipConnector.cs:650 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyShipConnector.DetachFinished	local	-	-	-	0	MyShipConnector.cs:652 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyShipConnector.IsConnectedChanged	local	-	-	-	1	MyShipConnector.cs:648 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyShipConnector.OtherConnector	local	-	-	-	11	MyShipConnector.cs:615 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyShipController.BuildToolbar	local	-	-	-	0	MyShipController.cs:399 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyShipController.HasFirstPersonCamera	local	-	-	-	0	MyShipController.cs:915 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyShipController.IsDefault3rdView	local	-	-	-	0	MyShipController.cs:815 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyShipController.IsShooting	local	-	-	-	0	MyShipController.cs:921 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyShipController.LastPilot	local	-	-	-	0	MyShipController.cs:919 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyShipController.Pilot	local	-	-	-	16	MyShipController.cs:917 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyShipController.TargetLockToolbar	local	-	-	-	0	MyShipController.cs:401 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyShipController.Toolbar	local	-	-	-	0	MyShipController.cs:397 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyShipDrill.DrillHarvestMultiplier	local	-	-	-	1	MyShipDrill.cs:279 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyShipDrill.PowerConsumptionMultiplier	local	-	-	-	0	MyShipDrill.cs:296 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyShipWelder.IsWithinWorldLimits	local	-	-	-	4	MyShipWelder.cs:170 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyShootOrigin.GetAmmoDefinition	local	-	-	-	0	MyCubeGrid.cs:3588 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyShootOrigin.MaxShootRange	local	-	-	-	0	MyCubeGrid.cs:3604 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyShootOrigin.ShootOrigin	local	-	-	-	0	MyCubeGrid.cs:3576 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMySignalReceiverEntityComponent.AllowSignalsFrom	local	-	-	-	0	MySignalReceiverEntityComponent.cs:77 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMySignalReceiverEntityComponent.Receive	local	-	-	-	0	MySignalReceiverEntityComponent.cs:138 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMySignalReceiverEntityComponent.SignalReceived	local	-	-	-	0	MySignalReceiverEntityComponent.cs:89 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMySignalReceiverEntityComponent.TriggeredBySignal	local	-	-	-	0	MySignalReceiverEntityComponent.cs:91 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMySignalSenderEntityComponent.SendSignal	node-scoped	B2	no	request (WA)	0	MySignalSenderEntityComponent.cs:89 UNCERTAIN: signal receivers in range may be on another node; not traced
Sandbox.ModAPI.IMyStoreBlock.CreateStoreItem	local	-	-	-	1	MyStoreBlock.cs:2033 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyStoreBlock.GetStoreItemById	local	-	-	-	0	MyStoreBlock.cs:2074 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyStoreBlock.GetStoreItems	local	-	-	-	2	MyStoreBlock.cs:2066 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyStoreBlock.InsertOffer	local	-	-	-	1	MyStoreBlock.cs:1988 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyStoreBlock.InsertOrder	local	-	-	-	1	MyStoreBlock.cs:2013 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyStoreBlock.InsertStoreItem	local	-	-	-	1	MyStoreBlock.cs:2086 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyStoreBlock.RemoveStoreItem	local	-	-	-	1	MyStoreBlock.cs:2095 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyStoredPowerRatio.StoredPowerRatio	local	-	-	-	1	MyBatteryBlock.cs:301 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyStoredPowerRatio.StoredPowerRatioChanged	local	-	-	-	0	MyBatteryBlock.cs:305 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMySurvivalBuff.DoProgressStep	local	-	-	-	0	MySurvivalBuffBase.cs:134 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMySurvivalBuff.GetCurrentBuffValue	local	-	-	-	0	MySurvivalBuffBase.cs:159 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMySurvivalBuff.GetCurrentBuffValueString	local	-	-	-	0	MySurvivalBuffBase.cs:164 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMySurvivalBuff.Level	local	-	-	-	0	MySurvivalBuffBase.cs:47 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMySurvivalBuff.MaxLevel	local	-	-	-	0	MySurvivalBuffBase.cs:59 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMySurvivalBuff.NameId	local	-	-	-	0	MySurvivalBuffBase.cs:77 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMySurvivalBuff.NotificationText	local	-	-	-	0	MySurvivalBuffBase.cs:75 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMySurvivalBuff.NotificationTitle	local	-	-	-	0	MySurvivalBuffBase.cs:73 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMySurvivalBuff.ProgressionTime	local	-	-	-	0	MySurvivalBuffBase.cs:71 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMySurvivalBuff.Reset	local	-	-	-	0	MySurvivalBuffBase.cs:152 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyTargetingCapableBlock.CanActiveToolShoot	local	-	-	-	0	MyShipController.cs:1445 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyTargetingCapableBlock.GetActiveToolPosition	local	-	-	-	0	MyShipController.cs:1480 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyTargetingCapableBlock.GetWorldMatrix	local	-	-	-	0	MyShipController.cs:1440 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyTargetingCapableBlock.IsShipToolSelected	local	-	-	-	0	MyShipController.cs:1464 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyTargetingCapableBlock.IsTargetLockingEnabled	local	-	-	-	0	MyShipController.cs:1395 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyTargetingCapableBlock.SetLockedTarget	local	-	-	-	1	MyShipController.cs:1400 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyTerminalActionsHelper.GetActionWithName	local	-	-	-	1	MyTerminalControlFactoryHelper.cs:67 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyTerminalActionsHelper.GetActions	local	-	-	-	1	MyTerminalControlFactoryHelper.cs:33 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyTerminalActionsHelper.GetProperties	local	-	-	-	0	MyTerminalControlFactoryHelper.cs:105 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyTerminalActionsHelper.GetProperty	local	-	-	-	0	MyTerminalControlFactoryHelper.cs:86 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyTerminalActionsHelper.GetTerminalSystemForGrid	local	-	-	-	22	MyTerminalControlFactoryHelper.cs:122 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyTerminalActionsHelper.SearchActionsOfName	local	-	-	-	0	MyTerminalControlFactoryHelper.cs:50 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyTerminalBlock.AppendingCustomInfo	local	-	-	-	50	MyTerminalBlock.cs:519 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyTerminalBlock.ClearDetailedInfo	local	-	-	-	1	MyTerminalBlock.cs:738 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyTerminalBlock.CustomDataChanged	local	-	-	-	7	MyTerminalBlock.cs:531 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyTerminalBlock.CustomNameChanged	local	-	-	-	6	MyTerminalBlock.cs:459 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyTerminalBlock.GetDetailedInfo	local	-	-	-	4	MyTerminalBlock.cs:743 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyTerminalBlock.IsDetailedInfoDirty	local	-	-	-	0	MyTerminalBlock.cs:388 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyTerminalBlock.IsInSameLogicalGroupAs	local	-	-	-	3	MyTerminalBlock.cs:1383 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyTerminalBlock.IsSameConstructAs	local	-	-	-	8	MyTerminalBlock.cs:1388 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyTerminalBlock.OwnershipChanged	local	-	-	-	5	MyTerminalBlock.cs:471 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyTerminalBlock.PropertiesChanged	local	-	-	-	6	MyTerminalBlock.cs:483 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyTerminalBlock.RefreshCustomInfo	local	-	-	-	53	MyTerminalBlock.cs:662 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyTerminalBlock.SetDetailedInfoDirty	local	-	-	-	16	MyTerminalBlock.cs:725 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyTerminalBlock.ShowOnHUDChanged	local	-	-	-	0	MyTerminalBlock.cs:495 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyTerminalBlock.VisibilityChanged	local	-	-	-	0	MyTerminalBlock.cs:507 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyTerminalControls.AddAction	local	-	-	-	41	MyTerminalControls.cs:371 terminal UI definitions, process-wide and identical on every node; the UI itself runs on the client
Sandbox.ModAPI.IMyTerminalControls.AddControl	local	-	-	-	63	MyTerminalControls.cs:97 terminal UI definitions, process-wide and identical on every node; the UI itself runs on the client
Sandbox.ModAPI.IMyTerminalControls.CreateAction	local	-	-	-	46	MyTerminalControls.cs:395 terminal UI definitions, process-wide and identical on every node; the UI itself runs on the client
Sandbox.ModAPI.IMyTerminalControls.CreateControl	local	-	-	-	75	MyTerminalControls.cs:128 terminal UI definitions, process-wide and identical on every node; the UI itself runs on the client
Sandbox.ModAPI.IMyTerminalControls.CreateProperty	local	-	-	-	31	MyTerminalControls.cs:257 terminal UI definitions, process-wide and identical on every node; the UI itself runs on the client
Sandbox.ModAPI.IMyTerminalControls.CustomActionGetter	local	-	-	-	16	MyTerminalControls.cs:43 terminal UI definitions, process-wide and identical on every node; the UI itself runs on the client
Sandbox.ModAPI.IMyTerminalControls.CustomControlGetter	local	-	-	-	40	MyTerminalControls.cs:29 terminal UI definitions, process-wide and identical on every node; the UI itself runs on the client
Sandbox.ModAPI.IMyTerminalControls.GetActions	local	-	-	-	13	MyTerminalControls.cs:342 terminal UI definitions, process-wide and identical on every node; the UI itself runs on the client
Sandbox.ModAPI.IMyTerminalControls.GetControls	local	-	-	-	28	MyTerminalControls.cs:68 terminal UI definitions, process-wide and identical on every node; the UI itself runs on the client
Sandbox.ModAPI.IMyTerminalControls.RemoveAction	local	-	-	-	3	MyTerminalControls.cs:383 terminal UI definitions, process-wide and identical on every node; the UI itself runs on the client
Sandbox.ModAPI.IMyTerminalControls.RemoveControl	local	-	-	-	5	MyTerminalControls.cs:109 terminal UI definitions, process-wide and identical on every node; the UI itself runs on the client
Sandbox.ModAPI.IMyThrust.PowerConsumptionMultiplier	local	-	-	-	3	MyThrust.cs:338 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyThrust.ThrustChanged	local	-	-	-	0	MyThrust.cs:384 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyThrust.ThrustMultiplier	local	-	-	-	8	MyThrust.cs:318 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyThrust.ThrustOverrideChanged	local	-	-	-	0	MyThrust.cs:386 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyVendingMachine.Buy	local	-	-	-	0	MyVendingMachine.cs:237 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyVendingMachine.SelectNextItem	local	-	-	-	1	MyVendingMachine.cs:205 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyVendingMachine.SelectPreviewsItem	local	-	-	-	0	MyVendingMachine.cs:221 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyWindTurbine.Effectivity	local	-	-	-	0	MyWindTurbine.cs:104 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyWorkAreaTool.BusyStatusChanged	local	-	-	-	0	MyShipDrill.cs:319 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyWorkAreaTool.InventoryActionTriggered	local	-	-	-	0	MyShipDrill.cs:321 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.IMyWorkAreaTool.IsBusy	local	-	-	-	0	MyShipDrill.cs:253 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.BroadcastTarget.*	local	-	-	-	0	value, parser or helper; no world state
Sandbox.ModAPI.Ingame.ChargeMode.*	local	-	-	-	0	value, parser or helper; no world state
Sandbox.ModAPI.Ingame.DoorStatus.*	local	-	-	-	0	value, parser or helper; no world state
Sandbox.ModAPI.Ingame.FlightMode.*	local	-	-	-	0	value, parser or helper; no world state
Sandbox.ModAPI.Ingame.IMyAssembler.CooperativeMode	local	-	-	-	3	MyAssembler.cs:356 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyAssembler.CurrentProgress	local	-	-	-	0	MyAssembler.cs:222 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyAssembler.DisassembleEnabled	local	-	-	-	0	MyAssembler.cs:338 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyAssembler.Mode	local	-	-	-	8	MyAssembler.cs:340 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyAssembler.Repeating	local	-	-	-	3	MyAssembler.cs:368 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyAttachableTopBlock.Base	local	-	-	-	0	MyAttachableTopBlockBase.cs:24 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyAttachableTopBlock.IsAttached	local	-	-	-	2	MyAttachableTopBlockBase.cs:26 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyAttackPatternComponent.AttackPatternId	local	-	-	-	0	MyOffensiveCombatAbstractComponent.cs:173 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyAttackPatternComponent.AttackPatternName	local	-	-	-	0	MyOffensiveCombatAbstractComponent.cs:169 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyAttackPatternComponent.AttackPatternTooltip	local	-	-	-	0	MyOffensiveCombatAbstractComponent.cs:171 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyAttackPatternComponent.HasEnemy	local	-	-	-	0	MyOffensiveCombatAbstractComponent.cs:108 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyAttackPatternComponent.IsSelected	local	-	-	-	0	MyOffensiveCombatAbstractComponent.cs:148 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyAutopilotWaypoint.Matrix	local	-	-	-	0	MyAutopilotWaypoint.cs:45 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyAutopilotWaypoint.Name	local	-	-	-	0	MyAutopilotWaypoint.cs:187 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyAutopilotWaypoint.RelatedEntityId	local	-	-	-	0	MyAutopilotWaypoint.cs:149 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyAutopilotWaypoint.RelativeMatrix	local	-	-	-	0	MyAutopilotWaypoint.cs:80 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyBasicMissionBlock.SelectedMissionId	local	-	-	-	0	MyBasicMissionBlock.cs:47 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyBasicMissionComponent.DetailedInfo	local	-	-	-	0	MyBasicMissionAutopilot.cs:154 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyBasicMissionComponent.IsSelected	local	-	-	-	1	MyBasicMissionAutopilot.cs:156 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyBasicMissionComponent.MissionName	local	-	-	-	0	MyBasicMissionAutopilot.cs:187 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyBasicMissionComponent.UniqueSelectionId	local	-	-	-	0	MyBasicMissionAutopilot.cs:152 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyBasicMissionFollowHome.MaxRange	local	-	-	-	0	MyBasicMissionFollowHome.cs:216 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyBasicMissionFollowHome.MinRange	local	-	-	-	0	MyBasicMissionFollowHome.cs:201 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyBasicMissionFollowHome.Wander	local	-	-	-	0	MyBasicMissionFollowHome.cs:231 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyBasicMissionFollowPlayer.FollowDistance	local	-	-	-	0	MyBasicMissionFollowPlayer.cs:183 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyBatteryBlock.ChargeMode	local	-	-	-	8	MyBatteryBlock.cs:286 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyBatteryBlock.CurrentInput	local	-	-	-	7	MyBatteryBlock.cs:211 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyBatteryBlock.CurrentStoredPower	local	-	-	-	11	MyBatteryBlock.cs:174 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyBatteryBlock.HasCapacityRemaining	local	-	-	-	0	MyBatteryBlock.cs:155 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyBatteryBlock.IsCharging	local	-	-	-	0	MyBatteryBlock.cs:235 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyBatteryBlock.MaxInput	local	-	-	-	3	MyBatteryBlock.cs:223 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyBatteryBlock.MaxStoredPower	local	-	-	-	10	MyBatteryBlock.cs:157 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyBatteryBlock.OnlyDischarge	local	-	-	-	0	MyBatteryBlock.cs:274 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyBatteryBlock.OnlyRecharge	local	-	-	-	0	MyBatteryBlock.cs:262 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyBatteryBlock.SemiautoEnabled	local	-	-	-	0	MyBatteryBlock.cs:247 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyBeacon.HudText	local	-	-	-	8	MyBeacon.cs:173 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyBeacon.Radius	local	-	-	-	11	MyBeacon.cs:161 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyBlockGroup.GetBlocks	local	-	-	-	3	MyBlockGroup.cs:54 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyBlockGroup.GetBlocksOfType	local	-	-	-	1	MyBlockGroup.cs:66 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyBlockGroup.Name	local	-	-	-	6	MyBlockGroup.cs:19 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyBroadcastListener.IsActive	local	-	-	-	0	BroadcastListener.cs:10 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.IMyBroadcastListener.Tag	local	-	-	-	0	BroadcastListener.cs:8 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.IMyCameraBlock.AvailableScanRange	local	-	-	-	0	MyCameraBlock.cs:293 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyCameraBlock.CanScan	local	-	-	-	1	MyCameraBlock.cs:1012 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyCameraBlock.EnableRaycast	local	-	-	-	1	MyCameraBlock.cs:295 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyCameraBlock.IsActive	local	-	-	-	0	MyCameraBlock.cs:320 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyCameraBlock.Raycast	node-scoped	B1	no	to-owner	1	MyCameraBlock.cs:867 casts in this node's Havok world; a camera can scan beyond sync distance and miss grids on other nodes
Sandbox.ModAPI.Ingame.IMyCameraBlock.RaycastConeLimit	local	-	-	-	2	MyCameraBlock.cs:312 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyCameraBlock.RaycastDistanceLimit	local	-	-	-	0	MyCameraBlock.cs:314 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyCameraBlock.RaycastTimeMultiplier	local	-	-	-	0	MyCameraBlock.cs:316 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyCameraBlock.TimeUntilScan	local	-	-	-	0	MyCameraBlock.cs:1062 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyChatBroadcastControllerComponent.BroadcastTarget	local	-	-	-	0	MyChatBroadcastEntityComponent.cs:185 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyChatBroadcastControllerComponent.CustomName	local	-	-	-	0	MyChatBroadcastEntityComponent.cs:197 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyChatBroadcastControllerComponent.GetMessage	local	-	-	-	0	MyChatBroadcastEntityComponent.cs:343 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyChatBroadcastControllerComponent.MaxMessageCount	local	-	-	-	0	MyChatBroadcastEntityComponent.cs:221 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyChatBroadcastControllerComponent.SendGps	node-scoped	B5	yes	player-nodes + to-node	0	MyChatBroadcastEntityComponent.cs:383 UNCERTAIN: MyMultiplayer.RaiseEvent OnTransmitMessage to nearby players; receivers attached to another node were not traced
Sandbox.ModAPI.Ingame.IMyChatBroadcastControllerComponent.SendMessage	node-scoped	B5	yes	player-nodes + to-node	0	MyChatBroadcastEntityComponent.cs:352 UNCERTAIN: MyMultiplayer.RaiseEvent OnTransmitMessage to nearby players; receivers attached to another node were not traced
Sandbox.ModAPI.Ingame.IMyChatBroadcastControllerComponent.SendRandomMessage	node-scoped	B5	yes	player-nodes + to-node	0	MyChatBroadcastEntityComponent.cs:373 UNCERTAIN: MyMultiplayer.RaiseEvent OnTransmitMessage to nearby players; receivers attached to another node were not traced
Sandbox.ModAPI.Ingame.IMyChatBroadcastControllerComponent.SetMessage	local	-	-	-	0	MyChatBroadcastEntityComponent.cs:328 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyChatBroadcastControllerComponent.UseAntenna	local	-	-	-	0	MyChatBroadcastEntityComponent.cs:209 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyCockpit.OxygenCapacity	local	-	-	-	4	MyCockpit.cs:494 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyCockpit.OxygenFilledRatio	local	-	-	-	0	MyCockpit.cs:476 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyCollector.UseConveyorSystem	local	-	-	-	0	MyCollector.cs:130 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyConveyorSorter.AddItem	local	-	-	-	1	MyConveyorSorter.cs:828 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyConveyorSorter.DrainAll	local	-	-	-	2	MyConveyorSorter.cs:173 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyConveyorSorter.GetFilterList	local	-	-	-	1	MyConveyorSorter.cs:810 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyConveyorSorter.IsAllowed	local	-	-	-	0	MyConveyorSorter.cs:219 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyConveyorSorter.Mode	local	-	-	-	1	MyConveyorSorter.cs:207 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyConveyorSorter.RemoveItem	local	-	-	-	0	MyConveyorSorter.cs:843 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyConveyorSorter.SetFilter	local	-	-	-	1	MyConveyorSorter.cs:823 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyDefensiveCombatBlock.CustomFleeCoordinates	local	-	-	-	0	MyDefensiveCombatBlock.cs:444 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyDefensiveCombatBlock.Flee	local	-	-	-	0	MyDefensiveCombatBlock.cs:515 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyDefensiveCombatBlock.FleeBeaconEntityId	local	-	-	-	0	MyDefensiveCombatBlock.cs:388 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyDefensiveCombatBlock.FleeCoordinates	local	-	-	-	0	MyDefensiveCombatBlock.cs:404 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyDefensiveCombatBlock.FleeGps	local	-	-	-	0	MyDefensiveCombatBlock.cs:419 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyDefensiveCombatBlock.FleeTrigger	local	-	-	-	0	MyDefensiveCombatBlock.cs:376 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyDefensiveCombatBlock.IsFleeing	local	-	-	-	0	MyDefensiveCombatBlock.cs:358 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyDefensiveCombatBlock.LockTarget	local	-	-	-	0	MyDefensiveCombatBlock.cs:469 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyDefensiveCombatBlock.SearchEnemyComponent	local	-	-	-	0	MyDefensiveCombatBlock.cs:481 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyDefensiveCombatBlock.WaypointZoneSize	local	-	-	-	0	MyDefensiveCombatBlock.cs:406 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyDestroyBlockComponent.Detach	local	-	-	-	0	MyDestroyBlockComponent.cs:122 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyDoor.CloseDoor	local	-	-	-	6	MyAdvancedDoor.cs:229 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyDoor.Open	local	-	-	-	0	IMyDoor.cs:15 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyDoor.OpenDoor	local	-	-	-	6	MyAdvancedDoor.cs:217 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyDoor.OpenRatio	local	-	-	-	3	MyAdvancedDoor.cs:168 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyDoor.Status	local	-	-	-	8	MyAdvancedDoor.cs:101 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyDoor.ToggleDoor	local	-	-	-	2	MyAdvancedDoor.cs:241 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyEmpEntityComponent.Countdown	local	-	-	-	0	MyEmpEntityComponent.cs:127 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyEmpEntityComponent.Discharge	local	-	-	-	0	MyEmpEntityComponent.cs:405 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyEmpEntityComponent.IsCountingDown	local	-	-	-	0	MyEmpEntityComponent.cs:143 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyEmpEntityComponent.StartCountdown	local	-	-	-	0	MyEmpEntityComponent.cs:395 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyEmpEntityComponent.StopCountdown	local	-	-	-	0	MyEmpEntityComponent.cs:400 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyEventControllerBlock.IsAndModeEnabled	local	-	-	-	4	MyEventControllerBlock.cs:202 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyEventControllerBlock.IsLowerOrEqualCondition	local	-	-	-	5	MyEventControllerBlock.cs:190 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyEventControllerBlock.Threshold	local	-	-	-	4	MyEventControllerBlock.cs:178 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyFarmPlotLogic.AmountOfSeedsRequired	local	-	-	-	1	MyFarmPlotLogic.cs:383 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyFarmPlotLogic.GetDetailedInfoWithoutRequiredInput	local	-	-	-	3	MyFarmPlotLogic.cs:885 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyFarmPlotLogic.IsAlive	local	-	-	-	4	MyFarmPlotLogic.cs:335 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyFarmPlotLogic.IsHarvestable	local	-	-	-	2	MyFarmPlotLogic.cs:341 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyFarmPlotLogic.IsPlantFullyGrown	local	-	-	-	3	MyFarmPlotLogic.cs:337 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyFarmPlotLogic.IsPlantPlanted	local	-	-	-	4	MyFarmPlotLogic.cs:339 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyFarmPlotLogic.OutputItem	local	-	-	-	3	MyFarmPlotLogic.cs:357 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyFarmPlotLogic.OutputItemAmount	local	-	-	-	2	MyFarmPlotLogic.cs:371 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyFlightMovementBlock.AlignToPGravity	local	-	-	-	0	MyFlightMovementBlock.cs:306 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyFlightMovementBlock.CollisionAvoidance	local	-	-	-	0	MyFlightMovementBlock.cs:230 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyFlightMovementBlock.CurrentWaypoint	local	-	-	-	0	MyFlightMovementBlock.cs:325 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyFlightMovementBlock.FlightMode	local	-	-	-	0	MyFlightMovementBlock.cs:415 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyFlightMovementBlock.GetWaypoints	local	-	-	-	0	MyFlightMovementBlock.cs:1585 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyFlightMovementBlock.IsAutoPilotEnabled	local	-	-	-	2	MyFlightMovementBlock.cs:337 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyFlightMovementBlock.LookAtPosition	local	-	-	-	1	MyFlightMovementBlock.cs:349 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyFlightMovementBlock.MinimalAltitude	local	-	-	-	0	MyFlightMovementBlock.cs:268 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyFlightMovementBlock.PrecisionMode	local	-	-	-	0	MyFlightMovementBlock.cs:287 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyFlightMovementBlock.SpeedLimit	local	-	-	-	0	MyFlightMovementBlock.cs:249 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyFunctionalBlock.Enabled	local	-	-	-	74	MyFunctionalBlock.cs:151 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyFunctionalBlock.RequestEnable	local	-	-	-	0	MyFunctionalBlock.cs:776 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyGasGenerator.AutoRefill	local	-	-	-	3	MyGasGenerator.cs:170 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyGasGenerator.IsProducing	local	-	-	-	1	MyGasGenerator.cs:194 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyGasGenerator.UseConveyorSystem	local	-	-	-	0	MyGasGenerator.cs:146 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyGasTank.AutoRefillBottles	local	-	-	-	3	MyGasTank.cs:217 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyGasTank.Capacity	local	-	-	-	9	MyGasTank.cs:229 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyGasTank.FilledRatio	local	-	-	-	14	MyGasTank.cs:233 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyGasTank.RefillBottles	local	-	-	-	0	MyGasTank.cs:318 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyGasTank.Stockpile	local	-	-	-	4	MyGasTank.cs:205 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyGridProgramRuntimeInfo.CurrentCallChainDepth	local	-	-	-	0	MyProgrammableBlock.cs:142 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.IMyGridProgramRuntimeInfo.CurrentInstructionCount	local	-	-	-	0	MyProgrammableBlock.cs:138 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.IMyGridProgramRuntimeInfo.LastRunTimeMs	local	-	-	-	1	MyProgrammableBlock.cs:134 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.IMyGridProgramRuntimeInfo.LifetimeTicks	local	-	-	-	0	MyProgrammableBlock.cs:160 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.IMyGridProgramRuntimeInfo.MaxCallChainDepth	local	-	-	-	0	MyProgrammableBlock.cs:140 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.IMyGridProgramRuntimeInfo.MaxInstructionCount	local	-	-	-	0	MyProgrammableBlock.cs:136 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.IMyGridProgramRuntimeInfo.TimeSinceLastRun	local	-	-	-	0	MyProgrammableBlock.cs:132 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.IMyGridProgramRuntimeInfo.UpdateFrequency	local	-	-	-	1	MyProgrammableBlock.cs:144 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.IMyGridProgramWorldInfo.InventoryMultiplier	local	-	-	-	0	MyProgrammableBlock.cs:105 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.IMyGridProgramWorldInfo.LargeShipMaxAngularSpeed	local	-	-	-	0	MyProgrammableBlock.cs:109 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.IMyGridProgramWorldInfo.LargeShipMaxSpeed	local	-	-	-	0	MyProgrammableBlock.cs:107 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.IMyGridProgramWorldInfo.PressurizationEnabled	local	-	-	-	0	MyProgrammableBlock.cs:93 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.IMyGridProgramWorldInfo.SmallShipMaxAngularSpeed	local	-	-	-	0	MyProgrammableBlock.cs:113 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.IMyGridProgramWorldInfo.SmallShipMaxSpeed	local	-	-	-	0	MyProgrammableBlock.cs:111 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.IMyGridTerminalSystem.CanAccess	local	-	-	-	0	MyProgrammableBlock.cs:252 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyGridTerminalSystem.GetBlockGroupWithName	local	-	-	-	0	MyProgrammableBlock.cs:242 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyGridTerminalSystem.GetBlockGroups	local	-	-	-	3	MyProgrammableBlock.cs:217 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyGridTerminalSystem.GetBlockWithId	local	-	-	-	0	MyProgrammableBlock.cs:247 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyGridTerminalSystem.GetBlockWithName	local	-	-	-	0	MyProgrammableBlock.cs:237 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyGridTerminalSystem.GetBlocks	local	-	-	-	0	MyProgrammableBlock.cs:212 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyGridTerminalSystem.GetBlocksOfType	local	-	-	-	12	MyProgrammableBlock.cs:222 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyGridTerminalSystem.SearchBlocksOfName	local	-	-	-	0	MyProgrammableBlock.cs:232 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyGyro.GyroOverride	local	-	-	-	6	MyGyro.cs:127 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyGyro.GyroPower	local	-	-	-	7	MyGyro.cs:111 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyGyro.Pitch	local	-	-	-	6	MyGyro.cs:158 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyGyro.Roll	local	-	-	-	6	MyGyro.cs:175 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyGyro.Yaw	local	-	-	-	6	MyGyro.cs:141 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyIntergridCommunicationSystem.DisableBroadcastListener	replicated	-	-	-	0	MyIntergridCommunicationContext.cs:276 IGC broadcast and unicast cross nodes through the cluster (cluster: Patches/MyIGCSystemSessionComponentPatch.cs, Cluster/Patches/MyIntergridCommunicationContextPatch.cs:9)
Sandbox.ModAPI.Ingame.IMyIntergridCommunicationSystem.GetBroadcastListeners	replicated	-	-	-	0	MyIntergridCommunicationContext.cs:288 IGC broadcast and unicast cross nodes through the cluster (cluster: Patches/MyIGCSystemSessionComponentPatch.cs, Cluster/Patches/MyIntergridCommunicationContextPatch.cs:9)
Sandbox.ModAPI.Ingame.IMyIntergridCommunicationSystem.IsEndpointReachable	node-scoped	B2	no	request (WA)	0	MyIntergridCommunicationContext.cs:207 UNCERTAIN: reachability of an endpoint on another node not traced
Sandbox.ModAPI.Ingame.IMyIntergridCommunicationSystem.Me	replicated	-	-	-	0	MyIntergridCommunicationContext.cs:55 IGC broadcast and unicast cross nodes through the cluster (cluster: Patches/MyIGCSystemSessionComponentPatch.cs, Cluster/Patches/MyIntergridCommunicationContextPatch.cs:9)
Sandbox.ModAPI.Ingame.IMyIntergridCommunicationSystem.RegisterBroadcastListener	replicated	-	-	-	0	MyIntergridCommunicationContext.cs:248 IGC broadcast and unicast cross nodes through the cluster (cluster: Patches/MyIGCSystemSessionComponentPatch.cs, Cluster/Patches/MyIntergridCommunicationContextPatch.cs:9)
Sandbox.ModAPI.Ingame.IMyIntergridCommunicationSystem.SendBroadcastMessage	replicated	-	-	-	0	MyIntergridCommunicationContext.cs:226 IGC broadcast and unicast cross nodes through the cluster (cluster: Patches/MyIGCSystemSessionComponentPatch.cs, Cluster/Patches/MyIntergridCommunicationContextPatch.cs:9)
Sandbox.ModAPI.Ingame.IMyIntergridCommunicationSystem.SendUnicastMessage	replicated	-	-	-	0	MyIntergridCommunicationContext.cs:232 IGC broadcast and unicast cross nodes through the cluster (cluster: Patches/MyIGCSystemSessionComponentPatch.cs, Cluster/Patches/MyIntergridCommunicationContextPatch.cs:9)
Sandbox.ModAPI.Ingame.IMyIntergridCommunicationSystem.UnicastListener	replicated	-	-	-	0	MyIntergridCommunicationContext.cs:57 IGC broadcast and unicast cross nodes through the cluster (cluster: Patches/MyIGCSystemSessionComponentPatch.cs, Cluster/Patches/MyIntergridCommunicationContextPatch.cs:9)
Sandbox.ModAPI.Ingame.IMyJumpDrive.CurrentStoredPower	local	-	-	-	1	MyJumpDrive.cs:350 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyJumpDrive.JumpDistanceMeters	local	-	-	-	1	MyJumpDrive.cs:369 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyJumpDrive.JumpDistanceRatio	local	-	-	-	0	MyJumpDrive.cs:354 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyJumpDrive.MaxJumpDistanceMeters	local	-	-	-	0	MyJumpDrive.cs:392 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyJumpDrive.MaxStoredPower	local	-	-	-	8	MyJumpDrive.cs:352 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyJumpDrive.MinJumpDistanceMeters	local	-	-	-	0	MyJumpDrive.cs:394 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyJumpDrive.Recharge	local	-	-	-	1	MyJumpDrive.cs:424 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyJumpDrive.Status	local	-	-	-	5	MyJumpDrive.cs:408 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyLargeTurretBase.AIEnabled	local	-	-	-	2	MyLargeTurretBase.cs:1277 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyLargeTurretBase.Azimuth	local	-	-	-	1	MyLargeTurretBase.cs:1253 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyLargeTurretBase.CanControl	local	-	-	-	0	MyLargeTurretBase.cs:1281 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyLargeTurretBase.Elevation	local	-	-	-	1	MyLargeTurretBase.cs:1241 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyLargeTurretBase.EnableIdleRotation	local	-	-	-	2	MyLargeTurretBase.cs:1265 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyLargeTurretBase.GetTargetedEntity	local	-	-	-	2	MyLargeTurretBase.cs:3791 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyLargeTurretBase.GetTargetingGroup	local	-	-	-	0	MyLargeTurretBase.cs:3854 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyLargeTurretBase.GetTargetingGroups	local	-	-	-	0	MyLargeTurretBase.cs:3830 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyLargeTurretBase.HasTarget	local	-	-	-	4	MyLargeTurretBase.cs:1233 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyLargeTurretBase.IsAimed	local	-	-	-	0	MyLargeTurretBase.cs:1231 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyLargeTurretBase.IsUnderControl	local	-	-	-	1	MyLargeTurretBase.cs:1279 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyLargeTurretBase.Range	local	-	-	-	4	MyLargeTurretBase.cs:1283 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyLargeTurretBase.ResetTargetingToDefault	local	-	-	-	3	MyLargeTurretBase.cs:3786 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyLargeTurretBase.SetManualAzimuthAndElevation	local	-	-	-	3	MyLargeTurretBase.cs:3810 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyLargeTurretBase.SetTarget	local	-	-	-	1	MyLargeTurretBase.cs:3757 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyLargeTurretBase.SetTargetingGroup	local	-	-	-	0	MyLargeTurretBase.cs:3864 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyLargeTurretBase.SyncAzimuth	local	-	-	-	1	MyLargeTurretBase.cs:3800 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyLargeTurretBase.SyncElevation	local	-	-	-	1	MyLargeTurretBase.cs:3805 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyLargeTurretBase.SyncEnableIdleRotation	local	-	-	-	0	MyLargeTurretBase.cs:3796 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyLargeTurretBase.TargetCharacters	local	-	-	-	1	MyLargeTurretBase.cs:937 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyLargeTurretBase.TargetEnemies	local	-	-	-	1	MyLargeTurretBase.cs:994 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyLargeTurretBase.TargetLargeGrids	local	-	-	-	1	MyLargeTurretBase.cs:918 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyLargeTurretBase.TargetMeteors	local	-	-	-	1	MyLargeTurretBase.cs:861 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyLargeTurretBase.TargetMissiles	local	-	-	-	1	MyLargeTurretBase.cs:880 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyLargeTurretBase.TargetNeutrals	local	-	-	-	1	MyLargeTurretBase.cs:975 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyLargeTurretBase.TargetSmallGrids	local	-	-	-	1	MyLargeTurretBase.cs:899 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyLargeTurretBase.TargetStations	local	-	-	-	1	MyLargeTurretBase.cs:956 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyLargeTurretBase.TrackTarget	local	-	-	-	0	MyLargeTurretBase.cs:3730 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyLaserAntenna.Connect	node-scoped	B3	no	owner-of / to-owner	0	MyLaserAntenna.cs:2083 the partner antenna is usually far away and may be on another node; UNCERTAIN how MyLaserAntennaPatch resolves it
Sandbox.ModAPI.Ingame.IMyLaserAntenna.IsOutsideLimits	local	-	-	-	0	MyLaserAntenna.cs:369 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyLaserAntenna.IsPermanent	local	-	-	-	0	MyLaserAntenna.cs:355 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyLaserAntenna.Range	local	-	-	-	2	MyLaserAntenna.cs:394 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyLaserAntenna.RequireLoS	local	-	-	-	0	MyLaserAntenna.cs:367 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyLaserAntenna.SetTargetCoords	node-scoped	B3	no	owner-of / to-owner	0	MyLaserAntenna.cs:2075 the partner antenna is usually far away and may be on another node; UNCERTAIN how MyLaserAntennaPatch resolves it
Sandbox.ModAPI.Ingame.IMyLaserAntenna.Status	local	-	-	-	5	MyLaserAntenna.cs:371 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyLaserAntenna.TargetCoords	node-scoped	B3	no	owner-of / to-owner	0	MyLaserAntenna.cs:353 the partner antenna is usually far away and may be on another node; UNCERTAIN how MyLaserAntennaPatch resolves it
Sandbox.ModAPI.Ingame.IMyLightingBlock.BlinkIntervalSeconds	local	-	-	-	6	MyLightingBlock.cs:338 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyLightingBlock.BlinkLenght	local	-	-	-	0	MyLightingBlock.cs:281 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyLightingBlock.BlinkLength	local	-	-	-	6	MyLightingBlock.cs:355 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyLightingBlock.BlinkOffset	local	-	-	-	5	MyLightingBlock.cs:372 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyLightingBlock.Color	local	-	-	-	6	MyLightingBlock.cs:389 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyLightingBlock.Falloff	local	-	-	-	1	MyLightingBlock.cs:321 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyLightingBlock.Intensity	local	-	-	-	4	MyLightingBlock.cs:304 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyLightingBlock.Radius	local	-	-	-	2	MyLightingBlock.cs:283 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyLightingBlock.ReflectorRadius	local	-	-	-	0	MyLightingBlock.cs:279 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyLightingComponent.BlinkIntervalSeconds	local	-	-	-	0	MyLightingComponent.cs:234 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyLightingComponent.BlinkLength	local	-	-	-	0	MyLightingComponent.cs:210 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyLightingComponent.BlinkOffset	local	-	-	-	0	MyLightingComponent.cs:222 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyLightingComponent.Color	local	-	-	-	1	MyLightingComponent.cs:174 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyLightingComponent.Falloff	local	-	-	-	0	MyLightingComponent.cs:246 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyLightingComponent.Intensity	local	-	-	-	1	MyLightingComponent.cs:258 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyLightingComponent.Radius	local	-	-	-	1	MyLightingComponent.cs:186 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyMechanicalConnectionBlock.Attach	local	-	-	-	3	MyMechanicalConnectionBlockBase.cs:1268 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyMechanicalConnectionBlock.Detach	local	-	-	-	5	MyMechanicalConnectionBlockBase.cs:1279 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyMechanicalConnectionBlock.IsAttached	local	-	-	-	6	MyMechanicalConnectionBlockBase.cs:323 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyMechanicalConnectionBlock.PendingAttachment	local	-	-	-	2	MyMechanicalConnectionBlockBase.cs:325 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyMechanicalConnectionBlock.Top	local	-	-	-	0	MyMechanicalConnectionBlockBase.cs:339 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyMechanicalConnectionBlock.TopGrid	local	-	-	-	1	MyMechanicalConnectionBlockBase.cs:337 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyMessageProvider.AcceptMessage	local	-	-	-	0	BroadcastListener.cs:27 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.IMyMessageProvider.DisableMessageCallback	local	-	-	-	0	MyMessageListener.cs:101 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.IMyMessageProvider.HasPendingMessage	local	-	-	-	0	MyMessageListener.cs:28 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.IMyMessageProvider.MaxWaitingMessages	local	-	-	-	0	MyMessageListener.cs:26 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.IMyMessageProvider.SetMessageCallback	local	-	-	-	0	BroadcastListener.cs:18 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.IMyMotorStator.Angle	local	-	-	-	6	MyMotorStator.cs:336 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyMotorStator.BrakingTorque	local	-	-	-	3	MyMotorStator.cs:351 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyMotorStator.Displacement	local	-	-	-	4	MyMotorStator.cs:454 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyMotorStator.LowerLimitDeg	local	-	-	-	3	MyMotorStator.cs:406 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyMotorStator.LowerLimitRad	local	-	-	-	4	MyMotorStator.cs:390 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyMotorStator.RotateToAngle	local	-	-	-	0	MyMotorStator.cs:1766 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyMotorStator.RotorLock	local	-	-	-	4	MyMotorStator.cs:482 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyMotorStator.TargetVelocityRPM	local	-	-	-	4	MyMotorStator.cs:377 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyMotorStator.TargetVelocityRad	local	-	-	-	4	MyMotorStator.cs:364 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyMotorStator.Torque	local	-	-	-	3	MyMotorStator.cs:338 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyMotorStator.UpperLimitDeg	local	-	-	-	3	MyMotorStator.cs:438 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyMotorStator.UpperLimitRad	local	-	-	-	4	MyMotorStator.cs:422 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyMotorSuspension.AirShockEnabled	local	-	-	-	0	MyMotorSuspension.cs:588 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyMotorSuspension.Brake	local	-	-	-	0	MyMotorSuspension.cs:746 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyMotorSuspension.Damping	local	-	-	-	0	MyMotorSuspension.cs:771 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyMotorSuspension.Friction	local	-	-	-	0	MyMotorSuspension.cs:696 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyMotorSuspension.Height	local	-	-	-	0	MyMotorSuspension.cs:720 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyMotorSuspension.InvertPropulsion	local	-	-	-	0	MyMotorSuspension.cs:521 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyMotorSuspension.InvertSteer	local	-	-	-	0	MyMotorSuspension.cs:509 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyMotorSuspension.IsParkingEnabled	local	-	-	-	0	MyMotorSuspension.cs:758 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyMotorSuspension.MaxSteerAngle	local	-	-	-	0	MyMotorSuspension.cs:734 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyMotorSuspension.Power	local	-	-	-	0	MyMotorSuspension.cs:708 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyMotorSuspension.Propulsion	local	-	-	-	0	MyMotorSuspension.cs:576 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyMotorSuspension.PropulsionOverride	local	-	-	-	0	MyMotorSuspension.cs:624 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyMotorSuspension.SteerAngle	local	-	-	-	0	MyMotorSuspension.cs:732 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyMotorSuspension.SteerReturnSpeed	local	-	-	-	0	MyMotorSuspension.cs:777 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyMotorSuspension.SteerSpeed	local	-	-	-	0	MyMotorSuspension.cs:774 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyMotorSuspension.Steering	local	-	-	-	0	MyMotorSuspension.cs:564 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyMotorSuspension.SteeringOverride	local	-	-	-	0	MyMotorSuspension.cs:636 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyMotorSuspension.Strength	local	-	-	-	0	MyMotorSuspension.cs:684 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyMotorSuspension.SuspensionTravel	local	-	-	-	0	MyMotorSuspension.cs:780 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyOffensiveCombatBlock.GetAttackPatternIds	local	-	-	-	0	MyOffensiveCombatBlock.cs:807 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyOffensiveCombatBlock.LastScan	local	-	-	-	0	MyOffensiveCombatBlock.cs:182 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyOffensiveCombatBlock.SearchEnemyComponent	local	-	-	-	0	MyOffensiveCombatBlock.cs:196 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyOffensiveCombatBlock.SelectedAttackPattern	local	-	-	-	0	MyOffensiveCombatBlock.cs:200 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyOffensiveCombatBlock.TargetPriority	local	-	-	-	1	MyOffensiveCombatBlock.cs:224 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyOffensiveCombatBlock.TryGetSelectedAttackPattern	local	-	-	-	0	MyOffensiveCombatBlock.cs:504 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyOffensiveCombatBlock.UpdateTargetInterval	local	-	-	-	0	MyOffensiveCombatBlock.cs:212 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyOffensiveCombatCircleOrbit.CircleDistance	local	-	-	-	0	MyOffensiveCombatCircleOrbit.cs:86 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyOffensiveCombatCircleOrbit.CircleInPGravity	local	-	-	-	0	MyOffensiveCombatCircleOrbit.cs:98 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyOffensiveCombatCircleOrbit.Facing	local	-	-	-	0	IMyOffensiveCombatCircleOrbit.cs:24 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyOffensiveCombatCircleOrbit.IsOrbiting	local	-	-	-	0	MyOffensiveCombatCircleOrbit.cs:110 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyOffensiveCombatHitAndRun.BreakOffDistance	local	-	-	-	0	MyOffensiveCombatHitAndRun.cs:163 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyOffensiveCombatHitAndRun.GetSelectedWeapons	local	-	-	-	0	IMyOffensiveCombatHitAndRun.cs:45 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyOffensiveCombatHitAndRun.IsMovingAwayFromTarget	local	-	-	-	0	MyOffensiveCombatHitAndRun.cs:113 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyOffensiveCombatHitAndRun.IsMovingToTarget	local	-	-	-	0	MyOffensiveCombatHitAndRun.cs:125 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyOffensiveCombatHitAndRun.IsWeaponSelected	local	-	-	-	0	IMyOffensiveCombatHitAndRun.cs:60 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyOffensiveCombatHitAndRun.RetreatAngle	local	-	-	-	0	MyOffensiveCombatHitAndRun.cs:151 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyOffensiveCombatHitAndRun.RetreatDistance	local	-	-	-	0	MyOffensiveCombatHitAndRun.cs:139 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyOffensiveCombatHitAndRun.RetreatTimeout	local	-	-	-	0	MyOffensiveCombatHitAndRun.cs:127 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyOffensiveCombatHitAndRun.SetSelectedWeapons	local	-	-	-	0	IMyOffensiveCombatHitAndRun.cs:53 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyOffensiveCombatIntercept.GuidanceType	local	-	-	-	0	MyOffensiveCombatIntercept.cs:114 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyOffensiveCombatIntercept.ShouldOverrideCollisionAvoidance	local	-	-	-	0	MyOffensiveCombatIntercept.cs:102 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyOffensiveCombatStayAtRange.EvasiveManeuverDistance	local	-	-	-	0	MyOffensiveCombatStayAtRange.cs:103 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyOffensiveCombatStayAtRange.EvasiveManeuvers	local	-	-	-	0	MyOffensiveCombatStayAtRange.cs:105 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyOffensiveCombatStayAtRange.FacingPriority	local	-	-	-	0	MyOffensiveCombatStayAtRange.cs:117 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyOffensiveCombatStayAtRange.MaximalDistance	local	-	-	-	0	MyOffensiveCombatStayAtRange.cs:119 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyOffensiveCombatStayAtRange.MinimalDistance	local	-	-	-	0	MyOffensiveCombatStayAtRange.cs:131 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyOreDetector.BroadcastUsingAntennas	local	-	-	-	3	MyOreDetector.cs:113 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyOreDetector.Range	local	-	-	-	2	MyOreDetector.cs:125 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyPathRecorderComponent.BeaconDisplayText	local	-	-	-	0	MyPathRecorderComponent.cs:326 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyPathRecorderComponent.BeaconEntityId	local	-	-	-	0	MyPathRecorderComponent.cs:431 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyPathRecorderComponent.BeaconWorldPosition	local	-	-	-	0	MyPathRecorderComponent.cs:314 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyPathRecorderComponent.IsPlaying	local	-	-	-	0	MyPathRecorderComponent.cs:310 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyPathRecorderComponent.IsRecording	local	-	-	-	0	MyPathRecorderComponent.cs:312 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyPathRecorderComponent.MinimalDistance	local	-	-	-	0	MyPathRecorderComponent.cs:386 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyPathRecorderComponent.Play	local	-	-	-	0	MyPathRecorderComponent.cs:1670 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyPathRecorderComponent.Record	local	-	-	-	0	MyPathRecorderComponent.cs:1680 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyPathRecorderComponent.RecordInterval	local	-	-	-	0	MyPathRecorderComponent.cs:374 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyPathRecorderComponent.RemoveBeacon	local	-	-	-	0	MyPathRecorderComponent.cs:1665 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyPathRecorderComponent.RepeatPath	local	-	-	-	0	MyPathRecorderComponent.cs:338 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyPathRecorderComponent.ShowPath	local	-	-	-	0	MyPathRecorderComponent.cs:350 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyPathRecorderComponent.ShowSelectedPoints	local	-	-	-	0	MyPathRecorderComponent.cs:362 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyPathRecorderComponent.StopPlay	local	-	-	-	0	MyPathRecorderComponent.cs:1675 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyPathRecorderComponent.StopRecord	local	-	-	-	0	MyPathRecorderComponent.cs:1685 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyPistonBase.CurrentPosition	local	-	-	-	6	MyPistonBase.cs:229 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyPistonBase.Extend	local	-	-	-	0	MyPistonBase.cs:1608 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyPistonBase.HighestPosition	local	-	-	-	1	MyPistonBase.cs:323 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyPistonBase.LowestPosition	local	-	-	-	1	MyPistonBase.cs:321 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyPistonBase.MaxLimit	local	-	-	-	3	MyPistonBase.cs:305 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyPistonBase.MaxVelocity	local	-	-	-	1	MyPistonBase.cs:287 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyPistonBase.MinLimit	local	-	-	-	3	MyPistonBase.cs:289 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyPistonBase.MoveToPosition	local	-	-	-	0	MyPistonBase.cs:1632 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyPistonBase.NormalizedPosition	local	-	-	-	0	MyPistonBase.cs:259 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyPistonBase.Retract	local	-	-	-	0	MyPistonBase.cs:1616 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyPistonBase.Reverse	local	-	-	-	1	MyPistonBase.cs:1624 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyPistonBase.Status	local	-	-	-	1	MyPistonBase.cs:231 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyPistonBase.Velocity	local	-	-	-	4	MyPistonBase.cs:271 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyPowerProducer.CurrentOutput	local	-	-	-	13	MyBatteryBlock.cs:187 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyPowerProducer.CurrentOutputRatio	local	-	-	-	0	MyBatteryBlock.cs:303 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyPowerProducer.MaxOutput	local	-	-	-	12	MyBatteryBlock.cs:199 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyProductionBlock.AddQueueItem	local	-	-	-	4	MyProductionBlock.cs:1066 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyProductionBlock.CanUseBlueprint	local	-	-	-	4	MyProductionBlock.cs:1061 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyProductionBlock.ClearQueue	local	-	-	-	1	MyProductionBlock.cs:1152 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyProductionBlock.GetQueue	local	-	-	-	3	MyProductionBlock.cs:1076 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyProductionBlock.InputInventory	local	-	-	-	1	MyProductionBlock.cs:279 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyProductionBlock.InsertQueueItem	local	-	-	-	0	MyProductionBlock.cs:1071 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyProductionBlock.IsProducing	local	-	-	-	9	MyProductionBlock.cs:219 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyProductionBlock.IsQueueEmpty	local	-	-	-	7	MyProductionBlock.cs:213 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyProductionBlock.MoveQueueItemRequest	local	-	-	-	0	MyProductionBlock.cs:511 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyProductionBlock.NextItemId	local	-	-	-	0	MyProductionBlock.cs:243 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyProductionBlock.OutputInventory	local	-	-	-	1	MyProductionBlock.cs:281 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyProductionBlock.RemoveQueueItem	local	-	-	-	1	MyProductionBlock.cs:1137 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyProductionBlock.UseConveyorSystem	local	-	-	-	2	MyProductionBlock.cs:245 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyProgrammableBlock.IsRunning	local	-	-	-	1	MyProgrammableBlock.cs:484 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyProgrammableBlock.TerminalRunArgument	local	-	-	-	1	MyProgrammableBlock.cs:472 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyProgrammableBlock.TryRun	local	-	-	-	0	MyProgrammableBlock.cs:1431 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyProjector.BuildableBlocksCount	local	-	-	-	5	MyProjectorBase.cs:844 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyProjector.IsProjecting	local	-	-	-	13	MyProjectorBase.cs:836 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyProjector.ProjectionOffset	local	-	-	-	3	MyProjectorBase.cs:794 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyProjector.ProjectionOffsetX	local	-	-	-	0	MyProjectorBase.cs:819 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyProjector.ProjectionOffsetY	local	-	-	-	0	MyProjectorBase.cs:822 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyProjector.ProjectionOffsetZ	local	-	-	-	0	MyProjectorBase.cs:825 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyProjector.ProjectionRotX	local	-	-	-	0	MyProjectorBase.cs:828 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyProjector.ProjectionRotY	local	-	-	-	0	MyProjectorBase.cs:831 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyProjector.ProjectionRotZ	local	-	-	-	0	MyProjectorBase.cs:834 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyProjector.ProjectionRotation	local	-	-	-	3	MyProjectorBase.cs:806 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyProjector.RemainingArmorBlocks	local	-	-	-	0	MyProjectorBase.cs:842 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyProjector.RemainingBlocks	local	-	-	-	8	MyProjectorBase.cs:838 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyProjector.RemainingBlocksPerType	local	-	-	-	3	MyProjectorBase.cs:846 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyProjector.ShowOnlyBuildable	local	-	-	-	2	MyProjectorBase.cs:859 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyProjector.TotalBlocks	local	-	-	-	7	MyProjectorBase.cs:840 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyProjector.UpdateOffsetAndRotation	local	-	-	-	4	MyProjectorBase.cs:2502 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyRadioAntenna.EnableBroadcasting	local	-	-	-	6	MyRadioAntenna.cs:224 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyRadioAntenna.HudText	local	-	-	-	8	MyRadioAntenna.cs:210 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyRadioAntenna.IsBroadcasting	local	-	-	-	9	MyRadioAntenna.cs:222 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyRadioAntenna.Radius	local	-	-	-	15	MyRadioAntenna.cs:198 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyRadioAntenna.ShowShipName	local	-	-	-	1	MyRadioAntenna.cs:179 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyReactor.UseConveyorSystem	local	-	-	-	1	MyReactor.cs:101 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyRemoteControl.AddWaypoint	local	-	-	-	3	MyRemoteControl.cs:1042 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyRemoteControl.ClearWaypoints	local	-	-	-	3	MyRemoteControl.cs:915 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyRemoteControl.CurrentWaypoint	local	-	-	-	0	MyRemoteControl.cs:332 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyRemoteControl.Direction	local	-	-	-	0	MyRemoteControl.cs:320 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyRemoteControl.FlightMode	local	-	-	-	3	MyRemoteControl.cs:308 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyRemoteControl.GetNearestPlayer	node-scoped	B1	no	broadcast / request	0	MyRemoteControl.cs:899 enumerates or queries this node's entities only (MyEntities); entities on other nodes and in offline partitions are missing
Sandbox.ModAPI.Ingame.IMyRemoteControl.GetWaypointInfo	local	-	-	-	0	MyRemoteControl.cs:1029 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyRemoteControl.IsAutoPilotEnabled	local	-	-	-	3	MyRemoteControl.cs:270 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyRemoteControl.SetAutoPilotEnabled	local	-	-	-	4	MyRemoteControl.cs:884 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyRemoteControl.SetCollisionAvoidance	local	-	-	-	2	MyRemoteControl.cs:889 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyRemoteControl.SetDockingMode	local	-	-	-	2	MyRemoteControl.cs:894 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyRemoteControl.SpeedLimit	local	-	-	-	6	MyRemoteControl.cs:296 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyRemoteControl.WaitForFreeWay	local	-	-	-	1	MyRemoteControl.cs:344 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyResourceStorageComponent.FilledRatio	local	-	-	-	3	MyResourceStorageComponent.cs:108 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyResourceStorageComponent.IsResourceStorage	local	-	-	-	0	MyResourceStorageComponent.cs:315 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyResourceStorageComponent.ResourceCapacity	local	-	-	-	1	MyResourceStorageComponent.cs:131 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMySearchEnemyComponent.FoundEnemyId	local	-	-	-	0	MySearchEnemyComponent.cs:173 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMySearchEnemyComponent.SubsystemsToDestroy	local	-	-	-	1	MySearchEnemyComponent.cs:187 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMySearchEnemyComponent.TargetingLockOptions	local	-	-	-	1	MySearchEnemyComponent.cs:175 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMySearchlight.AimingRadius	local	-	-	-	0	MySearchlight.cs:527 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMySearchlight.BlinkInterval	local	-	-	-	0	MySearchlight.cs:700 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMySearchlight.BlinkLength	local	-	-	-	0	MySearchlight.cs:712 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMySearchlight.BlinkOffset	local	-	-	-	0	MySearchlight.cs:724 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMySearchlight.Color	local	-	-	-	0	MySearchlight.cs:652 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMySearchlight.EnableIdleMovement	local	-	-	-	1	MySearchlight.cs:546 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMySearchlight.Intensity	local	-	-	-	0	MySearchlight.cs:676 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMySearchlight.Offset	local	-	-	-	0	MySearchlight.cs:688 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMySearchlight.Radius	local	-	-	-	0	MySearchlight.cs:664 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMySearchlight.SetManualAzimuthAndElevation	local	-	-	-	1	MySearchlight.cs:1307 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMySearchlight.SetTargetOptions	local	-	-	-	0	MySearchlight.cs:1302 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMySearchlight.TargetCharacters	local	-	-	-	0	MySearchlight.cs:936 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMySearchlight.TargetEnemy	local	-	-	-	0	MySearchlight.cs:613 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMySearchlight.TargetFriends	local	-	-	-	0	MySearchlight.cs:594 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMySearchlight.TargetLargeShips	local	-	-	-	0	MySearchlight.cs:582 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMySearchlight.TargetLockEnemy	local	-	-	-	0	MySearchlight.cs:625 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMySearchlight.TargetMeteors	local	-	-	-	0	MySearchlight.cs:860 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMySearchlight.TargetNeutrals	local	-	-	-	0	MySearchlight.cs:974 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMySearchlight.TargetOptions	local	-	-	-	0	MySearchlight.cs:637 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMySearchlight.TargetRockets	local	-	-	-	0	MySearchlight.cs:558 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMySearchlight.TargetSmallShips	local	-	-	-	0	MySearchlight.cs:570 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMySearchlight.TargetStations	local	-	-	-	0	MySearchlight.cs:955 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMySensorBlock.BackExtend	local	-	-	-	0	MySensorBlock.cs:625 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMySensorBlock.BottomExtend	local	-	-	-	0	MySensorBlock.cs:601 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMySensorBlock.DetectAsteroids	local	-	-	-	0	MySensorBlock.cs:709 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMySensorBlock.DetectEnemy	local	-	-	-	0	MySensorBlock.cs:757 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMySensorBlock.DetectFloatingObjects	local	-	-	-	0	MySensorBlock.cs:661 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMySensorBlock.DetectFriendly	local	-	-	-	0	MySensorBlock.cs:733 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMySensorBlock.DetectLargeShips	local	-	-	-	0	MySensorBlock.cs:685 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMySensorBlock.DetectNeutral	local	-	-	-	0	MySensorBlock.cs:745 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMySensorBlock.DetectOwner	local	-	-	-	0	MySensorBlock.cs:721 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMySensorBlock.DetectPlayers	local	-	-	-	0	MySensorBlock.cs:649 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMySensorBlock.DetectSmallShips	local	-	-	-	0	MySensorBlock.cs:673 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMySensorBlock.DetectStations	local	-	-	-	0	MySensorBlock.cs:697 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMySensorBlock.DetectSubgrids	local	-	-	-	0	MySensorBlock.cs:349 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMySensorBlock.DetectedEntities	local	-	-	-	1	MySensorBlock.cs:1716 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMySensorBlock.FrontExtend	local	-	-	-	0	MySensorBlock.cs:613 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMySensorBlock.IsActive	local	-	-	-	1	MySensorBlock.cs:793 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMySensorBlock.LastDetectedEntity	local	-	-	-	0	MySensorBlock.cs:795 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMySensorBlock.LeftExtend	local	-	-	-	0	MySensorBlock.cs:565 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMySensorBlock.MaxRange	local	-	-	-	0	MySensorBlock.cs:228 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMySensorBlock.PlayProximitySound	local	-	-	-	0	MySensorBlock.cs:637 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMySensorBlock.RightExtend	local	-	-	-	0	MySensorBlock.cs:577 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMySensorBlock.TopExtend	local	-	-	-	0	MySensorBlock.cs:589 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyShipConnector.CollectAll	local	-	-	-	1	MyShipConnector.cs:545 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyShipConnector.Connect	local	-	-	-	1	MyShipConnector.cs:2513 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyShipConnector.Disconnect	local	-	-	-	4	MyShipConnector.cs:2521 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyShipConnector.IsConnected	local	-	-	-	2	MyShipConnector.cs:585 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyShipConnector.IsLocked	local	-	-	-	0	MyShipConnector.cs:573 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyShipConnector.IsParkingEnabled	local	-	-	-	3	MyShipConnector.cs:587 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyShipConnector.OtherConnector	local	-	-	-	1	MyShipConnector.cs:615 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyShipConnector.PullStrength	local	-	-	-	0	MyShipConnector.cs:557 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyShipConnector.Status	local	-	-	-	14	MyShipConnector.cs:599 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyShipConnector.ThrowOut	local	-	-	-	1	MyShipConnector.cs:533 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyShipConnector.ToggleConnect	local	-	-	-	1	MyShipConnector.cs:2529 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyShipController.CalculateShipMass	local	-	-	-	5	MyShipController.cs:3329 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyShipController.CanControlShip	local	-	-	-	10	MyShipController.cs:890 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyShipController.CenterOfMass	local	-	-	-	1	MyShipController.cs:813 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyShipController.ControlThrusters	local	-	-	-	2	MyShipController.cs:764 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyShipController.ControlWheels	local	-	-	-	0	MyShipController.cs:749 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyShipController.DampenersOverride	local	-	-	-	6	MyShipController.cs:794 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyShipController.GetArtificialGravity	local	-	-	-	0	MyShipController.cs:3305 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyShipController.GetNaturalGravity	local	-	-	-	5	MyShipController.cs:3300 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyShipController.GetShipSpeed	local	-	-	-	2	MyShipController.cs:3315 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyShipController.GetShipVelocities	local	-	-	-	2	MyShipController.cs:3321 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyShipController.GetTotalGravity	local	-	-	-	2	MyShipController.cs:3310 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyShipController.HandBrake	local	-	-	-	0	MyShipController.cs:779 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyShipController.HasWheels	local	-	-	-	0	MyShipController.cs:892 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyShipController.IsMainCockpit	local	-	-	-	13	MyShipController.cs:732 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyShipController.IsUnderControl	local	-	-	-	9	MyShipController.cs:747 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyShipController.MoveIndicator	local	-	-	-	6	MyShipController.cs:909 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyShipController.RollIndicator	local	-	-	-	5	MyShipController.cs:913 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyShipController.RotationIndicator	local	-	-	-	3	MyShipController.cs:911 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyShipController.ShowHorizonIndicator	local	-	-	-	0	MyShipController.cs:894 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyShipController.TryGetPlanetElevation	local	-	-	-	0	MyShipController.cs:3337 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyShipController.TryGetPlanetPosition	local	-	-	-	1	MyShipController.cs:3368 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyShipDrill.TerrainClearingMode	local	-	-	-	0	MyShipDrill.cs:235 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyShipToolBase.IsActivated	local	-	-	-	2	MyShipDrill.cs:277 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyShipToolBase.UseConveyorSystem	local	-	-	-	4	MyShipDrill.cs:265 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyShipWelder.HelpOthers	local	-	-	-	2	MyShipWelder.cs:65 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMySmallGatlingGun.UseConveyorSystem	local	-	-	-	0	MySmallGatlingGun.cs:239 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMySmallMissileLauncher.UseConveyorSystem	local	-	-	-	0	MySmallMissileLauncher.cs:335 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMySolarFoodGenerator.ItemsPerMinute	local	-	-	-	1	MySolarFoodGenerator.cs:112 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMySolarFoodGenerator.TimeRemainingUntilNextBatch	local	-	-	-	1	MySolarFoodGenerator.cs:114 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMySolarOccludable.GetEntityId	local	-	-	-	0	MyOxygenFarm.cs:296 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMySolarOccludable.IsSolarOccluded	local	-	-	-	0	MyOxygenFarm.cs:85 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyStoreBlock.CancelStoreItem	local	-	-	-	1	MyStoreBlock.cs:1790 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyStoreBlock.GetPlayerStoreItems	local	-	-	-	1	MyStoreBlock.cs:2118 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyStoreBlock.InsertOffer	local	-	-	-	0	MyStoreBlock.cs:1988 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyStoreBlock.InsertOrder	local	-	-	-	0	MyStoreBlock.cs:2013 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyStoreBlock.Searchable	local	-	-	-	0	MyStoreBlock.cs:334 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyTerminalBlock.CustomData	local	-	-	-	35	MyTerminalBlock.cs:290 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyTerminalBlock.CustomInfo	local	-	-	-	6	MyTerminalBlock.cs:433 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyTerminalBlock.CustomName	local	-	-	-	61	MyTerminalBlock.cs:417 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyTerminalBlock.CustomNameWithFaction	local	-	-	-	0	MyTerminalBlock.cs:429 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyTerminalBlock.DetailedInfo	local	-	-	-	4	MyTerminalBlock.cs:431 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyTerminalBlock.GetActionWithName	local	-	-	-	1	MyTerminalBlock.cs:1403 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyTerminalBlock.GetActions	local	-	-	-	9	MyTerminalBlock.cs:1393 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyTerminalBlock.GetProperties	local	-	-	-	4	MyTerminalBlock.cs:1413 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyTerminalBlock.GetProperty	local	-	-	-	17	MyTerminalBlock.cs:1408 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyTerminalBlock.HasLocalPlayerAccess	local	-	-	-	8	MyTerminalBlock.cs:894 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyTerminalBlock.HasNobodyPlayerAccessToBlock	local	-	-	-	0	MyTerminalBlock.cs:847 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyTerminalBlock.HasPlayerAccess	local	-	-	-	15	MyTerminalBlock.cs:917 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyTerminalBlock.HasPlayerAccessWithNobodyCheck	local	-	-	-	0	MyTerminalBlock.cs:899 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyTerminalBlock.IsSameConstructAs	local	-	-	-	2	MyTerminalBlock.cs:1388 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyTerminalBlock.SearchActionsOfName	local	-	-	-	0	MyTerminalBlock.cs:1398 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyTerminalBlock.SetCustomName	local	-	-	-	1	MyTerminalBlock.cs:668 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyTerminalBlock.ShowInInventory	local	-	-	-	1	MyTerminalBlock.cs:338 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyTerminalBlock.ShowInTerminal	local	-	-	-	2	MyTerminalBlock.cs:322 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyTerminalBlock.ShowInToolbarConfig	local	-	-	-	9	MyTerminalBlock.cs:354 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyTerminalBlock.ShowOnHUD	local	-	-	-	2	MyTerminalBlock.cs:306 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyTextPanel.GetPublicTitle	local	-	-	-	0	MyTextPanel.cs:1269 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyTextPanel.WritePublicTitle	local	-	-	-	0	MyTextPanel.cs:1274 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyTextSurface.AddImageToSelection	local	-	-	-	1	MyTextPanel.cs:1351 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyTextSurface.AddImagesToSelection	local	-	-	-	0	MyTextPanel.cs:1378 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyTextSurface.Alignment	local	-	-	-	3	MyTextPanel.cs:452 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyTextSurface.BackgroundAlpha	local	-	-	-	2	MyTextPanel.cs:391 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyTextSurface.BackgroundColor	local	-	-	-	4	MyTextPanel.cs:376 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyTextSurface.ChangeInterval	local	-	-	-	1	MyTextPanel.cs:406 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyTextSurface.ClearImagesFromSelection	local	-	-	-	1	MyTextPanel.cs:1491 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyTextSurface.ContentType	local	-	-	-	8	MyTextPanel.cs:486 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyTextSurface.CurrentlyShownImage	local	-	-	-	0	MyTextPanel.cs:421 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyTextSurface.DisplayName	local	-	-	-	5	MyTextPanel.cs:564 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyTextSurface.DrawFrame	local	-	-	-	19	MyTextPanel.cs:1547 own LCD; the sprites are synced to this node's clients, which are the ones near it
Sandbox.ModAPI.Ingame.IMyTextSurface.Font	local	-	-	-	5	MyTextPanel.cs:437 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyTextSurface.FontColor	local	-	-	-	2	MyTextPanel.cs:361 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyTextSurface.FontSize	local	-	-	-	4	MyTextPanel.cs:346 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyTextSurface.GetFonts	local	-	-	-	2	MyTextPanel.cs:1525 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyTextSurface.GetScripts	local	-	-	-	0	MyTextPanel.cs:1542 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyTextSurface.GetSelectedImages	local	-	-	-	0	MyTextPanel.cs:1513 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyTextSurface.GetSprites	local	-	-	-	2	MyTextPanel.cs:1537 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyTextSurface.GetText	local	-	-	-	0	MyTextPanel.cs:1322 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyTextSurface.MeasureStringInPixels	local	-	-	-	12	MyTextPanel.cs:1552 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyTextSurface.Name	local	-	-	-	6	MyTextPanel.cs:562 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyTextSurface.PreserveAspectRatio	local	-	-	-	0	MyTextPanel.cs:502 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyTextSurface.ReadText	local	-	-	-	1	MyTextPanel.cs:1342 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyTextSurface.RemoveImageFromSelection	local	-	-	-	0	MyTextPanel.cs:1418 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyTextSurface.RemoveImagesFromSelection	local	-	-	-	0	MyTextPanel.cs:1453 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyTextSurface.Script	local	-	-	-	6	MyTextPanel.cs:467 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyTextSurface.ScriptBackgroundColor	local	-	-	-	12	MyTextPanel.cs:532 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyTextSurface.ScriptForegroundColor	local	-	-	-	17	MyTextPanel.cs:547 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyTextSurface.SurfaceSize	local	-	-	-	18	MyTextPanel.cs:498 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyTextSurface.TextPadding	local	-	-	-	3	MyTextPanel.cs:517 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyTextSurface.TextureSize	local	-	-	-	14	MyTextPanel.cs:500 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyTextSurface.WriteText	local	-	-	-	5	MyTextPanel.cs:1303 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyTextSurfaceProvider.GetSurface	local	-	-	-	10	MyProjectorBase.cs:2536 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyTextSurfaceProvider.SurfaceCount	local	-	-	-	11	MyProjectorBase.cs:871 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyTextSurfaceProvider.UseGenericLcd	local	-	-	-	0	MyTextPanel.cs:159 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyThrust.CurrentThrust	local	-	-	-	11	MyThrust.cs:374 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyThrust.CurrentThrustPercentage	local	-	-	-	2	MyThrust.cs:376 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyThrust.GridThrustDirection	local	-	-	-	1	MyThrust.cs:378 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyThrust.MaxEffectiveThrust	local	-	-	-	11	MyThrust.cs:362 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyThrust.MaxThrust	local	-	-	-	12	MyThrust.cs:360 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyThrust.ThrustOverride	local	-	-	-	3	MyThrust.cs:294 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyThrust.ThrustOverridePercentage	local	-	-	-	6	MyThrust.cs:306 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyUpgradableBlock.FillUpgradesDictionary	local	-	-	-	0	MyCubeBlock.cs:2102 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyUpgradableBlock.GetUpgrades	local	-	-	-	0	MyCubeBlock.cs:2114 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyUpgradableBlock.UpgradeCount	local	-	-	-	0	MyCubeBlock.cs:393 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyUpgradeModule.Connections	local	-	-	-	1	MyUpgradeModule.cs:55 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyUpgradeModule.FillUpgradeList	local	-	-	-	0	MyUpgradeModule.cs:450 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyUpgradeModule.GetUpgradeList	local	-	-	-	1	MyUpgradeModule.cs:462 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyUpgradeModule.UpgradeCount	local	-	-	-	1	MyUpgradeModule.cs:53 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyUserControllableGun.CycleSelectedAmmoType	local	-	-	-	0	MyUserControllableGun.cs:301 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyUserControllableGun.ForceReload	local	-	-	-	0	MyUserControllableGun.cs:352 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyUserControllableGun.IsShooting	local	-	-	-	3	MyUserControllableGun.cs:181 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyUserControllableGun.SelectedAmmoType	local	-	-	-	0	MyUserControllableGun.cs:126 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyUserControllableGun.Shoot	local	-	-	-	2	MyUserControllableGun.cs:183 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyUserControllableGun.ShootOnce	local	-	-	-	1	MyUserControllableGun.cs:596 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyWarhead.Detonate	local	-	-	-	4	MyWarhead.cs:644 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyWarhead.DetonationTime	local	-	-	-	6	MyWarhead.cs:197 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyWarhead.IsArmed	local	-	-	-	8	MyWarhead.cs:165 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyWarhead.IsCountingDown	local	-	-	-	4	MyWarhead.cs:195 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyWarhead.StartCountdown	local	-	-	-	2	MyWarhead.cs:338 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyWarhead.StopCountdown	local	-	-	-	1	MyWarhead.cs:351 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyWindTurbine.Effectivity	local	-	-	-	0	MyWindTurbine.cs:104 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyWindTurbine.PlacementEffectivity	local	-	-	-	0	MyWindTurbine.cs:102 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IMyWindTurbine.WindEffectivity	local	-	-	-	0	MyWindTurbine.cs:100 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
Sandbox.ModAPI.Ingame.IUserCustomizableTerminalAction.FetchAllBlocks	local	-	-	-	0	MyToolbarItemTerminalBlock.cs:47 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.IUserCustomizableTerminalAction.GetBlock	local	-	-	-	0	MyToolbarItemTerminalBlock.cs:42 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.IUserCustomizableTerminalAction.Parameters	local	-	-	-	0	IUserCustomizableTerminalAction.cs:13 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.IUserCustomizableTerminalAction.SetCustomIconTitle	local	-	-	-	0	IUserCustomizableTerminalAction.cs:19 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.MyAssemblerMode.*	local	-	-	-	0	value, parser or helper; no world state
Sandbox.ModAPI.Ingame.MyConveyorSorterMode.*	local	-	-	-	0	value, parser or helper; no world state
Sandbox.ModAPI.Ingame.MyDetectedEntityInfo.BoundingBox	local	-	-	-	0	MyDetectedEntityInfo.cs:49 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.MyDetectedEntityInfo.EntityId	local	-	-	-	4	MyDetectedEntityInfo.cs:14 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.MyDetectedEntityInfo.HitPosition	local	-	-	-	0	MyDetectedEntityInfo.cs:29 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.MyDetectedEntityInfo.IsEmpty	local	-	-	-	2	MyDetectedEntityInfo.cs:90 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.MyDetectedEntityInfo.Name	local	-	-	-	0	MyDetectedEntityInfo.cs:19 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.MyDetectedEntityInfo.Orientation	local	-	-	-	0	MyDetectedEntityInfo.cs:34 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.MyDetectedEntityInfo.Position	local	-	-	-	2	MyDetectedEntityInfo.cs:59 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.MyDetectedEntityInfo.Relationship	local	-	-	-	2	MyDetectedEntityInfo.cs:44 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.MyDetectedEntityInfo.TimeStamp	local	-	-	-	0	MyDetectedEntityInfo.cs:54 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.MyDetectedEntityInfo.Type	local	-	-	-	3	MyDetectedEntityInfo.cs:24 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.MyDetectedEntityInfo.Velocity	local	-	-	-	0	MyDetectedEntityInfo.cs:39 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.MyDetectedEntityType.*	local	-	-	-	0	value, parser or helper; no world state
Sandbox.ModAPI.Ingame.MyGridProgram.Echo	local	-	-	-	1	MyGridProgram.cs:70 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.MyGridProgram.GridTerminalSystem	local	-	-	-	1	MyGridProgram.cs:35 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.MyGridProgram.IGC	local	-	-	-	0	MyGridProgram.cs:83 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.MyGridProgram.Me	local	-	-	-	5	MyGridProgram.cs:40 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.MyGridProgram.Runtime	local	-	-	-	1	MyGridProgram.cs:45 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.MyGridProgram.Storage	local	-	-	-	0	MyGridProgram.cs:55 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.MyGridProgram.World	local	-	-	-	0	MyGridProgram.cs:50 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.MyIGCMessage.As	local	-	-	-	0	MyIGCMessage.cs:41 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.MyIGCMessage.Data	local	-	-	-	0	MyIGCMessage.cs:11 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.MyIGCMessage.Source	local	-	-	-	0	MyIGCMessage.cs:21 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.MyIGCMessage.Tag	local	-	-	-	0	MyIGCMessage.cs:16 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.MyInventoryItemFilter.AllSubTypes	local	-	-	-	1	MyInventoryItemFilter.cs:48 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.MyInventoryItemFilter.ItemId	local	-	-	-	1	MyInventoryItemFilter.cs:53 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.MyInventoryItemFilter.ItemType	local	-	-	-	1	MyInventoryItemFilter.cs:58 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.MyJumpDriveStatus.*	local	-	-	-	0	value, parser or helper; no world state
Sandbox.ModAPI.Ingame.MyLaserAntennaStatus.*	local	-	-	-	0	value, parser or helper; no world state
Sandbox.ModAPI.Ingame.MyPlanetElevation.*	local	-	-	-	0	value, parser or helper; no world state
Sandbox.ModAPI.Ingame.MyProductionItem.Amount	local	-	-	-	3	MyProductionItem.cs:14 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.MyProductionItem.BlueprintId	local	-	-	-	2	MyProductionItem.cs:19 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.MyProductionItem.ItemId	local	-	-	-	0	MyProductionItem.cs:24 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.MyRotationDirection.*	local	-	-	-	0	value, parser or helper; no world state
Sandbox.ModAPI.Ingame.MyShipConnectorStatus.*	local	-	-	-	0	value, parser or helper; no world state
Sandbox.ModAPI.Ingame.MyShipMass.BaseMass	local	-	-	-	0	MyShipMass.cs:11 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.MyShipMass.PhysicalMass	local	-	-	-	5	MyShipMass.cs:21 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.MyShipMass.TotalMass	local	-	-	-	1	MyShipMass.cs:16 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.MyShipVelocities.AngularVelocity	local	-	-	-	0	MyShipVelocities.cs:18 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.MyShipVelocities.LinearVelocity	local	-	-	-	2	MyShipVelocities.cs:13 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.MyStoreInsertResults.*	local	-	-	-	0	value, parser or helper; no world state
Sandbox.ModAPI.Ingame.MyStoreQueryItem.Amount	local	-	-	-	1	MyStoreQueryItem.cs:18 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.MyStoreQueryItem.Id	local	-	-	-	1	MyStoreQueryItem.cs:28 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.MyStoreQueryItem.ItemId	local	-	-	-	1	MyStoreQueryItem.cs:13 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.MyStoreQueryItem.PricePerUnit	local	-	-	-	1	MyStoreQueryItem.cs:23 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.MyTerminalAccessScope.*	local	-	-	-	0	value, parser or helper; no world state
Sandbox.ModAPI.Ingame.MyTransmitTarget.*	local	-	-	-	0	value, parser or helper; no world state
Sandbox.ModAPI.Ingame.MyWaypointInfo.Coords	local	-	-	-	0	MyWaypointInfo.cs:27 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.MyWaypointInfo.Empty	local	-	-	-	0	MyWaypointInfo.cs:17 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.MyWaypointInfo.Equals	local	-	-	-	0	MyWaypointInfo.cs:215 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.MyWaypointInfo.FindAll	local	-	-	-	0	MyWaypointInfo.cs:46 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.MyWaypointInfo.GetHashCode	local	-	-	-	0	MyWaypointInfo.cs:257 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.MyWaypointInfo.IsEmpty	local	-	-	-	0	MyWaypointInfo.cs:196 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.MyWaypointInfo.Name	local	-	-	-	0	MyWaypointInfo.cs:22 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.MyWaypointInfo.ToString	local	-	-	-	0	MyWaypointInfo.cs:205 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.MyWaypointInfo.TryParse	local	-	-	-	0	MyWaypointInfo.cs:74 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.PistonStatus.*	local	-	-	-	0	value, parser or helper; no world state
Sandbox.ModAPI.Ingame.TargetingGroupOptions.*	local	-	-	-	0	value, parser or helper; no world state
Sandbox.ModAPI.Ingame.TerminalActionParameter.Deserialize	local	-	-	-	0	TerminalActionParameter.cs:62 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.TerminalActionParameter.Empty	local	-	-	-	0	TerminalActionParameter.cs:16 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.TerminalActionParameter.Get	local	-	-	-	1	TerminalActionParameter.cs:79 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.TerminalActionParameter.GetObjectBuilder	local	-	-	-	0	TerminalActionParameter.cs:108 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.TerminalActionParameter.IsEmpty	local	-	-	-	0	TerminalActionParameter.cs:31 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.TerminalActionParameter.TypeCode	local	-	-	-	0	TerminalActionParameter.cs:21 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.TerminalActionParameter.Value	local	-	-	-	0	TerminalActionParameter.cs:26 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.TerminalBlockExtentions.ApplyAction	local	-	-	-	0	TerminalBlockExtentions.cs:30 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.TerminalBlockExtentions.GetId	local	-	-	-	0	TerminalBlockExtentions.cs:19 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.TerminalBlockExtentions.GetInventory	local	-	-	-	0	TerminalBlockExtentions.cs:84 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.TerminalBlockExtentions.GetInventoryCount	local	-	-	-	0	TerminalBlockExtentions.cs:103 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.TerminalBlockExtentions.GetUseConveyorSystem	local	-	-	-	0	TerminalBlockExtentions.cs:118 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.TerminalBlockExtentions.HasAction	local	-	-	-	0	TerminalBlockExtentions.cs:53 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.TerminalBlockExtentions.HasInventory	local	-	-	-	0	TerminalBlockExtentions.cs:64 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.TerminalBlockExtentions.SetUseConveyorSystem	local	-	-	-	0	TerminalBlockExtentions.cs:133 value, parser or helper; no world state
Sandbox.ModAPI.Ingame.TransmissionDistance.*	local	-	-	-	0	value, parser or helper; no world state
Sandbox.ModAPI.Ingame.UpdateFrequency.*	local	-	-	-	0	value, parser or helper; no world state
Sandbox.ModAPI.Ingame.UpdateType.*	local	-	-	-	0	value, parser or helper; no world state
Sandbox.ModAPI.Interfaces.ITerminalAction.Apply	local	-	-	-	5	MyTerminalAction.cs:406 terminal UI definitions, process-wide and identical on every node; the UI itself runs on the client
Sandbox.ModAPI.Interfaces.ITerminalAction.Icon	local	-	-	-	0	MyTerminalAction.cs:88 terminal UI definitions, process-wide and identical on every node; the UI itself runs on the client
Sandbox.ModAPI.Interfaces.ITerminalAction.Id	local	-	-	-	23	MyTerminalAction.cs:86 terminal UI definitions, process-wide and identical on every node; the UI itself runs on the client
Sandbox.ModAPI.Interfaces.ITerminalAction.IsEnabled	local	-	-	-	5	MyTerminalAction.cs:430 terminal UI definitions, process-wide and identical on every node; the UI itself runs on the client
Sandbox.ModAPI.Interfaces.ITerminalAction.Name	local	-	-	-	8	MyTerminalAction.cs:90 terminal UI definitions, process-wide and identical on every node; the UI itself runs on the client
Sandbox.ModAPI.Interfaces.ITerminalAction.WriteValue	local	-	-	-	3	MyTerminalAction.cs:422 terminal UI definitions, process-wide and identical on every node; the UI itself runs on the client
Sandbox.ModAPI.Interfaces.ITerminalProperty.GetDefaultValue	local	-	-	-	2	MyTerminalControlCheckbox.cs:202 terminal UI definitions, process-wide and identical on every node; the UI itself runs on the client
Sandbox.ModAPI.Interfaces.ITerminalProperty.GetMaximum	local	-	-	-	3	MyTerminalControlCheckbox.cs:212 terminal UI definitions, process-wide and identical on every node; the UI itself runs on the client
Sandbox.ModAPI.Interfaces.ITerminalProperty.GetMinimum	local	-	-	-	3	MyTerminalControlCheckbox.cs:207 terminal UI definitions, process-wide and identical on every node; the UI itself runs on the client
Sandbox.ModAPI.Interfaces.ITerminalProperty.GetMininum	local	-	-	-	0	MyTerminalValueControl.cs:108 terminal UI definitions, process-wide and identical on every node; the UI itself runs on the client
Sandbox.ModAPI.Interfaces.ITerminalProperty.GetValue	local	-	-	-	15	MyTerminalControlCheckbox.cs:222 terminal UI definitions, process-wide and identical on every node; the UI itself runs on the client
Sandbox.ModAPI.Interfaces.ITerminalProperty.Id	local	-	-	-	7	MyTerminalValueControl.cs:41 terminal UI definitions, process-wide and identical on every node; the UI itself runs on the client
Sandbox.ModAPI.Interfaces.ITerminalProperty.SetValue	local	-	-	-	5	MyTerminalControlCheckbox.cs:217 terminal UI definitions, process-wide and identical on every node; the UI itself runs on the client
Sandbox.ModAPI.Interfaces.ITerminalProperty.TypeName	local	-	-	-	2	MyTerminalValueControl.cs:43 terminal UI definitions, process-wide and identical on every node; the UI itself runs on the client
Sandbox.ModAPI.Interfaces.Terminal.IMyTerminalAction.Action	local	-	-	-	49	MyTerminalAction.cs:158 terminal UI definitions, process-wide and identical on every node; the UI itself runs on the client
Sandbox.ModAPI.Interfaces.Terminal.IMyTerminalAction.ActionWithParameters	local	-	-	-	0	MyTerminalAction.cs:186 terminal UI definitions, process-wide and identical on every node; the UI itself runs on the client
Sandbox.ModAPI.Interfaces.Terminal.IMyTerminalAction.DoUserParameterRequestWithItem	local	-	-	-	0	MyTerminalAction.cs:174 terminal UI definitions, process-wide and identical on every node; the UI itself runs on the client
Sandbox.ModAPI.Interfaces.Terminal.IMyTerminalAction.Enabled	local	-	-	-	46	MyTerminalAction.cs:97 terminal UI definitions, process-wide and identical on every node; the UI itself runs on the client
Sandbox.ModAPI.Interfaces.Terminal.IMyTerminalAction.Icon	local	-	-	-	37	MyTerminalAction.cs:146 terminal UI definitions, process-wide and identical on every node; the UI itself runs on the client
Sandbox.ModAPI.Interfaces.Terminal.IMyTerminalAction.InvalidToolbarTypes	local	-	-	-	6	MyTerminalAction.cs:110 terminal UI definitions, process-wide and identical on every node; the UI itself runs on the client
Sandbox.ModAPI.Interfaces.Terminal.IMyTerminalAction.Name	local	-	-	-	47	MyTerminalAction.cs:134 terminal UI definitions, process-wide and identical on every node; the UI itself runs on the client
Sandbox.ModAPI.Interfaces.Terminal.IMyTerminalAction.ParameterDefinitions	local	-	-	-	0	MyTerminalAction.cs:92 terminal UI definitions, process-wide and identical on every node; the UI itself runs on the client
Sandbox.ModAPI.Interfaces.Terminal.IMyTerminalAction.ValidForGroups	local	-	-	-	30	MyTerminalAction.cs:122 terminal UI definitions, process-wide and identical on every node; the UI itself runs on the client
Sandbox.ModAPI.Interfaces.Terminal.IMyTerminalAction.Writer	local	-	-	-	40	MyTerminalAction.cs:202 terminal UI definitions, process-wide and identical on every node; the UI itself runs on the client
Sandbox.ModAPI.Interfaces.Terminal.IMyTerminalControl.Enabled	local	-	-	-	67	MyTerminalControl.cs:75 terminal UI definitions, process-wide and identical on every node; the UI itself runs on the client
Sandbox.ModAPI.Interfaces.Terminal.IMyTerminalControl.Id	local	-	-	-	46	MyTerminalControl.cs:73 terminal UI definitions, process-wide and identical on every node; the UI itself runs on the client
Sandbox.ModAPI.Interfaces.Terminal.IMyTerminalControl.RedrawControl	local	-	-	-	7	MyTerminalControl.cs:159 terminal UI definitions, process-wide and identical on every node; the UI itself runs on the client
Sandbox.ModAPI.Interfaces.Terminal.IMyTerminalControl.SupportsMultipleBlocks	local	-	-	-	54	MyTerminalControl.cs:65 terminal UI definitions, process-wide and identical on every node; the UI itself runs on the client
Sandbox.ModAPI.Interfaces.Terminal.IMyTerminalControl.UpdateVisual	local	-	-	-	25	MyTerminalControl.cs:151 terminal UI definitions, process-wide and identical on every node; the UI itself runs on the client
Sandbox.ModAPI.Interfaces.Terminal.IMyTerminalControl.Visible	local	-	-	-	69	MyTerminalControl.cs:88 terminal UI definitions, process-wide and identical on every node; the UI itself runs on the client
Sandbox.ModAPI.Interfaces.Terminal.IMyTerminalControlButton.Action	local	-	-	-	53	MyTerminalControlButton.cs:47 terminal UI definitions, process-wide and identical on every node; the UI itself runs on the client
Sandbox.ModAPI.Interfaces.Terminal.IMyTerminalControlCheckbox.OffText	local	-	-	-	13	MyTerminalControlCheckbox.cs:55 terminal UI definitions, process-wide and identical on every node; the UI itself runs on the client
Sandbox.ModAPI.Interfaces.Terminal.IMyTerminalControlCheckbox.OnText	local	-	-	-	13	MyTerminalControlCheckbox.cs:43 terminal UI definitions, process-wide and identical on every node; the UI itself runs on the client
Sandbox.ModAPI.Interfaces.Terminal.IMyTerminalControlCombobox.ComboBoxContent	local	-	-	-	27	MyTerminalControlCombobox.cs:64 terminal UI definitions, process-wide and identical on every node; the UI itself runs on the client
Sandbox.ModAPI.Interfaces.Terminal.IMyTerminalControlLabel.Label	local	-	-	-	28	MyTerminalControlLabel.cs:19 terminal UI definitions, process-wide and identical on every node; the UI itself runs on the client
Sandbox.ModAPI.Interfaces.Terminal.IMyTerminalControlListbox.ItemSelected	local	-	-	-	28	MyTerminalControlListbox.cs:145 terminal UI definitions, process-wide and identical on every node; the UI itself runs on the client
Sandbox.ModAPI.Interfaces.Terminal.IMyTerminalControlListbox.ListContent	local	-	-	-	28	MyTerminalControlListbox.cs:123 terminal UI definitions, process-wide and identical on every node; the UI itself runs on the client
Sandbox.ModAPI.Interfaces.Terminal.IMyTerminalControlListbox.Multiselect	local	-	-	-	28	MyTerminalControlListbox.cs:99 terminal UI definitions, process-wide and identical on every node; the UI itself runs on the client
Sandbox.ModAPI.Interfaces.Terminal.IMyTerminalControlListbox.VisibleRowsCount	local	-	-	-	28	MyTerminalControlListbox.cs:111 terminal UI definitions, process-wide and identical on every node; the UI itself runs on the client
Sandbox.ModAPI.Interfaces.Terminal.IMyTerminalControlOnOffSwitch.OffText	local	-	-	-	33	MyTerminalControlOnOffSwitch.cs:78 terminal UI definitions, process-wide and identical on every node; the UI itself runs on the client
Sandbox.ModAPI.Interfaces.Terminal.IMyTerminalControlOnOffSwitch.OnText	local	-	-	-	33	MyTerminalControlOnOffSwitch.cs:66 terminal UI definitions, process-wide and identical on every node; the UI itself runs on the client
Sandbox.ModAPI.Interfaces.Terminal.IMyTerminalControlSlider.SetDualLogLimits	local	-	-	-	2	MyTerminalControlSlider.cs:337 terminal UI definitions, process-wide and identical on every node; the UI itself runs on the client
Sandbox.ModAPI.Interfaces.Terminal.IMyTerminalControlSlider.SetLimits	local	-	-	-	36	MyTerminalControlSlider.cs:291 terminal UI definitions, process-wide and identical on every node; the UI itself runs on the client
Sandbox.ModAPI.Interfaces.Terminal.IMyTerminalControlSlider.SetLogLimits	local	-	-	-	14	MyTerminalControlSlider.cs:314 terminal UI definitions, process-wide and identical on every node; the UI itself runs on the client
Sandbox.ModAPI.Interfaces.Terminal.IMyTerminalControlSlider.Writer	local	-	-	-	38	MyTerminalControlSlider.cs:115 terminal UI definitions, process-wide and identical on every node; the UI itself runs on the client
Sandbox.ModAPI.Interfaces.Terminal.IMyTerminalControlTitleTooltip.Title	local	-	-	-	72	MyTerminalControlButton.cs:63 terminal UI definitions, process-wide and identical on every node; the UI itself runs on the client
Sandbox.ModAPI.Interfaces.Terminal.IMyTerminalControlTitleTooltip.Tooltip	local	-	-	-	57	MyTerminalControlButton.cs:75 terminal UI definitions, process-wide and identical on every node; the UI itself runs on the client
Sandbox.ModAPI.Interfaces.Terminal.IMyTerminalValueControl.Getter	local	-	-	-	73	MyTerminalControlTextbox.cs:68 terminal UI definitions, process-wide and identical on every node; the UI itself runs on the client
Sandbox.ModAPI.Interfaces.Terminal.IMyTerminalValueControl.Setter	local	-	-	-	69	MyTerminalControlTextbox.cs:81 terminal UI definitions, process-wide and identical on every node; the UI itself runs on the client
Sandbox.ModAPI.Interfaces.TerminalPropertyExtensions.As	local	-	-	-	13	TerminalPropertyExtensions.cs:18 value, parser or helper; no world state
Sandbox.ModAPI.Interfaces.TerminalPropertyExtensions.AsBool	local	-	-	-	4	TerminalPropertyExtensions.cs:79 value, parser or helper; no world state
Sandbox.ModAPI.Interfaces.TerminalPropertyExtensions.AsColor	local	-	-	-	1	TerminalPropertyExtensions.cs:69 value, parser or helper; no world state
Sandbox.ModAPI.Interfaces.TerminalPropertyExtensions.AsFloat	local	-	-	-	1	TerminalPropertyExtensions.cs:59 value, parser or helper; no world state
Sandbox.ModAPI.Interfaces.TerminalPropertyExtensions.Cast	local	-	-	-	0	TerminalPropertyExtensions.cs:29 value, parser or helper; no world state
Sandbox.ModAPI.Interfaces.TerminalPropertyExtensions.GetDefaultValue	local	-	-	-	0	TerminalPropertyExtensions.cs:168 value, parser or helper; no world state
Sandbox.ModAPI.Interfaces.TerminalPropertyExtensions.GetMaximum	local	-	-	-	0	TerminalPropertyExtensions.cs:205 value, parser or helper; no world state
Sandbox.ModAPI.Interfaces.TerminalPropertyExtensions.GetMinimum	local	-	-	-	0	TerminalPropertyExtensions.cs:193 value, parser or helper; no world state
Sandbox.ModAPI.Interfaces.TerminalPropertyExtensions.GetMininum	local	-	-	-	0	TerminalPropertyExtensions.cs:181 value, parser or helper; no world state
Sandbox.ModAPI.Interfaces.TerminalPropertyExtensions.GetValue	local	-	-	-	6	TerminalPropertyExtensions.cs:156 value, parser or helper; no world state
Sandbox.ModAPI.Interfaces.TerminalPropertyExtensions.GetValueBool	local	-	-	-	0	TerminalPropertyExtensions.cs:112 value, parser or helper; no world state
Sandbox.ModAPI.Interfaces.TerminalPropertyExtensions.GetValueColor	local	-	-	-	0	TerminalPropertyExtensions.cs:134 value, parser or helper; no world state
Sandbox.ModAPI.Interfaces.TerminalPropertyExtensions.GetValueFloat	local	-	-	-	3	TerminalPropertyExtensions.cs:90 value, parser or helper; no world state
Sandbox.ModAPI.Interfaces.TerminalPropertyExtensions.Is	local	-	-	-	0	TerminalPropertyExtensions.cs:49 value, parser or helper; no world state
Sandbox.ModAPI.Interfaces.TerminalPropertyExtensions.SetValue	local	-	-	-	3	TerminalPropertyExtensions.cs:217 value, parser or helper; no world state
Sandbox.ModAPI.Interfaces.TerminalPropertyExtensions.SetValueBool	local	-	-	-	2	TerminalPropertyExtensions.cs:123 value, parser or helper; no world state
Sandbox.ModAPI.Interfaces.TerminalPropertyExtensions.SetValueColor	local	-	-	-	0	TerminalPropertyExtensions.cs:145 value, parser or helper; no world state
Sandbox.ModAPI.Interfaces.TerminalPropertyExtensions.SetValueFloat	local	-	-	-	2	TerminalPropertyExtensions.cs:101 value, parser or helper; no world state
Sandbox.ModAPI.MissileMoveDelegate.*	local	-	-	-	0	value, parser or helper; no world state
Sandbox.ModAPI.MyAPIGateway.Clean	local	-	-	-	0	MyAPIGateway.cs:193 process-wide registration or service; every node runs the mod and registers its own
Sandbox.ModAPI.MyAPIGateway.ContractSystem	local	-	-	-	2	MyAPIGateway.cs:84 accessor of a ModAPI service; the policy is on the members of the returned interface
Sandbox.ModAPI.MyAPIGateway.CubeBuilder	local	-	-	-	4	MyAPIGateway.cs:29 accessor of a ModAPI service; the policy is on the members of the returned interface
Sandbox.ModAPI.MyAPIGateway.DLC	local	-	-	-	6	MyAPIGateway.cs:118 accessor of a ModAPI service; the policy is on the members of the returned interface
Sandbox.ModAPI.MyAPIGateway.DoorBase	local	-	-	-	0	MyAPIGateway.cs:221 accessor of a ModAPI service; the policy is on the members of the returned interface
Sandbox.ModAPI.MyAPIGateway.Entities	local	-	-	-	118	MyAPIGateway.cs:149 accessor of a ModAPI service; the policy is on the members of the returned interface
Sandbox.ModAPI.MyAPIGateway.GeneratedTextures	local	-	-	-	1	MyAPIGateway.cs:129 accessor of a ModAPI service; the policy is on the members of the returned interface
Sandbox.ModAPI.MyAPIGateway.GetMessageBoxPointer	client	-	-	-	0	MyAPIGateway.cs:177 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyAPIGateway.GravityProviderSystem	local	-	-	-	5	MyAPIGateway.cs:123 accessor of a ModAPI service; the policy is on the members of the returned interface
Sandbox.ModAPI.MyAPIGateway.GridGroups	local	-	-	-	45	MyAPIGateway.cs:93 accessor of a ModAPI service; the policy is on the members of the returned interface
Sandbox.ModAPI.MyAPIGateway.Gui	local	-	-	-	82	MyAPIGateway.cs:64 accessor of a ModAPI service; the policy is on the members of the returned interface
Sandbox.ModAPI.MyAPIGateway.GuiControlCreated	client	-	-	-	0	MyAPIGateway.cs:19 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyAPIGateway.IngameScripting	local	-	-	-	0	MyAPIGateway.cs:74 accessor of a ModAPI service; the policy is on the members of the returned interface
Sandbox.ModAPI.MyAPIGateway.Input	local	-	-	-	58	MyAPIGateway.cs:79 accessor of a ModAPI service; the policy is on the members of the returned interface
Sandbox.ModAPI.MyAPIGateway.Missiles	local	-	-	-	5	MyAPIGateway.cs:113 accessor of a ModAPI service; the policy is on the members of the returned interface
Sandbox.ModAPI.MyAPIGateway.Multiplayer	local	-	-	-	152	MyAPIGateway.cs:49 accessor of a ModAPI service; the policy is on the members of the returned interface
Sandbox.ModAPI.MyAPIGateway.Parallel	local	-	-	-	54	MyAPIGateway.cs:54 accessor of a ModAPI service; the policy is on the members of the returned interface
Sandbox.ModAPI.MyAPIGateway.Physics	local	-	-	-	58	MyAPIGateway.cs:59 accessor of a ModAPI service; the policy is on the members of the returned interface
Sandbox.ModAPI.MyAPIGateway.Players	local	-	-	-	85	MyAPIGateway.cs:24 accessor of a ModAPI service; the policy is on the members of the returned interface
Sandbox.ModAPI.MyAPIGateway.PrefabManager	local	-	-	-	5	MyAPIGateway.cs:69 accessor of a ModAPI service; the policy is on the members of the returned interface
Sandbox.ModAPI.MyAPIGateway.Projectiles	local	-	-	-	4	MyAPIGateway.cs:108 accessor of a ModAPI service; the policy is on the members of the returned interface
Sandbox.ModAPI.MyAPIGateway.Reflection	local	-	-	-	6	MyAPIGateway.cs:98 accessor of a ModAPI service; the policy is on the members of the returned interface
Sandbox.ModAPI.MyAPIGateway.Session	local	-	-	-	190	MyAPIGateway.cs:134 accessor of a ModAPI service; the policy is on the members of the returned interface
Sandbox.ModAPI.MyAPIGateway.SpectatorTools	local	-	-	-	0	MyAPIGateway.cs:103 accessor of a ModAPI service; the policy is on the members of the returned interface
Sandbox.ModAPI.MyAPIGateway.TerminalActionsHelper	local	-	-	-	22	MyAPIGateway.cs:34 accessor of a ModAPI service; the policy is on the members of the returned interface
Sandbox.ModAPI.MyAPIGateway.TerminalControls	local	-	-	-	94	MyAPIGateway.cs:39 accessor of a ModAPI service; the policy is on the members of the returned interface
Sandbox.ModAPI.MyAPIGateway.Utilities	local	-	-	-	205	MyAPIGateway.cs:44 accessor of a ModAPI service; the policy is on the members of the returned interface
Sandbox.ModAPI.MyAPIUtilities.EnterMessage	client	-	-	-	0	MyAPIUtilities.cs:114 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyAPIUtilities.EnterMessageSender	client	-	-	-	0	MyAPIUtilities.cs:120 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyAPIUtilities.MessageEntered	client	-	-	-	0	MyAPIUtilities.cs:47 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyAPIUtilities.MessageEnteredSender	client	-	-	-	2	MyAPIUtilities.cs:49 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyAPIUtilities.MessageRecieved	client	-	-	-	0	MyAPIUtilities.cs:51 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyAPIUtilities.RecieveMessage	client	-	-	-	0	MyAPIUtilities.cs:125 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyAPIUtilities.RegisterMessageHandler	local	-	-	-	12	MyAPIUtilities.cs:640 in-process cross-mod bus; both mods always run in the same process
Sandbox.ModAPI.MyAPIUtilities.SendModMessage	local	-	-	-	14	MyAPIUtilities.cs:658 in-process cross-mod bus; both mods always run in the same process
Sandbox.ModAPI.MyAPIUtilities.Static	local	-	-	-	27	MyAPIUtilities.cs:25 process-wide registration or service; every node runs the mod and registers its own
Sandbox.ModAPI.MyAPIUtilities.UnregisterMessageHandler	local	-	-	-	12	MyAPIUtilities.cs:650 in-process cross-mod bus; both mods always run in the same process
Sandbox.ModAPI.MyAPIUtilities.Variables	per-node	B7	no	request (WA)	2	MyAPIUtilities.cs:29 MyAPIUtilities.Variables lives in this node's checkpoint (ScriptManagerData); every node has its own and only the WA copy is saved
Sandbox.ModAPI.MyAutopilotPathfindingState.*	local	-	-	-	0	value, parser or helper; no world state
Sandbox.ModAPI.MyExplosionTypeEnum.*	local	-	-	-	0	value, parser or helper; no world state
Sandbox.ModAPI.MyGridRetrievalOrder.CallerEndpointId	local	-	-	-	0	MyGridRetrievalOrder.cs:33 value, parser or helper; no world state
Sandbox.ModAPI.MyGridRetrievalOrder.CallerEntityId	local	-	-	-	0	MyGridRetrievalOrder.cs:38 value, parser or helper; no world state
Sandbox.ModAPI.MyGridRetrievalOrder.CountdownMs	local	-	-	-	0	MyGridRetrievalOrder.cs:23 value, parser or helper; no world state
Sandbox.ModAPI.MyGridRetrievalOrder.CreditsPaid	local	-	-	-	0	MyGridRetrievalOrder.cs:18 value, parser or helper; no world state
Sandbox.ModAPI.MyGridRetrievalOrder.DisplayName	local	-	-	-	0	MyGridRetrievalOrder.cs:28 value, parser or helper; no world state
Sandbox.ModAPI.MyGridRetrievalOrder.Id	local	-	-	-	0	MyGridRetrievalOrder.cs:13 value, parser or helper; no world state
Sandbox.ModAPI.MyGridRetrievalOrder.InProgress	local	-	-	-	0	MyGridRetrievalOrder.cs:43 value, parser or helper; no world state
Sandbox.ModAPI.MyGridStorageItemInfo.BlockCount	local	-	-	-	0	MyGridStorageItemInfo.cs:52 value, parser or helper; no world state
Sandbox.ModAPI.MyGridStorageItemInfo.BlockLimits	local	-	-	-	0	MyGridStorageItemInfo.cs:47 value, parser or helper; no world state
Sandbox.ModAPI.MyGridStorageItemInfo.CombatCooldownRemainingMs	local	-	-	-	0	MyGridStorageItemInfo.cs:62 value, parser or helper; no world state
Sandbox.ModAPI.MyGridStorageItemInfo.DisplayName	local	-	-	-	0	MyGridStorageItemInfo.cs:42 value, parser or helper; no world state
Sandbox.ModAPI.MyGridStorageItemInfo.EntityId	local	-	-	-	0	MyGridStorageItemInfo.cs:22 value, parser or helper; no world state
Sandbox.ModAPI.MyGridStorageItemInfo.GridType	local	-	-	-	0	MyGridStorageItemInfo.cs:57 value, parser or helper; no world state
Sandbox.ModAPI.MyGridStorageItemInfo.Id	local	-	-	-	0	MyGridStorageItemInfo.cs:17 value, parser or helper; no world state
Sandbox.ModAPI.MyGridStorageItemInfo.OwnerId	local	-	-	-	0	MyGridStorageItemInfo.cs:27 value, parser or helper; no world state
Sandbox.ModAPI.MyGridStorageItemInfo.OwnershipKind	local	-	-	-	0	MyGridStorageItemInfo.cs:32 value, parser or helper; no world state
Sandbox.ModAPI.MyGridStorageItemInfo.PcuCount	local	-	-	-	0	MyGridStorageItemInfo.cs:37 value, parser or helper; no world state
Sandbox.ModAPI.MyGridStorageItemInfo.StorageEntityPosition	local	-	-	-	0	MyGridStorageItemInfo.cs:68 value, parser or helper; no world state
Sandbox.ModAPI.MyGridStorageRequestResult.*	local	-	-	-	0	value, parser or helper; no world state
Sandbox.ModAPI.MyGuiModHelpers.ActiveGamePlayScreen	client	-	-	-	0	MyGuiModHelpers.cs:13 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyGuiModHelpers.ChangeInteractedEntity	client	-	-	-	0	MyGuiModHelpers.cs:87 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyGuiModHelpers.ChatEntryVisible	client	-	-	-	0	MyGuiModHelpers.cs:34 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyGuiModHelpers.CloseTerminalScreen	client	-	-	-	0	MyGuiModHelpers.cs:82 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyGuiModHelpers.GetCurrentScreen	client	-	-	-	0	MyGuiModHelpers.cs:32 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyGuiModHelpers.GuiControlCreated	client	-	-	-	0	MyGuiModHelpers.cs:48 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyGuiModHelpers.GuiControlRemoved	client	-	-	-	0	MyGuiModHelpers.cs:60 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyGuiModHelpers.InteractedEntity	client	-	-	-	0	MyGuiModHelpers.cs:30 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyGuiModHelpers.IsCursorVisible	client	-	-	-	0	MyGuiModHelpers.cs:46 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyGuiModHelpers.ShowTerminalPage	client	-	-	-	0	MyGuiModHelpers.cs:77 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyModAPIHelper.Initialize	local	-	-	-	0	MyModAPIHelper.cs:378 value, parser or helper; no world state
Sandbox.ModAPI.MyModAPIHelper.MyMultiplayer.IsServer	local	-	-	-	0	MyModAPIHelper.cs:103 every node is the server of its own clients; true and correct per process
Sandbox.ModAPI.MyModAPIHelper.MyMultiplayer.IsServerPlayer	local	-	-	-	0	MyModAPIHelper.cs:120 every node is the server of its own clients; true and correct per process
Sandbox.ModAPI.MyModAPIHelper.MyMultiplayer.JoinServer	client	-	-	-	0	MyModAPIHelper.cs:195 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyModAPIHelper.MyMultiplayer.MultiplayerActive	local	-	-	-	0	MyModAPIHelper.cs:101 every node is the server of its own clients; true and correct per process
Sandbox.ModAPI.MyModAPIHelper.MyMultiplayer.MyId	local	-	-	-	0	MyModAPIHelper.cs:107 every node is the server of its own clients; true and correct per process
Sandbox.ModAPI.MyModAPIHelper.MyMultiplayer.MyName	local	-	-	-	0	MyModAPIHelper.cs:109 every node is the server of its own clients; true and correct per process
Sandbox.ModAPI.MyModAPIHelper.MyMultiplayer.Players	local	-	-	-	0	MyModAPIHelper.cs:111 accessor of a ModAPI service; the policy is on the members of the returned interface
Sandbox.ModAPI.MyModAPIHelper.MyMultiplayer.RegisterMessageHandler	local	-	-	-	0	MyModAPIHelper.cs:209 process-wide registration or service; every node runs the mod and registers its own
Sandbox.ModAPI.MyModAPIHelper.MyMultiplayer.RegisterSecureMessageHandler	local	-	-	-	0	MyModAPIHelper.cs:238 process-wide registration or service; every node runs the mod and registers its own
Sandbox.ModAPI.MyModAPIHelper.MyMultiplayer.SendEntitiesCreated	local	-	-	-	0	MyModAPIHelper.cs:129 empty method
Sandbox.ModAPI.MyModAPIHelper.MyMultiplayer.SendMessageTo	node-scoped	B5	yes	player-nodes + to-node	3	MyModAPIHelper.cs:178 raises ModMessageClient* to EndpointId(recipient); undeliverable when the client is attached to another node
Sandbox.ModAPI.MyModAPIHelper.MyMultiplayer.SendMessageToOthers	node-scoped	B5	yes	player-nodes + to-node / broadcast	0	MyModAPIHelper.cs:150 on a server raises ModMessageBroadcast*FromServer ([Broadcast]); reaches only clients attached to this node
Sandbox.ModAPI.MyModAPIHelper.MyMultiplayer.SendMessageToServer	affinity	B8	no	request (WA) for shared state	3	MyModAPIHelper.cs:133 raises ModMessageServer* to the node the client is attached to; after a node switch it is another node and another mod instance (cluster delivers it there: Cluster/Patches/ModMessageToServerPatch.cs:58)
Sandbox.ModAPI.MyModAPIHelper.MyMultiplayer.ServerId	local	-	-	-	0	MyModAPIHelper.cs:105 every node is the server of its own clients; true and correct per process
Sandbox.ModAPI.MyModAPIHelper.MyMultiplayer.Static	local	-	-	-	3	MyMultiplayer.cs:82 accessor of a ModAPI service; the policy is on the members of the returned interface
Sandbox.ModAPI.MyModAPIHelper.MyMultiplayer.UnregisterMessageHandler	local	-	-	-	0	MyModAPIHelper.cs:225 process-wide registration or service; every node runs the mod and registers its own
Sandbox.ModAPI.MyModAPIHelper.MyMultiplayer.UnregisterSecureMessageHandler	local	-	-	-	0	MyModAPIHelper.cs:254 process-wide registration or service; every node runs the mod and registers its own
Sandbox.ModAPI.MyModScreen.BackgroundColor	client	-	-	-	0	MyModScreen.cs:276 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyModScreen.Close	client	-	-	-	0	MyModScreen.cs:448 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyModScreen.CloseButtonEnabled	client	-	-	-	1	MyModScreen.cs:310 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyModScreen.CloseNow	client	-	-	-	1	MyModScreen.cs:459 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyModScreen.CloseOnEscape	client	-	-	-	1	MyModScreen.cs:293 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyModScreen.DrawMouseCursor	client	-	-	-	1	MyModScreen.cs:327 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyModScreen.EnabledBackgroundFade	client	-	-	-	1	MyModScreen.cs:343 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyModScreen.HasFocus	client	-	-	-	0	MyModScreen.cs:214 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyModScreen.Hide	client	-	-	-	0	MyModScreen.cs:471 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyModScreen.IsOpen	client	-	-	-	1	MyModScreen.cs:209 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyModScreen.IsVisible	client	-	-	-	0	MyModScreen.cs:229 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyModScreen.Name	client	-	-	-	0	MyModScreen.cs:204 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyModScreen.Open	client	-	-	-	1	MyModScreen.cs:417 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyModScreen.PausesGameInSinglePlayer	client	-	-	-	1	MyModScreen.cs:376 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyModScreen.Position	client	-	-	-	0	MyModScreen.cs:244 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyModScreen.Size	client	-	-	-	0	MyModScreen.cs:260 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyModScreen.SkipTransition	client	-	-	-	1	MyModScreen.cs:359 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyModScreen.Unhide	client	-	-	-	0	MyModScreen.cs:482 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyProductionQueueItem.Amount	local	-	-	-	5	MyProductionQueueItem.cs:14 value, parser or helper; no world state
Sandbox.ModAPI.MyProductionQueueItem.Blueprint	local	-	-	-	6	MyProductionQueueItem.cs:19 value, parser or helper; no world state
Sandbox.ModAPI.MyProductionQueueItem.ItemId	local	-	-	-	0	MyProductionQueueItem.cs:24 value, parser or helper; no world state
Sandbox.ModAPI.MyProjectileHitInfo.AddDecals	client	-	-	-	0	MyProjectileHitInfo.cs:61 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyProjectileHitInfo.AddHitIndicator	client	-	-	-	0	MyProjectileHitInfo.cs:66 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyProjectileHitInfo.AddHitParticles	client	-	-	-	0	MyProjectileHitInfo.cs:71 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyProjectileHitInfo.AddSZNotification	client	-	-	-	0	MyProjectileHitInfo.cs:81 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyProjectileHitInfo.Damage	local	-	-	-	1	MyProjectileHitInfo.cs:16 value, parser or helper; no world state
Sandbox.ModAPI.MyProjectileHitInfo.HitEntity	local	-	-	-	1	MyProjectileHitInfo.cs:41 value, parser or helper; no world state
Sandbox.ModAPI.MyProjectileHitInfo.HitMaterial	local	-	-	-	0	MyProjectileHitInfo.cs:51 value, parser or helper; no world state
Sandbox.ModAPI.MyProjectileHitInfo.HitNormal	local	-	-	-	0	MyProjectileHitInfo.cs:26 value, parser or helper; no world state
Sandbox.ModAPI.MyProjectileHitInfo.HitPosition	local	-	-	-	0	MyProjectileHitInfo.cs:31 value, parser or helper; no world state
Sandbox.ModAPI.MyProjectileHitInfo.HitShapeKey	local	-	-	-	0	MyProjectileHitInfo.cs:36 value, parser or helper; no world state
Sandbox.ModAPI.MyProjectileHitInfo.HitVoxelMaterial	local	-	-	-	0	MyProjectileHitInfo.cs:56 value, parser or helper; no world state
Sandbox.ModAPI.MyProjectileHitInfo.Impulse	local	-	-	-	0	MyProjectileHitInfo.cs:21 value, parser or helper; no world state
Sandbox.ModAPI.MyProjectileHitInfo.PlayHitSound	client	-	-	-	0	MyProjectileHitInfo.cs:76 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyProjectileHitInfo.ToString	local	-	-	-	0	MyProjectileHitInfo.cs:84 value, parser or helper; no world state
Sandbox.ModAPI.MyProjectileHitInfo.Velocity	local	-	-	-	0	MyProjectileHitInfo.cs:46 value, parser or helper; no world state
Sandbox.ModAPI.MyProjectileInfo.CachedGravity	local	-	-	-	0	MyProjectileInfo.cs:35 value, parser or helper; no world state
Sandbox.ModAPI.MyProjectileInfo.Index	local	-	-	-	0	MyProjectileInfo.cs:15 value, parser or helper; no world state
Sandbox.ModAPI.MyProjectileInfo.MaxTrajectory	local	-	-	-	0	MyProjectileInfo.cs:40 value, parser or helper; no world state
Sandbox.ModAPI.MyProjectileInfo.Origin	local	-	-	-	1	MyProjectileInfo.cs:25 value, parser or helper; no world state
Sandbox.ModAPI.MyProjectileInfo.OwnerEntity	local	-	-	-	2	MyProjectileInfo.cs:55 value, parser or helper; no world state
Sandbox.ModAPI.MyProjectileInfo.OwnerEntityAbsolute	local	-	-	-	0	MyProjectileInfo.cs:60 value, parser or helper; no world state
Sandbox.ModAPI.MyProjectileInfo.OwningPlayer	local	-	-	-	0	MyProjectileInfo.cs:65 value, parser or helper; no world state
Sandbox.ModAPI.MyProjectileInfo.Position	local	-	-	-	2	MyProjectileInfo.cs:20 value, parser or helper; no world state
Sandbox.ModAPI.MyProjectileInfo.ProjectileAmmoDefinition	local	-	-	-	2	MyProjectileInfo.cs:45 value, parser or helper; no world state
Sandbox.ModAPI.MyProjectileInfo.Velocity	local	-	-	-	1	MyProjectileInfo.cs:30 value, parser or helper; no world state
Sandbox.ModAPI.MyProjectileInfo.WeaponDefinition	local	-	-	-	2	MyProjectileInfo.cs:50 value, parser or helper; no world state
Sandbox.ModAPI.MyScreenSpace.GetSafeGuiArea	client	-	-	-	0	MyScreenSpace.cs:106 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyScreenSpace.MyScissorScope.Dispose	client	-	-	-	0	MyScreenSpace.cs:40 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyScreenSpace.NormalizedSizeToPixels	client	-	-	-	0	MyScreenSpace.cs:81 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyScreenSpace.NormalizedToPixels	client	-	-	-	0	MyScreenSpace.cs:53 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyScreenSpace.PixelSizeToNormalized	client	-	-	-	0	MyScreenSpace.cs:94 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyScreenSpace.PixelsToNormalized	client	-	-	-	0	MyScreenSpace.cs:67 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyScreenSpace.TryGetCursorPosition	client	-	-	-	0	MyScreenSpace.cs:126 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyScreenSpace.TryGetCursorPositionPixels	client	-	-	-	0	MyScreenSpace.cs:143 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyScreenSpace.UsingScissorRectangle	client	-	-	-	0	MyScreenSpace.cs:165 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyScreenSpace.UsingScissorRectanglePixels	client	-	-	-	0	MyScreenSpace.cs:185 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyScreenSpaceLayer.*	local	-	-	-	0	value, parser or helper; no world state
Sandbox.ModAPI.MyScreenSpaceSprite.Draw	client	-	-	-	0	MyScreenSpaceSprite.cs:46 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyScreenSpaceSprite.DrawPivoted	client	-	-	-	0	MyScreenSpaceSprite.cs:90 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyScreenSpaceSprite.DrawPixels	client	-	-	-	0	MyScreenSpaceSprite.cs:69 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyScreenSpaceSprite.GetTextureSize	client	-	-	-	0	MyScreenSpaceSprite.cs:106 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyScreenSpaceSprite.IsMaterialAvailable	client	-	-	-	0	MyScreenSpaceSprite.cs:125 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyScreenSpaceText.Draw	client	-	-	-	0	MyScreenSpaceText.cs:36 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyScreenSpaceText.DrawPixels	client	-	-	-	0	MyScreenSpaceText.cs:56 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyScreenSpaceText.GetCharsThatFit	client	-	-	-	0	MyScreenSpaceText.cs:130 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyScreenSpaceText.GetLineHeight	client	-	-	-	0	MyScreenSpaceText.cs:114 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyScreenSpaceText.IsFontAvailable	client	-	-	-	0	MyScreenSpaceText.cs:145 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyScreenSpaceText.Measure	client	-	-	-	0	MyScreenSpaceText.cs:70 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyScreenSpaceText.MeasurePixels	client	-	-	-	0	MyScreenSpaceText.cs:100 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyStoredGridOwnershipKind.*	local	-	-	-	0	value, parser or helper; no world state
Sandbox.ModAPI.MySurfaceImpactEnum.*	local	-	-	-	0	value, parser or helper; no world state
Sandbox.ModAPI.MyTerminalControlFactoryHelper.GetProperties	local	-	-	-	0	MyTerminalControlFactoryHelper.cs:105 value, parser or helper; no world state
Sandbox.ModAPI.MyTerminalControlFactoryHelper.GetProperty	local	-	-	-	0	MyTerminalControlFactoryHelper.cs:86 value, parser or helper; no world state
Sandbox.ModAPI.MyTerminalControlFactoryHelper.Static	local	-	-	-	0	MyTerminalControlFactoryHelper.cs:21 value, parser or helper; no world state
Sandbox.ModAPI.MyTerminalInterfaceAttribute.LinkedTypes	local	-	-	-	0	MyTerminalInterfaceAttribute.cs:14 value, parser or helper; no world state
Sandbox.ModAPI.MyToolbarApiExtensions.BlockAction	client	-	-	-	0	MyToolbarApiExtensions.cs:48 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyToolbarApiExtensions.Definition	client	-	-	-	0	MyToolbarApiExtensions.cs:88 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyToolbarApiExtensions.GetActionParameterDefinitions	client	-	-	-	0	MyToolbarApiExtensions.cs:128 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyToolbarApiExtensions.GetParameters	client	-	-	-	0	MyToolbarApiExtensions.cs:101 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyToolbarApiExtensions.GroupAction	client	-	-	-	0	MyToolbarApiExtensions.cs:71 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyToolbarApiExtensions.Set	client	-	-	-	0	MyToolbarApiExtensions.cs:27 client UI, input, camera, audio or HUD; nothing to do on a server
Sandbox.ModAPI.MyVRageIngameScriptingAdapter.Clean	local	-	-	-	0	MyVRageIngameScriptingAdapter.cs:110 value, parser or helper; no world state
Sandbox.ModAPI.MyVRageIngameScriptingAdapter.GetBlacklistedIngameEntries	local	-	-	-	0	MyVRageIngameScriptingAdapter.cs:128 value, parser or helper; no world state
Sandbox.ModAPI.MyVRageIngameScriptingAdapter.GetWhitelist	local	-	-	-	0	MyVRageIngameScriptingAdapter.cs:119 value, parser or helper; no world state
Sandbox.ModAPI.MyVRageIngameScriptingAdapter.OpenIngameBlacklistBatch	local	-	-	-	0	MyVRageIngameScriptingAdapter.cs:137 value, parser or helper; no world state
Sandbox.ModAPI.MyVRageIngameScriptingAdapter.ScriptBlacklist	local	-	-	-	0	MyVRageIngameScriptingAdapter.cs:93 value, parser or helper; no world state
Sandbox.ModAPI.OnProjectileAddedRemoved.*	local	-	-	-	0	value, parser or helper; no world state
Sandbox.ModAPI.RepairResult.*	local	-	-	-	0	value, parser or helper; no world state
SpaceEngineers.Game.ModAPI.IMyAirVent.GasInputPerSecond	local	-	-	-	0	MyAirVent.cs:306 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.IMyAirVent.GasOutputPerSecond	local	-	-	-	0	MyAirVent.cs:304 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.IMyAirVent.OxygenSinkInfo	local	-	-	-	0	MyAirVent.cs:280 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.IMyAirVent.SourceComp	local	-	-	-	0	MyAirVent.cs:292 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.IMyButtonPanel.ButtonPressed	local	-	-	-	2	MyButtonPanel.cs:132 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.IMyGravityGenerator.FieldSize	local	-	-	-	1	MyGravityGenerator.cs:77 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.IMyGravityGeneratorBase.GravityAcceleration	local	-	-	-	1	MyGravityGeneratorBase.cs:76 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.IMyGravityGeneratorSphere.Radius	local	-	-	-	1	MyGravityGeneratorSphere.cs:76 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.IMyHeatVent.ColorCurrent	local	-	-	-	0	MyHeatVentBlock.cs:328 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.IMyHeatVent.ColorMaximal	local	-	-	-	0	MyHeatVentBlock.cs:304 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.IMyHeatVent.ColorMinimal	local	-	-	-	0	MyHeatVentBlock.cs:292 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.IMyHeatVent.CurrentPowerLevel	local	-	-	-	0	MyHeatVentBlock.cs:343 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.IMyHeatVent.PowerDependency	local	-	-	-	0	MyHeatVentBlock.cs:316 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.IMyLandingGear.GetAttachedEntity	local	-	-	-	5	MyLandingGear.cs:1481 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.IMyLandingGear.LockModeChanged	local	-	-	-	1	MyLandingGear.cs:433 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.IMyLandingGear.StateChanged	local	-	-	-	0	MyLandingGear.cs:445 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.IMyParachute.DoorStateChanged	local	-	-	-	0	MyParachute.cs:263 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.IMyParachute.ParachuteStateChanged	local	-	-	-	0	MyParachute.cs:277 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.IMySafeZoneBlock.EnableSafeZone	local	-	-	-	2	MySafeZoneBlock.cs:863 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.IMySafeZoneBlock.IsSafeZoneEnabled	local	-	-	-	1	MySafeZoneBlock.cs:871 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.IMyShipMergeBlock.BeforeMerge	local	-	-	-	1	MyShipMergeBlock.cs:174 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.IMyShipMergeBlock.GridCount	local	-	-	-	0	MyShipMergeBlock.cs:162 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.IMyShipMergeBlock.MergeStateChanged	local	-	-	-	0	MyShipMergeBlock.cs:170 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.IMyShipMergeBlock.Other	local	-	-	-	1	MyShipMergeBlock.cs:164 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.IMySolarPanel.SourceComp	local	-	-	-	1	IMySolarPanel.cs:19 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.IMySoundBlock.Range	local	-	-	-	2	MySoundBlock.cs:206 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.IMySoundBlock.Volume	local	-	-	-	0	MySoundBlock.cs:194 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.IMySurvivalBuffsProgression.GetActiveBuffs	local	-	-	-	0	MySurvivalBuffsProgression.cs:81 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.IMyTransponder.SignalReceived	local	-	-	-	2	MyTransponderBlock.cs:67 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.IMyTransponder.TriggeredBySignal	local	-	-	-	0	MyTransponderBlock.cs:85 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.IMyTurretControlBlock.Pilot	local	-	-	-	0	MyTurretControlBlock.cs:1173 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.IMyTurretControlBlock.PreviousControlledEntity	local	-	-	-	0	MyTurretControlBlock.cs:838 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.IMyTurretControlBlock.SetTarget	local	-	-	-	0	MyTurretControlBlock.cs:3912 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.IMyTurretControlBlock.Target	local	-	-	-	1	MyTurretControlBlock.cs:1171 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.IMyTurretControlBlock.TrackTarget	local	-	-	-	1	MyTurretControlBlock.cs:3920 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyAirVent.CanPressurize	local	-	-	-	3	MyAirVent.cs:267 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyAirVent.Depressurize	local	-	-	-	3	MyAirVent.cs:310 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyAirVent.GetOxygenLevel	local	-	-	-	1	MyAirVent.cs:1113 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyAirVent.IsDepressurizing	local	-	-	-	0	MyAirVent.cs:226 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyAirVent.IsPressurized	local	-	-	-	0	MyAirVent.cs:1108 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyAirVent.PressurizationEnabled	local	-	-	-	0	MyAirVent.cs:322 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyAirVent.Status	local	-	-	-	1	MyAirVent.cs:308 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyButtonPanel.AnyoneCanUse	local	-	-	-	2	MyButtonPanel.cs:118 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyButtonPanel.ClearCustomButtonName	local	-	-	-	0	MyButtonPanel.cs:601 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyButtonPanel.GetButtonName	local	-	-	-	1	MyButtonPanel.cs:591 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyButtonPanel.HasCustomButtonName	local	-	-	-	0	MyButtonPanel.cs:606 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyButtonPanel.IsButtonAssigned	local	-	-	-	3	MyButtonPanel.cs:611 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyButtonPanel.SetCustomButtonName	local	-	-	-	1	MyButtonPanel.cs:596 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyGravityGenerator.FieldDepth	local	-	-	-	0	MyGravityGenerator.cs:93 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyGravityGenerator.FieldHeight	local	-	-	-	0	MyGravityGenerator.cs:91 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyGravityGenerator.FieldSize	local	-	-	-	0	MyGravityGenerator.cs:77 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyGravityGenerator.FieldWidth	local	-	-	-	0	MyGravityGenerator.cs:89 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyGravityGeneratorBase.Gravity	local	-	-	-	0	MyGravityGeneratorBase.cs:88 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyGravityGeneratorBase.GravityAcceleration	local	-	-	-	0	MyGravityGeneratorBase.cs:76 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyGravityGeneratorSphere.Radius	local	-	-	-	0	MyGravityGeneratorSphere.cs:76 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyLandingGear.AutoLock	local	-	-	-	3	MyLandingGear.cs:391 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyLandingGear.IsBreakable	local	-	-	-	0	MyLandingGear.cs:387 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyLandingGear.IsLocked	local	-	-	-	5	MyLandingGear.cs:389 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyLandingGear.IsParkingEnabled	local	-	-	-	0	MyLandingGear.cs:403 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyLandingGear.Lock	local	-	-	-	1	MyLandingGear.cs:1666 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyLandingGear.LockMode	local	-	-	-	6	MyLandingGear.cs:415 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyLandingGear.ResetAutoLock	local	-	-	-	0	MyLandingGear.cs:1676 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyLandingGear.ToggleLock	local	-	-	-	2	MyLandingGear.cs:1661 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyLandingGear.Unlock	local	-	-	-	4	MyLandingGear.cs:1671 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyLargeConveyorTurretBase.UseConveyorSystem	local	-	-	-	0	MyLargeConveyorTurretBase.cs:53 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyOxygenFarm.CanProduce	local	-	-	-	2	MyOxygenFarm.cs:89 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyOxygenFarm.GetOutput	local	-	-	-	0	MyOxygenFarm.cs:306 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyParachute.Atmosphere	local	-	-	-	1	MyParachute.cs:242 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyParachute.AutoDeploy	local	-	-	-	2	MyParachute.cs:193 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyParachute.AutoDeployHeight	local	-	-	-	2	MyParachute.cs:178 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyParachute.CloseDoor	local	-	-	-	1	MyParachute.cs:297 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyParachute.GetArtificialGravity	local	-	-	-	0	MyParachute.cs:972 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyParachute.GetNaturalGravity	local	-	-	-	0	MyParachute.cs:967 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyParachute.GetTotalGravity	local	-	-	-	0	MyParachute.cs:977 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyParachute.GetVelocity	local	-	-	-	0	MyParachute.cs:957 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyParachute.OpenDoor	local	-	-	-	1	MyParachute.cs:289 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyParachute.OpenRatio	local	-	-	-	1	MyParachute.cs:166 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyParachute.Status	local	-	-	-	2	MyParachute.cs:154 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyParachute.ToggleDoor	local	-	-	-	0	MyParachute.cs:305 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyParachute.TryGetClosestPoint	local	-	-	-	0	MyParachute.cs:943 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyShipMergeBlock.IsConnected	local	-	-	-	1	MyShipMergeBlock.cs:166 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyShipMergeBlock.State	local	-	-	-	0	MyShipMergeBlock.cs:168 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMySoundBlock.GetSounds	local	-	-	-	1	MySoundBlock.cs:837 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMySoundBlock.IsSoundSelected	local	-	-	-	1	MySoundBlock.cs:242 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMySoundBlock.LoopPeriod	local	-	-	-	1	MySoundBlock.cs:244 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMySoundBlock.Play	local	-	-	-	1	MySoundBlock.cs:827 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMySoundBlock.Range	local	-	-	-	0	MySoundBlock.cs:206 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMySoundBlock.SelectedSound	local	-	-	-	1	MySoundBlock.cs:256 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMySoundBlock.Stop	local	-	-	-	1	MySoundBlock.cs:832 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMySoundBlock.Volume	local	-	-	-	0	MySoundBlock.cs:194 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMySpaceBall.Broadcasting	local	-	-	-	0	MySpaceBall.cs:220 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMySpaceBall.Friction	local	-	-	-	0	MySpaceBall.cs:194 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMySpaceBall.IsBroadcasting	local	-	-	-	0	MySpaceBall.cs:218 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMySpaceBall.Restitution	local	-	-	-	0	MySpaceBall.cs:206 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMySpaceBall.VirtualMass	local	-	-	-	0	MySpaceBall.cs:182 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyTimerBlock.IsCountingDown	local	-	-	-	2	MyTimerBlock.cs:150 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyTimerBlock.Silent	local	-	-	-	2	MyTimerBlock.cs:164 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyTimerBlock.StartCountdown	local	-	-	-	1	MyTimerBlock.cs:617 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyTimerBlock.StopCountdown	local	-	-	-	1	MyTimerBlock.cs:622 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyTimerBlock.Trigger	local	-	-	-	6	MyTimerBlock.cs:612 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyTimerBlock.TriggerDelay	local	-	-	-	1	MyTimerBlock.cs:152 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyTransponder.AllowSignalsFrom	local	-	-	-	0	MyTransponderBlock.cs:48 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyTransponder.Channel	local	-	-	-	0	MyTransponderBlock.cs:29 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyTransponder.SendSignal	node-scoped	B2	no	request (WA)	2	MyTransponderBlock.cs:103 UNCERTAIN: signal receivers in range may be on another node; not traced
SpaceEngineers.Game.ModAPI.Ingame.IMyTurretControlBlock.AIEnabled	local	-	-	-	2	MyTurretControlBlock.cs:852 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyTurretControlBlock.AddTool	local	-	-	-	1	MyTurretControlBlock.cs:4023 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyTurretControlBlock.AddTools	local	-	-	-	0	MyTurretControlBlock.cs:3983 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyTurretControlBlock.AngleDeviation	local	-	-	-	1	MyTurretControlBlock.cs:644 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyTurretControlBlock.AzimuthRotor	local	-	-	-	2	MyTurretControlBlock.cs:1197 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyTurretControlBlock.Camera	local	-	-	-	1	MyTurretControlBlock.cs:1235 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyTurretControlBlock.ClearTools	local	-	-	-	0	MyTurretControlBlock.cs:4039 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyTurretControlBlock.ElevationRotor	local	-	-	-	2	MyTurretControlBlock.cs:1216 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyTurretControlBlock.GetDirectionSource	local	-	-	-	0	MyTurretControlBlock.cs:4057 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyTurretControlBlock.GetShootDirection	local	-	-	-	0	MyTurretControlBlock.cs:4052 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyTurretControlBlock.GetTargetedEntity	local	-	-	-	0	MyTurretControlBlock.cs:3928 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyTurretControlBlock.GetTargetingGroup	local	-	-	-	0	MyTurretControlBlock.cs:3952 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyTurretControlBlock.GetTargetingGroups	local	-	-	-	0	MyTurretControlBlock.cs:3933 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyTurretControlBlock.GetTools	local	-	-	-	0	MyTurretControlBlock.cs:3967 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyTurretControlBlock.HasTarget	local	-	-	-	0	MyTurretControlBlock.cs:1195 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyTurretControlBlock.IsAimed	local	-	-	-	1	MyTurretControlBlock.cs:727 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyTurretControlBlock.IsSunTrackerEnabled	local	-	-	-	2	MyTurretControlBlock.cs:864 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyTurretControlBlock.IsUnderControl	local	-	-	-	1	MyTurretControlBlock.cs:1181 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyTurretControlBlock.MoveIndicator	local	-	-	-	0	MyTurretControlBlock.cs:1256 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyTurretControlBlock.Range	local	-	-	-	0	MyTurretControlBlock.cs:1183 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyTurretControlBlock.RemoveTool	local	-	-	-	0	MyTurretControlBlock.cs:4031 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyTurretControlBlock.RemoveTools	local	-	-	-	0	MyTurretControlBlock.cs:4003 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyTurretControlBlock.RollIndicator	local	-	-	-	0	MyTurretControlBlock.cs:1258 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyTurretControlBlock.RotationIndicator	local	-	-	-	0	MyTurretControlBlock.cs:1254 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyTurretControlBlock.SetTargetingGroup	local	-	-	-	0	MyTurretControlBlock.cs:3962 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyTurretControlBlock.TargetCharacters	local	-	-	-	0	MyTurretControlBlock.cs:956 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyTurretControlBlock.TargetFriends	local	-	-	-	0	MyTurretControlBlock.cs:1032 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyTurretControlBlock.TargetLargeGrids	local	-	-	-	0	MyTurretControlBlock.cs:937 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyTurretControlBlock.TargetMeteors	local	-	-	-	0	MyTurretControlBlock.cs:880 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyTurretControlBlock.TargetMissiles	local	-	-	-	0	MyTurretControlBlock.cs:899 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyTurretControlBlock.TargetNeutrals	local	-	-	-	0	MyTurretControlBlock.cs:994 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyTurretControlBlock.TargetSmallGrids	local	-	-	-	0	MyTurretControlBlock.cs:918 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyTurretControlBlock.TargetStations	local	-	-	-	0	MyTurretControlBlock.cs:975 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyTurretControlBlock.VelocityMultiplierAzimuthRpm	local	-	-	-	1	MyTurretControlBlock.cs:793 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyTurretControlBlock.VelocityMultiplierElevationRpm	local	-	-	-	1	MyTurretControlBlock.cs:805 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.IMyVirtualMass.VirtualMass	local	-	-	-	0	MyVirtualMass.cs:39 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
SpaceEngineers.Game.ModAPI.Ingame.LandingGearMode.*	local	-	-	-	0	value, parser or helper; no world state
SpaceEngineers.Game.ModAPI.Ingame.MergeState.*	local	-	-	-	0	value, parser or helper; no world state
SpaceEngineers.Game.ModAPI.Ingame.VentStatus.*	local	-	-	-	0	value, parser or helper; no world state
VRage.Game.ModAPI.BeforeDamageApplied.*	local	-	-	-	0	value, parser or helper; no world state
VRage.Game.ModAPI.CharacterMovementStateChangedDelegate.*	local	-	-	-	0	value, parser or helper; no world state
VRage.Game.ModAPI.CharacterMovementStateDelegate.*	local	-	-	-	0	value, parser or helper; no world state
VRage.Game.ModAPI.GridLinkTypeEnum.*	local	-	-	-	0	value, parser or helper; no world state
VRage.Game.ModAPI.IHitInfo.Fraction	local	-	-	-	10	MyPhysics.cs:56 value, parser or helper; no world state
VRage.Game.ModAPI.IHitInfo.HitEntity	local	-	-	-	46	MyPhysics.cs:52 value, parser or helper; no world state
VRage.Game.ModAPI.IHitInfo.Normal	local	-	-	-	11	MyPhysics.cs:54 value, parser or helper; no world state
VRage.Game.ModAPI.IHitInfo.Position	local	-	-	-	32	MyPhysics.cs:50 value, parser or helper; no world state
VRage.Game.ModAPI.IMyCharacter.AimedPoint	local	-	-	-	1	MyCharacter.cs:1473 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCharacter.BaseMass	local	-	-	-	1	MyCharacter.cs:2017 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCharacter.CanSprint	local	-	-	-	0	MyCharacter.cs:1670 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCharacter.CharacterDied	local	-	-	-	4	MyCharacter.cs:2798 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCharacter.CharacterGeneralDamageModifier	local	-	-	-	0	MyCharacter.cs:2776 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCharacter.CurrentMass	local	-	-	-	2	MyCharacter.cs:2019 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCharacter.CurrentMovementState	local	-	-	-	9	MyCharacter.cs:2545 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCharacter.Definition	local	-	-	-	11	MyCharacter.cs:2042 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCharacter.EnvironmentOxygenLevel	local	-	-	-	4	MyCharacter.cs:2471 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCharacter.EquippedTool	local	-	-	-	20	MyCharacter.cs:2494 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCharacter.GetOutsideTemperature	local	-	-	-	0	MyCharacter.cs:7554 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCharacter.GetSuitGasFillLevel	local	-	-	-	0	MyCharacter.cs:6973 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCharacter.IsBot	local	-	-	-	5	MyCharacter.cs:2488 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCharacter.IsDead	local	-	-	-	21	MyCharacter.cs:2266 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCharacter.IsPlayer	local	-	-	-	6	MyCharacter.cs:2483 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCharacter.Kill	local	-	-	-	3	MyCharacter.cs:5764 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCharacter.MovementStateChanged	local	-	-	-	0	MyCharacter.cs:2813 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCharacter.OnMovementStateChanged	local	-	-	-	0	MyCharacter.cs:2811 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCharacter.OxygenLevel	local	-	-	-	2	MyCharacter.cs:2473 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCharacter.PreviousMovementState	local	-	-	-	0	MyCharacter.cs:2557 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCharacter.SetCharacterSpeedDebuff	local	-	-	-	0	MyCharacter.cs:10067 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCharacter.SuitEnergyLevel	local	-	-	-	3	MyCharacter.cs:2478 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCharacter.TriggerCharacterAnimationEvent	local	-	-	-	4	MyCharacter.cs:5774 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCharacter.UsingEntity	local	-	-	-	3	MyCharacter.cs:2526 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyComponentStack.BuildIntegrity	local	-	-	-	0	MyComponentStack.cs:95 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyComponentStack.BuildRatio	local	-	-	-	1	MyComponentStack.cs:93 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyComponentStack.GetAllMissingComponents	local	-	-	-	0	MyComponentStack.cs:378 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyComponentStack.GetComponentStackInfo	local	-	-	-	1	MyComponentStack.cs:645 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyComponentStack.GetMissingInfo	local	-	-	-	1	MyComponentStack.cs:331 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyComponentStack.GroupCount	local	-	-	-	0	MyComponentStack.cs:158 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyComponentStack.Integrity	local	-	-	-	0	MyComponentStack.cs:29 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyComponentStack.IntegrityChanged	local	-	-	-	2	MyComponentStack.cs:162 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyComponentStack.IntegrityRatio	local	-	-	-	0	MyComponentStack.cs:89 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyComponentStack.IsBuilt	local	-	-	-	4	MyComponentStack.cs:146 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyComponentStack.IsDestroyed	local	-	-	-	0	MyComponentStack.cs:67 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyComponentStack.IsFullIntegrity	local	-	-	-	0	MyComponentStack.cs:63 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyComponentStack.IsFullyDismounted	local	-	-	-	0	MyComponentStack.cs:65 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyComponentStack.IsFunctional	local	-	-	-	2	MyComponentStack.cs:131 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyComponentStack.IsFunctionalChanged	local	-	-	-	3	MyComponentStack.cs:174 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyComponentStack.MaxIntegrity	local	-	-	-	0	MyComponentStack.cs:31 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyComponentStack.YieldLastComponent	local	-	-	-	0	MyComponentStack.cs:61 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyConfig.AmbientOcclusionEnabled	client	-	-	-	0	MyConfig.cs:1573 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyConfig.AnisotropicFiltering	client	-	-	-	0	MyConfig.cs:1497 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyConfig.AntialiasingMode	client	-	-	-	2	MyConfig.cs:1499 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyConfig.CaptureMouse	client	-	-	-	0	MyConfig.cs:1571 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyConfig.ControlsButtons	client	-	-	-	0	MyConfig.cs:1575 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyConfig.ControlsGeneral	client	-	-	-	0	MyConfig.cs:1577 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyConfig.ControlsHints	client	-	-	-	0	MyConfig.cs:1501 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyConfig.CubeBuilderBuildingMode	client	-	-	-	0	MyConfig.cs:1503 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyConfig.CubeBuilderUseSymmetry	client	-	-	-	0	MyConfig.cs:1505 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyConfig.EnableDamageEffects	client	-	-	-	0	MyConfig.cs:1507 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyConfig.EnableDynamicMusic	client	-	-	-	0	MyConfig.cs:1579 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyConfig.EnableMuteWhenNotInFocus	client	-	-	-	0	MyConfig.cs:1581 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyConfig.EnablePerformanceWarnings	client	-	-	-	0	MyConfig.cs:1583 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyConfig.EnableReverb	client	-	-	-	0	MyConfig.cs:1585 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyConfig.EnableTrading	client	-	-	-	0	MyConfig.cs:1559 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyConfig.EnableVoiceChat	client	-	-	-	0	MyConfig.cs:1587 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyConfig.FieldOfView	client	-	-	-	0	MyConfig.cs:1519 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyConfig.FirstTimeRun	client	-	-	-	0	MyConfig.cs:1589 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyConfig.FlaresIntensity	client	-	-	-	1	MyConfig.cs:1591 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyConfig.GameVolume	client	-	-	-	1	MyConfig.cs:1521 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyConfig.GraphicsRenderer	client	-	-	-	0	MyConfig.cs:1535 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyConfig.GrassDensityFactor	client	-	-	-	0	MyConfig.cs:1593 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyConfig.GrassDrawDistance	client	-	-	-	0	MyConfig.cs:1595 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyConfig.HUDBkOpacity	client	-	-	-	3	MyConfig.cs:1597 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyConfig.HudState	client	-	-	-	8	MyConfig.cs:1529 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyConfig.HudWarnings	client	-	-	-	0	MyConfig.cs:1523 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyConfig.Language	client	-	-	-	38	MyConfig.cs:1525 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyConfig.LightsQuality	client	-	-	-	0	MyConfig.cs:1621 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyConfig.MemoryLimits	client	-	-	-	0	MyConfig.cs:1527 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyConfig.MinimalHud	client	-	-	-	3	MyConfig.cs:1040 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyConfig.ModelQuality	client	-	-	-	0	MyConfig.cs:1599 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyConfig.MusicVolume	client	-	-	-	0	MyConfig.cs:1531 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyConfig.MutedPlayers	client	-	-	-	0	MyConfig.cs:1601 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyConfig.RefreshRate	client	-	-	-	0	MyConfig.cs:1533 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyConfig.RotationHints	client	-	-	-	1	MyConfig.cs:1547 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyConfig.ScreenHeight	client	-	-	-	4	MyConfig.cs:1549 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyConfig.ScreenWidth	client	-	-	-	4	MyConfig.cs:1551 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyConfig.ScreenshotSizeMultiplier	client	-	-	-	0	MyConfig.cs:1603 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyConfig.ShaderQuality	client	-	-	-	0	MyConfig.cs:1605 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyConfig.ShadowQuality	client	-	-	-	0	MyConfig.cs:1553 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyConfig.ShipSoundsAreBasedOnSpeed	client	-	-	-	0	MyConfig.cs:1607 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyConfig.ShowCrosshair	client	-	-	-	0	MyConfig.cs:1555 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyConfig.ShowCrosshair2	client	-	-	-	0	MyConfig.cs:1557 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyConfig.Skin	client	-	-	-	0	MyConfig.cs:1609 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyConfig.TextureQuality	client	-	-	-	1	MyConfig.cs:1561 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyConfig.UIBkOpacity	client	-	-	-	5	MyConfig.cs:1611 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyConfig.UIOpacity	client	-	-	-	1	MyConfig.cs:1613 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyConfig.VegetationDrawDistance	client	-	-	-	0	MyConfig.cs:1615 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyConfig.VerticalSync	client	-	-	-	0	MyConfig.cs:1565 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyConfig.VideoAdapter	client	-	-	-	0	MyConfig.cs:1567 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyConfig.VoiceChatVolume	client	-	-	-	0	MyConfig.cs:1617 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyConfig.VoxelQuality	client	-	-	-	0	MyConfig.cs:1619 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyConfig.VoxelTextureQuality	client	-	-	-	0	MyConfig.cs:1563 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyConfig.WindowMode	client	-	-	-	0	MyConfig.cs:1569 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyConfigDedicated.Administrators	local	-	-	-	0	MyConfigDedicated.cs:502 this node's dedicated server config; the cluster deploys the same settings to every node
VRage.Game.ModAPI.IMyConfigDedicated.AsteroidAmount	local	-	-	-	0	MyConfigDedicated.cs:514 this node's dedicated server config; the cluster deploys the same settings to every node
VRage.Game.ModAPI.IMyConfigDedicated.AutoRestartEnabled	local	-	-	-	0	MyConfigDedicated.cs:610 this node's dedicated server config; the cluster deploys the same settings to every node
VRage.Game.ModAPI.IMyConfigDedicated.AutoRestatTimeInMin	local	-	-	-	0	MyConfigDedicated.cs:622 this node's dedicated server config; the cluster deploys the same settings to every node
VRage.Game.ModAPI.IMyConfigDedicated.AutoUpdateBranchPassword	local	-	-	-	0	MyConfigDedicated.cs:682 this node's dedicated server config; the cluster deploys the same settings to every node
VRage.Game.ModAPI.IMyConfigDedicated.AutoUpdateCheckIntervalInMin	local	-	-	-	0	MyConfigDedicated.cs:646 this node's dedicated server config; the cluster deploys the same settings to every node
VRage.Game.ModAPI.IMyConfigDedicated.AutoUpdateEnabled	local	-	-	-	0	MyConfigDedicated.cs:634 this node's dedicated server config; the cluster deploys the same settings to every node
VRage.Game.ModAPI.IMyConfigDedicated.AutoUpdateRestartDelayInMin	local	-	-	-	0	MyConfigDedicated.cs:658 this node's dedicated server config; the cluster deploys the same settings to every node
VRage.Game.ModAPI.IMyConfigDedicated.AutoUpdateSteamBranch	local	-	-	-	0	MyConfigDedicated.cs:670 this node's dedicated server config; the cluster deploys the same settings to every node
VRage.Game.ModAPI.IMyConfigDedicated.AutodetectDependencies	local	-	-	-	0	MyConfigDedicated.cs:874 this node's dedicated server config; the cluster deploys the same settings to every node
VRage.Game.ModAPI.IMyConfigDedicated.Banned	local	-	-	-	0	MyConfigDedicated.cs:526 this node's dedicated server config; the cluster deploys the same settings to every node
VRage.Game.ModAPI.IMyConfigDedicated.ChatAntiSpamEnabled	local	-	-	-	0	MyConfigDedicated.cs:982 this node's dedicated server config; the cluster deploys the same settings to every node
VRage.Game.ModAPI.IMyConfigDedicated.ConsoleCompatibility	local	-	-	-	0	MyConfigDedicated.cs:958 this node's dedicated server config; the cluster deploys the same settings to every node
VRage.Game.ModAPI.IMyConfigDedicated.CrossPlatform	local	-	-	-	0	MyConfigDedicated.cs:46 this node's dedicated server config; the cluster deploys the same settings to every node
VRage.Game.ModAPI.IMyConfigDedicated.DedicatedId	local	-	-	-	0	MyConfigDedicated.cs:1030 this node's dedicated server config; the cluster deploys the same settings to every node
VRage.Game.ModAPI.IMyConfigDedicated.GenerateRemoteSecurityKey	local	-	-	-	0	MyConfigDedicated.cs:1152 this node's dedicated server config; the cluster deploys the same settings to every node
VRage.Game.ModAPI.IMyConfigDedicated.GetFilePath	local	-	-	-	1	MyConfigDedicated.cs:1121 this node's dedicated server config; the cluster deploys the same settings to every node
VRage.Game.ModAPI.IMyConfigDedicated.GroupID	local	-	-	-	0	MyConfigDedicated.cs:550 this node's dedicated server config; the cluster deploys the same settings to every node
VRage.Game.ModAPI.IMyConfigDedicated.IP	local	-	-	-	0	MyConfigDedicated.cs:70 this node's dedicated server config; the cluster deploys the same settings to every node
VRage.Game.ModAPI.IMyConfigDedicated.IgnoreLastSession	local	-	-	-	0	MyConfigDedicated.cs:334 this node's dedicated server config; the cluster deploys the same settings to every node
VRage.Game.ModAPI.IMyConfigDedicated.Load	local	-	-	-	0	MyConfigDedicated.cs:1075 this node's dedicated server config; the cluster deploys the same settings to every node
VRage.Game.ModAPI.IMyConfigDedicated.LoadWorld	local	-	-	-	0	MyConfigDedicated.cs:562 this node's dedicated server config; the cluster deploys the same settings to every node
VRage.Game.ModAPI.IMyConfigDedicated.ManualActionChatMessage	local	-	-	-	0	MyConfigDedicated.cs:862 this node's dedicated server config; the cluster deploys the same settings to every node
VRage.Game.ModAPI.IMyConfigDedicated.ManualActionDelay	local	-	-	-	0	MyConfigDedicated.cs:850 this node's dedicated server config; the cluster deploys the same settings to every node
VRage.Game.ModAPI.IMyConfigDedicated.MessageOfTheDay	local	-	-	-	0	MyConfigDedicated.cs:586 this node's dedicated server config; the cluster deploys the same settings to every node
VRage.Game.ModAPI.IMyConfigDedicated.MessageOfTheDayUrl	local	-	-	-	0	MyConfigDedicated.cs:598 this node's dedicated server config; the cluster deploys the same settings to every node
VRage.Game.ModAPI.IMyConfigDedicated.NetworkParameters	local	-	-	-	0	MyConfigDedicated.cs:934 this node's dedicated server config; the cluster deploys the same settings to every node
VRage.Game.ModAPI.IMyConfigDedicated.NetworkType	local	-	-	-	0	MyConfigDedicated.cs:910 this node's dedicated server config; the cluster deploys the same settings to every node
VRage.Game.ModAPI.IMyConfigDedicated.PauseGameWhenEmpty	local	-	-	-	0	MyConfigDedicated.cs:574 this node's dedicated server config; the cluster deploys the same settings to every node
VRage.Game.ModAPI.IMyConfigDedicated.Plugins	local	-	-	-	1	MyConfigDedicated.cs:814 this node's dedicated server config; the cluster deploys the same settings to every node
VRage.Game.ModAPI.IMyConfigDedicated.PremadeCheckpointPath	local	-	-	-	1	MyConfigDedicated.cs:190 this node's dedicated server config; the cluster deploys the same settings to every node
VRage.Game.ModAPI.IMyConfigDedicated.RemoteApiEnabled	local	-	-	-	0	MyConfigDedicated.cs:778 this node's dedicated server config; the cluster deploys the same settings to every node
VRage.Game.ModAPI.IMyConfigDedicated.RemoteApiIP	local	-	-	-	0	MyConfigDedicated.cs:802 this node's dedicated server config; the cluster deploys the same settings to every node
VRage.Game.ModAPI.IMyConfigDedicated.RemoteApiPort	local	-	-	-	0	MyConfigDedicated.cs:790 this node's dedicated server config; the cluster deploys the same settings to every node
VRage.Game.ModAPI.IMyConfigDedicated.RemoteSecurityKey	local	-	-	-	0	MyConfigDedicated.cs:766 this node's dedicated server config; the cluster deploys the same settings to every node
VRage.Game.ModAPI.IMyConfigDedicated.Reserved	local	-	-	-	0	MyConfigDedicated.cs:538 this node's dedicated server config; the cluster deploys the same settings to every node
VRage.Game.ModAPI.IMyConfigDedicated.RestartSave	local	-	-	-	0	MyConfigDedicated.cs:694 this node's dedicated server config; the cluster deploys the same settings to every node
VRage.Game.ModAPI.IMyConfigDedicated.SameMessageTimeout	local	-	-	-	0	MyConfigDedicated.cs:994 this node's dedicated server config; the cluster deploys the same settings to every node
VRage.Game.ModAPI.IMyConfigDedicated.Save	local	-	-	-	0	MyConfigDedicated.cs:1126 this node's dedicated server config; the cluster deploys the same settings to every node
VRage.Game.ModAPI.IMyConfigDedicated.SaveChatToLog	local	-	-	-	0	MyConfigDedicated.cs:898 this node's dedicated server config; the cluster deploys the same settings to every node
VRage.Game.ModAPI.IMyConfigDedicated.ServerDescription	local	-	-	-	0	MyConfigDedicated.cs:346 this node's dedicated server config; the cluster deploys the same settings to every node
VRage.Game.ModAPI.IMyConfigDedicated.ServerName	local	-	-	-	0	MyConfigDedicated.cs:706 this node's dedicated server config; the cluster deploys the same settings to every node
VRage.Game.ModAPI.IMyConfigDedicated.ServerPasswordHash	local	-	-	-	0	MyConfigDedicated.cs:742 this node's dedicated server config; the cluster deploys the same settings to every node
VRage.Game.ModAPI.IMyConfigDedicated.ServerPasswordSalt	local	-	-	-	0	MyConfigDedicated.cs:754 this node's dedicated server config; the cluster deploys the same settings to every node
VRage.Game.ModAPI.IMyConfigDedicated.ServerPort	local	-	-	-	0	MyConfigDedicated.cs:94 this node's dedicated server config; the cluster deploys the same settings to every node
VRage.Game.ModAPI.IMyConfigDedicated.SessionSettings	local	-	-	-	0	MyConfigDedicated.cs:718 this node's dedicated server config; the cluster deploys the same settings to every node
VRage.Game.ModAPI.IMyConfigDedicated.SetPassword	local	-	-	-	0	MyConfigDedicated.cs:1131 this node's dedicated server config; the cluster deploys the same settings to every node
VRage.Game.ModAPI.IMyConfigDedicated.SpamMessagesTime	local	-	-	-	0	MyConfigDedicated.cs:1006 this node's dedicated server config; the cluster deploys the same settings to every node
VRage.Game.ModAPI.IMyConfigDedicated.SpamMessagesTimeout	local	-	-	-	0	MyConfigDedicated.cs:1018 this node's dedicated server config; the cluster deploys the same settings to every node
VRage.Game.ModAPI.IMyConfigDedicated.SteamPort	local	-	-	-	0	MyConfigDedicated.cs:82 this node's dedicated server config; the cluster deploys the same settings to every node
VRage.Game.ModAPI.IMyConfigDedicated.VerboseNetworkLogging	local	-	-	-	0	MyConfigDedicated.cs:58 this node's dedicated server config; the cluster deploys the same settings to every node
VRage.Game.ModAPI.IMyConfigDedicated.WatcherInterval	local	-	-	-	0	MyConfigDedicated.cs:826 this node's dedicated server config; the cluster deploys the same settings to every node
VRage.Game.ModAPI.IMyConfigDedicated.WatcherSimulationSpeedMinimum	local	-	-	-	0	MyConfigDedicated.cs:838 this node's dedicated server config; the cluster deploys the same settings to every node
VRage.Game.ModAPI.IMyConfigDedicated.WorldName	local	-	-	-	0	MyConfigDedicated.cs:730 this node's dedicated server config; the cluster deploys the same settings to every node
VRage.Game.ModAPI.IMyContract.Collateral	local	-	-	-	0	MyContract.cs:123 value, parser or helper; no world state
VRage.Game.ModAPI.IMyContract.ContractCondition	local	-	-	-	0	MyContract.cs:141 value, parser or helper; no world state
VRage.Game.ModAPI.IMyContract.Duration	local	-	-	-	0	MyContract.cs:125 value, parser or helper; no world state
VRage.Game.ModAPI.IMyContract.FailReputationPrice	local	-	-	-	0	MyContract.cs:139 value, parser or helper; no world state
VRage.Game.ModAPI.IMyContract.Id	local	-	-	-	0	MyContract.cs:64 value, parser or helper; no world state
VRage.Game.ModAPI.IMyContract.MoneyReward	local	-	-	-	0	MyContract.cs:121 value, parser or helper; no world state
VRage.Game.ModAPI.IMyContract.OnContractAcquired	local	-	-	-	0	MyContract.cs:143 value, parser or helper; no world state
VRage.Game.ModAPI.IMyContract.OnContractFailed	local	-	-	-	0	MyContract.cs:167 value, parser or helper; no world state
VRage.Game.ModAPI.IMyContract.OnContractSucceeded	local	-	-	-	0	MyContract.cs:155 value, parser or helper; no world state
VRage.Game.ModAPI.IMyContract.RewardReputation	local	-	-	-	0	MyContract.cs:137 value, parser or helper; no world state
VRage.Game.ModAPI.IMyContract.StartBlockId	local	-	-	-	0	MyContract.cs:119 value, parser or helper; no world state
VRage.Game.ModAPI.IMyContractAcquisition.EndBlockId	local	-	-	-	0	MyContractObtainAndDeliver.cs:13 value, parser or helper; no world state
VRage.Game.ModAPI.IMyContractAcquisition.ItemAmount	local	-	-	-	0	MyContractObtainAndDeliver.cs:37 value, parser or helper; no world state
VRage.Game.ModAPI.IMyContractAcquisition.ItemTypeId	local	-	-	-	0	MyContractObtainAndDeliver.cs:25 value, parser or helper; no world state
VRage.Game.ModAPI.IMyContractBounty.TargetIdentityId	local	-	-	-	0	MyContractHunt.cs:50 value, parser or helper; no world state
VRage.Game.ModAPI.IMyContractCondition.BlockEndId	local	-	-	-	0	MyContractCondition.cs:20 value, parser or helper; no world state
VRage.Game.ModAPI.IMyContractCondition.ContractId	local	-	-	-	0	MyContractCondition.cs:14 value, parser or helper; no world state
VRage.Game.ModAPI.IMyContractCondition.FactionEndId	local	-	-	-	0	MyContractCondition.cs:18 value, parser or helper; no world state
VRage.Game.ModAPI.IMyContractCondition.FinalizeCondition	local	-	-	-	0	MyContractCondition.cs:44 value, parser or helper; no world state
VRage.Game.ModAPI.IMyContractCondition.Id	local	-	-	-	0	MyContractCondition.cs:10 value, parser or helper; no world state
VRage.Game.ModAPI.IMyContractCondition.IsFinished	local	-	-	-	0	MyContractCondition.cs:12 value, parser or helper; no world state
VRage.Game.ModAPI.IMyContractCondition.StationEndId	local	-	-	-	0	MyContractCondition.cs:16 value, parser or helper; no world state
VRage.Game.ModAPI.IMyContractConditionDeliverItems.ItemAmount	local	-	-	-	0	MyContractConditionDeliverItems.cs:13 value, parser or helper; no world state
VRage.Game.ModAPI.IMyContractConditionDeliverItems.ItemType	local	-	-	-	0	MyContractConditionDeliverItems.cs:11 value, parser or helper; no world state
VRage.Game.ModAPI.IMyContractConditionDeliverItems.ItemVolume	local	-	-	-	0	MyContractConditionDeliverItems.cs:15 value, parser or helper; no world state
VRage.Game.ModAPI.IMyContractConditionDeliverItems.TransferItems	local	-	-	-	0	MyContractConditionDeliverItems.cs:17 value, parser or helper; no world state
VRage.Game.ModAPI.IMyContractCustom.DefinitionId	local	-	-	-	0	MyContractCustom.cs:34 value, parser or helper; no world state
VRage.Game.ModAPI.IMyContractCustom.Description	local	-	-	-	0	MyContractCustom.cs:50 value, parser or helper; no world state
VRage.Game.ModAPI.IMyContractCustom.EndBlockId	local	-	-	-	0	MyContractCustom.cs:36 value, parser or helper; no world state
VRage.Game.ModAPI.IMyContractCustom.Name	local	-	-	-	0	MyContractCustom.cs:48 value, parser or helper; no world state
VRage.Game.ModAPI.IMyContractEscort.End	local	-	-	-	0	MyContractEscort.cs:94 value, parser or helper; no world state
VRage.Game.ModAPI.IMyContractEscort.OwnerIdentityId	local	-	-	-	0	MyContractEscort.cs:96 value, parser or helper; no world state
VRage.Game.ModAPI.IMyContractEscort.Start	local	-	-	-	0	MyContractEscort.cs:92 value, parser or helper; no world state
VRage.Game.ModAPI.IMyContractGridHauling.GridPosition	local	-	-	-	0	MyContractGridHauling.cs:62 value, parser or helper; no world state
VRage.Game.ModAPI.IMyContractGridHauling.PrefabName	local	-	-	-	0	MyContractGridHauling.cs:64 value, parser or helper; no world state
VRage.Game.ModAPI.IMyContractGridHauling.RiskLevel	local	-	-	-	0	MyContractGridHauling.cs:66 value, parser or helper; no world state
VRage.Game.ModAPI.IMyContractGridHauling.TargetGridId	local	-	-	-	0	MyContractGridHauling.cs:60 value, parser or helper; no world state
VRage.Game.ModAPI.IMyContractHauling.EndBlockId	local	-	-	-	0	MyContractDeliver.cs:28 value, parser or helper; no world state
VRage.Game.ModAPI.IMyContractHauling.EndFactionStationId	local	-	-	-	0	MyContractDeliver.cs:40 value, parser or helper; no world state
VRage.Game.ModAPI.IMyContractPvEBounty.TargetFactionId	local	-	-	-	0	MyContractPvEBounty.cs:66 value, parser or helper; no world state
VRage.Game.ModAPI.IMyContractPvEBounty.TargetPosition	local	-	-	-	0	MyContractPvEBounty.cs:68 value, parser or helper; no world state
VRage.Game.ModAPI.IMyContractPvEBounty.TargetSpawnGroup	local	-	-	-	0	MyContractPvEBounty.cs:72 value, parser or helper; no world state
VRage.Game.ModAPI.IMyContractRepair.GridId	local	-	-	-	0	MyContractRepair.cs:42 value, parser or helper; no world state
VRage.Game.ModAPI.IMyContractSalvage.GridPosition	local	-	-	-	0	MyContractSalvage.cs:61 value, parser or helper; no world state
VRage.Game.ModAPI.IMyContractSalvage.PrefabName	local	-	-	-	0	MyContractSalvage.cs:63 value, parser or helper; no world state
VRage.Game.ModAPI.IMyContractSalvage.TargetGridId	local	-	-	-	0	MyContractSalvage.cs:67 value, parser or helper; no world state
VRage.Game.ModAPI.IMyContractSearch.SearchRadius	local	-	-	-	0	MyContractFind.cs:70 value, parser or helper; no world state
VRage.Game.ModAPI.IMyContractSearch.TargetGridId	local	-	-	-	0	MyContractFind.cs:68 value, parser or helper; no world state
VRage.Game.ModAPI.IMyContractSystem.AcceptContract	node-scoped	B2	no	request (WA)	0	MySessionComponentContractSystem.cs:685 UNCERTAIN: contract timers and payouts run on the WA only (cluster: Patches/MySessionComponentContractSystemPatch.cs:16); whether a node's contract add/remove reaches the WA was not verified
VRage.Game.ModAPI.IMyContractSystem.ActiveContracts	node-scoped	B2	no	request (WA)	0	MySessionComponentContractSystem.cs:101 UNCERTAIN: contract timers and payouts run on the WA only (cluster: Patches/MySessionComponentContractSystemPatch.cs:16); whether a node's contract add/remove reaches the WA was not verified
VRage.Game.ModAPI.IMyContractSystem.AddContract	node-scoped	B2	no	request (WA)	2	MySessionComponentContractSystem.cs:1708 UNCERTAIN: contract timers and payouts run on the WA only (cluster: Patches/MySessionComponentContractSystemPatch.cs:16); whether a node's contract add/remove reaches the WA was not verified
VRage.Game.ModAPI.IMyContractSystem.CustomActivateContract	node-scoped	B2	no	request (WA)	1	MySessionComponentContractSystem.cs:131 UNCERTAIN: contract timers and payouts run on the WA only (cluster: Patches/MySessionComponentContractSystemPatch.cs:16); whether a node's contract add/remove reaches the WA was not verified
VRage.Game.ModAPI.IMyContractSystem.CustomCanActivateContract	node-scoped	B2	no	request (WA)	0	MySessionComponentContractSystem.cs:115 UNCERTAIN: contract timers and payouts run on the WA only (cluster: Patches/MySessionComponentContractSystemPatch.cs:16); whether a node's contract add/remove reaches the WA was not verified
VRage.Game.ModAPI.IMyContractSystem.CustomCleanUp	node-scoped	B2	no	request (WA)	0	MySessionComponentContractSystem.cs:156 UNCERTAIN: contract timers and payouts run on the WA only (cluster: Patches/MySessionComponentContractSystemPatch.cs:16); whether a node's contract add/remove reaches the WA was not verified
VRage.Game.ModAPI.IMyContractSystem.CustomConditionFinished	node-scoped	B2	no	request (WA)	0	MySessionComponentContractSystem.cs:126 UNCERTAIN: contract timers and payouts run on the WA only (cluster: Patches/MySessionComponentContractSystemPatch.cs:16); whether a node's contract add/remove reaches the WA was not verified
VRage.Game.ModAPI.IMyContractSystem.CustomFail	node-scoped	B2	no	request (WA)	0	MySessionComponentContractSystem.cs:151 UNCERTAIN: contract timers and payouts run on the WA only (cluster: Patches/MySessionComponentContractSystemPatch.cs:16); whether a node's contract add/remove reaches the WA was not verified
VRage.Game.ModAPI.IMyContractSystem.CustomFailFor	node-scoped	B2	no	request (WA)	0	MySessionComponentContractSystem.cs:136 UNCERTAIN: contract timers and payouts run on the WA only (cluster: Patches/MySessionComponentContractSystemPatch.cs:16); whether a node's contract add/remove reaches the WA was not verified
VRage.Game.ModAPI.IMyContractSystem.CustomFinish	node-scoped	B2	no	request (WA)	0	MySessionComponentContractSystem.cs:146 UNCERTAIN: contract timers and payouts run on the WA only (cluster: Patches/MySessionComponentContractSystemPatch.cs:16); whether a node's contract add/remove reaches the WA was not verified
VRage.Game.ModAPI.IMyContractSystem.CustomFinishCondition	node-scoped	B2	no	request (WA)	1	MySessionComponentContractSystem.cs:109 UNCERTAIN: contract timers and payouts run on the WA only (cluster: Patches/MySessionComponentContractSystemPatch.cs:16); whether a node's contract add/remove reaches the WA was not verified
VRage.Game.ModAPI.IMyContractSystem.CustomFinishFor	node-scoped	B2	no	request (WA)	0	MySessionComponentContractSystem.cs:141 UNCERTAIN: contract timers and payouts run on the WA only (cluster: Patches/MySessionComponentContractSystemPatch.cs:16); whether a node's contract add/remove reaches the WA was not verified
VRage.Game.ModAPI.IMyContractSystem.CustomNeedsUpdate	node-scoped	B2	no	request (WA)	0	MySessionComponentContractSystem.cs:121 UNCERTAIN: contract timers and payouts run on the WA only (cluster: Patches/MySessionComponentContractSystemPatch.cs:16); whether a node's contract add/remove reaches the WA was not verified
VRage.Game.ModAPI.IMyContractSystem.CustomTimeRanOut	node-scoped	B2	no	request (WA)	1	MySessionComponentContractSystem.cs:161 UNCERTAIN: contract timers and payouts run on the WA only (cluster: Patches/MySessionComponentContractSystemPatch.cs:16); whether a node's contract add/remove reaches the WA was not verified
VRage.Game.ModAPI.IMyContractSystem.CustomUpdate	node-scoped	B2	no	request (WA)	0	MySessionComponentContractSystem.cs:166 UNCERTAIN: contract timers and payouts run on the WA only (cluster: Patches/MySessionComponentContractSystemPatch.cs:16); whether a node's contract add/remove reaches the WA was not verified
VRage.Game.ModAPI.IMyContractSystem.GetAvailableContractsForBlock	node-scoped	B2	no	request (WA)	0	MySessionComponentContractSystem.cs:1461 UNCERTAIN: contract timers and payouts run on the WA only (cluster: Patches/MySessionComponentContractSystemPatch.cs:16); whether a node's contract add/remove reaches the WA was not verified
VRage.Game.ModAPI.IMyContractSystem.GetAvailableContractsForFactionStation	node-scoped	B2	no	request (WA)	0	MySessionComponentContractSystem.cs:1435 UNCERTAIN: contract timers and payouts run on the WA only (cluster: Patches/MySessionComponentContractSystemPatch.cs:16); whether a node's contract add/remove reaches the WA was not verified
VRage.Game.ModAPI.IMyContractSystem.GetContractById	node-scoped	B2	no	request (WA)	0	MySessionComponentContractSystem.cs:1648 UNCERTAIN: contract timers and payouts run on the WA only (cluster: Patches/MySessionComponentContractSystemPatch.cs:16); whether a node's contract add/remove reaches the WA was not verified
VRage.Game.ModAPI.IMyContractSystem.GetContractDefinitionId	node-scoped	B2	no	request (WA)	0	MySessionComponentContractSystem.cs:1929 UNCERTAIN: contract timers and payouts run on the WA only (cluster: Patches/MySessionComponentContractSystemPatch.cs:16); whether a node's contract add/remove reaches the WA was not verified
VRage.Game.ModAPI.IMyContractSystem.GetContractState	node-scoped	B2	no	request (WA)	0	MySessionComponentContractSystem.cs:1871 UNCERTAIN: contract timers and payouts run on the WA only (cluster: Patches/MySessionComponentContractSystemPatch.cs:16); whether a node's contract add/remove reaches the WA was not verified
VRage.Game.ModAPI.IMyContractSystem.InactiveContracts	node-scoped	B2	no	request (WA)	0	MySessionComponentContractSystem.cs:103 UNCERTAIN: contract timers and payouts run on the WA only (cluster: Patches/MySessionComponentContractSystemPatch.cs:16); whether a node's contract add/remove reaches the WA was not verified
VRage.Game.ModAPI.IMyContractSystem.IsContractActive	node-scoped	B2	no	request (WA)	0	MySessionComponentContractSystem.cs:1862 UNCERTAIN: contract timers and payouts run on the WA only (cluster: Patches/MySessionComponentContractSystemPatch.cs:16); whether a node's contract add/remove reaches the WA was not verified
VRage.Game.ModAPI.IMyContractSystem.RemoveContract	node-scoped	B2	no	request (WA)	2	MySessionComponentContractSystem.cs:1842 UNCERTAIN: contract timers and payouts run on the WA only (cluster: Patches/MySessionComponentContractSystemPatch.cs:16); whether a node's contract add/remove reaches the WA was not verified
VRage.Game.ModAPI.IMyContractSystem.TryAbandonCustomContract	node-scoped	B2	no	request (WA)	0	MySessionComponentContractSystem.cs:1914 UNCERTAIN: contract timers and payouts run on the WA only (cluster: Patches/MySessionComponentContractSystemPatch.cs:16); whether a node's contract add/remove reaches the WA was not verified
VRage.Game.ModAPI.IMyContractSystem.TryFailCustomContract	node-scoped	B2	no	request (WA)	2	MySessionComponentContractSystem.cs:1899 UNCERTAIN: contract timers and payouts run on the WA only (cluster: Patches/MySessionComponentContractSystemPatch.cs:16); whether a node's contract add/remove reaches the WA was not verified
VRage.Game.ModAPI.IMyContractSystem.TryFinishCustomContract	node-scoped	B2	no	request (WA)	2	MySessionComponentContractSystem.cs:1885 UNCERTAIN: contract timers and payouts run on the WA only (cluster: Patches/MySessionComponentContractSystemPatch.cs:16); whether a node's contract add/remove reaches the WA was not verified
VRage.Game.ModAPI.IMyControllerInfo.ControlAcquired	local	-	-	-	1	MyControllerInfo.cs:48 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyControllerInfo.ControlReleased	local	-	-	-	1	MyControllerInfo.cs:60 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyControllerInfo.Controller	local	-	-	-	5	MyControllerInfo.cs:40 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyControllerInfo.ControllingIdentityId	local	-	-	-	13	MyControllerInfo.cs:42 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyControllerInfo.IsLocallyControlled	local	-	-	-	0	MyControllerInfo.cs:104 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyControllerInfo.IsLocallyHumanControlled	local	-	-	-	1	MyControllerInfo.cs:109 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyControllerInfo.IsRemotelyControlled	local	-	-	-	0	MyControllerInfo.cs:114 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeBlock.AddUpgradeValue	local	-	-	-	4	MyCubeBlock.cs:1759 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeBlock.CalcLocalMatrix	local	-	-	-	0	MyCubeBlock.cs:2007 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeBlock.CalculateCurrentModel	local	-	-	-	0	MyCubeBlock.cs:2012 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeBlock.CheckConnectionAllowed	local	-	-	-	0	MyCubeBlock.cs:377 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeBlock.CubeGrid	local	-	-	-	122	MyCubeBlock.cs:373 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeBlock.CubeGridChanged	local	-	-	-	3	MyCubeBlock.cs:441 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeBlock.GetObjectBuilderCubeBlock	local	-	-	-	9	MyCubeBlock.cs:2017 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeBlock.Init	local	-	-	-	1	MyCubeBlock.cs:2032 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeBlock.IsWorkingChanged	local	-	-	-	32	MyCubeBlock.cs:429 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeBlock.OnBuildSuccess	local	-	-	-	0	MyCubeBlock.cs:2042 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeBlock.OnDestroy	local	-	-	-	0	MyCubeBlock.cs:2052 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeBlock.OnModelChange	local	-	-	-	0	MyCubeBlock.cs:2057 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeBlock.OnRegisteredToGridSystems	local	-	-	-	0	MyCubeBlock.cs:2062 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeBlock.OnRemovedByCubeBuilder	local	-	-	-	0	MyCubeBlock.cs:2067 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeBlock.OnUnregisteredFromGridSystems	local	-	-	-	0	MyCubeBlock.cs:2072 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeBlock.OnUpgradeValuesChanged	local	-	-	-	4	MyCubeBlock.cs:427 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeBlock.RaycastDetectors	local	-	-	-	0	MyCubeBlock.cs:2077 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeBlock.ReloadDetectors	local	-	-	-	0	MyCubeBlock.cs:2082 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeBlock.RemoveEffect	local	-	-	-	0	MyCubeBlock.cs:2133 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeBlock.ResourceSink	local	-	-	-	13	MyCubeBlock.cs:395 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeBlock.SetDamageEffect	local	-	-	-	5	MyCubeBlock.cs:2097 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeBlock.SetEffect	local	-	-	-	0	MyCubeBlock.cs:2123 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeBlock.SlimBlock	local	-	-	-	65	MyCubeBlock.cs:391 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeBlock.UpgradeValues	local	-	-	-	9	MyCubeBlock.cs:359 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeBuilder.Activate	client	-	-	-	1	MyCubeBuilder.cs:5657 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyCubeBuilder.AddConstruction	client	-	-	-	0	MyCubeBuilder.cs:5647 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyCubeBuilder.BlockCreationIsActivated	client	-	-	-	1	MyCubeBuilder.cs:886 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyCubeBuilder.Deactivate	client	-	-	-	1	MyCubeBuilder.cs:5662 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyCubeBuilder.DeactivateBlockCreation	client	-	-	-	1	MyCubeBuilder.cs:5667 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyCubeBuilder.FindClosestGrid	client	-	-	-	1	MyCubeBuilder.cs:5652 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyCubeBuilder.FreezeGizmo	client	-	-	-	2	MyCubeBuilder.cs:888 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyCubeBuilder.IsActivated	client	-	-	-	4	MyCubeBuilder.cs:936 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyCubeBuilder.ShowRemoveGizmo	client	-	-	-	1	MyCubeBuilder.cs:900 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyCubeBuilder.StartNewGridPlacement	client	-	-	-	0	MyCubeBuilder.cs:5672 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyCubeBuilder.UseSymmetry	client	-	-	-	1	MyCubeBuilder.cs:912 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyCubeBuilder.UseTransparency	client	-	-	-	1	MyCubeBuilder.cs:924 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyCubeGrid.AddBlock	local	-	-	-	9	MyCubeGrid.cs:16248 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.ApplyDeformation	local	-	-	-	0	MyCubeGrid.cs:16515 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.ApplyDestructionDeformation	local	-	-	-	2	MyCubeGrid.cs:16253 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.BigOwners	local	-	-	-	37	MyCubeGrid.cs:3731 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.CalculateMergeTransform	local	-	-	-	1	MyCubeGrid.cs:16261 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.CanAddCube	local	-	-	-	2	MyCubeGrid.cs:16458 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.CanAddCubes	local	-	-	-	1	MyCubeGrid.cs:16463 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.CanMergeCubes	local	-	-	-	0	MyCubeGrid.cs:16270 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.ChangeGridOwnership	local	-	-	-	3	MyCubeGrid.cs:16353 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.ClearSymmetries	local	-	-	-	1	MyCubeGrid.cs:16358 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.ColorBlocks	local	-	-	-	6	MyCubeGrid.cs:16363 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.ControlSystem	local	-	-	-	10	MyCubeGrid.cs:3848 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.ConveyorSystem	local	-	-	-	0	MyCubeGrid.cs:3844 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.CustomName	local	-	-	-	29	MyCubeGrid.cs:3704 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.FixTargetCube	local	-	-	-	2	MyCubeGrid.cs:16373 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.GasSystem	local	-	-	-	7	MyCubeGrid.cs:3855 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.GetBlocks	local	-	-	-	65	MyCubeGrid.cs:16279 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.GetBlocksInsideSphere	local	-	-	-	7	MyCubeGrid.cs:16290 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.GetClosestCorner	local	-	-	-	0	MyCubeGrid.cs:16378 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.GetCubeBlock	local	-	-	-	52	MyCubeGrid.cs:16302 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.GetFatBlocks	local	-	-	-	22	MyCubeGrid.cs:16510 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.GetGridGroup	local	-	-	-	17	MyCubeGrid.cs:16505 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.GetLineIntersectionExactAll	local	-	-	-	4	MyCubeGrid.cs:16312 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.GetLineIntersectionExactGrid	local	-	-	-	0	MyCubeGrid.cs:16383 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.GetMaxThrustInDirection	local	-	-	-	0	MyCubeGrid.cs:16530 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.GridPresenceTier	local	-	-	-	0	MyCubeGrid.cs:3838 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.GridPresenceTierChanged	local	-	-	-	1	MyCubeGrid.cs:4057 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.InvokeDepressurizeEffect	local	-	-	-	0	MyCubeGrid.cs:12926 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.IsBlockTrasferInProgress	local	-	-	-	0	MyCubeGrid.cs:3341 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.IsInSameLogicalGroupAs	local	-	-	-	6	MyCubeGrid.cs:16478 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.IsNpcSpawnedGrid	local	-	-	-	1	MyCubeGrid.cs:2994 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.IsRespawnGrid	local	-	-	-	3	MyCubeGrid.cs:3735 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.IsRoomAtPositionAirtight	local	-	-	-	9	MyCubeGrid.cs:16496 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.IsSameConstructAs	local	-	-	-	11	MyCubeGrid.cs:16307 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.IsStatic	local	-	-	-	48	MyCubeGrid.cs:3747 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.IsTouchingAnyNeighbor	local	-	-	-	0	MyCubeGrid.cs:16388 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.JumpSystem	local	-	-	-	4	MyCubeGrid.cs:3850 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.MergeGrid_MergeBlock	local	-	-	-	0	MyCubeGrid.cs:16320 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.NaturalGravity	local	-	-	-	3	MyCubeGrid.cs:2962 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.OnBlockAdded	local	-	-	-	35	MyCubeGrid.cs:3973 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.OnBlockIntegrityChanged	local	-	-	-	9	MyCubeGrid.cs:4045 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.OnBlockOwnershipChanged	local	-	-	-	3	MyCubeGrid.cs:3997 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.OnBlockRemoved	local	-	-	-	26	MyCubeGrid.cs:3985 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.OnConvertToDynamic	local	-	-	-	0	MyCubeGrid.cs:8267 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.OnGridBlockDamaged	local	-	-	-	0	MyCubeGrid.cs:3965 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.OnGridChanged	local	-	-	-	0	MyCubeGrid.cs:4009 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.OnGridMerge	local	-	-	-	11	MyCubeGrid.cs:4085 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.OnGridSplit	local	-	-	-	17	MyCubeGrid.cs:4021 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.OnIsStaticChanged	local	-	-	-	9	MyCubeGrid.cs:4033 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.OnMaxThrustChanged	local	-	-	-	0	MyCubeGrid.cs:4097 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.OnNaturalGravityChanged	local	-	-	-	0	MyCubeGrid.cs:3881 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.OnTargetLocked	local	-	-	-	0	MyCubeGrid.cs:3967 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.PlayerPresenceTier	local	-	-	-	0	MyCubeGrid.cs:3840 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.PlayerPresenceTierChanged	local	-	-	-	0	MyCubeGrid.cs:4069 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.RayCastBlocks	local	-	-	-	8	MyCubeGrid.cs:16393 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.RayCastCells	local	-	-	-	4	MyCubeGrid.cs:16398 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.RazeBlock	local	-	-	-	9	MyCubeGrid.cs:16403 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.RazeBlocks	local	-	-	-	1	MyCubeGrid.cs:16408 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.RemoveBlock	local	-	-	-	13	MyCubeGrid.cs:16329 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.RemoveDestroyedBlock	local	-	-	-	3	MyCubeGrid.cs:16337 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.ResourceDistributor	local	-	-	-	15	MyCubeGrid.cs:3842 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.RvoAgentId	local	-	-	-	0	MyCubeGrid.cs:2992 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.SkinBlocks	local	-	-	-	7	MyCubeGrid.cs:16368 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.SmallOwners	local	-	-	-	10	MyCubeGrid.cs:3733 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.SpeedChanged	local	-	-	-	1	MyCubeGrid.cs:3875 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.Split	local	-	-	-	1	MyCubeGrid.cs:16473 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.SplitByPlane	local	-	-	-	0	MyCubeGrid.cs:16468 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.UpdateBlockNeighbours	local	-	-	-	1	MyCubeGrid.cs:16345 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.UpdateOwnership	local	-	-	-	1	MyCubeGrid.cs:10975 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.WeaponSystem	local	-	-	-	1	MyCubeGrid.cs:3846 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.WillRemoveBlockSplitGrid	local	-	-	-	1	MyCubeGrid.cs:16423 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.XSymmetryOdd	local	-	-	-	2	MyCubeGrid.cs:3802 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.XSymmetryPlane	local	-	-	-	2	MyCubeGrid.cs:3766 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.YSymmetryOdd	local	-	-	-	2	MyCubeGrid.cs:3814 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.YSymmetryPlane	local	-	-	-	2	MyCubeGrid.cs:3778 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.ZSymmetryOdd	local	-	-	-	2	MyCubeGrid.cs:3826 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyCubeGrid.ZSymmetryPlane	local	-	-	-	2	MyCubeGrid.cs:3790 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyDLC.AppId	local	-	-	-	1	MyDlcDefinition.cs:37 value, parser or helper; no world state
VRage.Game.ModAPI.IMyDLC.Badge	local	-	-	-	0	MyDlcDefinition.cs:59 value, parser or helper; no world state
VRage.Game.ModAPI.IMyDLC.Description	local	-	-	-	0	MyDlcDefinition.cs:49 value, parser or helper; no world state
VRage.Game.ModAPI.IMyDLC.DisplayName	local	-	-	-	2	MyDlcDefinition.cs:44 value, parser or helper; no world state
VRage.Game.ModAPI.IMyDLC.Icon	local	-	-	-	0	MyDlcDefinition.cs:54 value, parser or helper; no world state
VRage.Game.ModAPI.IMyDLC.Name	local	-	-	-	1	MyDlcDefinition.cs:39 value, parser or helper; no world state
VRage.Game.ModAPI.IMyDLCs.ContainsRequiredDLC	node-scoped	B2	no	request (WA)	0	MySessionComponentDLC.cs:373 UNCERTAIN: DLC ownership checks per client; nodes behind the gateway see the client only through Direct Transport (SE-1158), not verified for the mod API
VRage.Game.ModAPI.IMyDLCs.DLCInstalled	node-scoped	B2	no	request (WA)	1	MySessionComponentDLC.cs:56 UNCERTAIN: DLC ownership checks per client; nodes behind the gateway see the client only through Direct Transport (SE-1158), not verified for the mod API
VRage.Game.ModAPI.IMyDLCs.GetAvailableClientDLCIds	node-scoped	B2	no	request (WA)	0	MySessionComponentDLC.cs:378 UNCERTAIN: DLC ownership checks per client; nodes behind the gateway see the client only through Direct Transport (SE-1158), not verified for the mod API
VRage.Game.ModAPI.IMyDLCs.GetDLC	node-scoped	B2	no	request (WA)	2	MySessionComponentDLC.cs:389 UNCERTAIN: DLC ownership checks per client; nodes behind the gateway see the client only through Direct Transport (SE-1158), not verified for the mod API
VRage.Game.ModAPI.IMyDLCs.GetDLCs	node-scoped	B2	no	request (WA)	0	MySessionComponentDLC.cs:399 UNCERTAIN: DLC ownership checks per client; nodes behind the gateway see the client only through Direct Transport (SE-1158), not verified for the mod API
VRage.Game.ModAPI.IMyDLCs.GetFirstMissingDefinitionDLC	node-scoped	B2	no	request (WA)	0	MySessionComponentDLC.cs:409 UNCERTAIN: DLC ownership checks per client; nodes behind the gateway see the client only through Direct Transport (SE-1158), not verified for the mod API
VRage.Game.ModAPI.IMyDLCs.GetRequiredDLCTooltip	node-scoped	B2	no	request (WA)	0	MySessionComponentDLC.cs:414 UNCERTAIN: DLC ownership checks per client; nodes behind the gateway see the client only through Direct Transport (SE-1158), not verified for the mod API
VRage.Game.ModAPI.IMyDLCs.HasDLC	node-scoped	B2	no	request (WA)	3	MySessionComponentDLC.cs:434 UNCERTAIN: DLC ownership checks per client; nodes behind the gateway see the client only through Direct Transport (SE-1158), not verified for the mod API
VRage.Game.ModAPI.IMyDLCs.HasDefinitionDLC	node-scoped	B2	no	request (WA)	3	MySessionComponentDLC.cs:424 UNCERTAIN: DLC ownership checks per client; nodes behind the gateway see the client only through Direct Transport (SE-1158), not verified for the mod API
VRage.Game.ModAPI.IMyDLCs.IsDLCSupported	node-scoped	B2	no	request (WA)	0	MySessionComponentDLC.cs:448 UNCERTAIN: DLC ownership checks per client; nodes behind the gateway see the client only through Direct Transport (SE-1158), not verified for the mod API
VRage.Game.ModAPI.IMyDLCs.TryGetDLC	node-scoped	B2	no	request (WA)	2	MySessionComponentDLC.cs:453 UNCERTAIN: DLC ownership checks per client; nodes behind the gateway see the client only through Direct Transport (SE-1158), not verified for the mod API
VRage.Game.ModAPI.IMyDamageSystem.RaiseAfterDamageApplied	local	-	-	-	1	MyDamageSystem.cs:78 an interaction happens on exactly one node (proximity rule), so the handler fires once cluster-wide
VRage.Game.ModAPI.IMyDamageSystem.RaiseBeforeDamageApplied	local	-	-	-	1	MyDamageSystem.cs:57 an interaction happens on exactly one node (proximity rule), so the handler fires once cluster-wide
VRage.Game.ModAPI.IMyDamageSystem.RegisterAfterDamageHandler	local	-	-	-	5	MyDamageSystem.cs:115 an interaction happens on exactly one node (proximity rule), so the handler fires once cluster-wide
VRage.Game.ModAPI.IMyDamageSystem.RegisterBeforeDamageHandler	local	-	-	-	23	MyDamageSystem.cs:103 an interaction happens on exactly one node (proximity rule), so the handler fires once cluster-wide
VRage.Game.ModAPI.IMyDamageSystem.RegisterDestroyHandler	local	-	-	-	1	MyDamageSystem.cs:91 an interaction happens on exactly one node (proximity rule), so the handler fires once cluster-wide
VRage.Game.ModAPI.IMyEntityController.ControlledEntity	local	-	-	-	50	MyEntityController.cs:28 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyEntityController.ControlledEntityChanged	local	-	-	-	6	MyEntityController.cs:35 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyEntityController.TakeControl	local	-	-	-	3	MyEntityController.cs:178 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyFaction.AcceptHumans	replicated	-	-	-	6	MyFaction.cs:123 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFaction.AutoAcceptMember	replicated	-	-	-	3	MyFaction.cs:119 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFaction.AutoAcceptPeace	replicated	-	-	-	3	MyFaction.cs:121 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFaction.CustomColor	replicated	-	-	-	4	MyFaction.cs:79 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFaction.Description	replicated	-	-	-	4	MyFaction.cs:113 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFaction.FactionIcon	replicated	-	-	-	3	MyFaction.cs:117 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFaction.FactionId	replicated	-	-	-	27	MyFaction.cs:107 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFaction.FactionType	replicated	-	-	-	2	MyFaction.cs:169 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFaction.FounderId	replicated	-	-	-	9	MyFaction.cs:143 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFaction.GetBalanceShortString	replicated	-	-	-	1	MyFaction.cs:779 banking is executed on the WA (cluster: Cluster/Patches/MyBankingSystemPatch.cs:15)
VRage.Game.ModAPI.IMyFaction.GetStationById	replicated	-	-	-	1	MyFaction.cs:796 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFaction.HasLastMember	replicated	-	-	-	0	MyFaction.cs:395 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFaction.IconColor	replicated	-	-	-	4	MyFaction.cs:81 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFaction.IsEnemy	replicated	-	-	-	1	MyFaction.cs:758 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFaction.IsEveryoneNpc	replicated	-	-	-	12	MyFaction.cs:383 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFaction.IsFounder	replicated	-	-	-	2	MyFaction.cs:738 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFaction.IsFriendly	replicated	-	-	-	1	MyFaction.cs:763 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFaction.IsLeader	replicated	-	-	-	2	MyFaction.cs:743 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFaction.IsMember	replicated	-	-	-	4	MyFaction.cs:748 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFaction.IsNeutral	replicated	-	-	-	1	MyFaction.cs:753 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFaction.JoinRequests	replicated	-	-	-	1	MyFaction.cs:173 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFaction.Members	replicated	-	-	-	12	MyFaction.cs:171 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFaction.Name	replicated	-	-	-	13	MyFaction.cs:111 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFaction.ObjectivePercentageCompleted	replicated	-	-	-	0	MyFaction.cs:157 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFaction.PrivateInfo	replicated	-	-	-	3	MyFaction.cs:115 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFaction.RequestChangeBalance	replicated	-	-	-	3	MyFaction.cs:788 banking is executed on the WA (cluster: Cluster/Patches/MyBankingSystemPatch.cs:15)
VRage.Game.ModAPI.IMyFaction.Score	replicated	-	-	-	0	MyFaction.cs:145 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFaction.Stations	replicated	-	-	-	1	MyFaction.cs:175 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFaction.Tag	replicated	-	-	-	16	MyFaction.cs:109 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFaction.TryGetBalanceInfo	replicated	-	-	-	3	MyFaction.cs:768 banking is executed on the WA (cluster: Cluster/Patches/MyBankingSystemPatch.cs:15)
VRage.Game.ModAPI.IMyFactionCollection.AcceptJoin	replicated	-	-	-	1	MyFactionCollection.cs:3614 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFactionCollection.AcceptPeace	replicated	-	-	-	2	MyFactionCollection.cs:3594 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFactionCollection.AddNewNPCToFaction	replicated	-	-	-	2	MyFactionCollection.cs:1268 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFactionCollection.AddPlayerToFaction	replicated	-	-	-	1	MyFactionCollection.cs:3525 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFactionCollection.AreFactionsEnemies	replicated	-	-	-	4	MyFactionCollection.cs:3564 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFactionCollection.CancelJoinRequest	replicated	-	-	-	1	MyFactionCollection.cs:3609 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFactionCollection.CancelPeaceRequest	replicated	-	-	-	1	MyFactionCollection.cs:3589 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFactionCollection.ChangeAutoAccept	replicated	-	-	-	2	MyFactionCollection.cs:3639 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFactionCollection.CreateFaction	replicated	-	-	-	0	MyFactionCollection.cs:3657 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFactionCollection.CreateFactionNew	replicated	-	-	-	0	MyFactionCollection.cs:3667 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFactionCollection.CreateNPCFaction	replicated	-	-	-	2	MyFactionCollection.cs:3672 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFactionCollection.DeclareWar	replicated	-	-	-	1	MyFactionCollection.cs:3599 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFactionCollection.DemoteMember	replicated	-	-	-	0	MyFactionCollection.cs:3629 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFactionCollection.EditFaction	replicated	-	-	-	3	MyFactionCollection.cs:3644 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFactionCollection.FactionAutoAcceptChanged	per-node	B6	no	role (act on WA only)	1	MyFactionCollection.cs:849 raised on every node when the WA state is applied there, so a handler with world-wide side effects runs once per node
VRage.Game.ModAPI.IMyFactionCollection.FactionCreated	per-node	B6	no	role (act on WA only)	5	MyFactionCollection.cs:861 raised on every node when the WA state is applied there, so a handler with world-wide side effects runs once per node
VRage.Game.ModAPI.IMyFactionCollection.FactionEdited	per-node	B6	no	role (act on WA only)	5	MyFactionCollection.cs:873 raised on every node when the WA state is applied there, so a handler with world-wide side effects runs once per node
VRage.Game.ModAPI.IMyFactionCollection.FactionNameExists	replicated	-	-	-	0	MyFactionCollection.cs:3492 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFactionCollection.FactionStateChanged	per-node	B6	no	role (act on WA only)	6	MyFactionCollection.cs:885 raised on every node when the WA state is applied there, so a handler with world-wide side effects runs once per node
VRage.Game.ModAPI.IMyFactionCollection.FactionTagExists	replicated	-	-	-	0	MyFactionCollection.cs:3487 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFactionCollection.Factions	replicated	-	-	-	9	MyFactionCollection.cs:829 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFactionCollection.GetObjectBuilder	replicated	-	-	-	1	MyFactionCollection.cs:3677 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFactionCollection.GetRelationBetweenFactions	replicated	-	-	-	13	MyFactionCollection.cs:3539 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFactionCollection.GetReputationBetweenFactions	replicated	-	-	-	5	MyFactionCollection.cs:3544 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFactionCollection.GetReputationBetweenPlayerAndFaction	replicated	-	-	-	9	MyFactionCollection.cs:3554 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFactionCollection.IsPeaceRequestStatePending	replicated	-	-	-	0	MyFactionCollection.cs:3574 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFactionCollection.IsPeaceRequestStateSent	replicated	-	-	-	0	MyFactionCollection.cs:3569 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFactionCollection.KickMember	replicated	-	-	-	3	MyFactionCollection.cs:3619 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFactionCollection.KickPlayerFromFaction	replicated	-	-	-	0	MyFactionCollection.cs:3530 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFactionCollection.MemberLeaves	replicated	-	-	-	0	MyFactionCollection.cs:3634 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFactionCollection.PromoteMember	replicated	-	-	-	1	MyFactionCollection.cs:3624 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFactionCollection.RemoveFaction	replicated	-	-	-	0	MyFactionCollection.cs:3579 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFactionCollection.ReputationChanged	per-node	B6	no	role (act on WA only)	0	MyFactionCollection.cs:897 raised on every node when the WA state is applied there, so a handler with world-wide side effects runs once per node
VRage.Game.ModAPI.IMyFactionCollection.SendJoinRequest	replicated	-	-	-	2	MyFactionCollection.cs:3604 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFactionCollection.SendPeaceRequest	replicated	-	-	-	1	MyFactionCollection.cs:3584 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFactionCollection.SetReputation	replicated	-	-	-	5	MyFactionCollection.cs:3549 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFactionCollection.SetReputationBetweenPlayerAndFaction	replicated	-	-	-	4	MyFactionCollection.cs:3559 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFactionCollection.TryGetFactionById	replicated	-	-	-	6	MyFactionCollection.cs:3497 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFactionCollection.TryGetFactionByName	replicated	-	-	-	0	MyFactionCollection.cs:3512 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFactionCollection.TryGetFactionByTag	replicated	-	-	-	12	MyFactionCollection.cs:3507 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFactionCollection.TryGetPlayerFaction	replicated	-	-	-	39	MyFactionCollection.cs:3502 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFactionStation.FactionId	replicated	-	-	-	0	MyFactionStation.cs:40 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFactionStation.Id	replicated	-	-	-	0	MyFactionStation.cs:34 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFactionStation.IsDeepSpaceStation	replicated	-	-	-	0	MyFactionStation.cs:52 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFactionStation.IsOnPlanetWithAtmosphere	replicated	-	-	-	0	MyFactionStation.cs:30 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFactionStation.Name	replicated	-	-	-	2	MyFactionStation.cs:44 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFactionStation.Position	replicated	-	-	-	0	MyFactionStation.cs:46 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFactionStation.PrefabName	replicated	-	-	-	0	MyFactionStation.cs:38 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFactionStation.SafeZoneEntityId	replicated	-	-	-	0	MyFactionStation.cs:28 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFactionStation.StationEntityId	replicated	-	-	-	1	MyFactionStation.cs:36 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFactionStation.StoreItems	replicated	-	-	-	2	MyFactionStation.cs:54 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyFactionStation.Type	replicated	-	-	-	1	MyFactionStation.cs:42 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyGamePaths.ContentPath	local	-	-	-	9	MyAPIUtilities.cs:33 process-wide registration or service; every node runs the mod and registers its own
VRage.Game.ModAPI.IMyGamePaths.ModScopeName	local	-	-	-	43	MyAPIUtilities.cs:41 process-wide registration or service; every node runs the mod and registers its own
VRage.Game.ModAPI.IMyGamePaths.ModsPath	local	-	-	-	4	MyAPIUtilities.cs:35 process-wide registration or service; every node runs the mod and registers its own
VRage.Game.ModAPI.IMyGamePaths.SavesPath	local	-	-	-	4	MyAPIUtilities.cs:39 process-wide registration or service; every node runs the mod and registers its own
VRage.Game.ModAPI.IMyGamePaths.UserDataPath	local	-	-	-	7	MyAPIUtilities.cs:37 process-wide registration or service; every node runs the mod and registers its own
VRage.Game.ModAPI.IMyGeneratedTexture.BeginUpdate	client	-	-	-	1	MySessionComponentGeneratedTextures.cs:85 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyGeneratedTexture.ByteCount	client	-	-	-	0	MySessionComponentGeneratedTextures.cs:70 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyGeneratedTexture.EndUpdate	client	-	-	-	1	MySessionComponentGeneratedTextures.cs:98 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyGeneratedTexture.Format	client	-	-	-	0	MySessionComponentGeneratedTextures.cs:68 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyGeneratedTexture.IsValid	client	-	-	-	1	MySessionComponentGeneratedTextures.cs:72 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyGeneratedTexture.MaterialId	client	-	-	-	0	MySessionComponentGeneratedTextures.cs:54 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyGeneratedTexture.Name	client	-	-	-	1	MySessionComponentGeneratedTextures.cs:42 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyGeneratedTexture.Size	client	-	-	-	1	MySessionComponentGeneratedTextures.cs:66 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyGeneratedTextures.Create	client	-	-	-	1	MySessionComponentGeneratedTextures.cs:171 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyGeneratedTextures.IsSupported	client	-	-	-	1	MySessionComponentGeneratedTextures.cs:167 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyGeneratedTextures.MaxTextureSide	client	-	-	-	1	MySessionComponentGeneratedTextures.cs:169 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyGps.ContainerRemainingTime	local	-	-	-	1	MyGps.cs:156 value, parser or helper; no world state
VRage.Game.ModAPI.IMyGps.Coords	local	-	-	-	15	MyGps.cs:196 value, parser or helper; no world state
VRage.Game.ModAPI.IMyGps.Description	local	-	-	-	7	MyGps.cs:180 value, parser or helper; no world state
VRage.Game.ModAPI.IMyGps.DiscardAt	local	-	-	-	6	MyGps.cs:232 value, parser or helper; no world state
VRage.Game.ModAPI.IMyGps.GPSColor	local	-	-	-	15	MyGps.cs:208 value, parser or helper; no world state
VRage.Game.ModAPI.IMyGps.Hash	local	-	-	-	8	MyGps.cs:158 value, parser or helper; no world state
VRage.Game.ModAPI.IMyGps.Name	local	-	-	-	13	MyGps.cs:164 value, parser or helper; no world state
VRage.Game.ModAPI.IMyGps.ShowOnHud	local	-	-	-	8	MyGps.cs:220 value, parser or helper; no world state
VRage.Game.ModAPI.IMyGps.ToString	local	-	-	-	4	MyGps.cs:373 value, parser or helper; no world state
VRage.Game.ModAPI.IMyGps.UpdateHash	local	-	-	-	3	MyGps.cs:362 value, parser or helper; no world state
VRage.Game.ModAPI.IMyGpsCollection.AddGps	replicated	-	-	-	11	MyGpsCollection.cs:2049 GPS collection is WA-replicated; requests are executed on the WA (cluster: Patches/MyGpsCollectionPatch.cs:15)
VRage.Game.ModAPI.IMyGpsCollection.AddLocalGps	client	-	-	-	14	MyGpsCollection.cs:2081 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyGpsCollection.Create	replicated	-	-	-	22	MyGpsCollection.cs:1994 GPS collection is WA-replicated; requests are executed on the WA (cluster: Patches/MyGpsCollectionPatch.cs:15)
VRage.Game.ModAPI.IMyGpsCollection.GetGpsList	replicated	-	-	-	14	MyGpsCollection.cs:2014 GPS collection is WA-replicated; requests are executed on the WA (cluster: Patches/MyGpsCollectionPatch.cs:15)
VRage.Game.ModAPI.IMyGpsCollection.ModifyGps	replicated	-	-	-	5	MyGpsCollection.cs:2065 GPS collection is WA-replicated; requests are executed on the WA (cluster: Patches/MyGpsCollectionPatch.cs:15)
VRage.Game.ModAPI.IMyGpsCollection.RemoveGps	replicated	-	-	-	6	MyGpsCollection.cs:2055 GPS collection is WA-replicated; requests are executed on the WA (cluster: Patches/MyGpsCollectionPatch.cs:15)
VRage.Game.ModAPI.IMyGpsCollection.RemoveLocalGps	client	-	-	-	11	MyGpsCollection.cs:2091 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyGpsCollection.SetShowOnHud	replicated	-	-	-	3	MyGpsCollection.cs:2071 GPS collection is WA-replicated; requests are executed on the WA (cluster: Patches/MyGpsCollectionPatch.cs:15)
VRage.Game.ModAPI.IMyGravityProviderSystem.AddNaturalGravity	local	-	-	-	1	MyGravityProviderSystem.cs:410 process-wide registration or service; every node runs the mod and registers its own
VRage.Game.ModAPI.IMyGravityProviderSystem.AddNaturalGravityToEntity	local	-	-	-	1	MyGravityProviderSystem.cs:420 process-wide registration or service; every node runs the mod and registers its own
VRage.Game.ModAPI.IMyGravityProviderSystem.AddNaturalModAPI	local	-	-	-	1	MyGravityProviderSystem.cs:436 process-wide registration or service; every node runs the mod and registers its own
VRage.Game.ModAPI.IMyGravityProviderSystem.AddNaturalModAPIGravityToEntity	local	-	-	-	0	MyGravityProviderSystem.cs:446 process-wide registration or service; every node runs the mod and registers its own
VRage.Game.ModAPI.IMyGravityProviderSystem.CalculateArtificialGravityInPoint	node-scoped	B1	no	-	0	MyGravityProviderSystem.cs:462 sums this node's gravity generators; generators are short range, so complete within sync distance
VRage.Game.ModAPI.IMyGravityProviderSystem.CalculateHighestNaturalGravityMultiplierInPoint	local	-	-	-	0	MyGravityProviderSystem.cs:467 planets are loaded on every node
VRage.Game.ModAPI.IMyGravityProviderSystem.CalculateNaturalGravityInPoint	local	-	-	-	5	MyGravityProviderSystem.cs:472 planets are loaded on every node
VRage.Game.ModAPI.IMyGravityProviderSystem.CalculateTotalGravityInPoint	node-scoped	B1	no	-	0	MyGravityProviderSystem.cs:482 sums this node's gravity generators; generators are short range, so complete within sync distance
VRage.Game.ModAPI.IMyGravityProviderSystem.DoesEntityHaveGravityProvider	node-scoped	B1	no	-	0	MyGravityProviderSystem.cs:487 sums this node's gravity generators; generators are short range, so complete within sync distance
VRage.Game.ModAPI.IMyGravityProviderSystem.DoesTrajectoryIntersectNaturalGravity	local	-	-	-	0	MyGravityProviderSystem.cs:492 planets are loaded on every node
VRage.Game.ModAPI.IMyGravityProviderSystem.GetStrongestNaturalGravityWell	local	-	-	-	3	MyGravityProviderSystem.cs:502 planets are loaded on every node
VRage.Game.ModAPI.IMyGravityProviderSystem.IsPositionInNaturalGravity	local	-	-	-	3	MyGravityProviderSystem.cs:497 planets are loaded on every node
VRage.Game.ModAPI.IMyGravityProviderSystem.RemoveNaturalGravity	local	-	-	-	1	MyGravityProviderSystem.cs:510 process-wide registration or service; every node runs the mod and registers its own
VRage.Game.ModAPI.IMyGravityProviderSystem.RemoveNaturalGravityFromEntity	local	-	-	-	1	MyGravityProviderSystem.cs:515 process-wide registration or service; every node runs the mod and registers its own
VRage.Game.ModAPI.IMyGridControlSystem.CurrentShipController	local	-	-	-	7	MyGroupControlSystem.cs:109 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyGridControlSystem.IsControlled	local	-	-	-	2	MyGroupControlSystem.cs:107 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyGridControlSystem.IsControlledChanged	local	-	-	-	1	MyGroupControlSystem.cs:113 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyGridGasSystem.ForcePressurize	local	-	-	-	0	MyGridGasSystem.cs:153 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyGridGasSystem.GetOxygenBlock	local	-	-	-	2	MyGridGasSystem.cs:1773 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyGridGasSystem.GetOxygenRoomForCubeGridPosition	local	-	-	-	3	MyGridGasSystem.cs:1768 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyGridGasSystem.GetRooms	local	-	-	-	4	MyGridGasSystem.cs:1753 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyGridGasSystem.IsProcessingData	local	-	-	-	4	MyGridGasSystem.cs:151 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyGridGasSystem.OnProcessingDataComplete	local	-	-	-	1	MyGridGasSystem.cs:167 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyGridGasSystem.OnProcessingDataStart	local	-	-	-	0	MyGridGasSystem.cs:165 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyGridGroupData.GetGrids	local	-	-	-	21	MyGridGroupData.cs:22 grid groups are atomic to a partition and never split across nodes
VRage.Game.ModAPI.IMyGridGroupData.GetVariable	per-node	B4	no	handover-hooks	0	MyGridGroupData.cs:42 in-memory variables of the group object; the group is rebuilt on the target node after a handover
VRage.Game.ModAPI.IMyGridGroupData.LinkType	local	-	-	-	3	MyGridGroupData.cs:14 grid groups are atomic to a partition and never split across nodes
VRage.Game.ModAPI.IMyGridGroupData.OnGridAdded	local	-	-	-	8	MyGridGroupData.cs:16 grid groups are atomic to a partition and never split across nodes
VRage.Game.ModAPI.IMyGridGroupData.OnGridRemoved	local	-	-	-	8	MyGridGroupData.cs:18 grid groups are atomic to a partition and never split across nodes
VRage.Game.ModAPI.IMyGridGroupData.OnReleased	local	-	-	-	3	MyGridGroupData.cs:20 grid groups are atomic to a partition and never split across nodes
VRage.Game.ModAPI.IMyGridGroupData.RemoveVariable	per-node	B4	no	handover-hooks	0	MyGridGroupData.cs:57 in-memory variables of the group object; the group is rebuilt on the target node after a handover
VRage.Game.ModAPI.IMyGridGroupData.SetVariable	per-node	B4	no	handover-hooks	0	MyGridGroupData.cs:52 in-memory variables of the group object; the group is rebuilt on the target node after a handover
VRage.Game.ModAPI.IMyGridGroupData.TryGetVariable	per-node	B4	no	handover-hooks	0	MyGridGroupData.cs:31 in-memory variables of the group object; the group is rebuilt on the target node after a handover
VRage.Game.ModAPI.IMyGridGroups.AddGridGroupLogic	local	-	-	-	0	MyGridGroupsHelper.cs:66 grid groups are atomic to a partition and never split across nodes
VRage.Game.ModAPI.IMyGridGroups.FindConnection	local	-	-	-	0	MyGridGroupsHelper.cs:71 grid groups are atomic to a partition and never split across nodes
VRage.Game.ModAPI.IMyGridGroups.GetGridGroup	local	-	-	-	9	MyGridGroupsHelper.cs:56 grid groups are atomic to a partition and never split across nodes
VRage.Game.ModAPI.IMyGridGroups.GetGridGroups	local	-	-	-	1	MyGridGroupsHelper.cs:61 grid groups are atomic to a partition and never split across nodes
VRage.Game.ModAPI.IMyGridGroups.GetGroup	local	-	-	-	33	MyGridGroupsHelper.cs:35 grid groups are atomic to a partition and never split across nodes
VRage.Game.ModAPI.IMyGridGroups.HasConnection	local	-	-	-	8	MyGridGroupsHelper.cs:51 grid groups are atomic to a partition and never split across nodes
VRage.Game.ModAPI.IMyGridGroups.OnGridGroupCreated	node-scoped	B2	no	handover-hooks	4	MyGridGroupsHelper.cs:10 raised by this node's MyEntities; a grid arriving or leaving by handover looks like a create or delete, and other nodes' entities never show up
VRage.Game.ModAPI.IMyGridGroups.OnGridGroupDestroyed	node-scoped	B2	no	handover-hooks	5	MyGridGroupsHelper.cs:22 raised by this node's MyEntities; a grid arriving or leaving by handover looks like a create or delete, and other nodes' entities never show up
VRage.Game.ModAPI.IMyGridJumpDriveSystem.AbortJump	local	-	-	-	1	MyGridJumpDriveSystem.cs:1092 own grid; after a jump the grid may be handed over to another node (cluster lands it off other nodes' grids, SE-1871), so ids held elsewhere become B3
VRage.Game.ModAPI.IMyGridJumpDriveSystem.FindSuitableJumpLocation	local	-	-	-	1	MyGridJumpDriveSystem.cs:1053 own grid; after a jump the grid may be handed over to another node (cluster lands it off other nodes' grids, SE-1871), so ids held elsewhere become B3
VRage.Game.ModAPI.IMyGridJumpDriveSystem.GetJumpDriveDirection	local	-	-	-	0	MyGridJumpDriveSystem.cs:129 own grid; after a jump the grid may be handed over to another node (cluster lands it off other nodes' grids, SE-1871), so ids held elsewhere become B3
VRage.Game.ModAPI.IMyGridJumpDriveSystem.GetJumpDriveDirectionNormalized	local	-	-	-	0	MyGridJumpDriveSystem.cs:1083 own grid; after a jump the grid may be handed over to another node (cluster lands it off other nodes' grids, SE-1871), so ids held elsewhere become B3
VRage.Game.ModAPI.IMyGridJumpDriveSystem.GetJumpDriveTarget	local	-	-	-	1	MyGridJumpDriveSystem.cs:1074 own grid; after a jump the grid may be handed over to another node (cluster lands it off other nodes' grids, SE-1871), so ids held elsewhere become B3
VRage.Game.ModAPI.IMyGridJumpDriveSystem.GetMaxJumpDistance	local	-	-	-	0	MyGridJumpDriveSystem.cs:190 own grid; after a jump the grid may be handed over to another node (cluster lands it off other nodes' grids, SE-1871), so ids held elsewhere become B3
VRage.Game.ModAPI.IMyGridJumpDriveSystem.GetMinJumpDistance	local	-	-	-	0	MyGridJumpDriveSystem.cs:173 own grid; after a jump the grid may be handed over to another node (cluster lands it off other nodes' grids, SE-1871), so ids held elsewhere become B3
VRage.Game.ModAPI.IMyGridJumpDriveSystem.GetRemainingJumpTime	local	-	-	-	0	MyGridJumpDriveSystem.cs:1042 own grid; after a jump the grid may be handed over to another node (cluster lands it off other nodes' grids, SE-1871), so ids held elsewhere become B3
VRage.Game.ModAPI.IMyGridJumpDriveSystem.IsJumpValid	local	-	-	-	1	MyGridJumpDriveSystem.cs:1047 own grid; after a jump the grid may be handed over to another node (cluster lands it off other nodes' grids, SE-1871), so ids held elsewhere become B3
VRage.Game.ModAPI.IMyGridJumpDriveSystem.IsJumping	local	-	-	-	3	MyGridJumpDriveSystem.cs:98 own grid; after a jump the grid may be handed over to another node (cluster lands it off other nodes' grids, SE-1871), so ids held elsewhere become B3
VRage.Game.ModAPI.IMyGridJumpDriveSystem.Jump	local	-	-	-	1	MyGridJumpDriveSystem.cs:1058 own grid; after a jump the grid may be handed over to another node (cluster lands it off other nodes' grids, SE-1871), so ids held elsewhere become B3
VRage.Game.ModAPI.IMyGridJumpDriveSystem.Jumped	local	-	-	-	1	MyGridJumpDriveSystem.cs:105 own grid; after a jump the grid may be handed over to another node (cluster lands it off other nodes' grids, SE-1871), so ids held elsewhere become B3
VRage.Game.ModAPI.IMyGridJumpDriveSystem.PerformJump	local	-	-	-	0	MyGridJumpDriveSystem.cs:1066 own grid; after a jump the grid may be handed over to another node (cluster lands it off other nodes' grids, SE-1871), so ids held elsewhere become B3
VRage.Game.ModAPI.IMyGridJumpDriveSystem.RequestJump	local	-	-	-	0	MyGridJumpDriveSystem.cs:269 own grid; after a jump the grid may be handed over to another node (cluster lands it off other nodes' grids, SE-1871), so ids held elsewhere become B3
VRage.Game.ModAPI.IMyGridWeaponSystem.GetGun	local	-	-	-	0	MyGridWeaponSystem.cs:66 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyGridWeaponSystem.Register	local	-	-	-	1	MyGridWeaponSystem.cs:103 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyGridWeaponSystem.Unregister	local	-	-	-	1	MyGridWeaponSystem.cs:127 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyGui.ActiveGamePlayScreen	client	-	-	-	4	MyGuiModHelpers.cs:13 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyGui.ChangeInteractedEntity	client	-	-	-	4	MyGuiModHelpers.cs:87 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyGui.ChatEntryVisible	client	-	-	-	34	MyGuiModHelpers.cs:34 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyGui.CloseTerminalScreen	client	-	-	-	0	MyGuiModHelpers.cs:82 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyGui.GetCurrentScreen	client	-	-	-	32	MyGuiModHelpers.cs:32 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyGui.GuiControlCreated	client	-	-	-	9	MyGuiModHelpers.cs:48 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyGui.GuiControlRemoved	client	-	-	-	34	MyGuiModHelpers.cs:60 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyGui.InteractedEntity	client	-	-	-	5	MyGuiModHelpers.cs:30 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyGui.IsCursorVisible	client	-	-	-	38	MyGuiModHelpers.cs:46 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyGui.ShowTerminalPage	client	-	-	-	3	MyGuiModHelpers.cs:77 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyGunObject.BackkickForcePerSecond	local	-	-	-	0	MyHandToolBase.cs:190 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyGunObject.BeginFailReaction	local	-	-	-	0	MyHandToolBase.cs:798 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyGunObject.BeginFailReactionLocal	client	-	-	-	0	MyHandToolBase.cs:802 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyGunObject.BeginShoot	local	-	-	-	0	MyHandToolBase.cs:785 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyGunObject.CanShoot	local	-	-	-	3	MyHandToolBase.cs:327 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyGunObject.DefinitionId	local	-	-	-	5	MyHandToolBase.cs:203 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyGunObject.DirectionToTarget	local	-	-	-	0	MyHandToolBase.cs:780 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyGunObject.DrawHud	client	-	-	-	0	MyHandToolBase.cs:741 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyGunObject.EnabledInWorldRules	local	-	-	-	0	MyHandToolBase.cs:188 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyGunObject.EndShoot	local	-	-	-	2	MyHandToolBase.cs:789 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyGunObject.GetAmmunitionAmount	local	-	-	-	3	MyHandToolBase.cs:815 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyGunObject.GetMagazineAmount	local	-	-	-	0	MyHandToolBase.cs:820 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyGunObject.GetMuzzlePosition	local	-	-	-	1	MyHandToolBase.cs:898 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyGunObject.GetShootDirection	local	-	-	-	0	MyHandToolBase.cs:427 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyGunObject.GetTotalAmmunitionAmount	local	-	-	-	0	MyHandToolBase.cs:810 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyGunObject.GunBase	local	-	-	-	9	MyHandToolBase.cs:205 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyGunObject.IsShooting	local	-	-	-	5	MyHandToolBase.cs:160 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyGunObject.IsSkinnable	local	-	-	-	0	MyHandToolBase.cs:249 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyGunObject.IsTargetLockingCapable	local	-	-	-	0	MyHandToolBase.cs:251 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyGunObject.IsToolbarUsable	local	-	-	-	0	MyHandToolBase.cs:912 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyGunObject.MaximumShotLength	local	-	-	-	0	MyLargeTurretBase.cs:823 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyGunObject.NeedsShootDirectionWhileAiming	local	-	-	-	0	MyLargeTurretBase.cs:820 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyGunObject.OnControlAcquired	local	-	-	-	3	MyHandToolBase.cs:451 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyGunObject.OnControlReleased	local	-	-	-	2	MyHandToolBase.cs:466 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyGunObject.ShakeAmount	local	-	-	-	0	MyHandToolBase.cs:192 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyGunObject.Shoot	local	-	-	-	6	MyHandToolBase.cs:356 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyGunObject.ShootDirectionUpdateTime	local	-	-	-	0	MyEngineerToolBase.cs:250 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyGunObject.ShootFailReactionLocal	client	-	-	-	0	MyHandToolBase.cs:806 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyGunObject.SupressShootAnimation	local	-	-	-	0	MyHandToolBase.cs:873 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyGunObject.UpdateSoundEmitter	client	-	-	-	0	MyHandToolBase.cs:865 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyHudNotification.AliveTime	client	-	-	-	21	MyHudNotification.cs:39 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyHudNotification.Font	client	-	-	-	11	MyHudNotification.cs:52 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyHudNotification.Hide	client	-	-	-	27	MyHudNotification.cs:80 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyHudNotification.ResetAliveTime	client	-	-	-	18	MyHudNotification.cs:85 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyHudNotification.Show	client	-	-	-	34	MyHudNotification.cs:75 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyHudNotification.Text	client	-	-	-	33	MyHudNotification.cs:27 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyHudObjectiveLine.AdvanceObjective	client	-	-	-	0	MyHudObjectiveLine.cs:57 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyHudObjectiveLine.CurrentObjective	client	-	-	-	0	MyHudObjectiveLine.cs:28 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyHudObjectiveLine.Hide	client	-	-	-	0	MyHudObjectiveLine.cs:52 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyHudObjectiveLine.Objectives	client	-	-	-	0	MyHudObjectiveLine.cs:30 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyHudObjectiveLine.Show	client	-	-	-	0	MyHudObjectiveLine.cs:47 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyHudObjectiveLine.Title	client	-	-	-	0	MyHudObjectiveLine.cs:16 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyHudObjectiveLine.Visible	client	-	-	-	0	MyHudObjectiveLine.cs:14 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyIdentity.CharacterChanged	replicated	-	-	-	1	MyIdentity.cs:182 identity and player bindings come from the WA binding view (cluster: Cluster/Patches/MyPlayerCollectionPatch.cs:398)
VRage.Game.ModAPI.IMyIdentity.ColorMask	replicated	-	-	-	0	MyIdentity.cs:174 identity and player bindings come from the WA binding view (cluster: Cluster/Patches/MyPlayerCollectionPatch.cs:398)
VRage.Game.ModAPI.IMyIdentity.DisplayName	replicated	-	-	-	4	MyIdentity.cs:170 identity and player bindings come from the WA binding view (cluster: Cluster/Patches/MyPlayerCollectionPatch.cs:398)
VRage.Game.ModAPI.IMyIdentity.IdentityId	replicated	-	-	-	9	MyIdentity.cs:168 identity and player bindings come from the WA binding view (cluster: Cluster/Patches/MyPlayerCollectionPatch.cs:398)
VRage.Game.ModAPI.IMyIdentity.IsDead	replicated	-	-	-	1	MyIdentity.cs:176 identity and player bindings come from the WA binding view (cluster: Cluster/Patches/MyPlayerCollectionPatch.cs:398)
VRage.Game.ModAPI.IMyIdentity.Model	replicated	-	-	-	0	MyIdentity.cs:172 identity and player bindings come from the WA binding view (cluster: Cluster/Patches/MyPlayerCollectionPatch.cs:398)
VRage.Game.ModAPI.IMyIdentity.PlayerId	replicated	-	-	-	0	MyIdentity.cs:166 identity and player bindings come from the WA binding view (cluster: Cluster/Patches/MyPlayerCollectionPatch.cs:398)
VRage.Game.ModAPI.IMyInventory.AddItems	local	-	-	-	29	MyInventory.cs:2812 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyInventory.CanAddItemAmount	local	-	-	-	1	MyInventory.cs:2960 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyInventory.CanPutItems	local	-	-	-	0	MyInventory.cs:508 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyInventory.Clear	local	-	-	-	4	MyInventory.cs:1382 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyInventory.Empty	local	-	-	-	5	MyInventory.cs:1327 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyInventory.FindItem	local	-	-	-	3	MyInventory.cs:2714 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyInventory.GetItemByID	local	-	-	-	6	MyInventory.cs:2709 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyInventory.GetItems	local	-	-	-	7	MyInventory.cs:2724 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyInventory.OnVolumeChanged	local	-	-	-	0	MyInventory.cs:526 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyInventory.Owner	local	-	-	-	7	MyInventory.cs:504 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyInventory.RemoveItemAmount	local	-	-	-	3	MyInventory.cs:2837 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyInventory.RemoveItems	local	-	-	-	5	MyInventory.cs:2832 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyInventory.RemoveItemsAt	local	-	-	-	4	MyInventory.cs:2827 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyInventory.RemoveItemsOfType	local	-	-	-	9	MyInventory.cs:2817 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyInventory.TransferItemFrom	local	-	-	-	3	MyInventory.cs:2746 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyInventory.TransferItemTo	local	-	-	-	8	MyInventory.cs:2736 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyModAPINaturalGravityComponent.Implementation	local	-	-	-	0	MyModAPINaturalGravityComponent.cs:27 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyModAPINaturalGravityImplementation.DoesTrajectoryIntersectNaturalGravity	local	-	-	-	0	IMyModAPINaturalGravityImplementation.cs:50 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyModAPINaturalGravityImplementation.GetGravityLimit	local	-	-	-	0	IMyModAPINaturalGravityImplementation.cs:56 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyModAPINaturalGravityImplementation.GetGravityMultiplier	local	-	-	-	0	IMyModAPINaturalGravityImplementation.cs:36 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyModAPINaturalGravityImplementation.GetProxyAABB	local	-	-	-	0	IMyModAPINaturalGravityImplementation.cs:42 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyModAPINaturalGravityImplementation.GetWorldGravity	local	-	-	-	0	IMyModAPINaturalGravityImplementation.cs:15 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyModAPINaturalGravityImplementation.GetWorldGravityNormalized	local	-	-	-	0	IMyModAPINaturalGravityImplementation.cs:22 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyModAPINaturalGravityImplementation.IsPositionInRange	local	-	-	-	0	IMyModAPINaturalGravityImplementation.cs:29 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyModAPINaturalGravityImplementation.OnPositionChanged	local	-	-	-	0	IMyModAPINaturalGravityImplementation.cs:62 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyModContext.IsBaseGame	local	-	-	-	6	MyModContext.cs:58 definitions, mods and session settings are the same on every node (cluster join hash)
VRage.Game.ModAPI.IMyModContext.ModId	local	-	-	-	27	MyModContext.cs:45 definitions, mods and session settings are the same on every node (cluster join hash)
VRage.Game.ModAPI.IMyModContext.ModItem	local	-	-	-	15	MyModContext.cs:56 definitions, mods and session settings are the same on every node (cluster join hash)
VRage.Game.ModAPI.IMyModContext.ModName	local	-	-	-	47	MyModContext.cs:42 definitions, mods and session settings are the same on every node (cluster join hash)
VRage.Game.ModAPI.IMyModContext.ModPath	local	-	-	-	47	MyModContext.cs:51 definitions, mods and session settings are the same on every node (cluster join hash)
VRage.Game.ModAPI.IMyModContext.ModPathData	local	-	-	-	34	MyModContext.cs:54 definitions, mods and session settings are the same on every node (cluster join hash)
VRage.Game.ModAPI.IMyModContext.ModServiceName	local	-	-	-	6	MyModContext.cs:48 definitions, mods and session settings are the same on every node (cluster join hash)
VRage.Game.ModAPI.IMyModel.AssetName	local	-	-	-	9	MyModel.cs:138 value, parser or helper; no world state
VRage.Game.ModAPI.IMyModel.BoneMapping	local	-	-	-	0	MyModel.cs:152 value, parser or helper; no world state
VRage.Game.ModAPI.IMyModel.BoundingBox	local	-	-	-	3	MyModel.cs:142 value, parser or helper; no world state
VRage.Game.ModAPI.IMyModel.BoundingBoxSize	local	-	-	-	3	MyModel.cs:144 value, parser or helper; no world state
VRage.Game.ModAPI.IMyModel.BoundingBoxSizeHalf	local	-	-	-	1	MyModel.cs:146 value, parser or helper; no world state
VRage.Game.ModAPI.IMyModel.BoundingSphere	local	-	-	-	1	MyModel.cs:140 value, parser or helper; no world state
VRage.Game.ModAPI.IMyModel.DataVersion	local	-	-	-	1	MyModel.cs:150 value, parser or helper; no world state
VRage.Game.ModAPI.IMyModel.GetDrawTechnique	local	-	-	-	0	MyModel.cs:881 value, parser or helper; no world state
VRage.Game.ModAPI.IMyModel.GetDummies	local	-	-	-	30	MyModel.cs:686 value, parser or helper; no world state
VRage.Game.ModAPI.IMyModel.GetTriangle	local	-	-	-	2	MyModel.cs:712 value, parser or helper; no world state
VRage.Game.ModAPI.IMyModel.GetTrianglesCount	local	-	-	-	0	MyModel.cs:862 value, parser or helper; no world state
VRage.Game.ModAPI.IMyModel.GetVertex	local	-	-	-	2	MyModel.cs:717 value, parser or helper; no world state
VRage.Game.ModAPI.IMyModel.GetVerticesCount	local	-	-	-	0	MyModel.cs:867 value, parser or helper; no world state
VRage.Game.ModAPI.IMyModel.PatternScale	local	-	-	-	0	MyModel.cs:154 value, parser or helper; no world state
VRage.Game.ModAPI.IMyModel.ScaleFactor	local	-	-	-	2	MyModel.cs:133 value, parser or helper; no world state
VRage.Game.ModAPI.IMyModel.UniqueId	local	-	-	-	0	MyModel.cs:148 value, parser or helper; no world state
VRage.Game.ModAPI.IMyMultiplayer.IsServer	local	-	-	-	113	MyModAPIHelper.cs:103 every node is the server of its own clients; true and correct per process
VRage.Game.ModAPI.IMyMultiplayer.IsServerPlayer	local	-	-	-	8	MyModAPIHelper.cs:120 every node is the server of its own clients; true and correct per process
VRage.Game.ModAPI.IMyMultiplayer.JoinServer	client	-	-	-	0	MyModAPIHelper.cs:195 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyMultiplayer.MultiplayerActive	local	-	-	-	50	MyModAPIHelper.cs:101 every node is the server of its own clients; true and correct per process
VRage.Game.ModAPI.IMyMultiplayer.MyId	local	-	-	-	51	MyModAPIHelper.cs:107 every node is the server of its own clients; true and correct per process
VRage.Game.ModAPI.IMyMultiplayer.MyName	local	-	-	-	0	MyModAPIHelper.cs:109 every node is the server of its own clients; true and correct per process
VRage.Game.ModAPI.IMyMultiplayer.Players	local	-	-	-	25	MyModAPIHelper.cs:111 accessor of a ModAPI service; the policy is on the members of the returned interface
VRage.Game.ModAPI.IMyMultiplayer.RegisterMessageHandler	local	-	-	-	34	MyModAPIHelper.cs:209 process-wide registration or service; every node runs the mod and registers its own
VRage.Game.ModAPI.IMyMultiplayer.RegisterSecureMessageHandler	local	-	-	-	78	MyModAPIHelper.cs:238 process-wide registration or service; every node runs the mod and registers its own
VRage.Game.ModAPI.IMyMultiplayer.SendEntitiesCreated	local	-	-	-	1	MyModAPIHelper.cs:129 empty method
VRage.Game.ModAPI.IMyMultiplayer.SendMessageTo	node-scoped	B5	yes	player-nodes + to-node	93	MyModAPIHelper.cs:178 raises ModMessageClient* to EndpointId(recipient); undeliverable when the client is attached to another node
VRage.Game.ModAPI.IMyMultiplayer.SendMessageToOthers	node-scoped	B5	yes	player-nodes + to-node / broadcast	42	MyModAPIHelper.cs:150 on a server raises ModMessageBroadcast*FromServer ([Broadcast]); reaches only clients attached to this node
VRage.Game.ModAPI.IMyMultiplayer.SendMessageToServer	affinity	B8	no	request (WA) for shared state	94	MyModAPIHelper.cs:133 raises ModMessageServer* to the node the client is attached to; after a node switch it is another node and another mod instance (cluster delivers it there: Cluster/Patches/ModMessageToServerPatch.cs:58)
VRage.Game.ModAPI.IMyMultiplayer.ServerId	local	-	-	-	37	MyModAPIHelper.cs:105 every node is the server of its own clients; true and correct per process
VRage.Game.ModAPI.IMyMultiplayer.UnregisterMessageHandler	local	-	-	-	32	MyModAPIHelper.cs:225 process-wide registration or service; every node runs the mod and registers its own
VRage.Game.ModAPI.IMyMultiplayer.UnregisterSecureMessageHandler	local	-	-	-	74	MyModAPIHelper.cs:254 process-wide registration or service; every node runs the mod and registers its own
VRage.Game.ModAPI.IMyNaturalGravityComponent.GetGravityMultiplier	local	-	-	-	0	MyModAPINaturalGravityComponent.cs:65 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyNaturalGravityComponent.GetProxyAABB	local	-	-	-	0	MyModAPINaturalGravityComponent.cs:45 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyNaturalGravityComponent.GetWorldGravity	local	-	-	-	0	MyModAPINaturalGravityComponent.cs:60 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyNaturalGravityComponent.GravityLimit	local	-	-	-	2	MyModAPINaturalGravityComponent.cs:31 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyNaturalGravityComponent.IsPositionInRange	local	-	-	-	0	MyModAPINaturalGravityComponent.cs:40 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyNaturalGravityComponent.IsWorking	local	-	-	-	0	MyModAPINaturalGravityComponent.cs:29 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyNaturalGravityComponent.Position	local	-	-	-	2	MySphericalNaturalGravityComponent.cs:29 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyOxygenBlock.OxygenChangeTime	local	-	-	-	0	MyOxygenBlock.cs:39 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyOxygenBlock.OxygenLevel	local	-	-	-	0	MyOxygenBlock.cs:100 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyOxygenBlock.PreviousOxygenAmount	local	-	-	-	0	MyOxygenBlock.cs:23 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyOxygenBlock.Room	local	-	-	-	2	MyOxygenBlock.cs:57 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyOxygenProvider.GetOxygenForPosition	local	-	-	-	0	MyPlanet.cs:251 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyOxygenProvider.IsPositionInRange	local	-	-	-	0	MyPlanet.cs:242 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyOxygenProviderSystem.AddOxygenGenerator	local	-	-	-	5	MyOxygenProviderSystemHelper.cs:13 process-wide registration or service; every node runs the mod and registers its own
VRage.Game.ModAPI.IMyOxygenProviderSystem.GetOxygenInPoint	local	-	-	-	5	MyOxygenProviderSystemHelper.cs:8 planet atmospheres are on every node; registered generators are this node's own
VRage.Game.ModAPI.IMyOxygenProviderSystem.RemoveOxygenGenerator	local	-	-	-	5	MyOxygenProviderSystemHelper.cs:18 process-wide registration or service; every node runs the mod and registers its own
VRage.Game.ModAPI.IMyOxygenRoom.BlockCount	local	-	-	-	3	MyOxygenRoom.cs:20 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyOxygenRoom.Blocks	local	-	-	-	4	MyOxygenRoom.cs:33 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyOxygenRoom.EnvironmentOxygen	local	-	-	-	0	MyOxygenRoom.cs:16 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyOxygenRoom.IsAirtight	local	-	-	-	7	MyOxygenRoom.cs:14 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyOxygenRoom.IsDirty	local	-	-	-	0	MyOxygenRoom.cs:27 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyOxygenRoom.MaxOxygen	local	-	-	-	0	MyOxygenRoom.cs:60 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyOxygenRoom.MissingOxygen	local	-	-	-	0	MyOxygenRoom.cs:55 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyOxygenRoom.OxygenAmount	local	-	-	-	0	MyOxygenRoom.cs:18 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyOxygenRoom.OxygenLevel	local	-	-	-	3	MyOxygenRoom.cs:50 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyOxygenRoom.StartingPosition	local	-	-	-	0	MyOxygenRoom.cs:31 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyParallelTask.DefaultOptions	local	-	-	-	0	MyParallelTask.cs:13 process-wide registration or service; every node runs the mod and registers its own
VRage.Game.ModAPI.IMyParallelTask.Do	local	-	-	-	0	MyParallelTask.cs:45 process-wide registration or service; every node runs the mod and registers its own
VRage.Game.ModAPI.IMyParallelTask.For	local	-	-	-	7	MyParallelTask.cs:65 process-wide registration or service; every node runs the mod and registers its own
VRage.Game.ModAPI.IMyParallelTask.ForEach	local	-	-	-	5	MyParallelTask.cs:75 process-wide registration or service; every node runs the mod and registers its own
VRage.Game.ModAPI.IMyParallelTask.Sleep	local	-	-	-	4	MyParallelTask.cs:115 process-wide registration or service; every node runs the mod and registers its own
VRage.Game.ModAPI.IMyParallelTask.Start	local	-	-	-	34	MyParallelTask.cs:80 process-wide registration or service; every node runs the mod and registers its own
VRage.Game.ModAPI.IMyParallelTask.StartBackground	local	-	-	-	25	MyParallelTask.cs:20 process-wide registration or service; every node runs the mod and registers its own
VRage.Game.ModAPI.IMyPhysics.CalculateArtificialGravityAt	node-scoped	B1	no	-	10	MyPhysics.cs:127 sums this node's gravity generators; generators are short range, so complete within sync distance
VRage.Game.ModAPI.IMyPhysics.CalculateNaturalGravityAt	local	-	-	-	22	MyPhysics.cs:122 planets are loaded on every node
VRage.Game.ModAPI.IMyPhysics.CastLongRay	node-scoped	B1	no	to-owner	1	MyPhysics.cs:42 casts in this node's Havok worlds; complete within sync distance, misses grids on other nodes beyond it
VRage.Game.ModAPI.IMyPhysics.CastRay	node-scoped	B1	no	to-owner	45	MyPhysics.cs:55 casts in this node's Havok worlds; complete within sync distance, misses grids on other nodes beyond it
VRage.Game.ModAPI.IMyPhysics.CastRayParallel	node-scoped	B1	no	to-owner	7	MyPhysics.cs:90 casts in this node's Havok worlds; complete within sync distance, misses grids on other nodes beyond it
VRage.Game.ModAPI.IMyPhysics.CreateBoxPhysics	local	-	-	-	2	MyPhysics.cs:212 value, parser or helper; no world state
VRage.Game.ModAPI.IMyPhysics.CreateCapsulePhysics	local	-	-	-	0	MyPhysics.cs:222 value, parser or helper; no world state
VRage.Game.ModAPI.IMyPhysics.CreateMassCombined	local	-	-	-	0	MyPhysics.cs:141 value, parser or helper; no world state
VRage.Game.ModAPI.IMyPhysics.CreateMassForBox	local	-	-	-	1	MyPhysics.cs:154 value, parser or helper; no world state
VRage.Game.ModAPI.IMyPhysics.CreateMassForCapsule	local	-	-	-	0	MyPhysics.cs:159 value, parser or helper; no world state
VRage.Game.ModAPI.IMyPhysics.CreateMassForCylinder	local	-	-	-	0	MyPhysics.cs:164 value, parser or helper; no world state
VRage.Game.ModAPI.IMyPhysics.CreateMassForSphere	local	-	-	-	0	MyPhysics.cs:169 value, parser or helper; no world state
VRage.Game.ModAPI.IMyPhysics.CreateModelPhysics	local	-	-	-	0	MyPhysics.cs:227 value, parser or helper; no world state
VRage.Game.ModAPI.IMyPhysics.CreateSettingsForDetector	local	-	-	-	2	MyPhysics.cs:174 value, parser or helper; no world state
VRage.Game.ModAPI.IMyPhysics.CreateSettingsForPhysics	local	-	-	-	0	MyPhysics.cs:192 value, parser or helper; no world state
VRage.Game.ModAPI.IMyPhysics.CreateSpherePhysics	local	-	-	-	1	MyPhysics.cs:217 value, parser or helper; no world state
VRage.Game.ModAPI.IMyPhysics.EnsurePhysicsSpace	local	-	-	-	0	MyPhysics.cs:111 value, parser or helper; no world state
VRage.Game.ModAPI.IMyPhysics.GetCollisionLayer	local	-	-	-	1	MyPhysics.cs:117 value, parser or helper; no world state
VRage.Game.ModAPI.IMyPhysics.GetWeaponLeadPrediction	local	-	-	-	0	MyPhysics.cs:273 value, parser or helper; no world state
VRage.Game.ModAPI.IMyPhysics.ServerSimulationRatio	node-scoped	B2	no	-	2	MyPhysics.cs:35 this node's simulation speed
VRage.Game.ModAPI.IMyPhysics.SimulationRatio	node-scoped	B2	no	-	3	MyPhysics.cs:33 this node's simulation speed
VRage.Game.ModAPI.IMyPhysics.StepsLastSecond	node-scoped	B2	no	-	1	MyPhysics.cs:31 this node's simulation speed
VRage.Game.ModAPI.IMyPlayer.AddGrid	node-scoped	B2	no	request (WA)	0	MyPlayer.cs:1289 UNCERTAIN: MyPlayer.Grids is a per-process list; not traced whether the cluster keeps it per player across nodes
VRage.Game.ModAPI.IMyPlayer.BuildColorSlots	local	-	-	-	2	MyPlayer.cs:595 an IMyPlayer is only reachable for players attached to this node, so its character and controller are here
VRage.Game.ModAPI.IMyPlayer.ChangeOrSwitchToColor	local	-	-	-	1	MyPlayer.cs:1299 an IMyPlayer is only reachable for players attached to this node, so its character and controller are here
VRage.Game.ModAPI.IMyPlayer.Character	local	-	-	-	78	MyPlayer.cs:553 an IMyPlayer is only reachable for players attached to this node, so its character and controller are here
VRage.Game.ModAPI.IMyPlayer.CharacterToolbar	local	-	-	-	0	MyPlayer.cs:519 an IMyPlayer is only reachable for players attached to this node, so its character and controller are here
VRage.Game.ModAPI.IMyPlayer.Client	local	-	-	-	8	MyPlayer.cs:507 an IMyPlayer is only reachable for players attached to this node, so its character and controller are here
VRage.Game.ModAPI.IMyPlayer.Controller	local	-	-	-	51	MyPlayer.cs:511 an IMyPlayer is only reachable for players attached to this node, so its character and controller are here
VRage.Game.ModAPI.IMyPlayer.CurrentToolbar	local	-	-	-	0	MyPlayer.cs:531 an IMyPlayer is only reachable for players attached to this node, so its character and controller are here
VRage.Game.ModAPI.IMyPlayer.DefaultBuildColorSlots	local	-	-	-	1	MyPlayer.cs:607 an IMyPlayer is only reachable for players attached to this node, so its character and controller are here
VRage.Game.ModAPI.IMyPlayer.DisplayName	local	-	-	-	32	MyPlayer.cs:513 an IMyPlayer is only reachable for players attached to this node, so its character and controller are here
VRage.Game.ModAPI.IMyPlayer.GetBalanceShortString	replicated	-	-	-	2	MyPlayer.cs:1335 banking is executed on the WA (cluster: Cluster/Patches/MyBankingSystemPatch.cs:15)
VRage.Game.ModAPI.IMyPlayer.GetPosition	local	-	-	-	40	MyPlayer.cs:1294 an IMyPlayer is only reachable for players attached to this node, so its character and controller are here
VRage.Game.ModAPI.IMyPlayer.GetRelationTo	replicated	-	-	-	8	MyPlayer.cs:1279 factions, reputation and their requests are WA-replicated; mutations are executed on the WA (cluster: Cluster/Patches/MyFactionCollectionPatch.cs:22)
VRage.Game.ModAPI.IMyPlayer.Grids	node-scoped	B2	no	request (WA)	1	MyPlayer.cs:509 UNCERTAIN: MyPlayer.Grids is a per-process list; not traced whether the cluster keeps it per player across nodes
VRage.Game.ModAPI.IMyPlayer.Identity	local	-	-	-	6	MyPlayer.cs:581 an IMyPlayer is only reachable for players attached to this node, so its character and controller are here
VRage.Game.ModAPI.IMyPlayer.IdentityChanged	local	-	-	-	0	MyPlayer.cs:611 an IMyPlayer is only reachable for players attached to this node, so its character and controller are here
VRage.Game.ModAPI.IMyPlayer.IdentityId	local	-	-	-	73	MyPlayer.cs:545 an IMyPlayer is only reachable for players attached to this node, so its character and controller are here
VRage.Game.ModAPI.IMyPlayer.IsAdmin	local	-	-	-	1	MyPlayer.cs:547 an IMyPlayer is only reachable for players attached to this node, so its character and controller are here
VRage.Game.ModAPI.IMyPlayer.IsBot	local	-	-	-	37	MyPlayer.cs:579 an IMyPlayer is only reachable for players attached to this node, so its character and controller are here
VRage.Game.ModAPI.IMyPlayer.IsPromoted	local	-	-	-	0	MyPlayer.cs:549 an IMyPlayer is only reachable for players attached to this node, so its character and controller are here
VRage.Game.ModAPI.IMyPlayer.PlayerID	local	-	-	-	0	MyPlayer.cs:543 an IMyPlayer is only reachable for players attached to this node, so its character and controller are here
VRage.Game.ModAPI.IMyPlayer.PromoteLevel	local	-	-	-	27	MyPlayer.cs:551 an IMyPlayer is only reachable for players attached to this node, so its character and controller are here
VRage.Game.ModAPI.IMyPlayer.RemoveGrid	node-scoped	B2	no	request (WA)	0	MyPlayer.cs:1284 UNCERTAIN: MyPlayer.Grids is a per-process list; not traced whether the cluster keeps it per player across nodes
VRage.Game.ModAPI.IMyPlayer.RequestChangeBalance	replicated	-	-	-	5	MyPlayer.cs:1344 banking is executed on the WA (cluster: Cluster/Patches/MyBankingSystemPatch.cs:15)
VRage.Game.ModAPI.IMyPlayer.RespawnShip	local	-	-	-	0	MyPlayer.cs:583 an IMyPlayer is only reachable for players attached to this node, so its character and controller are here
VRage.Game.ModAPI.IMyPlayer.SelectedBuildColor	local	-	-	-	4	MyPlayer.cs:555 an IMyPlayer is only reachable for players attached to this node, so its character and controller are here
VRage.Game.ModAPI.IMyPlayer.SelectedBuildColorSlot	local	-	-	-	1	MyPlayer.cs:567 an IMyPlayer is only reachable for players attached to this node, so its character and controller are here
VRage.Game.ModAPI.IMyPlayer.SetDefaultColors	local	-	-	-	0	MyPlayer.cs:1304 an IMyPlayer is only reachable for players attached to this node, so its character and controller are here
VRage.Game.ModAPI.IMyPlayer.SpawnAt	local	-	-	-	0	MyPlayer.cs:1314 an IMyPlayer is only reachable for players attached to this node, so its character and controller are here
VRage.Game.ModAPI.IMyPlayer.SpawnIntoCharacter	local	-	-	-	0	MyPlayer.cs:1309 an IMyPlayer is only reachable for players attached to this node, so its character and controller are here
VRage.Game.ModAPI.IMyPlayer.SteamUserId	local	-	-	-	84	MyPlayer.cs:515 an IMyPlayer is only reachable for players attached to this node, so its character and controller are here
VRage.Game.ModAPI.IMyPlayer.Toolbar	local	-	-	-	0	MyPlayer.cs:517 an IMyPlayer is only reachable for players attached to this node, so its character and controller are here
VRage.Game.ModAPI.IMyPlayer.TryGetBalanceInfo	replicated	-	-	-	4	MyPlayer.cs:1324 banking is executed on the WA (cluster: Cluster/Patches/MyBankingSystemPatch.cs:15)
VRage.Game.ModAPI.IMyPlayerCollection.Count	node-scoped	B1	no	player-nodes	4	MyPlayerCollection.cs:693 enumerates m_players (MyPlayerCollection.cs:3150), which holds only clients attached to this node; players on other nodes are mirrored to clients only (cluster: RemotePlayerMirrorService.cs:29)
VRage.Game.ModAPI.IMyPlayerCollection.ExtendControl	affinity	B8	no	to-node (player's node)	0	MyPlayerCollection.cs:3159 needs the player and the entity on this node; for a player on another node act there
VRage.Game.ModAPI.IMyPlayerCollection.GetAllIdentites	replicated	-	-	-	6	MyPlayerCollection.cs:3240 identity and player bindings come from the WA binding view (cluster: Cluster/Patches/MyPlayerCollectionPatch.cs:398)
VRage.Game.ModAPI.IMyPlayerCollection.GetPlayerControllingEntity	local	-	-	-	15	MyPlayerCollection.cs:3227 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyPlayerCollection.GetPlayers	node-scoped	B1	no	player-nodes	89	MyPlayerCollection.cs:3148 enumerates m_players (MyPlayerCollection.cs:3150), which holds only clients attached to this node; players on other nodes are mirrored to clients only (cluster: RemotePlayerMirrorService.cs:29)
VRage.Game.ModAPI.IMyPlayerCollection.HasExtendedControl	local	-	-	-	0	MyPlayerCollection.cs:3169 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyPlayerCollection.ItemConsumed	local	-	-	-	1	MyPlayerCollection.cs:719 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyPlayerCollection.ReduceControl	affinity	B8	no	to-node (player's node)	0	MyPlayerCollection.cs:3180 needs the player and the entity on this node; for a player on another node act there
VRage.Game.ModAPI.IMyPlayerCollection.RemoveControlledEntity	affinity	B8	no	to-node (player's node)	0	MyPlayerCollection.cs:3190 needs the player and the entity on this node; for a player on another node act there
VRage.Game.ModAPI.IMyPlayerCollection.RequestChangeBalance	replicated	-	-	-	2	MyPlayerCollection.cs:3266 banking is executed on the WA (cluster: Cluster/Patches/MyBankingSystemPatch.cs:15)
VRage.Game.ModAPI.IMyPlayerCollection.SetControlledEntity	affinity	B8	no	to-node (player's node)	0	MyPlayerCollection.cs:3198 needs the player and the entity on this node; for a player on another node act there
VRage.Game.ModAPI.IMyPlayerCollection.TryExtendControl	affinity	B8	no	to-node (player's node)	0	MyPlayerCollection.cs:3206 needs the player and the entity on this node; for a player on another node act there
VRage.Game.ModAPI.IMyPlayerCollection.TryGetIdentityId	replicated	-	-	-	16	MyPlayerCollection.cs:3251 identity and player bindings come from the WA binding view (cluster: Cluster/Patches/MyPlayerCollectionPatch.cs:398)
VRage.Game.ModAPI.IMyPlayerCollection.TryGetSteamId	replicated	-	-	-	26	MyPlayerCollection.cs:3261 identity and player bindings come from the WA binding view (cluster: Cluster/Patches/MyPlayerCollectionPatch.cs:398)
VRage.Game.ModAPI.IMyPlayerCollection.TryReduceControl	affinity	B8	no	to-node (player's node)	0	MyPlayerCollection.cs:3216 needs the player and the entity on this node; for a player on another node act there
VRage.Game.ModAPI.IMyPrefabManager.IsPathClear	node-scoped	B1	no	broadcast / request	0	MyPrefabManager.cs:676 enumerates or queries this node's entities only (MyEntities); entities on other nodes and in offline partitions are missing
VRage.Game.ModAPI.IMyPrefabManager.SpawnPrefab	affinity	B8	no	to-owner / request	5	MyPrefabManager.cs:428 creates the entity on this node whatever the position; a spawn near another node's partition merges over there, and the WA must never spawn (cluster: SpawnDirectorService.cs:15)
VRage.Game.ModAPI.IMyReflection.BaseTypeOf	local	-	-	-	0	ScriptingReflection.cs:7 value, parser or helper; no world state
VRage.Game.ModAPI.IMyReflection.GetInterfaces	local	-	-	-	2	ScriptingReflection.cs:12 value, parser or helper; no world state
VRage.Game.ModAPI.IMyReflection.IsAssignableFrom	local	-	-	-	4	ScriptingReflection.cs:17 value, parser or helper; no world state
VRage.Game.ModAPI.IMyReflection.IsInstanceOfType	local	-	-	-	0	ScriptingReflection.cs:22 value, parser or helper; no world state
VRage.Game.ModAPI.IMyResourceDistributorComponent.MaxAvailableResourceByType	local	-	-	-	3	MyResourceDistributorComponent.cs:3009 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyResourceDistributorComponent.OnPowerGenerationChanged	local	-	-	-	0	MyResourceDistributorComponent.cs:665 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyResourceDistributorComponent.ResourceState	local	-	-	-	3	MyResourceDistributorComponent.cs:211 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyResourceDistributorComponent.SourcesEnabled	local	-	-	-	0	MyResourceDistributorComponent.cs:439 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyResourceDistributorComponent.TotalRequiredInputByType	local	-	-	-	2	MyResourceDistributorComponent.cs:3026 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMySession.AssemblerEfficiencyMultiplier	local	-	-	-	5	MySession.cs:1120 definitions, mods and session settings are the same on every node (cluster join hash)
VRage.Game.ModAPI.IMySession.AssemblerSpeedMultiplier	local	-	-	-	2	MySession.cs:1122 definitions, mods and session settings are the same on every node (cluster join hash)
VRage.Game.ModAPI.IMySession.AutoHealing	local	-	-	-	0	MySession.cs:1124 definitions, mods and session settings are the same on every node (cluster join hash)
VRage.Game.ModAPI.IMySession.AutoSaveInMinutes	local	-	-	-	0	MySession.cs:1126 definitions, mods and session settings are the same on every node (cluster join hash)
VRage.Game.ModAPI.IMySession.BeforeStartComponents	local	-	-	-	0	MySession.cs:5653 process-wide registration or service; every node runs the mod and registers its own
VRage.Game.ModAPI.IMySession.BlocksInventorySizeMultiplier	local	-	-	-	5	MySession.cs:1174 definitions, mods and session settings are the same on every node (cluster join hash)
VRage.Game.ModAPI.IMySession.Camera	client	-	-	-	69	MySession.cs:1316 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMySession.CameraController	client	-	-	-	21	MySession.cs:1118 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMySession.CameraTargetDistance	client	-	-	-	0	MySession.cs:1318 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMySession.CargoShipsEnabled	local	-	-	-	0	MySession.cs:1128 definitions, mods and session settings are the same on every node (cluster join hash)
VRage.Game.ModAPI.IMySession.CharactersInventoryMultiplier	local	-	-	-	0	MySession.cs:1172 definitions, mods and session settings are the same on every node (cluster join hash)
VRage.Game.ModAPI.IMySession.ClientCanSave	local	-	-	-	0	MySession.cs:1130 definitions, mods and session settings are the same on every node (cluster join hash)
VRage.Game.ModAPI.IMySession.Config	local	-	-	-	51	MySession.cs:1330 accessor of a ModAPI service; the policy is on the members of the returned interface
VRage.Game.ModAPI.IMySession.ControlledObject	client	-	-	-	27	MySession.cs:1310 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMySession.CreativeMode	local	-	-	-	28	MySession.cs:1132 definitions, mods and session settings are the same on every node (cluster join hash)
VRage.Game.ModAPI.IMySession.CurrentPath	local	-	-	-	4	MySession.cs:1134 definitions, mods and session settings are the same on every node (cluster join hash)
VRage.Game.ModAPI.IMySession.DamageSystem	local	-	-	-	26	MySession.cs:1332 accessor of a ModAPI service; the policy is on the members of the returned interface
VRage.Game.ModAPI.IMySession.Description	local	-	-	-	0	MySession.cs:1136 definitions, mods and session settings are the same on every node (cluster join hash)
VRage.Game.ModAPI.IMySession.Draw	client	-	-	-	0	MySession.cs:5658 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMySession.ElapsedPlayTime	replicated	-	-	-	23	MySession.cs:1148 session clock is slewed to the WA clock (cluster: Patches/MySessionPatch.cs:24)
VRage.Game.ModAPI.IMySession.EnableCopyPaste	local	-	-	-	7	MySession.cs:1150 definitions, mods and session settings are the same on every node (cluster join hash)
VRage.Game.ModAPI.IMySession.EnvironmentHostility	local	-	-	-	0	MySession.cs:1152 definitions, mods and session settings are the same on every node (cluster join hash)
VRage.Game.ModAPI.IMySession.Factions	local	-	-	-	43	MySession.cs:1314 accessor of a ModAPI service; the policy is on the members of the returned interface
VRage.Game.ModAPI.IMySession.GPS	local	-	-	-	24	MySession.cs:1334 accessor of a ModAPI service; the policy is on the members of the returned interface
VRage.Game.ModAPI.IMySession.GameDateTime	replicated	-	-	-	16	MySession.cs:1154 session clock is slewed to the WA clock (cluster: Patches/MySessionPatch.cs:24)
VRage.Game.ModAPI.IMySession.GameOver	per-node	B6	no	role (act on WA only)	0	MySession.cs:5663 acts on this node process only (save, unload, quit)
VRage.Game.ModAPI.IMySession.GameplayFrameCounter	node-scoped	B2	no	-	28	MySession.cs:978 frame counter of this process; differs between nodes, so a stored value is meaningless after a handover
VRage.Game.ModAPI.IMySession.GetCheckpoint	node-scoped	B1	no	request (WA)	9	MySession.cs:5673 snapshot of this node's session only (partial world)
VRage.Game.ModAPI.IMySession.GetComponentByInterfaceType	local	-	-	-	4	MySession.cs:1663 accessor of a ModAPI service; the policy is on the members of the returned interface
VRage.Game.ModAPI.IMySession.GetSector	node-scoped	B1	no	request (WA)	5	MySession.cs:5678 snapshot of this node's session only (partial world)
VRage.Game.ModAPI.IMySession.GetUserPromoteLevel	replicated	-	-	-	12	MySession.cs:5759 promote levels are replicated from the WA (cluster: Patches/MySessionPatch.cs:17)
VRage.Game.ModAPI.IMySession.GetVoxelMapsArray	node-scoped	B1	no	request (WA)	0	MySession.cs:5683 snapshot of this node's session only (partial world)
VRage.Game.ModAPI.IMySession.GetWorld	node-scoped	B1	no	request (WA)	0	MySession.cs:5688 snapshot of this node's session only (partial world)
VRage.Game.ModAPI.IMySession.GrinderSpeedMultiplier	local	-	-	-	9	MySession.cs:1166 definitions, mods and session settings are the same on every node (cluster join hash)
VRage.Game.ModAPI.IMySession.HackSpeedMultiplier	local	-	-	-	1	MySession.cs:1168 definitions, mods and session settings are the same on every node (cluster join hash)
VRage.Game.ModAPI.IMySession.HasAdminPrivileges	client	-	-	-	0	MySession.cs:1337 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMySession.HasCreativeRights	client	-	-	-	7	MySession.cs:1341 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMySession.InventoryMultiplier	local	-	-	-	0	MySession.cs:1170 definitions, mods and session settings are the same on every node (cluster join hash)
VRage.Game.ModAPI.IMySession.IsCameraAwaitingEntity	client	-	-	-	0	MySession.cs:1176 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMySession.IsCameraControlledObject	client	-	-	-	3	MySession.cs:1188 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMySession.IsCameraUserControlledSpectator	client	-	-	-	9	MySession.cs:1190 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMySession.IsPausable	local	-	-	-	1	MySession.cs:5693 definitions, mods and session settings are the same on every node (cluster join hash)
VRage.Game.ModAPI.IMySession.IsServer	local	-	-	-	90	MySession.cs:1072 every node is the server of its own clients; true and correct per process
VRage.Game.ModAPI.IMySession.IsUserAdmin	replicated	-	-	-	11	MySession.cs:5748 promote levels are replicated from the WA (cluster: Patches/MySessionPatch.cs:17)
VRage.Game.ModAPI.IMySession.IsUserIgnorePCULimit	node-scoped	B2	no	request (WA)	1	MySession.cs:5590 UNCERTAIN: admin settings live in MySession.RemoteAdminSettings of this process; set from the admin's client on its node, replication not verified
VRage.Game.ModAPI.IMySession.IsUserIgnoreSafeZones	node-scoped	B2	no	request (WA)	0	MySession.cs:5581 UNCERTAIN: admin settings live in MySession.RemoteAdminSettings of this process; set from the admin's client on its node, replication not verified
VRage.Game.ModAPI.IMySession.IsUserInvulnerable	node-scoped	B2	no	request (WA)	1	MySession.cs:5536 UNCERTAIN: admin settings live in MySession.RemoteAdminSettings of this process; set from the admin's client on its node, replication not verified
VRage.Game.ModAPI.IMySession.IsUserKeepOriginalOwnershipOnPaste	node-scoped	B2	no	request (WA)	0	MySession.cs:5572 UNCERTAIN: admin settings live in MySession.RemoteAdminSettings of this process; set from the admin's client on its node, replication not verified
VRage.Game.ModAPI.IMySession.IsUserPromoted	replicated	-	-	-	0	MySession.cs:5754 promote levels are replicated from the WA (cluster: Patches/MySessionPatch.cs:17)
VRage.Game.ModAPI.IMySession.IsUserShowAllPlayers	node-scoped	B2	no	request (WA)	0	MySession.cs:5545 UNCERTAIN: admin settings live in MySession.RemoteAdminSettings of this process; set from the admin's client on its node, replication not verified
VRage.Game.ModAPI.IMySession.IsUserUntargetable	node-scoped	B2	no	request (WA)	0	MySession.cs:5563 UNCERTAIN: admin settings live in MySession.RemoteAdminSettings of this process; set from the admin's client on its node, replication not verified
VRage.Game.ModAPI.IMySession.IsUserUseAllTerminals	node-scoped	B2	no	request (WA)	0	MySession.cs:5554 UNCERTAIN: admin settings live in MySession.RemoteAdminSettings of this process; set from the admin's client on its node, replication not verified
VRage.Game.ModAPI.IMySession.LocalHumanPlayer	client	-	-	-	44	MySession.cs:882 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMySession.MaxBackupSaves	local	-	-	-	0	MySession.cs:1194 definitions, mods and session settings are the same on every node (cluster join hash)
VRage.Game.ModAPI.IMySession.MaxFloatingObjects	local	-	-	-	0	MySession.cs:1192 definitions, mods and session settings are the same on every node (cluster join hash)
VRage.Game.ModAPI.IMySession.MaxPlayers	local	-	-	-	1	MySession.cs:1196 definitions, mods and session settings are the same on every node (cluster join hash)
VRage.Game.ModAPI.IMySession.Mods	local	-	-	-	54	MySession.cs:744 definitions, mods and session settings are the same on every node (cluster join hash)
VRage.Game.ModAPI.IMySession.MultiplayerAlive	local	-	-	-	2	MySession.cs:1198 definitions, mods and session settings are the same on every node (cluster join hash)
VRage.Game.ModAPI.IMySession.MultiplayerDirect	local	-	-	-	0	MySession.cs:1210 definitions, mods and session settings are the same on every node (cluster join hash)
VRage.Game.ModAPI.IMySession.MultiplayerLastMsg	local	-	-	-	0	MySession.cs:1222 definitions, mods and session settings are the same on every node (cluster join hash)
VRage.Game.ModAPI.IMySession.Name	local	-	-	-	11	MySession.cs:1234 definitions, mods and session settings are the same on every node (cluster join hash)
VRage.Game.ModAPI.IMySession.NegativeIntegrityTotal	local	-	-	-	0	MySession.cs:1246 definitions, mods and session settings are the same on every node (cluster join hash)
VRage.Game.ModAPI.IMySession.OnSessionLoading	per-node	B6	no	role (act on WA only)	0	MySession.cs:1385 fires once per node process (each node and the WA run the mod), so a world-wide action in the handler runs once per node
VRage.Game.ModAPI.IMySession.OnSessionReady	per-node	B6	no	role (act on WA only)	17	MySession.cs:1373 fires once per node process (each node and the WA run the mod), so a world-wide action in the handler runs once per node
VRage.Game.ModAPI.IMySession.OnlineMode	local	-	-	-	40	MySession.cs:1258 definitions, mods and session settings are the same on every node (cluster join hash)
VRage.Game.ModAPI.IMySession.OxygenProviderSystem	local	-	-	-	8	MySession.cs:1345 accessor of a ModAPI service; the policy is on the members of the returned interface
VRage.Game.ModAPI.IMySession.Password	local	-	-	-	0	MySession.cs:1260 definitions, mods and session settings are the same on every node (cluster join hash)
VRage.Game.ModAPI.IMySession.Player	client	-	-	-	112	MySession.cs:1308 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMySession.PositiveIntegrityTotal	local	-	-	-	0	MySession.cs:1272 definitions, mods and session settings are the same on every node (cluster join hash)
VRage.Game.ModAPI.IMySession.PromoteLevel	client	-	-	-	4	MySession.cs:1339 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMySession.RefinerySpeedMultiplier	local	-	-	-	2	MySession.cs:1284 definitions, mods and session settings are the same on every node (cluster join hash)
VRage.Game.ModAPI.IMySession.RegisterComponent	local	-	-	-	0	MySession.cs:5698 process-wide registration or service; every node runs the mod and registers its own
VRage.Game.ModAPI.IMySession.Save	per-node	B6	no	role (act on WA only)	1	MySession.cs:5703 acts on this node process only (save, unload, quit)
VRage.Game.ModAPI.IMySession.SessionSettings	local	-	-	-	69	MySession.cs:1312 definitions, mods and session settings are the same on every node (cluster join hash)
VRage.Game.ModAPI.IMySession.SetAsNotReady	per-node	B6	no	role (act on WA only)	0	MySession.cs:5708 acts on this node process only (save, unload, quit)
VRage.Game.ModAPI.IMySession.SetCameraController	client	-	-	-	4	MySession.cs:5713 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMySession.SetComponentUpdateOrder	local	-	-	-	0	MySession.cs:1787 process-wide registration or service; every node runs the mod and registers its own
VRage.Game.ModAPI.IMySession.ShowPlayerNamesOnHud	local	-	-	-	0	MySession.cs:1286 definitions, mods and session settings are the same on every node (cluster join hash)
VRage.Game.ModAPI.IMySession.SurvivalMode	local	-	-	-	3	MySession.cs:1288 definitions, mods and session settings are the same on every node (cluster join hash)
VRage.Game.ModAPI.IMySession.ThrusterDamage	local	-	-	-	0	MySession.cs:1290 definitions, mods and session settings are the same on every node (cluster join hash)
VRage.Game.ModAPI.IMySession.ThumbPath	local	-	-	-	1	MySession.cs:1292 definitions, mods and session settings are the same on every node (cluster join hash)
VRage.Game.ModAPI.IMySession.TimeOnBigShip	local	-	-	-	0	MySession.cs:1294 definitions, mods and session settings are the same on every node (cluster join hash)
VRage.Game.ModAPI.IMySession.TimeOnFoot	local	-	-	-	0	MySession.cs:1296 definitions, mods and session settings are the same on every node (cluster join hash)
VRage.Game.ModAPI.IMySession.TimeOnJetpack	local	-	-	-	0	MySession.cs:1298 definitions, mods and session settings are the same on every node (cluster join hash)
VRage.Game.ModAPI.IMySession.TimeOnSmallShip	local	-	-	-	0	MySession.cs:1300 definitions, mods and session settings are the same on every node (cluster join hash)
VRage.Game.ModAPI.IMySession.TotalBotLimit	local	-	-	-	0	MySession.cs:1347 definitions, mods and session settings are the same on every node (cluster join hash)
VRage.Game.ModAPI.IMySession.TryGetAdminSettings	node-scoped	B2	no	request (WA)	4	MySession.cs:5525 UNCERTAIN: admin settings live in MySession.RemoteAdminSettings of this process; set from the admin's client on its node, replication not verified
VRage.Game.ModAPI.IMySession.TryGetComponentByInterfaceType	local	-	-	-	0	MySession.cs:1675 accessor of a ModAPI service; the policy is on the members of the returned interface
VRage.Game.ModAPI.IMySession.Unload	per-node	B6	no	role (act on WA only)	0	MySession.cs:5718 acts on this node process only (save, unload, quit)
VRage.Game.ModAPI.IMySession.UnloadDataComponents	per-node	B6	no	role (act on WA only)	0	MySession.cs:5723 acts on this node process only (save, unload, quit)
VRage.Game.ModAPI.IMySession.UnloadMultiplayer	per-node	B6	no	role (act on WA only)	0	MySession.cs:5728 acts on this node process only (save, unload, quit)
VRage.Game.ModAPI.IMySession.UnregisterComponent	local	-	-	-	0	MySession.cs:5733 process-wide registration or service; every node runs the mod and registers its own
VRage.Game.ModAPI.IMySession.Update	local	-	-	-	0	MySession.cs:5738 process-wide registration or service; every node runs the mod and registers its own
VRage.Game.ModAPI.IMySession.UpdateComponents	local	-	-	-	0	MySession.cs:5743 process-wide registration or service; every node runs the mod and registers its own
VRage.Game.ModAPI.IMySession.Version	local	-	-	-	5	MySession.cs:1343 definitions, mods and session settings are the same on every node (cluster join hash)
VRage.Game.ModAPI.IMySession.VoxelMaps	local	-	-	-	13	MySession.cs:1116 accessor of a ModAPI service; the policy is on the members of the returned interface
VRage.Game.ModAPI.IMySession.WeaponsEnabled	local	-	-	-	1	MySession.cs:1302 definitions, mods and session settings are the same on every node (cluster join hash)
VRage.Game.ModAPI.IMySession.WeatherEffects	local	-	-	-	14	MySession.cs:878 accessor of a ModAPI service; the policy is on the members of the returned interface
VRage.Game.ModAPI.IMySession.WelderSpeedMultiplier	local	-	-	-	11	MySession.cs:1304 definitions, mods and session settings are the same on every node (cluster join hash)
VRage.Game.ModAPI.IMySession.WorkshopId	local	-	-	-	0	MySession.cs:1306 definitions, mods and session settings are the same on every node (cluster join hash)
VRage.Game.ModAPI.IMySession.WorldBoundaries	local	-	-	-	0	MySession.cs:746 definitions, mods and session settings are the same on every node (cluster join hash)
VRage.Game.ModAPI.IMySlimBlock.AddNeighbours	local	-	-	-	0	MySlimBlock.cs:3235 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMySlimBlock.ApplyAccumulatedDamage	local	-	-	-	0	MySlimBlock.cs:3248 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMySlimBlock.BlockDefinition	local	-	-	-	74	MySlimBlock.cs:1089 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMySlimBlock.BlockGeneralDamageModifier	local	-	-	-	1	MySlimBlock.cs:1118 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMySlimBlock.BuiltBy	local	-	-	-	7	MySlimBlock.cs:1116 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMySlimBlock.CalculateCurrentModel	local	-	-	-	0	MySlimBlock.cs:3253 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMySlimBlock.CanContinueBuild	local	-	-	-	5	MySlimBlock.cs:3373 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMySlimBlock.ClearConstructionStockpile	local	-	-	-	4	MySlimBlock.cs:3368 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMySlimBlock.ComponentStack	local	-	-	-	9	MySlimBlock.cs:1130 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMySlimBlock.ComputeScaledCenter	local	-	-	-	0	MySlimBlock.cs:3258 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMySlimBlock.ComputeScaledHalfExtents	local	-	-	-	7	MySlimBlock.cs:3263 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMySlimBlock.ComputeWorldCenter	local	-	-	-	16	MySlimBlock.cs:3268 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMySlimBlock.CubeGrid	local	-	-	-	58	MySlimBlock.cs:1081 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMySlimBlock.DecreaseMountLevel	local	-	-	-	10	MySlimBlock.cs:3348 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMySlimBlock.Dithering	local	-	-	-	11	MySlimBlock.cs:1099 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMySlimBlock.FatBlock	local	-	-	-	88	MySlimBlock.cs:1045 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMySlimBlock.FixBones	local	-	-	-	8	MySlimBlock.cs:3273 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMySlimBlock.FullyDismount	local	-	-	-	0	MySlimBlock.cs:3278 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMySlimBlock.GetColorMask	local	-	-	-	5	MySlimBlock.cs:3343 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMySlimBlock.GetConstructionStockpileItemAmount	local	-	-	-	1	MySlimBlock.cs:3358 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMySlimBlock.GetCopyObjectBuilder	local	-	-	-	0	MySlimBlock.cs:3283 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMySlimBlock.GetNeighbours	local	-	-	-	4	MySlimBlock.cs:3240 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMySlimBlock.GetObjectBuilder	local	-	-	-	10	MySlimBlock.cs:3288 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMySlimBlock.GetWorldBoundingBox	local	-	-	-	8	MySlimBlock.cs:3378 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMySlimBlock.IncreaseMountLevel	local	-	-	-	11	MySlimBlock.cs:3353 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMySlimBlock.InitOrientation	local	-	-	-	0	MySlimBlock.cs:3293 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMySlimBlock.IsMovedBySplit	local	-	-	-	0	MySlimBlock.cs:1041 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMySlimBlock.Max	local	-	-	-	25	MySlimBlock.cs:1091 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMySlimBlock.Min	local	-	-	-	31	MySlimBlock.cs:1093 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMySlimBlock.MoveItemsFromConstructionStockpile	local	-	-	-	6	MySlimBlock.cs:3323 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMySlimBlock.MoveItemsToConstructionStockpile	local	-	-	-	8	MySlimBlock.cs:3363 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMySlimBlock.Neighbours	local	-	-	-	1	MySlimBlock.cs:1097 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMySlimBlock.Orientation	local	-	-	-	21	MySlimBlock.cs:1095 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMySlimBlock.PlayConstructionSound	local	-	-	-	1	MySlimBlock.cs:2617 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMySlimBlock.RemoveNeighbours	local	-	-	-	0	MySlimBlock.cs:3308 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMySlimBlock.SetToConstructionSite	local	-	-	-	0	MySlimBlock.cs:3313 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMySlimBlock.SpawnConstructionStockpile	local	-	-	-	5	MySlimBlock.cs:3318 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMySlimBlock.SpawnFirstItemInConstructionStockpile	local	-	-	-	0	MySlimBlock.cs:3328 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMySlimBlock.UpdateVisual	local	-	-	-	4	MySlimBlock.cs:3333 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMySpectatorTools.ClearTrackedSlot	client	-	-	-	0	MySessionComponentSpectatorTools.cs:275 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMySpectatorTools.GetMode	client	-	-	-	0	MySessionComponentSpectatorTools.cs:113 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMySpectatorTools.GetTarget	client	-	-	-	0	MySessionComponentSpectatorTools.cs:99 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMySpectatorTools.LockHitEntity	client	-	-	-	0	MySessionComponentSpectatorTools.cs:258 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMySpectatorTools.NextPlayer	client	-	-	-	0	MySessionComponentSpectatorTools.cs:449 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMySpectatorTools.PreviousPlayer	client	-	-	-	0	MySessionComponentSpectatorTools.cs:454 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMySpectatorTools.SaveTrackedSlot	client	-	-	-	0	MySessionComponentSpectatorTools.cs:280 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMySpectatorTools.SelectTrackedSlot	client	-	-	-	0	MySessionComponentSpectatorTools.cs:285 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMySpectatorTools.SetMode	client	-	-	-	0	MySessionComponentSpectatorTools.cs:108 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMySpectatorTools.SetTarget	client	-	-	-	0	MySessionComponentSpectatorTools.cs:94 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMySpectatorTools.TrackedSlots	client	-	-	-	0	MySessionComponentSpectatorTools.cs:81 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMySphericalNaturalGravityComponent.Falloff	local	-	-	-	3	MySphericalNaturalGravityComponent.cs:86 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMySphericalNaturalGravityComponent.Intensity	local	-	-	-	4	MySphericalNaturalGravityComponent.cs:99 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMySphericalNaturalGravityComponent.MaxRadius	local	-	-	-	5	MySphericalNaturalGravityComponent.cs:73 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMySphericalNaturalGravityComponent.MinRadius	local	-	-	-	4	MySphericalNaturalGravityComponent.cs:60 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyStoreItem.Amount	local	-	-	-	2	MyStoreItem.cs:320 value, parser or helper; no world state
VRage.Game.ModAPI.IMyStoreItem.Id	local	-	-	-	1	MyStoreItem.cs:313 value, parser or helper; no world state
VRage.Game.ModAPI.IMyStoreItem.IsActive	local	-	-	-	2	MyStoreItem.cs:360 value, parser or helper; no world state
VRage.Game.ModAPI.IMyStoreItem.IsCustomStoreItem	local	-	-	-	1	MyStoreItem.cs:369 value, parser or helper; no world state
VRage.Game.ModAPI.IMyStoreItem.Item	local	-	-	-	3	MyStoreItem.cs:316 value, parser or helper; no world state
VRage.Game.ModAPI.IMyStoreItem.ItemType	local	-	-	-	3	MyStoreItem.cs:318 value, parser or helper; no world state
VRage.Game.ModAPI.IMyStoreItem.OnCancel	local	-	-	-	0	MyStoreItem.cs:404 value, parser or helper; no world state
VRage.Game.ModAPI.IMyStoreItem.OnTransaction	local	-	-	-	0	MyStoreItem.cs:392 value, parser or helper; no world state
VRage.Game.ModAPI.IMyStoreItem.PrefabName	local	-	-	-	1	MyStoreItem.cs:363 value, parser or helper; no world state
VRage.Game.ModAPI.IMyStoreItem.PrefabTotalPcu	local	-	-	-	0	MyStoreItem.cs:365 value, parser or helper; no world state
VRage.Game.ModAPI.IMyStoreItem.PreviousPricePerUnit	local	-	-	-	1	MyStoreItem.cs:356 value, parser or helper; no world state
VRage.Game.ModAPI.IMyStoreItem.PricePerUnit	local	-	-	-	3	MyStoreItem.cs:341 value, parser or helper; no world state
VRage.Game.ModAPI.IMyStoreItem.PricePerUnitDiscount	local	-	-	-	0	MyStoreItem.cs:367 value, parser or helper; no world state
VRage.Game.ModAPI.IMyStoreItem.RemovedAmount	local	-	-	-	1	MyStoreItem.cs:337 value, parser or helper; no world state
VRage.Game.ModAPI.IMyStoreItem.StoreItemType	local	-	-	-	3	MyStoreItem.cs:358 value, parser or helper; no world state
VRage.Game.ModAPI.IMyToolbar.GamepadSlots	client	-	-	-	0	MyToolbar.cs:313 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyToolbar.GetObjectBuilder	client	-	-	-	0	MyToolbar.cs:1735 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyToolbar.Type	client	-	-	-	0	MyToolbar.cs:305 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyToolbarChangeBuilder.Apply	client	-	-	-	0	MyToolbarChangeBuilder.cs:81 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyToolbarChangeBuilder.Clear	client	-	-	-	0	MyToolbarChangeBuilder.cs:76 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyToolbarChangeBuilder.Set	client	-	-	-	0	MyToolbarChangeBuilder.cs:32 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyToolbarProvider.GetToolbar	client	-	-	-	0	MyToolbarProviderComponent.cs:75 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyToolbarProvider.ToolbarCount	client	-	-	-	0	MyToolbarProviderComponent.cs:39 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyToolbarSlots.BeginChanges	client	-	-	-	0	MyToolbar.cs:1730 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyToolbarSlots.GetItems	client	-	-	-	0	MyToolbar.cs:1700 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyToolbarSlots.ItemCount	client	-	-	-	0	MyToolbar.cs:311 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyToolbarSlots.PageCount	client	-	-	-	0	MyToolbar.cs:309 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyToolbarSlots.SlotCount	client	-	-	-	0	MyToolbar.cs:307 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyToolbarSlots.Trigger	client	-	-	-	0	MyToolbar.cs:1710 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyToolbarSlots.TryGetItem	client	-	-	-	0	MyToolbar.cs:1695 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyTriangleVertexIndices.I0	local	-	-	-	2	MyTriangleVertexIndices.cs:16 value, parser or helper; no world state
VRage.Game.ModAPI.IMyTriangleVertexIndices.I1	local	-	-	-	2	MyTriangleVertexIndices.cs:18 value, parser or helper; no world state
VRage.Game.ModAPI.IMyTriangleVertexIndices.I2	local	-	-	-	2	MyTriangleVertexIndices.cs:20 value, parser or helper; no world state
VRage.Game.ModAPI.IMyUtilities.ConfigDedicated	local	-	-	-	2	MyAPIUtilities.cs:31 process-wide registration or service; every node runs the mod and registers its own
VRage.Game.ModAPI.IMyUtilities.CreateNotification	client	-	-	-	35	MyAPIUtilities.cs:94 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyUtilities.DeleteFileInGlobalStorage	per-node	B7	yes	request (WA)	4	MyAPIUtilities.cs:440 writes this node's own file (<world>/Storage/<mod> or UserDataPath/Storage); only the WA copy is kept (cluster: ModCompatService.cs:493)
VRage.Game.ModAPI.IMyUtilities.DeleteFileInLocalStorage	per-node	B7	yes	request (WA)	17	MyAPIUtilities.cs:414 writes this node's own file (<world>/Storage/<mod> or UserDataPath/Storage); only the WA copy is kept (cluster: ModCompatService.cs:493)
VRage.Game.ModAPI.IMyUtilities.DeleteFileInWorldStorage	per-node	B7	yes	request (WA)	14	MyAPIUtilities.cs:427 writes this node's own file (<world>/Storage/<mod> or UserDataPath/Storage); only the WA copy is kept (cluster: ModCompatService.cs:493)
VRage.Game.ModAPI.IMyUtilities.FileExistsInGameContent	local	-	-	-	7	MyAPIUtilities.cs:355 reads files shipped with the mod or the game; identical on every node
VRage.Game.ModAPI.IMyUtilities.FileExistsInGlobalStorage	per-node	B7	yes	request (WA)	10	MyAPIUtilities.cs:369 reads this node's own copy; consistent only while no node writes it at runtime (cluster: ModCompatService.cs:493)
VRage.Game.ModAPI.IMyUtilities.FileExistsInLocalStorage	per-node	B7	yes	request (WA)	53	MyAPIUtilities.cs:384 reads this node's own copy; consistent only while no node writes it at runtime (cluster: ModCompatService.cs:493)
VRage.Game.ModAPI.IMyUtilities.FileExistsInModLocation	local	-	-	-	31	MyAPIUtilities.cs:336 reads files shipped with the mod or the game; identical on every node
VRage.Game.ModAPI.IMyUtilities.FileExistsInWorldStorage	per-node	B7	yes	request (WA)	66	MyAPIUtilities.cs:399 reads this node's own copy; consistent only while no node writes it at runtime (cluster: ModCompatService.cs:493)
VRage.Game.ModAPI.IMyUtilities.GamePaths	local	-	-	-	45	MyAPIUtilities.cs:43 process-wide registration or service; every node runs the mod and registers its own
VRage.Game.ModAPI.IMyUtilities.GetObjectiveLine	client	-	-	-	0	MyAPIUtilities.cs:458 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyUtilities.GetTypeName	local	-	-	-	1	MyAPIUtilities.cs:82 value, parser or helper; no world state
VRage.Game.ModAPI.IMyUtilities.GetVariable	per-node	B7	no	request (WA)	26	MyAPIUtilities.cs:624 MyAPIUtilities.Variables lives in this node's checkpoint (ScriptManagerData); every node has its own and only the WA copy is saved
VRage.Game.ModAPI.IMyUtilities.InvokeOnGameThread	local	-	-	-	57	MyAPIUtilities.cs:328 process-wide registration or service; every node runs the mod and registers its own
VRage.Game.ModAPI.IMyUtilities.IsDedicated	local	-	-	-	149	MyAPIUtilities.cs:45 process-wide registration or service; every node runs the mod and registers its own
VRage.Game.ModAPI.IMyUtilities.MessageEntered	client	-	-	-	66	MyAPIUtilities.cs:53 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyUtilities.MessageEnteredSender	client	-	-	-	25	MyAPIUtilities.cs:49 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyUtilities.MessageRecieved	client	-	-	-	3	MyAPIUtilities.cs:65 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyUtilities.ReadBinaryFileInGameContent	local	-	-	-	8	MyAPIUtilities.cs:486 reads files shipped with the mod or the game; identical on every node
VRage.Game.ModAPI.IMyUtilities.ReadBinaryFileInGlobalStorage	per-node	B7	yes	request (WA)	1	MyAPIUtilities.cs:504 reads this node's own copy; consistent only while no node writes it at runtime (cluster: ModCompatService.cs:493)
VRage.Game.ModAPI.IMyUtilities.ReadBinaryFileInLocalStorage	per-node	B7	yes	request (WA)	14	MyAPIUtilities.cs:523 reads this node's own copy; consistent only while no node writes it at runtime (cluster: ModCompatService.cs:493)
VRage.Game.ModAPI.IMyUtilities.ReadBinaryFileInModLocation	local	-	-	-	13	MyAPIUtilities.cs:463 reads files shipped with the mod or the game; identical on every node
VRage.Game.ModAPI.IMyUtilities.ReadBinaryFileInWorldStorage	per-node	B7	yes	request (WA)	6	MyAPIUtilities.cs:542 reads this node's own copy; consistent only while no node writes it at runtime (cluster: ModCompatService.cs:493)
VRage.Game.ModAPI.IMyUtilities.ReadFileInGameContent	local	-	-	-	5	MyAPIUtilities.cs:163 reads files shipped with the mod or the game; identical on every node
VRage.Game.ModAPI.IMyUtilities.ReadFileInGlobalStorage	per-node	B7	yes	request (WA)	11	MyAPIUtilities.cs:181 reads this node's own copy; consistent only while no node writes it at runtime (cluster: ModCompatService.cs:493)
VRage.Game.ModAPI.IMyUtilities.ReadFileInLocalStorage	per-node	B7	yes	request (WA)	54	MyAPIUtilities.cs:200 reads this node's own copy; consistent only while no node writes it at runtime (cluster: ModCompatService.cs:493)
VRage.Game.ModAPI.IMyUtilities.ReadFileInModLocation	local	-	-	-	24	MyAPIUtilities.cs:140 reads files shipped with the mod or the game; identical on every node
VRage.Game.ModAPI.IMyUtilities.ReadFileInWorldStorage	per-node	B7	yes	request (WA)	71	MyAPIUtilities.cs:219 reads this node's own copy; consistent only while no node writes it at runtime (cluster: ModCompatService.cs:493)
VRage.Game.ModAPI.IMyUtilities.RegisterMessageHandler	local	-	-	-	97	MyAPIUtilities.cs:640 in-process cross-mod bus; both mods always run in the same process
VRage.Game.ModAPI.IMyUtilities.RemoveVariable	per-node	B7	no	request (WA)	6	MyAPIUtilities.cs:635 MyAPIUtilities.Variables lives in this node's checkpoint (ScriptManagerData); every node has its own and only the WA copy is saved
VRage.Game.ModAPI.IMyUtilities.SendMessage	node-scoped	B5	yes	player-nodes + to-node	6	MyAPIUtilities.cs:106 UNCERTAIN: MyMultiplayer.SendChatMessage from the server; player chat is accepted on the WA (cluster: Cluster/Patches/ChatAuthorityPatch.cs:58), server-sent chat was not traced
VRage.Game.ModAPI.IMyUtilities.SendModMessage	local	-	-	-	77	MyAPIUtilities.cs:658 in-process cross-mod bus; both mods always run in the same process
VRage.Game.ModAPI.IMyUtilities.SerializeFromBinary	local	-	-	-	105	MyAPIUtilities.cs:322 value, parser or helper; no world state
VRage.Game.ModAPI.IMyUtilities.SerializeFromXML	local	-	-	-	98	MyAPIUtilities.cs:303 value, parser or helper; no world state
VRage.Game.ModAPI.IMyUtilities.SerializeToBinary	local	-	-	-	135	MyAPIUtilities.cs:315 value, parser or helper; no world state
VRage.Game.ModAPI.IMyUtilities.SerializeToXML	local	-	-	-	95	MyAPIUtilities.cs:295 value, parser or helper; no world state
VRage.Game.ModAPI.IMyUtilities.SetVariable	per-node	B7	no	request (WA)	25	MyAPIUtilities.cs:618 MyAPIUtilities.Variables lives in this node's checkpoint (ScriptManagerData); every node has its own and only the WA copy is saved
VRage.Game.ModAPI.IMyUtilities.ShowMessage	client	-	-	-	99	MyAPIUtilities.cs:101 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyUtilities.ShowMissionScreen	client	-	-	-	34	MyAPIUtilities.cs:453 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyUtilities.ShowNotification	client	-	-	-	85	MyAPIUtilities.cs:87 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyUtilities.UnregisterMessageHandler	local	-	-	-	93	MyAPIUtilities.cs:650 in-process cross-mod bus; both mods always run in the same process
VRage.Game.ModAPI.IMyUtilities.WriteBinaryFileInGlobalStorage	per-node	B7	yes	request (WA)	4	MyAPIUtilities.cs:561 writes this node's own file (<world>/Storage/<mod> or UserDataPath/Storage); only the WA copy is kept (cluster: ModCompatService.cs:493)
VRage.Game.ModAPI.IMyUtilities.WriteBinaryFileInLocalStorage	per-node	B7	yes	request (WA)	16	MyAPIUtilities.cs:580 writes this node's own file (<world>/Storage/<mod> or UserDataPath/Storage); only the WA copy is kept (cluster: ModCompatService.cs:493)
VRage.Game.ModAPI.IMyUtilities.WriteBinaryFileInWorldStorage	per-node	B7	yes	request (WA)	6	MyAPIUtilities.cs:599 writes this node's own file (<world>/Storage/<mod> or UserDataPath/Storage); only the WA copy is kept (cluster: ModCompatService.cs:493)
VRage.Game.ModAPI.IMyUtilities.WriteFileInGlobalStorage	per-node	B7	yes	request (WA)	12	MyAPIUtilities.cs:238 writes this node's own file (<world>/Storage/<mod> or UserDataPath/Storage); only the WA copy is kept (cluster: ModCompatService.cs:493)
VRage.Game.ModAPI.IMyUtilities.WriteFileInLocalStorage	per-node	B7	yes	request (WA)	99	MyAPIUtilities.cs:257 writes this node's own file (<world>/Storage/<mod> or UserDataPath/Storage); only the WA copy is kept (cluster: ModCompatService.cs:493)
VRage.Game.ModAPI.IMyUtilities.WriteFileInWorldStorage	per-node	B7	yes	request (WA)	72	MyAPIUtilities.cs:276 writes this node's own file (<world>/Storage/<mod> or UserDataPath/Storage); only the WA copy is kept (cluster: ModCompatService.cs:493)
VRage.Game.ModAPI.IMyVoxelMap.ClampVoxelCoord	local	-	-	-	0	MyVoxelMap.cs:400 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyVoxelMap.Close	local	-	-	-	2	MyVoxelMap.cs:390 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyVoxelMap.DoOverlapSphereTest	local	-	-	-	0	MyVoxelMap.cs:395 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyVoxelMap.GetIntersectionWithSphere	local	-	-	-	0	MyVoxelMap.cs:405 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyVoxelMap.GetObjectBuilder	local	-	-	-	0	MyVoxelMap.cs:410 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyVoxelMap.GetVoxelContentInBoundingBox	local	-	-	-	0	MyVoxelMap.cs:415 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyVoxelMap.GetVoxelCoordinateFromMeters	local	-	-	-	0	MyVoxelMap.cs:421 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyVoxelMap.Init	local	-	-	-	0	MyVoxelMap.cs:427 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.IMyVoxelMaps.Clear	per-node	B6	no	role (act on WA only)	1	MyVoxelMaps.cs:243 acts on this node process only (save, unload, quit)
VRage.Game.ModAPI.IMyVoxelMaps.CreatePredefinedVoxelMap	affinity	B8	no	to-owner / request	1	MyVoxelMaps.cs:305 creates the entity on this node whatever the position; a spawn near another node's partition merges over there, and the WA must never spawn (cluster: SpawnDirectorService.cs:15)
VRage.Game.ModAPI.IMyVoxelMaps.CreateProceduralVoxelMap	affinity	B8	no	to-owner / request	2	MyVoxelMaps.cs:319 creates the entity on this node whatever the position; a spawn near another node's partition merges over there, and the WA must never spawn (cluster: SpawnDirectorService.cs:15)
VRage.Game.ModAPI.IMyVoxelMaps.CreateStorage	local	-	-	-	2	MyVoxelMaps.cs:280 value, parser or helper; no world state
VRage.Game.ModAPI.IMyVoxelMaps.CreateVoxelMap	affinity	B8	no	to-owner / request	2	MyVoxelMaps.cs:285 creates the entity on this node whatever the position; a spawn near another node's partition merges over there, and the WA must never spawn (cluster: SpawnDirectorService.cs:15)
VRage.Game.ModAPI.IMyVoxelMaps.CreateVoxelMapFromStorageName	affinity	B8	no	to-owner / request	1	MyVoxelMaps.cs:294 creates the entity on this node whatever the position; a spawn near another node's partition merges over there, and the WA must never spawn (cluster: SpawnDirectorService.cs:15)
VRage.Game.ModAPI.IMyVoxelMaps.CutOutShape	per-node	B7	yes	broadcast	2	MyVoxelMaps.cs:373 edits this node's voxel storage; the cluster voxel journal hooks only RequestVoxelCutoutSphere (cluster: Patches/MyVoxelBasePatch.cs:18), so other nodes keep the old shape
VRage.Game.ModAPI.IMyVoxelMaps.Exist	node-scoped	B1	no	broadcast / request	0	MyVoxelMaps.cs:248 enumerates or queries this node's entities only (MyEntities); entities on other nodes and in offline partitions are missing
VRage.Game.ModAPI.IMyVoxelMaps.FillInShape	per-node	B7	yes	broadcast	0	MyVoxelMaps.cs:378 edits this node's voxel storage; the cluster voxel journal hooks only RequestVoxelCutoutSphere (cluster: Patches/MyVoxelBasePatch.cs:18), so other nodes keep the old shape
VRage.Game.ModAPI.IMyVoxelMaps.GetBoxVoxelHand	local	-	-	-	1	MyVoxelMaps.cs:348 value, parser or helper; no world state
VRage.Game.ModAPI.IMyVoxelMaps.GetCapsuleVoxelHand	local	-	-	-	0	MyVoxelMaps.cs:353 value, parser or helper; no world state
VRage.Game.ModAPI.IMyVoxelMaps.GetInstances	node-scoped	B1	no	broadcast / request	7	MyVoxelMaps.cs:269 enumerates or queries this node's entities only (MyEntities); entities on other nodes and in offline partitions are missing
VRage.Game.ModAPI.IMyVoxelMaps.GetOverlappingWithSphere	node-scoped	B1	no	broadcast / request	2	MyVoxelMaps.cs:253 enumerates or queries this node's entities only (MyEntities); entities on other nodes and in offline partitions are missing
VRage.Game.ModAPI.IMyVoxelMaps.GetRampVoxelHand	local	-	-	-	0	MyVoxelMaps.cs:363 value, parser or helper; no world state
VRage.Game.ModAPI.IMyVoxelMaps.GetSphereVoxelHand	local	-	-	-	1	MyVoxelMaps.cs:358 value, parser or helper; no world state
VRage.Game.ModAPI.IMyVoxelMaps.GetVoxelMapWhoseBoundingBoxIntersectsBox	node-scoped	B1	no	broadcast / request	2	MyVoxelMaps.cs:264 enumerates or queries this node's entities only (MyEntities); entities on other nodes and in offline partitions are missing
VRage.Game.ModAPI.IMyVoxelMaps.MakeCrater	per-node	B7	yes	broadcast	0	MyVoxelMaps.cs:388 edits this node's voxel storage; the cluster voxel journal hooks only RequestVoxelCutoutSphere (cluster: Patches/MyVoxelBasePatch.cs:18), so other nodes keep the old shape
VRage.Game.ModAPI.IMyVoxelMaps.PaintInShape	per-node	B7	yes	broadcast	0	MyVoxelMaps.cs:368 edits this node's voxel storage; the cluster voxel journal hooks only RequestVoxelCutoutSphere (cluster: Patches/MyVoxelBasePatch.cs:18), so other nodes keep the old shape
VRage.Game.ModAPI.IMyVoxelMaps.RevertShape	per-node	B7	yes	broadcast	0	MyVoxelMaps.cs:383 edits this node's voxel storage; the cluster voxel journal hooks only RequestVoxelCutoutSphere (cluster: Patches/MyVoxelBasePatch.cs:18), so other nodes keep the old shape
VRage.Game.ModAPI.IMyVoxelMaps.SpawnPlanet	affinity	B8	no	to-owner / request	6	MyVoxelMaps.cs:394 creates the entity on this node whatever the position; a spawn near another node's partition merges over there, and the WA must never spawn (cluster: SpawnDirectorService.cs:15)
VRage.Game.ModAPI.IMyVoxelMaps.VoxelMaterialCount	local	-	-	-	0	MyVoxelMaps.cs:36 value, parser or helper; no world state
VRage.Game.ModAPI.IMyVoxelShape.GetIntersectionVolume	local	-	-	-	0	MyShape.cs:96 value, parser or helper; no world state
VRage.Game.ModAPI.IMyVoxelShape.GetWorldBoundary	local	-	-	-	0	MyShape.cs:86 value, parser or helper; no world state
VRage.Game.ModAPI.IMyVoxelShape.PeekWorldBoundary	local	-	-	-	0	MyShape.cs:91 value, parser or helper; no world state
VRage.Game.ModAPI.IMyVoxelShape.Transform	local	-	-	-	1	MyShape.cs:41 value, parser or helper; no world state
VRage.Game.ModAPI.IMyVoxelShapeBox.Boundaries	local	-	-	-	1	MyShapeBox.cs:11 value, parser or helper; no world state
VRage.Game.ModAPI.IMyVoxelShapeCapsule.A	local	-	-	-	0	MyShapeCapsule.cs:15 value, parser or helper; no world state
VRage.Game.ModAPI.IMyVoxelShapeCapsule.B	local	-	-	-	0	MyShapeCapsule.cs:27 value, parser or helper; no world state
VRage.Game.ModAPI.IMyVoxelShapeCapsule.Radius	local	-	-	-	0	MyShapeCapsule.cs:39 value, parser or helper; no world state
VRage.Game.ModAPI.IMyVoxelShapeRamp.Boundaries	local	-	-	-	0	MyShapeRamp.cs:16 value, parser or helper; no world state
VRage.Game.ModAPI.IMyVoxelShapeRamp.RampNormal	local	-	-	-	0	MyShapeRamp.cs:28 value, parser or helper; no world state
VRage.Game.ModAPI.IMyVoxelShapeRamp.RampNormalW	local	-	-	-	0	MyShapeRamp.cs:40 value, parser or helper; no world state
VRage.Game.ModAPI.IMyVoxelShapeSphere.Center	local	-	-	-	1	MyShapeSphere.cs:13 value, parser or helper; no world state
VRage.Game.ModAPI.IMyVoxelShapeSphere.Radius	local	-	-	-	1	MyShapeSphere.cs:25 value, parser or helper; no world state
VRage.Game.ModAPI.IMyWeatherEffects.CreateLightning	local	-	-	-	1	MySectorWeatherComponent.cs:754 lightning runs on the node that hosts the target (cluster: WeatherAuthorityService.cs:7)
VRage.Game.ModAPI.IMyWeatherEffects.FogAtmoOverride	client	-	-	-	3	MySectorWeatherComponent.cs:252 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyWeatherEffects.FogColorOverride	client	-	-	-	4	MySectorWeatherComponent.cs:248 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyWeatherEffects.FogDensityOverride	client	-	-	-	3	MySectorWeatherComponent.cs:246 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyWeatherEffects.FogMultiplierOverride	client	-	-	-	4	MySectorWeatherComponent.cs:244 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyWeatherEffects.FogSkyboxOverride	client	-	-	-	4	MySectorWeatherComponent.cs:250 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyWeatherEffects.GetOxygenMultiplier	replicated	-	-	-	0	MySectorWeatherComponent.cs:1061 weather is decided on the WA; a node's change is forwarded and published (cluster: WeatherAuthorityService.cs:24)
VRage.Game.ModAPI.IMyWeatherEffects.GetSolarMultiplier	replicated	-	-	-	2	MySectorWeatherComponent.cs:1077 weather is decided on the WA; a node's change is forwarded and published (cluster: WeatherAuthorityService.cs:24)
VRage.Game.ModAPI.IMyWeatherEffects.GetTemperatureMultiplier	replicated	-	-	-	0	MySectorWeatherComponent.cs:1087 weather is decided on the WA; a node's change is forwarded and published (cluster: WeatherAuthorityService.cs:24)
VRage.Game.ModAPI.IMyWeatherEffects.GetWeather	replicated	-	-	-	10	MySectorWeatherComponent.cs:778 weather is decided on the WA; a node's change is forwarded and published (cluster: WeatherAuthorityService.cs:24)
VRage.Game.ModAPI.IMyWeatherEffects.GetWeatherIntensity	replicated	-	-	-	8	MySectorWeatherComponent.cs:1035 weather is decided on the WA; a node's change is forwarded and published (cluster: WeatherAuthorityService.cs:24)
VRage.Game.ModAPI.IMyWeatherEffects.GetWeatherPlanetData	replicated	-	-	-	2	MySectorWeatherComponent.cs:824 weather is decided on the WA; a node's change is forwarded and published (cluster: WeatherAuthorityService.cs:24)
VRage.Game.ModAPI.IMyWeatherEffects.GetWindMultiplier	replicated	-	-	-	3	MySectorWeatherComponent.cs:1097 weather is decided on the WA; a node's change is forwarded and published (cluster: WeatherAuthorityService.cs:24)
VRage.Game.ModAPI.IMyWeatherEffects.ParticleDirectionOverride	client	-	-	-	2	MySectorWeatherComponent.cs:254 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyWeatherEffects.ParticleVelocityOverride	client	-	-	-	1	MySectorWeatherComponent.cs:256 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.IMyWeatherEffects.RemoveWeather	replicated	-	-	-	0	MySectorWeatherComponent.cs:978 weather is decided on the WA; a node's change is forwarded and published (cluster: WeatherAuthorityService.cs:24)
VRage.Game.ModAPI.IMyWeatherEffects.SetWeather	replicated	-	-	-	0	MySectorWeatherComponent.cs:875 weather is decided on the WA; a node's change is forwarded and published (cluster: WeatherAuthorityService.cs:24)
VRage.Game.ModAPI.IMyWeatherEffects.SunIntensityOverride	client	-	-	-	3	MySectorWeatherComponent.cs:258 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.Ingame.FleeMode.*	local	-	-	-	0	value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.FleeTrigger.*	local	-	-	-	0	value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.GuidanceType.*	local	-	-	-	0	value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.IMyCubeBlock.BlockDefinition	local	-	-	-	94	MyCubeBlock.cs:371 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyCubeBlock.CubeGrid	local	-	-	-	13	MyCubeBlock.cs:373 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyCubeBlock.DefinitionDisplayNameText	local	-	-	-	10	MyCubeBlock.cs:343 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyCubeBlock.DisassembleRatio	local	-	-	-	5	MyAdvancedDoor.cs:99 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyCubeBlock.DisplayNameText	local	-	-	-	13	MyTerminalBlock.cs:276 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyCubeBlock.GetOwnerFactionTag	local	-	-	-	4	MyCubeBlock.cs:443 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyCubeBlock.GetPlayerRelationToOwner	local	-	-	-	0	MyCubeBlock.cs:2022 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyCubeBlock.GetUserRelationToOwner	local	-	-	-	13	MyCubeBlock.cs:2027 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyCubeBlock.IsBeingHacked	local	-	-	-	0	MyTerminalBlock.cs:370 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyCubeBlock.IsFunctional	local	-	-	-	68	MyCubeBlock.cs:321 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyCubeBlock.IsWorking	local	-	-	-	89	MyCubeBlock.cs:327 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyCubeBlock.Mass	local	-	-	-	1	MyCubeBlock.cs:389 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyCubeBlock.Max	local	-	-	-	9	MyCubeBlock.cs:296 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyCubeBlock.Min	local	-	-	-	13	MyCubeBlock.cs:294 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyCubeBlock.NumberInGrid	local	-	-	-	0	MyCubeBlock.cs:319 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyCubeBlock.Orientation	local	-	-	-	23	MyCubeBlock.cs:298 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyCubeBlock.OwnerId	local	-	-	-	39	MyCubeBlock.cs:266 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyCubeBlock.Position	local	-	-	-	30	MyCockpit.cs:78 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyCubeBlock.UpdateIsWorking	local	-	-	-	2	MyCubeBlock.cs:2087 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyCubeBlock.UpdateVisual	local	-	-	-	2	MyCubeBlock.cs:2092 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyCubeGrid.CubeExists	local	-	-	-	9	MyCubeGrid.cs:8831 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyCubeGrid.CustomName	local	-	-	-	1	MyCubeGrid.cs:3704 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyCubeGrid.GetCubeBlock	local	-	-	-	0	MyCubeGrid.cs:16302 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyCubeGrid.GridIntegerToWorld	local	-	-	-	30	MyCubeGrid.cs:6120 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyCubeGrid.GridSize	local	-	-	-	41	MyCubeGrid.cs:3089 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyCubeGrid.GridSizeEnum	local	-	-	-	58	MyCubeGrid.cs:3278 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyCubeGrid.IsSameConstructAs	local	-	-	-	2	MyCubeGrid.cs:16307 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyCubeGrid.IsStatic	local	-	-	-	0	MyCubeGrid.cs:3747 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyCubeGrid.LinearVelocity	local	-	-	-	5	MyCubeGrid.cs:3101 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyCubeGrid.Max	local	-	-	-	20	MyCubeGrid.cs:143 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyCubeGrid.Min	local	-	-	-	20	MyCubeGrid.cs:141 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyCubeGrid.Speed	local	-	-	-	2	MyCubeGrid.cs:3117 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyCubeGrid.WorldToGridInteger	local	-	-	-	23	MyCubeGrid.cs:16418 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyEntity.Closed	local	-	-	-	66	MyEntity.cs:899 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyEntity.Components	local	-	-	-	64	MyEntity.cs:321 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyEntity.DisplayName	local	-	-	-	2	MyEntity.cs:883 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyEntity.EntityId	local	-	-	-	9	MyCubeGrid.cs:474 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyEntity.GetInventory	local	-	-	-	2	MyEntity.cs:2574 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyEntity.GetPosition	local	-	-	-	80	MyEntity.cs:2717 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyEntity.HasInventory	local	-	-	-	21	MyEntity.cs:1218 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyEntity.InventoryCount	local	-	-	-	16	MyEntity.cs:1221 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyEntity.Name	local	-	-	-	0	MyEntity.cs:867 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyEntity.WorldAABB	local	-	-	-	43	MyEntity.cs:1230 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyEntity.WorldAABBHr	local	-	-	-	2	MyEntity.cs:1233 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyEntity.WorldMatrix	local	-	-	-	5	MyEntity.cs:1196 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyEntity.WorldVolume	local	-	-	-	25	MyEntity.cs:1239 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyEntity.WorldVolumeHr	local	-	-	-	0	MyEntity.cs:1242 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyInventory.CanItemsBeAdded	local	-	-	-	16	MyInventory.cs:2719 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyInventory.CanPutItems	local	-	-	-	0	MyInventory.cs:508 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyInventory.CanTransferItemTo	local	-	-	-	5	MyInventory.cs:2774 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyInventory.ContainItems	local	-	-	-	5	MyInventory.cs:2695 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyInventory.CurrentMass	local	-	-	-	3	MyInventory.cs:428 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyInventory.CurrentVolume	local	-	-	-	13	MyInventory.cs:414 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyInventory.FindItem	local	-	-	-	0	MyInventory.cs:2714 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyInventory.GetAcceptedItems	local	-	-	-	2	MyInventory.cs:2783 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyInventory.GetItemAmount	local	-	-	-	10	MyInventory.cs:2690 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyInventory.GetItemAt	local	-	-	-	4	MyInventory.cs:2700 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyInventory.GetItemByID	local	-	-	-	0	MyInventory.cs:2709 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyInventory.GetItems	local	-	-	-	20	MyInventory.cs:2724 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyInventory.IsConnectedTo	local	-	-	-	7	MyInventory.cs:2765 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyInventory.IsFull	local	-	-	-	2	MyInventory.cs:472 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyInventory.IsItemAt	local	-	-	-	0	MyInventory.cs:1628 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyInventory.ItemCount	local	-	-	-	6	MyInventory.cs:506 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyInventory.MaxVolume	local	-	-	-	13	MyInventory.cs:347 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyInventory.Owner	local	-	-	-	0	MyInventory.cs:504 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyInventory.TransferItemFrom	local	-	-	-	0	MyInventory.cs:2746 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyInventory.TransferItemTo	local	-	-	-	0	MyInventory.cs:2736 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyInventory.VolumeFillFactor	local	-	-	-	1	MyInventory.cs:416 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyInventoryItem.Amount	local	-	-	-	12	MyPhysicalInventoryItem.cs:149 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.IMyInventoryItem.Content	local	-	-	-	16	MyPhysicalInventoryItem.cs:179 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.IMyInventoryItem.ItemId	local	-	-	-	2	MyPhysicalInventoryItem.cs:194 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.IMyInventoryItem.Scale	local	-	-	-	0	MyPhysicalInventoryItem.cs:164 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.IMyInventoryOwner.EntityId	local	-	-	-	0	MyCollector.cs:144 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyInventoryOwner.GetInventory	local	-	-	-	2	MyCollector.cs:208 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyInventoryOwner.HasInventory	local	-	-	-	0	MyCollector.cs:146 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyInventoryOwner.InventoryCount	local	-	-	-	2	MyCollector.cs:142 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMyInventoryOwner.UseConveyorSystem	local	-	-	-	1	MyCollector.cs:148 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMySlimBlock.AccumulatedDamage	local	-	-	-	3	MySlimBlock.cs:1049 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMySlimBlock.BlockDefinition	local	-	-	-	2	MySlimBlock.cs:1089 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMySlimBlock.BuildIntegrity	local	-	-	-	8	MySlimBlock.cs:1051 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMySlimBlock.BuildLevelRatio	local	-	-	-	19	MySlimBlock.cs:1053 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMySlimBlock.ColorMaskHSV	local	-	-	-	22	MySlimBlock.cs:1083 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMySlimBlock.CubeGrid	local	-	-	-	2	MySlimBlock.cs:1081 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMySlimBlock.CurrentDamage	local	-	-	-	10	MySlimBlock.cs:1055 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMySlimBlock.DamageRatio	local	-	-	-	3	MySlimBlock.cs:1057 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMySlimBlock.FatBlock	local	-	-	-	1	MySlimBlock.cs:1045 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMySlimBlock.GetMissingComponents	local	-	-	-	13	MySlimBlock.cs:3338 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMySlimBlock.HasDeformation	local	-	-	-	12	MySlimBlock.cs:1059 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMySlimBlock.IsDestroyed	local	-	-	-	24	MySlimBlock.cs:1061 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMySlimBlock.IsFullIntegrity	local	-	-	-	14	MySlimBlock.cs:1063 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMySlimBlock.IsFullyDismounted	local	-	-	-	9	MySlimBlock.cs:1065 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMySlimBlock.Mass	local	-	-	-	1	MySlimBlock.cs:1071 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMySlimBlock.MaxDeformation	local	-	-	-	3	MySlimBlock.cs:1067 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMySlimBlock.MaxIntegrity	local	-	-	-	28	MySlimBlock.cs:1069 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMySlimBlock.OwnerId	local	-	-	-	5	MySlimBlock.cs:1112 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMySlimBlock.Position	local	-	-	-	46	MySlimBlock.cs:1079 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMySlimBlock.ShowParts	local	-	-	-	0	MySlimBlock.cs:1073 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMySlimBlock.SkinSubtypeId	local	-	-	-	13	MySlimBlock.cs:1085 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMySlimBlock.StockpileAllocated	local	-	-	-	2	MySlimBlock.cs:1075 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.IMySlimBlock.StockpileEmpty	local	-	-	-	2	MySlimBlock.cs:1077 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Ingame.MyGridTargetingRelationFiltering.*	local	-	-	-	0	value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.MyInventoryItem.Amount	local	-	-	-	16	MyInventoryItem.cs:20 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.MyInventoryItem.CompareTo	local	-	-	-	0	MyInventoryItem.cs:68 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.MyInventoryItem.Equals	local	-	-	-	0	MyInventoryItem.cs:48 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.MyInventoryItem.GetHashCode	local	-	-	-	0	MyInventoryItem.cs:62 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.MyInventoryItem.ItemId	local	-	-	-	9	MyInventoryItem.cs:14 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.MyInventoryItem.ToString	local	-	-	-	1	MyInventoryItem.cs:84 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.MyInventoryItem.Type	local	-	-	-	23	MyInventoryItem.cs:25 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.MyInventoryItemExtension.GetDefinitionId	local	-	-	-	1	MyInventoryItemExtension.cs:5 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.MyItemInfo.IsAmmo	local	-	-	-	0	MyItemInfo.cs:58 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.MyItemInfo.IsComponent	local	-	-	-	1	MyItemInfo.cs:48 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.MyItemInfo.IsIngot	local	-	-	-	1	MyItemInfo.cs:43 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.MyItemInfo.IsOre	local	-	-	-	1	MyItemInfo.cs:38 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.MyItemInfo.IsTool	local	-	-	-	1	MyItemInfo.cs:53 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.MyItemInfo.Mass	local	-	-	-	0	MyItemInfo.cs:13 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.MyItemInfo.MaxStackAmount	local	-	-	-	0	MyItemInfo.cs:28 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.MyItemInfo.Size	local	-	-	-	0	MyItemInfo.cs:18 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.MyItemInfo.UsesFractions	local	-	-	-	0	MyItemInfo.cs:33 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.MyItemInfo.Volume	local	-	-	-	1	MyItemInfo.cs:23 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.MyItemType.CompareTo	local	-	-	-	0	MyItemType.cs:91 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.MyItemType.Equals	local	-	-	-	2	MyItemType.cs:72 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.MyItemType.GetHashCode	local	-	-	-	0	MyItemType.cs:86 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.MyItemType.MakeAmmo	local	-	-	-	2	MyItemType.cs:126 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.MyItemType.MakeComponent	local	-	-	-	1	MyItemType.cs:121 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.MyItemType.MakeIngot	local	-	-	-	0	MyItemType.cs:116 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.MyItemType.MakeOre	local	-	-	-	0	MyItemType.cs:111 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.MyItemType.MakeTool	local	-	-	-	2	MyItemType.cs:131 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.MyItemType.Parse	local	-	-	-	2	MyItemType.cs:106 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.MyItemType.SubtypeId	local	-	-	-	16	MyItemType.cs:25 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.MyItemType.ToString	local	-	-	-	2	MyItemType.cs:101 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.MyItemType.TypeId	local	-	-	-	11	MyItemType.cs:20 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.MyPhysicalInventoryItemExtensions_ModAPI.GetItemInfo	local	-	-	-	2	MyPhysicalInventoryItemExtensions_ModAPI.cs:7 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.MyStoreItemDataSimple.Amount	local	-	-	-	0	MyStoreItemDataSimple.cs:15 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.MyStoreItemDataSimple.ItemId	local	-	-	-	0	MyStoreItemDataSimple.cs:10 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.MyStoreItemDataSimple.PricePerUnit	local	-	-	-	0	MyStoreItemDataSimple.cs:20 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.MyTransponderRelationFilter.*	local	-	-	-	0	value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.OffensiveCombatTargetPriority.*	local	-	-	-	0	value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyCommandLine.Argument	local	-	-	-	3	MyCommandLine.cs:262 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyCommandLine.ArgumentCount	local	-	-	-	3	MyCommandLine.cs:218 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyCommandLine.Clear	local	-	-	-	2	MyCommandLine.cs:311 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyCommandLine.Enumerator.Current	local	-	-	-	0	MyCommandLine.cs:129 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyCommandLine.Enumerator.Dispose	local	-	-	-	0	MyCommandLine.cs:138 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyCommandLine.Enumerator.MoveNext	local	-	-	-	0	MyCommandLine.cs:143 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyCommandLine.ItemCollection.Count	local	-	-	-	0	MyCommandLine.cs:26 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyCommandLine.ItemCollection.GetEnumerator	local	-	-	-	0	MyCommandLine.cs:54 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyCommandLine.ItemCollection.Item	local	-	-	-	0	MyCommandLine.cs:33 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyCommandLine.Items	local	-	-	-	0	MyCommandLine.cs:208 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyCommandLine.Switch	local	-	-	-	1	MyCommandLine.cs:276 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyCommandLine.SwitchCollection.Count	local	-	-	-	0	MyCommandLine.cs:81 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyCommandLine.SwitchCollection.GetEnumerator	local	-	-	-	0	MyCommandLine.cs:92 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyCommandLine.SwitchEnumerator.Current	local	-	-	-	0	MyCommandLine.cs:129 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyCommandLine.SwitchEnumerator.Dispose	local	-	-	-	0	MyCommandLine.cs:138 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyCommandLine.SwitchEnumerator.MoveNext	local	-	-	-	0	MyCommandLine.cs:143 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyCommandLine.Switches	local	-	-	-	0	MyCommandLine.cs:213 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyCommandLine.TryParse	local	-	-	-	3	MyCommandLine.cs:234 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIni.AddSection	local	-	-	-	4	MyIni.cs:990 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIni.Clear	local	-	-	-	9	MyIni.cs:1029 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIni.ContainsKey	local	-	-	-	0	MyIni.cs:321 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIni.ContainsSection	local	-	-	-	5	MyIni.cs:310 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIni.Delete	local	-	-	-	0	MyIni.cs:680 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIni.DeleteSection	local	-	-	-	4	MyIni.cs:1002 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIni.EndComment	local	-	-	-	2	MyIni.cs:221 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIni.EndContent	local	-	-	-	1	MyIni.cs:203 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIni.Get	local	-	-	-	21	MyIni.cs:558 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIni.GetComment	local	-	-	-	0	MyIni.cs:485 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIni.GetKeys	local	-	-	-	3	MyIni.cs:341 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIni.GetSectionComment	local	-	-	-	0	MyIni.cs:431 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIni.GetSections	local	-	-	-	4	MyIni.cs:386 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIni.HasSection	local	-	-	-	3	MyIni.cs:252 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIni.Invalidate	local	-	-	-	1	MyIni.cs:1159 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIni.Set	local	-	-	-	19	MyIni.cs:707 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIni.SetComment	local	-	-	-	8	MyIni.cs:526 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIni.SetEndComment	local	-	-	-	0	MyIni.cs:414 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIni.SetSectionComment	local	-	-	-	3	MyIni.cs:461 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIni.ToString	local	-	-	-	19	MyIni.cs:1400 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIni.TryParse	local	-	-	-	21	MyIni.cs:1062 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIniKey.EMPTY	local	-	-	-	0	MyIniKey.cs:19 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIniKey.Equals	local	-	-	-	0	MyIni.cs:116 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIniKey.GetHashCode	local	-	-	-	0	MyIni.cs:121 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIniKey.IsEmpty	local	-	-	-	1	MyIniKey.cs:38 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIniKey.Name	local	-	-	-	3	MyIniKey.cs:33 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIniKey.Parse	local	-	-	-	0	MyIniKey.cs:124 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIniKey.Section	local	-	-	-	1	MyIniKey.cs:28 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIniKey.ToString	local	-	-	-	0	MyIni.cs:1400 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIniKey.TryParse	local	-	-	-	0	MyIni.cs:1062 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIniParseResult.Equals	local	-	-	-	0	MyIniParseResult.cs:79 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIniParseResult.Error	local	-	-	-	6	MyIniParseResult.cs:15 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIniParseResult.GetHashCode	local	-	-	-	0	MyIniParseResult.cs:110 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIniParseResult.IsDefined	local	-	-	-	0	MyIniParseResult.cs:50 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIniParseResult.LineNo	local	-	-	-	2	MyIniParseResult.cs:20 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIniParseResult.Success	local	-	-	-	1	MyIniParseResult.cs:35 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIniParseResult.ToString	local	-	-	-	3	MyIniParseResult.cs:126 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIniValue.EMPTY	local	-	-	-	0	MyIniValue.cs:16 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIniValue.GetLines	local	-	-	-	1	MyIniValue.cs:447 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIniValue.IsEmpty	local	-	-	-	0	MyIniValue.cs:30 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIniValue.Key	local	-	-	-	0	MyIniValue.cs:25 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIniValue.ToBoolean	local	-	-	-	9	MyIniValue.cs:53 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIniValue.ToByte	local	-	-	-	2	MyIniValue.cs:158 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIniValue.ToChar	local	-	-	-	0	MyIniValue.cs:94 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIniValue.ToDecimal	local	-	-	-	0	MyIniValue.cs:419 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIniValue.ToDouble	local	-	-	-	7	MyIniValue.cs:390 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIniValue.ToInt16	local	-	-	-	0	MyIniValue.cs:216 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIniValue.ToInt32	local	-	-	-	8	MyIniValue.cs:274 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIniValue.ToInt64	local	-	-	-	0	MyIniValue.cs:332 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIniValue.ToSByte	local	-	-	-	0	MyIniValue.cs:129 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIniValue.ToSingle	local	-	-	-	5	MyIniValue.cs:361 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIniValue.ToString	local	-	-	-	12	MyIniValue.cs:480 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIniValue.ToUInt16	local	-	-	-	0	MyIniValue.cs:187 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIniValue.ToUInt32	local	-	-	-	1	MyIniValue.cs:245 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIniValue.ToUInt64	local	-	-	-	0	MyIniValue.cs:303 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIniValue.TryGetBoolean	local	-	-	-	2	MyIniValue.cs:67 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIniValue.TryGetByte	local	-	-	-	0	MyIniValue.cs:172 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIniValue.TryGetChar	local	-	-	-	0	MyIniValue.cs:108 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIniValue.TryGetDecimal	local	-	-	-	0	MyIniValue.cs:433 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIniValue.TryGetDouble	local	-	-	-	0	MyIniValue.cs:404 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIniValue.TryGetInt16	local	-	-	-	0	MyIniValue.cs:230 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIniValue.TryGetInt32	local	-	-	-	0	MyIniValue.cs:288 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIniValue.TryGetInt64	local	-	-	-	0	MyIniValue.cs:346 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIniValue.TryGetSByte	local	-	-	-	0	MyIniValue.cs:143 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIniValue.TryGetSingle	local	-	-	-	2	MyIniValue.cs:375 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIniValue.TryGetString	local	-	-	-	2	MyIniValue.cs:500 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIniValue.TryGetUInt16	local	-	-	-	0	MyIniValue.cs:201 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIniValue.TryGetUInt32	local	-	-	-	1	MyIniValue.cs:259 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIniValue.TryGetUInt64	local	-	-	-	0	MyIniValue.cs:317 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.MyIniValue.Write	local	-	-	-	0	MyIniValue.cs:510 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.StringSegment.Equals	local	-	-	-	0	StringSegmentComparer.cs:20 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.StringSegment.EqualsIgnoreCase	local	-	-	-	1	StringSegment.cs:201 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.StringSegment.GetHashCode	local	-	-	-	0	StringSegmentComparer.cs:46 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.StringSegment.GetLines	local	-	-	-	2	StringSegment.cs:254 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.StringSegment.IndexOf	local	-	-	-	0	StringSegment.cs:87 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.StringSegment.IndexOfAny	local	-	-	-	0	StringSegment.cs:126 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.StringSegment.IsCached	local	-	-	-	0	StringSegment.cs:39 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.StringSegment.IsEmpty	local	-	-	-	0	StringSegment.cs:34 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.StringSegment.Item	local	-	-	-	0	StringSegment.cs:46 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.StringSegment.Length	local	-	-	-	0	StringSegment.cs:27 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.StringSegment.Start	local	-	-	-	1	StringSegment.cs:22 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.StringSegment.Text	local	-	-	-	1	StringSegment.cs:17 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.StringSegment.ToString	local	-	-	-	1	StringSegment.cs:237 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.StringSegmentComparer.DEFAULT	local	-	-	-	0	StringSegmentComparer.cs:14 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.StringSegmentComparer.Equals	local	-	-	-	0	StringSegmentComparer.cs:20 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.StringSegmentComparer.GetHashCode	local	-	-	-	0	StringSegmentComparer.cs:46 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.StringSegmentIgnoreCaseComparer.DEFAULT	local	-	-	-	0	StringSegmentIgnoreCaseComparer.cs:14 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.StringSegmentIgnoreCaseComparer.Equals	local	-	-	-	0	StringSegmentIgnoreCaseComparer.cs:20 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.StringSegmentIgnoreCaseComparer.GetHashCode	local	-	-	-	0	StringSegmentIgnoreCaseComparer.cs:46 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.TextPtr.Char	local	-	-	-	2	TextPtr.cs:26 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.TextPtr.Content	local	-	-	-	1	TextPtr.cs:16 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.TextPtr.Find	local	-	-	-	2	TextPtr.cs:161 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.TextPtr.FindAny	local	-	-	-	0	TextPtr.cs:199 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.TextPtr.FindAnyInLine	local	-	-	-	0	TextPtr.cs:246 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.TextPtr.FindEndOfLine	local	-	-	-	1	TextPtr.cs:274 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.TextPtr.FindInLine	local	-	-	-	0	TextPtr.cs:218 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.TextPtr.FindLineNo	local	-	-	-	0	TextPtr.cs:141 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.TextPtr.Index	local	-	-	-	1	TextPtr.cs:21 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.TextPtr.IsEmpty	local	-	-	-	0	TextPtr.cs:41 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.TextPtr.IsEndOfLine	local	-	-	-	2	TextPtr.cs:377 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.TextPtr.IsNewLine	local	-	-	-	0	TextPtr.cs:403 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.TextPtr.IsOutOfBounds	local	-	-	-	2	TextPtr.cs:128 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.TextPtr.IsStartOfLine	local	-	-	-	0	TextPtr.cs:390 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.TextPtr.SkipWhitespace	local	-	-	-	3	TextPtr.cs:344 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.TextPtr.StartsWith	local	-	-	-	2	TextPtr.cs:325 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.TextPtr.StartsWithCaseInsensitive	local	-	-	-	2	TextPtr.cs:306 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.TextPtr.TrimEnd	local	-	-	-	0	TextPtr.cs:443 value, parser or helper; no world state
VRage.Game.ModAPI.Ingame.Utilities.TextPtr.TrimStart	local	-	-	-	0	TextPtr.cs:424 value, parser or helper; no world state
VRage.Game.ModAPI.Interfaces.IMyCameraController.AllowCubeBuilding	client	-	-	-	0	MySpectatorCameraController.cs:122 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.Interfaces.IMyCameraController.ControlCamera	client	-	-	-	0	MySpectatorCameraController.cs:467 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.Interfaces.IMyCameraController.EnableFirstPersonView	client	-	-	-	1	MySpectatorCameraController.cs:111 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.Interfaces.IMyCameraController.Entity	client	-	-	-	7	MyCutsceneCamera.cs:38 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.Interfaces.IMyCameraController.ForceFirstPersonCamera	client	-	-	-	1	MySpectatorCameraController.cs:99 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.Interfaces.IMyCameraController.GetOverridingFocusMatrix	client	-	-	-	0	MyCutsceneCamera.cs:112 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.Interfaces.IMyCameraController.HandlePickUp	client	-	-	-	0	MySpectatorCameraController.cs:572 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.Interfaces.IMyCameraController.HandleUse	client	-	-	-	0	MySpectatorCameraController.cs:567 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.Interfaces.IMyCameraController.IsInFirstPersonView	client	-	-	-	10	MySpectatorCameraController.cs:87 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.Interfaces.IMyCameraController.OnAssumeControl	client	-	-	-	0	MySpectatorCameraController.cs:557 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.Interfaces.IMyCameraController.OnReleaseControl	client	-	-	-	0	MySpectatorCameraController.cs:562 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.Interfaces.IMyCameraController.Rotate	client	-	-	-	1	MySpectatorCameraController.cs:538 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.Interfaces.IMyCameraController.RotateStopped	client	-	-	-	0	MySpectatorCameraController.cs:543 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.Interfaces.IMyControllableEntity.CanSwitchLandingGears	local	-	-	-	0	MyEntityRespawnComponentBase.cs:132 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Interfaces.IMyControllableEntity.ControllerInfo	local	-	-	-	17	MyCharacter.cs:2728 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Interfaces.IMyControllableEntity.Crouch	local	-	-	-	3	MyCharacter.cs:6832 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Interfaces.IMyControllableEntity.Die	local	-	-	-	1	MyCharacter.cs:6902 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Interfaces.IMyControllableEntity.Down	local	-	-	-	0	MyCharacter.cs:6837 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Interfaces.IMyControllableEntity.DrawHud	client	-	-	-	0	MyCharacter.cs:12269 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.Interfaces.IMyControllableEntity.EnabledDamping	local	-	-	-	4	MyCharacter.cs:2406 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Interfaces.IMyControllableEntity.EnabledHelmet	local	-	-	-	6	MyCharacter.cs:2426 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Interfaces.IMyControllableEntity.EnabledLeadingGears	local	-	-	-	0	MyCharacter.cs:2420 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Interfaces.IMyControllableEntity.EnabledLights	local	-	-	-	1	MyCharacter.cs:2418 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Interfaces.IMyControllableEntity.EnabledReactors	local	-	-	-	0	MyCharacter.cs:2422 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Interfaces.IMyControllableEntity.EnabledThrusts	local	-	-	-	10	MyCharacter.cs:2394 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Interfaces.IMyControllableEntity.Entity	local	-	-	-	41	MyCharacter.cs:2722 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Interfaces.IMyControllableEntity.ForceFirstPersonCamera	local	-	-	-	1	MyCharacter.cs:1456 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Interfaces.IMyControllableEntity.GetHeadMatrix	local	-	-	-	21	MyCharacter.cs:6768 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Interfaces.IMyControllableEntity.IsAutopilotControlled	local	-	-	-	6	MyCharacter.cs:2203 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Interfaces.IMyControllableEntity.Jump	local	-	-	-	2	MyCharacter.cs:6813 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Interfaces.IMyControllableEntity.LastMotionIndicator	local	-	-	-	5	MyCharacter.cs:2724 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Interfaces.IMyControllableEntity.LastRotationIndicator	local	-	-	-	2	MyCharacter.cs:2726 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Interfaces.IMyControllableEntity.MoveAndRotate	local	-	-	-	8	MyCharacter.cs:6773 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Interfaces.IMyControllableEntity.MoveAndRotateStopped	local	-	-	-	0	MyCharacter.cs:6778 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Interfaces.IMyControllableEntity.PickUp	local	-	-	-	0	MyCharacter.cs:6798 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Interfaces.IMyControllableEntity.PickUpContinues	local	-	-	-	0	MyCharacter.cs:6803 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Interfaces.IMyControllableEntity.PrimaryLookaround	local	-	-	-	0	MyEntityRespawnComponentBase.cs:146 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Interfaces.IMyControllableEntity.RelativeDampeningDistance	local	-	-	-	0	MyCharacter.cs:2730 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Interfaces.IMyControllableEntity.RelativeDampeningTarget	local	-	-	-	1	MyCharacter.cs:2746 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Interfaces.IMyControllableEntity.ShowInventory	client	-	-	-	0	MyCharacter.cs:6842 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.Interfaces.IMyControllableEntity.ShowRemoteAccessMenu	client	-	-	-	0	MyShipController.cs:1187 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.Interfaces.IMyControllableEntity.ShowTerminal	local	-	-	-	0	MyCharacter.cs:6847 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Interfaces.IMyControllableEntity.SwitchDamping	local	-	-	-	2	MyCharacter.cs:6861 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Interfaces.IMyControllableEntity.SwitchHandbrake	local	-	-	-	0	MyCharacter.cs:6884 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Interfaces.IMyControllableEntity.SwitchHelmet	local	-	-	-	1	MyCharacter.cs:6894 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Interfaces.IMyControllableEntity.SwitchLandingGears	local	-	-	-	0	MyCharacter.cs:6879 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Interfaces.IMyControllableEntity.SwitchLights	local	-	-	-	1	MyCharacter.cs:6874 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Interfaces.IMyControllableEntity.SwitchReactors	local	-	-	-	0	MyCharacter.cs:6889 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Interfaces.IMyControllableEntity.SwitchReactorsLocal	local	-	-	-	0	MyEntityRespawnComponentBase.cs:388 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Interfaces.IMyControllableEntity.SwitchThrusts	local	-	-	-	2	MyCharacter.cs:6852 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Interfaces.IMyControllableEntity.SwitchWalk	local	-	-	-	2	MyEntityRespawnComponentBase.cs:336 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Interfaces.IMyControllableEntity.Up	local	-	-	-	0	MyCharacter.cs:6827 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Interfaces.IMyControllableEntity.Use	local	-	-	-	1	MyCharacter.cs:6783 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Interfaces.IMyControllableEntity.UseContinues	local	-	-	-	0	MyCharacter.cs:6788 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Interfaces.IMyDecalHandler.AddDecal	client	-	-	-	0	MyDecals.cs:130 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.Interfaces.IMyDecalProxy.AddDecals	client	-	-	-	0	MyCharacter.cs:5721 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.Interfaces.IMyDestroyableObject.DoDamage	local	-	-	-	34	MyCharacter.cs:6912 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Interfaces.IMyDestroyableObject.Integrity	local	-	-	-	31	MyCharacter.cs:2428 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Interfaces.IMyDestroyableObject.OnDestroy	local	-	-	-	0	MyCharacter.cs:6907 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Interfaces.IMyDestroyableObject.UseDamageSystem	local	-	-	-	4	MyWarhead.cs:181 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Interfaces.IMyEntityCapacitorComponent.Capacity	local	-	-	-	0	MyEntityCapacitorComponent.cs:82 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Interfaces.IMyEntityCapacitorComponent.IsCharged	local	-	-	-	0	MyEntityCapacitorComponent.cs:113 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Interfaces.IMyEntityCapacitorComponent.OnCharged	local	-	-	-	0	MyEntityCapacitorComponent.cs:129 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Interfaces.IMyEntityCapacitorComponent.OnNotCharged	local	-	-	-	0	MyEntityCapacitorComponent.cs:131 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Interfaces.IMyEntityCapacitorComponent.RechargeDraw	local	-	-	-	0	MyEntityCapacitorComponent.cs:84 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Interfaces.IMyEntityCapacitorComponent.StoredPower	local	-	-	-	0	MyEntityCapacitorComponent.cs:115 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Interfaces.IMyEntityCapacitorComponent.TimeRemaining	local	-	-	-	0	MyEntityCapacitorComponent.cs:88 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.Game.ModAPI.Interfaces.MoveInformation.MoveIndicator	local	-	-	-	0	MoveInformation.cs:13 value, parser or helper; no world state
VRage.Game.ModAPI.Interfaces.MoveInformation.RollIndicator	local	-	-	-	0	MoveInformation.cs:23 value, parser or helper; no world state
VRage.Game.ModAPI.Interfaces.MoveInformation.RotationIndicator	local	-	-	-	0	MoveInformation.cs:18 value, parser or helper; no world state
VRage.Game.ModAPI.MessageEnteredDel.*	local	-	-	-	0	value, parser or helper; no world state
VRage.Game.ModAPI.MessageEnteredSenderDel.*	local	-	-	-	0	value, parser or helper; no world state
VRage.Game.ModAPI.ModCrashedException.ModContext	local	-	-	-	1	ModCrashedException.cs:7 value, parser or helper; no world state
VRage.Game.ModAPI.MyActivationCustomResults.*	local	-	-	-	0	value, parser or helper; no world state
VRage.Game.ModAPI.MyActivationResults.*	local	-	-	-	0	value, parser or helper; no world state
VRage.Game.ModAPI.MyAddContractResultWrapper.ContractConditionId	local	-	-	-	0	MyAddContractResultWrapper.cs:12 value, parser or helper; no world state
VRage.Game.ModAPI.MyAddContractResultWrapper.ContractId	local	-	-	-	2	MyAddContractResultWrapper.cs:10 value, parser or helper; no world state
VRage.Game.ModAPI.MyAddContractResultWrapper.Success	local	-	-	-	2	MyAddContractResultWrapper.cs:8 value, parser or helper; no world state
VRage.Game.ModAPI.MyAdminSettingsEnum.*	local	-	-	-	0	value, parser or helper; no world state
VRage.Game.ModAPI.MyAiBlockType.*	local	-	-	-	0	value, parser or helper; no world state
VRage.Game.ModAPI.MyCameraMode.*	local	-	-	-	0	value, parser or helper; no world state
VRage.Game.ModAPI.MyComponentStackInfo.ComponentName	local	-	-	-	0	MyComponentStackInfo.cs:33 value, parser or helper; no world state
VRage.Game.ModAPI.MyComponentStackInfo.DefinitionId	local	-	-	-	0	MyComponentStackInfo.cs:28 value, parser or helper; no world state
VRage.Game.ModAPI.MyComponentStackInfo.Icons	local	-	-	-	0	MyComponentStackInfo.cs:38 value, parser or helper; no world state
VRage.Game.ModAPI.MyComponentStackInfo.Integrity	local	-	-	-	0	MyComponentStackInfo.cs:18 value, parser or helper; no world state
VRage.Game.ModAPI.MyComponentStackInfo.MaxIntegrity	local	-	-	-	0	MyComponentStackInfo.cs:23 value, parser or helper; no world state
VRage.Game.ModAPI.MyComponentStackInfo.MountedCount	local	-	-	-	1	MyComponentStackInfo.cs:8 value, parser or helper; no world state
VRage.Game.ModAPI.MyComponentStackInfo.TotalCount	local	-	-	-	0	MyComponentStackInfo.cs:13 value, parser or helper; no world state
VRage.Game.ModAPI.MyContractActivateDelegate.*	local	-	-	-	0	value, parser or helper; no world state
VRage.Game.ModAPI.MyContractChangeDelegate.*	local	-	-	-	0	value, parser or helper; no world state
VRage.Game.ModAPI.MyContractConditionDelegate.*	local	-	-	-	0	value, parser or helper; no world state
VRage.Game.ModAPI.MyContractFailedDelegate.*	local	-	-	-	0	value, parser or helper; no world state
VRage.Game.ModAPI.MyContractFinishedDelegate.*	local	-	-	-	0	value, parser or helper; no world state
VRage.Game.ModAPI.MyContractUpdateDelegate.*	local	-	-	-	0	value, parser or helper; no world state
VRage.Game.ModAPI.MyCustomContractStateEnum.*	local	-	-	-	0	value, parser or helper; no world state
VRage.Game.ModAPI.MyDamageInformation.Amount	local	-	-	-	24	MyDamageInformation.cs:124 value, parser or helper; no world state
VRage.Game.ModAPI.MyDamageInformation.AttackerId	local	-	-	-	19	MyDamageInformation.cs:136 value, parser or helper; no world state
VRage.Game.ModAPI.MyDamageInformation.ExtraInfo	local	-	-	-	0	MyDamageInformation.cs:142 value, parser or helper; no world state
VRage.Game.ModAPI.MyDamageInformation.IsDeformation	local	-	-	-	9	MyDamageInformation.cs:118 value, parser or helper; no world state
VRage.Game.ModAPI.MyDamageInformation.Type	local	-	-	-	23	MyDamageInformation.cs:130 value, parser or helper; no world state
VRage.Game.ModAPI.MyDamageSystemPriority.*	local	-	-	-	0	value, parser or helper; no world state
VRage.Game.ModAPI.MyDeviceBase.CanSwitchAmmoMagazine	local	-	-	-	0	MyGunBase.cs:969 value, parser or helper; no world state
VRage.Game.ModAPI.MyDeviceBase.GetMuzzleLocalPosition	local	-	-	-	0	MyGunBase.cs:1501 value, parser or helper; no world state
VRage.Game.ModAPI.MyDeviceBase.GetMuzzleWorldPosition	local	-	-	-	0	MyGunBase.cs:1540 value, parser or helper; no world state
VRage.Game.ModAPI.MyDeviceBase.Init	local	-	-	-	0	MyGunBase.cs:468 value, parser or helper; no world state
VRage.Game.ModAPI.MyDeviceBase.InventoryItemId	local	-	-	-	0	MyDeviceBase.cs:14 value, parser or helper; no world state
VRage.Game.ModAPI.MyDeviceBase.SwitchAmmoMagazineToNextAvailable	local	-	-	-	0	MyGunBase.cs:1010 value, parser or helper; no world state
VRage.Game.ModAPI.MyDeviceBase.SwitchToNextAmmoMagazine	local	-	-	-	1	MyGunBase.cs:1047 value, parser or helper; no world state
VRage.Game.ModAPI.MyFactionStateChange.*	local	-	-	-	0	value, parser or helper; no world state
VRage.Game.ModAPI.MyGeneratedTextureFormat.*	local	-	-	-	0	value, parser or helper; no world state
VRage.Game.ModAPI.MyGridGroupsDefaultEventHandler.IsClosed	local	-	-	-	0	MyGridGroupsDefaultEventHandler.cs:22 value, parser or helper; no world state
VRage.Game.ModAPI.MyGridGroupsDefaultEventHandler.ToString	local	-	-	-	0	MyGridGroupsDefaultEventHandler.cs:100 value, parser or helper; no world state
VRage.Game.ModAPI.MyGunStatusEnum.*	local	-	-	-	0	value, parser or helper; no world state
VRage.Game.ModAPI.MyHitInfo.Normal	local	-	-	-	3	MyHitInfo.cs:109 value, parser or helper; no world state
VRage.Game.ModAPI.MyHitInfo.Position	local	-	-	-	3	MySlimBlock.cs:188 value, parser or helper; no world state
VRage.Game.ModAPI.MyHitInfo.ShapeKey	local	-	-	-	0	MyHitInfo.cs:121 value, parser or helper; no world state
VRage.Game.ModAPI.MyHitInfo.Velocity	local	-	-	-	1	MyHitInfo.cs:115 value, parser or helper; no world state
VRage.Game.ModAPI.MyIntegrityChangeEnum.*	local	-	-	-	0	value, parser or helper; no world state
VRage.Game.ModAPI.MyLockEntityState.LastKnownPosition	local	-	-	-	0	MyLockEntityState.cs:28 value, parser or helper; no world state
VRage.Game.ModAPI.MyLockEntityState.LocalDistance	local	-	-	-	0	MyLockEntityState.cs:23 value, parser or helper; no world state
VRage.Game.ModAPI.MyLockEntityState.LocalMatrix	local	-	-	-	0	MyLockEntityState.cs:13 value, parser or helper; no world state
VRage.Game.ModAPI.MyLockEntityState.LocalVector	local	-	-	-	0	MyLockEntityState.cs:18 value, parser or helper; no world state
VRage.Game.ModAPI.MyLockEntityState.LockEntityDisplayName	local	-	-	-	0	MyLockEntityState.cs:38 value, parser or helper; no world state
VRage.Game.ModAPI.MyLockEntityState.LockEntityID	local	-	-	-	0	MyLockEntityState.cs:33 value, parser or helper; no world state
VRage.Game.ModAPI.MyPromoteLevel.*	local	-	-	-	0	value, parser or helper; no world state
VRage.Game.ModAPI.MyShootActionEnum.*	local	-	-	-	0	value, parser or helper; no world state
VRage.Game.ModAPI.MyStoreItemData.Amount	local	-	-	-	0	MyStoreItemData.cs:20 value, parser or helper; no world state
VRage.Game.ModAPI.MyStoreItemData.GasTanks	local	-	-	-	0	MyStoreItemData.cs:46 value, parser or helper; no world state
VRage.Game.ModAPI.MyStoreItemData.ItemId	local	-	-	-	0	MyStoreItemData.cs:15 value, parser or helper; no world state
VRage.Game.ModAPI.MyStoreItemData.OnCancel	local	-	-	-	0	MyStoreItemData.cs:41 value, parser or helper; no world state
VRage.Game.ModAPI.MyStoreItemData.OnTransaction	local	-	-	-	0	MyStoreItemData.cs:36 value, parser or helper; no world state
VRage.Game.ModAPI.MyStoreItemData.PricePerUnit	local	-	-	-	0	MyStoreItemData.cs:25 value, parser or helper; no world state
VRage.Game.ModAPI.MyTerminalPageEnum.*	local	-	-	-	0	value, parser or helper; no world state
VRage.Game.ModAPI.MyToolbarItemInfo.ActionId	client	-	-	-	0	MyToolbarItemInfo.cs:46 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.MyToolbarItemInfo.BlockAction	client	-	-	-	0	MyToolbarItemInfo.cs:66 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.MyToolbarItemInfo.BlockEntityId	client	-	-	-	0	MyToolbarItemInfo.cs:33 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.MyToolbarItemInfo.Definition	client	-	-	-	0	MyToolbarItemInfo.cs:92 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.MyToolbarItemInfo.DefinitionId	client	-	-	-	0	MyToolbarItemInfo.cs:51 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.MyToolbarItemInfo.GroupAction	client	-	-	-	0	MyToolbarItemInfo.cs:81 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.MyToolbarItemInfo.GroupName	client	-	-	-	0	MyToolbarItemInfo.cs:39 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.MyToolbarItemInfo.Kind	client	-	-	-	0	MyToolbarItemInfo.cs:25 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.MyToolbarItemInfo.Label	client	-	-	-	0	MyToolbarItemInfo.cs:57 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.Game.ModAPI.MyToolbarItemKind.*	local	-	-	-	0	value, parser or helper; no world state
VRage.Game.ModAPI.MyUpdateTiersGridPresence.*	local	-	-	-	0	value, parser or helper; no world state
VRage.Game.ModAPI.MyUpdateTiersPlayerPresence.*	local	-	-	-	0	value, parser or helper; no world state
VRage.Game.ModAPI.Network.MySync.Enabled	local	-	-	-	0	MySync.cs:42 entity-bound sync value; set on the entity's node and replicated to that node's clients
VRage.Game.ModAPI.Network.MySync.Id	local	-	-	-	0	MySync.cs:17 entity-bound sync value; set on the entity's node and replicated to that node's clients
VRage.Game.ModAPI.Network.MySync.SetLocalValue	local	-	-	-	5	MySync.cs:92 entity-bound sync value; set on the entity's node and replicated to that node's clients
VRage.Game.ModAPI.Network.MySync.Validate	local	-	-	-	1	MySync.cs:57 entity-bound sync value; set on the entity's node and replicated to that node's clients
VRage.Game.ModAPI.Network.MySync.ValidateAndSet	local	-	-	-	3	MySync.cs:83 entity-bound sync value; set on the entity's node and replicated to that node's clients
VRage.Game.ModAPI.Network.MySync.Value	local	-	-	-	7	MySync.cs:27 entity-bound sync value; set on the entity's node and replicated to that node's clients
VRage.Game.ModAPI.Network.MySync.ValueChanged	local	-	-	-	5	MySync.cs:73 entity-bound sync value; set on the entity's node and replicated to that node's clients
VRage.Game.ModAPI.Network.MySync.ValueType	local	-	-	-	0	MySync.cs:22 entity-bound sync value; set on the entity's node and replicated to that node's clients
VRage.Game.ModAPI.ReputationChangeReason.*	local	-	-	-	0	value, parser or helper; no world state
VRage.Game.ModAPI.ResultEnum.*	local	-	-	-	0	value, parser or helper; no world state
VRage.Game.ModAPI.ScriptingReflection.BaseTypeOf	local	-	-	-	0	ScriptingReflection.cs:7 value, parser or helper; no world state
VRage.Game.ModAPI.ScriptingReflection.GetInterfaces	local	-	-	-	0	ScriptingReflection.cs:12 value, parser or helper; no world state
VRage.Game.ModAPI.ScriptingReflection.IsAssignableFrom	local	-	-	-	0	ScriptingReflection.cs:17 value, parser or helper; no world state
VRage.Game.ModAPI.ScriptingReflection.IsInstanceOfType	local	-	-	-	0	ScriptingReflection.cs:22 value, parser or helper; no world state
VRage.Game.ModAPI.SpawningOptions.*	local	-	-	-	0	value, parser or helper; no world state
VRage.ModAPI.EntityFlags.*	local	-	-	-	0	value, parser or helper; no world state
VRage.ModAPI.IMyCamera.FarPlaneDistance	client	-	-	-	4	MyCamera.cs:162 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyCamera.FieldOfViewAngle	client	-	-	-	7	MyCamera.cs:136 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyCamera.FieldOfViewAngleForNearObjects	client	-	-	-	0	MyCamera.cs:138 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyCamera.FovWithZoom	client	-	-	-	12	MyCamera.cs:140 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyCamera.FovWithZoomForNearObjects	client	-	-	-	0	MyCamera.cs:142 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyCamera.GetDistanceWithFOV	client	-	-	-	0	MyCamera.cs:314 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyCamera.IsInFrustum	client	-	-	-	5	MyCamera.cs:319 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyCamera.NearPlaneDistance	client	-	-	-	13	MyCamera.cs:160 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyCamera.Position	client	-	-	-	29	MyCamera.cs:144 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyCamera.PreviousPosition	client	-	-	-	2	MyCamera.cs:146 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyCamera.ProjectionMatrix	client	-	-	-	11	MyCamera.cs:156 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyCamera.ProjectionMatrixForNearObjects	client	-	-	-	0	MyCamera.cs:158 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyCamera.ViewMatrix	client	-	-	-	10	MyCamera.cs:152 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyCamera.ViewportOffset	client	-	-	-	0	MyCamera.cs:148 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyCamera.ViewportSize	client	-	-	-	16	MyCamera.cs:150 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyCamera.WorldLineFromScreen	client	-	-	-	1	MyCamera.cs:292 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyCamera.WorldMatrix	client	-	-	-	53	MyCamera.cs:154 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyCamera.WorldToScreen	client	-	-	-	4	MyCamera.cs:309 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyControl.GetAnalogState	client	-	-	-	0	MyControl.cs:488 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyControl.GetControlButtonName	client	-	-	-	5	MyControl.cs:657 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyControl.GetControlDescription	client	-	-	-	0	MyControl.cs:587 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyControl.GetControlName	client	-	-	-	4	MyControl.cs:582 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyControl.GetControlTypeEnum	client	-	-	-	0	MyControl.cs:592 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyControl.GetGameControlEnum	client	-	-	-	9	MyControl.cs:597 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyControl.GetKeyboardControl	client	-	-	-	10	MyControl.cs:207 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyControl.GetKeyboardModifier	client	-	-	-	1	MyControl.cs:212 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyControl.GetMouseControl	client	-	-	-	6	MyControl.cs:227 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyControl.GetSecondKeyboardControl	client	-	-	-	8	MyControl.cs:217 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyControl.GetSecondKeyboardModifier	client	-	-	-	1	MyControl.cs:222 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyControl.IsControlAssigned	client	-	-	-	0	MyControl.cs:602 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyControl.IsEnabled	client	-	-	-	4	MyControl.cs:133 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyControl.IsJoystickPressed	client	-	-	-	0	MyControl.cs:469 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyControl.IsNewJoystickPressed	client	-	-	-	0	MyControl.cs:474 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyControl.IsNewJoystickReleased	client	-	-	-	0	MyControl.cs:479 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyControl.IsNewPressed	client	-	-	-	6	MyControl.cs:374 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyControl.IsNewReleased	client	-	-	-	2	MyControl.cs:424 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyControl.IsPressed	client	-	-	-	6	MyControl.cs:323 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyEntities.AddEntity	affinity	B8	no	to-owner / request	14	MyEntitiesHelper_ModAPI.cs:111 creates the entity on this node whatever the position; a spawn near another node's partition merges over there, and the WA must never spawn (cluster: SpawnDirectorService.cs:15)
VRage.ModAPI.IMyEntities.CreateFromObjectBuilder	affinity	B8	no	to-owner / request	7	MyEntitiesHelper_ModAPI.cs:119 creates the entity on this node whatever the position; a spawn near another node's partition merges over there, and the WA must never spawn (cluster: SpawnDirectorService.cs:15)
VRage.ModAPI.IMyEntities.CreateFromObjectBuilderAndAdd	affinity	B8	no	to-owner / request	9	MyEntitiesHelper_ModAPI.cs:124 creates the entity on this node whatever the position; a spawn near another node's partition merges over there, and the WA must never spawn (cluster: SpawnDirectorService.cs:15)
VRage.ModAPI.IMyEntities.CreateFromObjectBuilderNoinit	affinity	B8	no	to-owner / request	0	MyEntitiesHelper_ModAPI.cs:469 creates the entity on this node whatever the position; a spawn near another node's partition merges over there, and the WA must never spawn (cluster: SpawnDirectorService.cs:15)
VRage.ModAPI.IMyEntities.CreateFromObjectBuilderParallel	affinity	B8	no	to-owner / request	7	MyEntitiesHelper_ModAPI.cs:266 creates the entity on this node whatever the position; a spawn near another node's partition merges over there, and the WA must never spawn (cluster: SpawnDirectorService.cs:15)
VRage.ModAPI.IMyEntities.EnableEntityBoundingBoxDraw	client	-	-	-	0	MyEntitiesHelper_ModAPI.cs:474 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyEntities.EntityExists	node-scoped	B3	no	owner-of / to-owner	5	MyEntitiesHelper_ModAPI.cs:106 resolves the entity on this node only (MyEntities); null for entities on other nodes or concealed
VRage.ModAPI.IMyEntities.Exist	node-scoped	B3	no	owner-of / to-owner	3	MyEntitiesHelper_ModAPI.cs:304 resolves the entity on this node only (MyEntities); null for entities on other nodes or concealed
VRage.ModAPI.IMyEntities.FindFreePlace	node-scoped	B1	no	broadcast / request	4	MyEntitiesHelper_ModAPI.cs:149 enumerates or queries this node's entities only (MyEntities); entities on other nodes and in offline partitions are missing
VRage.ModAPI.IMyEntities.GetElementsInBox	node-scoped	B1	no	broadcast / request	1	MyEntitiesHelper_ModAPI.cs:242 enumerates or queries this node's entities only (MyEntities); entities on other nodes and in offline partitions are missing
VRage.ModAPI.IMyEntities.GetEntities	node-scoped	B1	no	broadcast / request	39	MyEntitiesHelper_ModAPI.cs:67 enumerates or queries this node's entities only (MyEntities); entities on other nodes and in offline partitions are missing
VRage.ModAPI.IMyEntities.GetEntitiesInAABB	node-scoped	B1	no	broadcast / request	1	MyEntitiesHelper_ModAPI.cs:206 enumerates or queries this node's entities only (MyEntities); entities on other nodes and in offline partitions are missing
VRage.ModAPI.IMyEntities.GetEntitiesInSphere	node-scoped	B1	no	broadcast / request	8	MyEntitiesHelper_ModAPI.cs:218 enumerates or queries this node's entities only (MyEntities); entities on other nodes and in offline partitions are missing
VRage.ModAPI.IMyEntities.GetEntity	node-scoped	B3	no	owner-of / to-owner	2	MyEntitiesHelper_ModAPI.cs:492 resolves the entity on this node only (MyEntities); null for entities on other nodes or concealed
VRage.ModAPI.IMyEntities.GetEntityById	node-scoped	B3	no	owner-of / to-owner	55	MyEntitiesHelper_ModAPI.cs:402 resolves the entity on this node only (MyEntities); null for entities on other nodes or concealed
VRage.ModAPI.IMyEntities.GetEntityByName	node-scoped	B3	no	owner-of / to-owner	0	MyEntitiesHelper_ModAPI.cs:434 resolves the entity on this node only (MyEntities); null for entities on other nodes or concealed
VRage.ModAPI.IMyEntities.GetInflatedPlayerBoundingBox	node-scoped	B1	no	broadcast / request	0	MyEntitiesHelper_ModAPI.cs:155 enumerates or queries this node's entities only (MyEntities); entities on other nodes and in offline partitions are missing
VRage.ModAPI.IMyEntities.GetIntersectionWithSphere	node-scoped	B1	no	broadcast / request	0	MyEntitiesHelper_ModAPI.cs:375 enumerates or queries this node's entities only (MyEntities); entities on other nodes and in offline partitions are missing
VRage.ModAPI.IMyEntities.GetTopMostEntitiesInBox	node-scoped	B1	no	broadcast / request	1	MyEntitiesHelper_ModAPI.cs:254 enumerates or queries this node's entities only (MyEntities); entities on other nodes and in offline partitions are missing
VRage.ModAPI.IMyEntities.GetTopMostEntitiesInSphere	node-scoped	B1	no	broadcast / request	19	MyEntitiesHelper_ModAPI.cs:230 enumerates or queries this node's entities only (MyEntities); entities on other nodes and in offline partitions are missing
VRage.ModAPI.IMyEntities.IsInsideVoxel	local	-	-	-	0	MyEntitiesHelper_ModAPI.cs:168 value, parser or helper; no world state
VRage.ModAPI.IMyEntities.IsInsideWorld	local	-	-	-	0	MyEntitiesHelper_ModAPI.cs:196 value, parser or helper; no world state
VRage.ModAPI.IMyEntities.IsNameExists	node-scoped	B3	no	owner-of / to-owner	0	MyEntitiesHelper_ModAPI.cs:279 resolves the entity on this node only (MyEntities); null for entities on other nodes or concealed
VRage.ModAPI.IMyEntities.IsRaycastBlocked	node-scoped	B1	no	broadcast / request	0	MyEntitiesHelper_ModAPI.cs:201 enumerates or queries this node's entities only (MyEntities); entities on other nodes and in offline partitions are missing
VRage.ModAPI.IMyEntities.IsSpherePenetrating	node-scoped	B1	no	broadcast / request	0	MyEntitiesHelper_ModAPI.cs:144 enumerates or queries this node's entities only (MyEntities); entities on other nodes and in offline partitions are missing
VRage.ModAPI.IMyEntities.IsTypeHidden	client	-	-	-	0	MyEntitiesHelper_ModAPI.cs:444 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyEntities.IsVisible	client	-	-	-	0	MyEntitiesHelper_ModAPI.cs:449 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyEntities.IsWorldLimited	local	-	-	-	0	MyEntitiesHelper_ModAPI.cs:181 value, parser or helper; no world state
VRage.ModAPI.IMyEntities.MarkForClose	local	-	-	-	4	MyEntitiesHelper_ModAPI.cs:313 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntities.OnCloseAll	node-scoped	B2	no	handover-hooks	1	MyEntitiesHelper_ModAPI.cs:43 raised by this node's MyEntities; a grid arriving or leaving by handover looks like a create or delete, and other nodes' entities never show up
VRage.ModAPI.IMyEntities.OnEntityAdd	node-scoped	B2	no	handover-hooks	42	MyEntitiesHelper_ModAPI.cs:31 raised by this node's MyEntities; a grid arriving or leaving by handover looks like a create or delete, and other nodes' entities never show up
VRage.ModAPI.IMyEntities.OnEntityNameSet	local	-	-	-	0	MyEntitiesHelper_ModAPI.cs:55 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntities.OnEntityRemove	node-scoped	B2	no	handover-hooks	22	MyEntitiesHelper_ModAPI.cs:19 raised by this node's MyEntities; a grid arriving or leaving by handover looks like a create or delete, and other nodes' entities never show up
VRage.ModAPI.IMyEntities.RegisterForDraw	client	-	-	-	0	MyEntitiesHelper_ModAPI.cs:340 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyEntities.RegisterForUpdate	local	-	-	-	0	MyEntitiesHelper_ModAPI.cs:321 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntities.RemapObjectBuilder	local	-	-	-	12	MyEntitiesHelper_ModAPI.cs:464 value, parser or helper; no world state
VRage.ModAPI.IMyEntities.RemapObjectBuilderCollection	local	-	-	-	2	MyEntitiesHelper_ModAPI.cs:459 value, parser or helper; no world state
VRage.ModAPI.IMyEntities.RemoveEntity	local	-	-	-	2	MyEntitiesHelper_ModAPI.cs:129 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntities.RemoveFromClosedEntities	local	-	-	-	0	MyEntitiesHelper_ModAPI.cs:288 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntities.RemoveName	local	-	-	-	0	MyEntitiesHelper_ModAPI.cs:296 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntities.SetEntityName	local	-	-	-	0	MyEntitiesHelper_ModAPI.cs:271 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntities.SetTypeHidden	client	-	-	-	0	MyEntitiesHelper_ModAPI.cs:439 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyEntities.TryGetEntityById	node-scoped	B3	no	owner-of / to-owner	41	MyEntitiesHelper_ModAPI.cs:78 resolves the entity on this node only (MyEntities); null for entities on other nodes or concealed
VRage.ModAPI.IMyEntities.TryGetEntityByName	node-scoped	B3	no	owner-of / to-owner	1	MyEntitiesHelper_ModAPI.cs:98 resolves the entity on this node only (MyEntities); null for entities on other nodes or concealed
VRage.ModAPI.IMyEntities.UnhideAllTypes	client	-	-	-	0	MyEntitiesHelper_ModAPI.cs:454 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyEntities.UnregisterForDraw	client	-	-	-	0	MyEntitiesHelper_ModAPI.cs:367 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyEntities.UnregisterForUpdate	local	-	-	-	0	MyEntitiesHelper_ModAPI.cs:348 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntities.WorldHalfExtent	local	-	-	-	0	MyEntitiesHelper_ModAPI.cs:186 value, parser or helper; no world state
VRage.ModAPI.IMyEntities.WorldSafeHalfExtent	local	-	-	-	0	MyEntitiesHelper_ModAPI.cs:191 value, parser or helper; no world state
VRage.ModAPI.IMyEntity.AddToGamePruningStructure	local	-	-	-	0	MyEntity.cs:1943 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.BeforeSave	local	-	-	-	0	MyEntity.cs:2320 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.CastShadows	local	-	-	-	1	MyEntity.cs:944 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.Close	local	-	-	-	29	MyEntity.cs:2520 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.DebugAsyncLoading	local	-	-	-	0	MyEntity.cs:880 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.DebugDraw	client	-	-	-	0	MyEntity.cs:1323 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyEntity.DebugDrawInvalidTriangles	client	-	-	-	0	MyEntity.cs:1339 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyEntity.Delete	local	-	-	-	7	MyEntity.cs:2526 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.DisplayName	local	-	-	-	49	MyEntity.cs:883 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.DoOverlapSphereTest	local	-	-	-	1	MyEntity.cs:2592 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.EnableColorMaskForSubparts	local	-	-	-	1	MyEntity.cs:2640 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.EntityId	local	-	-	-	118	MyCubeGrid.cs:474 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.FastCastShadowResolve	local	-	-	-	0	MyEntity.cs:957 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.Flags	local	-	-	-	15	MyEntity.cs:861 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.GameLogic	local	-	-	-	62	MyEntity.cs:905 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.GetChildren	local	-	-	-	0	MyEntity.cs:2496 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.GetDiffuseColor	local	-	-	-	0	MyEntity.cs:2532 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.GetDistanceBetweenCameraAndBoundingSphere	client	-	-	-	0	MyEntity.cs:2538 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyEntity.GetDistanceBetweenCameraAndPosition	client	-	-	-	0	MyEntity.cs:2544 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyEntity.GetFriendlyName	local	-	-	-	3	MyEntity.cs:2514 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.GetIntersectionWithAABB	local	-	-	-	1	MyLaserAntenna.cs:620 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.GetIntersectionWithLine	local	-	-	-	3	MyCharacter.cs:5112 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.GetIntersectionWithLineAndBoundingSphere	local	-	-	-	0	MyEntity.cs:2562 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.GetIntersectionWithSphere	local	-	-	-	2	MyEntity.cs:2568 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.GetInventory	local	-	-	-	52	MyEntity.cs:2574 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.GetLargestDistanceBetweenCameraAndBoundingSphere	client	-	-	-	0	MyEntity.cs:2550 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyEntity.GetObjectBuilder	local	-	-	-	22	MyEntity.cs:2598 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.GetSmallestDistanceBetweenCameraAndBoundingSphere	client	-	-	-	0	MyEntity.cs:2556 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyEntity.GetSubpart	local	-	-	-	3	MyEntity.cs:1588 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.GetTopMostParent	local	-	-	-	22	MyEntity.cs:2490 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.GetTrianglesIntersectingSphere	local	-	-	-	0	MyEntity.cs:2586 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.GetViewMatrix	local	-	-	-	0	MyEntity.cs:2610 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.GetWorldMatrixNormalizedInv	local	-	-	-	0	MyEntity.cs:2616 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.Hierarchy	local	-	-	-	2	MyEntity.cs:654 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.InScene	local	-	-	-	19	MyEntity.cs:1103 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.InvalidateOnMove	local	-	-	-	0	MyEntity.cs:1116 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.IsCCDForProjectiles	local	-	-	-	0	MyEntity.cs:1119 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.IsVisible	local	-	-	-	0	MyEntity.cs:2604 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.IsVolumetric	local	-	-	-	0	MyEntity.cs:1122 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.LocalAABB	local	-	-	-	18	MyEntity.cs:1125 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.LocalAABBHr	local	-	-	-	0	MyEntity.cs:1138 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.LocalMatrix	local	-	-	-	11	MyEntity.cs:1141 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.LocalVolume	local	-	-	-	4	MyEntity.cs:1154 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.LocalVolumeOffset	local	-	-	-	0	MyEntity.cs:1167 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.LocationForHudMarker	client	-	-	-	0	MyEntity.cs:1180 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyEntity.MarkedForClose	local	-	-	-	75	MyEntity.cs:896 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.MaxGlassDistSq	local	-	-	-	0	MyEntity.cs:983 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.Model	local	-	-	-	33	MyEntity.cs:902 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.ModelCollision	local	-	-	-	1	MyEntity.cs:1215 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.Name	local	-	-	-	21	MyEntity.cs:867 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.NearFlag	local	-	-	-	0	MyEntity.cs:931 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.NeedsDraw	client	-	-	-	0	MyEntity.cs:986 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyEntity.NeedsDrawFromParent	client	-	-	-	0	MyEntity.cs:999 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyEntity.NeedsResolveCastShadow	local	-	-	-	0	MyEntity.cs:970 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.NeedsUpdate	local	-	-	-	10	MyEntity.cs:918 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.NeedsWorldMatrix	local	-	-	-	3	MyEntity.cs:728 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.OnAddedToScene	local	-	-	-	0	MyCollector.cs:160 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.OnClose	local	-	-	-	13	MyEntity.cs:1272 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.OnClosing	local	-	-	-	10	MyEntity.cs:1285 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.OnMarkForClose	local	-	-	-	30	MyEntity.cs:1298 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.OnPhysicsChanged	local	-	-	-	2	MyEntity.cs:1311 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.OnRemovedFromScene	local	-	-	-	1	MyCollector.cs:169 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.Parent	local	-	-	-	27	MyEntity.cs:864 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.PersistentFlags	local	-	-	-	0	MyEntity.cs:1090 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.Physics	local	-	-	-	106	MyEntity.cs:670 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.PositionComp	local	-	-	-	66	MyEntity.cs:337 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.RemoveFromGamePruningStructure	local	-	-	-	0	MyEntity.cs:1951 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.Render	local	-	-	-	32	MyProjectorBase.cs:611 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.Save	local	-	-	-	12	MyEntity.cs:1077 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.SetColorMaskForSubparts	local	-	-	-	1	MyEntity.cs:2653 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.SetEmissiveParts	local	-	-	-	26	MyEntity.cs:2693 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.SetEmissivePartsForSubparts	local	-	-	-	3	MyEntity.cs:2699 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.SetLocalMatrix	local	-	-	-	1	MyEntity.cs:2622 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.SetPosition	local	-	-	-	6	MyEntity.cs:2634 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.SetTextureChangesForSubparts	local	-	-	-	0	MyEntity.cs:2667 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.SetWorldMatrix	local	-	-	-	9	MyEntity.cs:2628 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.ShadowBoxLod	local	-	-	-	0	MyEntity.cs:1025 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.SkipIfTooSmall	local	-	-	-	0	MyEntity.cs:1038 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.StopPhysicsActivation	local	-	-	-	2	MyEntity.cs:1244 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.Storage	local	-	-	-	49	MyEntity.cs:1051 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.SyncObject	local	-	-	-	0	MyEntity.cs:406 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.Synchronized	local	-	-	-	10	MyEntity.cs:1183 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.Teleport	local	-	-	-	7	MyCubeGrid.cs:10983 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.Transparent	local	-	-	-	5	MyEntity.cs:1012 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.TryGetSubpart	local	-	-	-	18	MyEntity.cs:1593 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.UpdateGamePruningStructure	local	-	-	-	0	MyEntity.cs:1969 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.Visible	local	-	-	-	3	MyEntity.cs:1064 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.WorldMatrix	local	-	-	-	91	MyEntity.cs:1196 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.WorldMatrixInvScaled	local	-	-	-	9	MyEntity.cs:1209 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyEntity.WorldMatrixNormalizedInv	local	-	-	-	7	MyEntity.cs:1212 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyGridConveyorSystem.PullItem	local	-	-	-	1	MyGridConveyorSystem.cs:1238 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyGridConveyorSystem.PushGenerateItem	local	-	-	-	1	MyGridConveyorSystem.cs:1530 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyHudStat.CurrentValue	client	-	-	-	0	MyStatBase.cs:17 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyHudStat.GetValueString	client	-	-	-	0	MyStatBase.cs:44 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyHudStat.Id	client	-	-	-	0	MyStatBase.cs:15 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyHudStat.MaxValue	client	-	-	-	0	MyStatBase.cs:33 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyHudStat.MinValue	client	-	-	-	0	MyStatBase.cs:35 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyHudStat.Update	client	-	-	-	0	MyStatBase.cs:37 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.DeltaMouseScrollWheelValue	client	-	-	-	20	MyFakeInput.cs:1533 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.EnumerateJoystickNames	client	-	-	-	0	MyFakeInput.cs:1223 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.GetCodeForControl	client	-	-	-	0	MyFakeInput.cs:1703 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.GetControl	client	-	-	-	11	MyFakeInput.cs:1638 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.GetGameControl	client	-	-	-	15	MyFakeInput.cs:1663 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.GetGameControlAnalogState	client	-	-	-	0	MyFakeInput.cs:1598 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.GetJoystickAxisStateForGameplay	client	-	-	-	4	MyFakeInput.cs:1433 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.GetKeyName	client	-	-	-	3	MyFakeInput.cs:1668 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.GetListOfPressedKeys	client	-	-	-	5	MyFakeInput.cs:1653 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.GetListOfPressedMouseButtons	client	-	-	-	1	MyFakeInput.cs:1658 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.GetMouseAreaSize	client	-	-	-	4	MyFakeInput.cs:1578 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.GetMousePosition	client	-	-	-	5	MyFakeInput.cs:1573 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.GetMouseSensitivity	client	-	-	-	1	MyFakeInput.cs:1568 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.GetMouseX	client	-	-	-	2	MyFakeInput.cs:1548 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.GetMouseXForGamePlay	client	-	-	-	5	MyFakeInput.cs:1538 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.GetMouseXInversion	client	-	-	-	0	MyFakeInput.cs:1558 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.GetMouseY	client	-	-	-	2	MyFakeInput.cs:1553 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.GetMouseYForGamePlay	client	-	-	-	5	MyFakeInput.cs:1543 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.GetMouseYInversion	client	-	-	-	0	MyFakeInput.cs:1563 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.GetName	client	-	-	-	2	MyFakeInput.cs:1673 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.GetPressedKeys	client	-	-	-	1	MyFakeInput.cs:1258 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.GetUnassignedName	client	-	-	-	0	MyFakeInput.cs:1688 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsAnyAltKeyPressed	client	-	-	-	16	MyFakeInput.cs:1248 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsAnyCtrlKeyPressed	client	-	-	-	20	MyFakeInput.cs:1253 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsAnyKeyPress	client	-	-	-	6	MyFakeInput.cs:1228 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsAnyMouseOrJoystickPressed	client	-	-	-	3	MyFakeInput.cs:1453 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsAnyMousePressed	client	-	-	-	5	MyFakeInput.cs:1233 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsAnyNewMouseOrJoystickPressed	client	-	-	-	1	MyFakeInput.cs:1458 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsAnyNewMousePressed	client	-	-	-	1	MyFakeInput.cs:1238 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsAnyShiftKeyPressed	client	-	-	-	16	MyFakeInput.cs:1243 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsButtonPressed	client	-	-	-	0	MyFakeInput.cs:1508 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsButtonReleased	client	-	-	-	0	MyFakeInput.cs:1518 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsControl	client	-	-	-	2	MyFakeInput.cs:1718 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsControlAnalog	client	-	-	-	0	MyFakeInput.cs:1708 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsDefined	client	-	-	-	0	MyFakeInput.cs:1713 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsGameControlPressed	client	-	-	-	9	MyFakeInput.cs:1588 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsGameControlReleased	client	-	-	-	1	MyFakeInput.cs:1603 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsJoystickAxisNewPressed	client	-	-	-	3	MyFakeInput.cs:1443 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsJoystickAxisPressed	client	-	-	-	4	MyFakeInput.cs:1438 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsJoystickAxisValid	client	-	-	-	0	MyFakeInput.cs:1628 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsJoystickButtonNewPressed	client	-	-	-	3	MyFakeInput.cs:1423 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsJoystickButtonPressed	client	-	-	-	10	MyFakeInput.cs:1418 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsJoystickButtonValid	client	-	-	-	0	MyFakeInput.cs:1623 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsJoystickConnected	client	-	-	-	1	MyFakeInput.cs:1633 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsJoystickLastUsed	client	-	-	-	5	MyFakeInput.cs:98 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsKeyDigit	client	-	-	-	0	MyFakeInput.cs:1613 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsKeyPress	client	-	-	-	21	MyFakeInput.cs:1263 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsKeyValid	client	-	-	-	1	MyFakeInput.cs:1608 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsLeftMousePressed	client	-	-	-	10	MyFakeInput.cs:1308 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsLeftMouseReleased	client	-	-	-	0	MyFakeInput.cs:1313 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsMiddleMousePressed	client	-	-	-	6	MyFakeInput.cs:1343 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsMouseButtonValid	client	-	-	-	1	MyFakeInput.cs:1618 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsMousePressed	client	-	-	-	8	MyFakeInput.cs:1283 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsMouseReleased	client	-	-	-	1	MyFakeInput.cs:1288 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsNewButtonPressed	client	-	-	-	0	MyFakeInput.cs:1503 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsNewButtonReleased	client	-	-	-	0	MyFakeInput.cs:1513 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsNewGameControlPressed	client	-	-	-	13	MyFakeInput.cs:1583 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsNewGameControlReleased	client	-	-	-	1	MyFakeInput.cs:1593 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsNewJoystickAxisReleased	client	-	-	-	1	MyFakeInput.cs:1448 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsNewJoystickButtonReleased	client	-	-	-	2	MyFakeInput.cs:1428 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsNewKeyPressed	client	-	-	-	19	MyFakeInput.cs:1273 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsNewKeyReleased	client	-	-	-	5	MyFakeInput.cs:1278 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsNewLeftMousePressed	client	-	-	-	10	MyFakeInput.cs:1298 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsNewLeftMouseReleased	client	-	-	-	6	MyFakeInput.cs:1303 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsNewMiddleMousePressed	client	-	-	-	5	MyFakeInput.cs:1348 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsNewMiddleMouseReleased	client	-	-	-	1	MyFakeInput.cs:1353 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsNewMousePressed	client	-	-	-	6	MyFakeInput.cs:1293 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsNewPrimaryButtonPressed	client	-	-	-	0	MyFakeInput.cs:1463 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsNewPrimaryButtonReleased	client	-	-	-	0	MyFakeInput.cs:1473 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsNewRightMousePressed	client	-	-	-	7	MyFakeInput.cs:1323 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsNewRightMouseReleased	client	-	-	-	6	MyFakeInput.cs:1328 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsNewSecondaryButtonPressed	client	-	-	-	0	MyFakeInput.cs:1468 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsNewSecondaryButtonReleased	client	-	-	-	0	MyFakeInput.cs:1478 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsNewXButton1MousePressed	client	-	-	-	0	MyFakeInput.cs:1373 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsNewXButton1MouseReleased	client	-	-	-	0	MyFakeInput.cs:1378 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsNewXButton2MousePressed	client	-	-	-	0	MyFakeInput.cs:1398 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsNewXButton2MouseReleased	client	-	-	-	0	MyFakeInput.cs:1403 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsPrimaryButtonPressed	client	-	-	-	1	MyFakeInput.cs:1493 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsPrimaryButtonReleased	client	-	-	-	0	MyFakeInput.cs:1483 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsRightMousePressed	client	-	-	-	9	MyFakeInput.cs:1318 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsSecondaryButtonPressed	client	-	-	-	0	MyFakeInput.cs:1498 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsSecondaryButtonReleased	client	-	-	-	0	MyFakeInput.cs:1488 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsXButton1MousePressed	client	-	-	-	3	MyFakeInput.cs:1368 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.IsXButton2MousePressed	client	-	-	-	3	MyFakeInput.cs:1393 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.JoystickAsMouse	client	-	-	-	0	MyFakeInput.cs:96 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.JoystickConnected	client	-	-	-	0	MyFakeInput.cs:104 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.JoystickInstanceName	client	-	-	-	0	MyFakeInput.cs:92 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.MouseScrollWheelValue	client	-	-	-	5	MyFakeInput.cs:1523 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.PreviousMouseScrollWheelValue	client	-	-	-	3	MyFakeInput.cs:1528 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.TextInput	client	-	-	-	13	MyFakeInput.cs:94 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.TryGetControl	client	-	-	-	0	MyFakeInput.cs:1698 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.WasKeyPress	client	-	-	-	0	MyFakeInput.cs:1268 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.WasMiddleMousePressed	client	-	-	-	0	MyFakeInput.cs:1358 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.WasMiddleMouseReleased	client	-	-	-	0	MyFakeInput.cs:1363 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.WasRightMousePressed	client	-	-	-	0	MyFakeInput.cs:1333 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.WasRightMouseReleased	client	-	-	-	0	MyFakeInput.cs:1338 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.WasXButton1MousePressed	client	-	-	-	0	MyFakeInput.cs:1383 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.WasXButton1MouseReleased	client	-	-	-	0	MyFakeInput.cs:1388 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.WasXButton2MousePressed	client	-	-	-	0	MyFakeInput.cs:1408 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyInput.WasXButton2MouseReleased	client	-	-	-	0	MyFakeInput.cs:1413 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.IMyRemapHelper.Clear	local	-	-	-	0	MyEntityIdRemapHelper.cs:46 value, parser or helper; no world state
VRage.ModAPI.IMyRemapHelper.GetRemapInfo	local	-	-	-	0	MyEntityIdRemapHelper.cs:52 value, parser or helper; no world state
VRage.ModAPI.IMyRemapHelper.RemapEntityId	local	-	-	-	0	MyEntityIdRemapHelper.cs:16 value, parser or helper; no world state
VRage.ModAPI.IMyRemapHelper.RemapEntityName	local	-	-	-	0	MyEntityIdRemapHelper.cs:26 value, parser or helper; no world state
VRage.ModAPI.IMyRemapHelper.RemapGroupId	local	-	-	-	0	MyEntityIdRemapHelper.cs:31 value, parser or helper; no world state
VRage.ModAPI.IMyStorage.Closed	local	-	-	-	2	MyStorageBase.cs:412 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyStorage.DeleteRange	per-node	B7	yes	broadcast	0	MyStorageBase.cs:1646 edits this node's voxel storage; the cluster voxel journal hooks only RequestVoxelCutoutSphere (cluster: Patches/MyVoxelBasePatch.cs:18), so other nodes keep the old shape
VRage.ModAPI.IMyStorage.DeleteSupported	local	-	-	-	0	MyStorageBase.cs:389 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyStorage.ExecuteOperationFast	local	-	-	-	4	MyStorageBase.cs:1651 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyStorage.Intersect	local	-	-	-	1	MyStorageBase.cs:1452 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyStorage.MarkedForClose	local	-	-	-	0	MyStorageBase.cs:414 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyStorage.NotifyRangeChanged	local	-	-	-	1	MyStorageBase.cs:1656 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyStorage.OverwriteAllMaterials	local	-	-	-	0	MyStorageBase.cs:1145 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyStorage.PinAndExecute	local	-	-	-	1	MyStorageBase.cs:1456 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyStorage.ReadRange	local	-	-	-	13	MyStorageBase.cs:1633 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyStorage.Reset	local	-	-	-	1	MyStorageBase.cs:1122 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyStorage.Save	local	-	-	-	2	MyStorageBase.cs:1044 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyStorage.Size	local	-	-	-	12	MyStorageBase.cs:416 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyStorage.WriteRange	per-node	B7	yes	broadcast	2	MyStorageBase.cs:1641 edits this node's voxel storage; the cluster voxel journal hooks only RequestVoxelCutoutSphere (cluster: Patches/MyVoxelBasePatch.cs:18), so other nodes keep the old shape
VRage.ModAPI.IMyVoxelBase.IsBoxIntersectingBoundingBoxOfThisVoxelMap	local	-	-	-	0	MyVoxelBase.cs:514 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyVoxelBase.PositionLeftBottomCorner	local	-	-	-	3	MyVoxelBase.cs:437 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyVoxelBase.Storage	local	-	-	-	6	MyVoxelBase.cs:496 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IMyVoxelBase.StorageName	local	-	-	-	3	MyVoxelBase.cs:498 acts on this entity or its grid group; an entity lives on one node and the mod code holding the reference runs there
VRage.ModAPI.IVoxelOperator.Flags	local	-	-	-	0	MyStorageDataWriteOperator.cs:11 value, parser or helper; no world state
VRage.ModAPI.IVoxelOperator.Op	local	-	-	-	0	MyStorageDataWriteOperator.cs:18 value, parser or helper; no world state
VRage.ModAPI.ModAPIMass.CenterOfMass	local	-	-	-	0	ModAPIMass.cs:25 value, parser or helper; no world state
VRage.ModAPI.ModAPIMass.FromHkMass	local	-	-	-	0	ModAPIMass.cs:32 value, parser or helper; no world state
VRage.ModAPI.ModAPIMass.InertiaTensor	local	-	-	-	0	ModAPIMass.cs:30 value, parser or helper; no world state
VRage.ModAPI.ModAPIMass.Mass	local	-	-	-	0	ModAPIMass.cs:20 value, parser or helper; no world state
VRage.ModAPI.ModAPIMass.Volume	local	-	-	-	0	ModAPIMass.cs:15 value, parser or helper; no world state
VRage.ModAPI.ModAPIMassElement.Properties	local	-	-	-	0	ModAPIMassElement.cs:11 value, parser or helper; no world state
VRage.ModAPI.ModAPIMassElement.Tranform	local	-	-	-	0	ModAPIMassElement.cs:13 value, parser or helper; no world state
VRage.ModAPI.MyAPIGatewayShortcuts.GetLocalPlayerPosition	client	-	-	-	0	MyAPIGatewayShortcuts.cs:34 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.MyAPIGatewayShortcuts.GetLocalPlayerPositionCallback.*	local	-	-	-	0	value, parser or helper; no world state
VRage.ModAPI.MyAPIGatewayShortcuts.GetMainCamera	client	-	-	-	0	MyAPIGatewayShortcuts.cs:30 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.MyAPIGatewayShortcuts.GetMainCameraCallback.*	local	-	-	-	0	value, parser or helper; no world state
VRage.ModAPI.MyAPIGatewayShortcuts.GetWorldBoundaries	client	-	-	-	0	MyAPIGatewayShortcuts.cs:32 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.MyAPIGatewayShortcuts.GetWorldBoundariesCallback.*	local	-	-	-	0	value, parser or helper; no world state
VRage.ModAPI.MyAPIGatewayShortcuts.RegisterEntityUpdate	client	-	-	-	0	MyAPIGatewayShortcuts.cs:22 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.MyAPIGatewayShortcuts.UnregisterEntityUpdate	client	-	-	-	0	MyAPIGatewayShortcuts.cs:28 client UI, input, camera, audio or HUD; nothing to do on a server
VRage.ModAPI.MyEntityUpdateEnum.*	local	-	-	-	0	value, parser or helper; no world state
VRage.ModAPI.MyKeyboardModifiers.*	local	-	-	-	0	value, parser or helper; no world state
VRage.ModAPI.MyTerminalControlComboBoxItem.Key	local	-	-	-	25	MyTerminalControlComboBoxItem.cs:13 value, parser or helper; no world state
VRage.ModAPI.MyTerminalControlComboBoxItem.Value	local	-	-	-	25	MyTerminalControlComboBoxItem.cs:18 value, parser or helper; no world state
VRage.ModAPI.MyTerminalControlListBoxItem.Text	local	-	-	-	9	MyTerminalControlListBoxItem.cs:13 value, parser or helper; no world state
VRage.ModAPI.MyTerminalControlListBoxItem.Tooltip	local	-	-	-	2	MyTerminalControlListBoxItem.cs:18 value, parser or helper; no world state
VRage.ModAPI.MyTerminalControlListBoxItem.UserData	local	-	-	-	23	MyTerminalControlListBoxItem.cs:23 value, parser or helper; no world state
VRage.ModAPI.PhysicsSettings.AngularDamping	local	-	-	-	1	PhysicsSettings.cs:43 value, parser or helper; no world state
VRage.ModAPI.PhysicsSettings.CollisionLayer	local	-	-	-	1	PhysicsSettings.cs:74 value, parser or helper; no world state
VRage.ModAPI.PhysicsSettings.DetectorColliderCallback	local	-	-	-	0	PhysicsSettings.cs:109 value, parser or helper; no world state
VRage.ModAPI.PhysicsSettings.Entity	local	-	-	-	1	PhysicsSettings.cs:23 value, parser or helper; no world state
VRage.ModAPI.PhysicsSettings.IsPhantom	local	-	-	-	1	PhysicsSettings.cs:89 value, parser or helper; no world state
VRage.ModAPI.PhysicsSettings.LinearDamping	local	-	-	-	1	PhysicsSettings.cs:38 value, parser or helper; no world state
VRage.ModAPI.PhysicsSettings.LocalCenter	local	-	-	-	1	PhysicsSettings.cs:33 value, parser or helper; no world state
VRage.ModAPI.PhysicsSettings.Mass	local	-	-	-	1	PhysicsSettings.cs:114 value, parser or helper; no world state
VRage.ModAPI.PhysicsSettings.MaterialType	local	-	-	-	1	PhysicsSettings.cs:84 value, parser or helper; no world state
VRage.ModAPI.PhysicsSettings.RigidBodyFlags	local	-	-	-	1	PhysicsSettings.cs:79 value, parser or helper; no world state
VRage.ModAPI.PhysicsSettings.WorldMatrix	local	-	-	-	1	PhysicsSettings.cs:28 value, parser or helper; no world state
VRage.ModAPI.VoxelOperatorFlags.*	local	-	-	-	0	value, parser or helper; no world state
System.*	local	-	-	-	0	BCL: pure computation and collections; no world state
VRageMath.*	local	-	-	-	0	math types: pure computation";
}
