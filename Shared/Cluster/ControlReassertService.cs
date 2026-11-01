using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using VRage.Game.Entity;
using VRage.Utils;

namespace ClusterPlugin;

/// <summary>
/// Tells a handed-over client what it controls again, a few times, after the transfer has quiesced.
/// </summary>
/// <remarks>
/// The adopt path already re-asserts control, and the trace proves it raises the right message:
/// <c>OnControlChangedSuccess(..., justUpdateClientCache: false)</c>, which is the form that makes a
/// client call <c>SetControlledEntityInternal</c> rather than only writing its cache. It raises it
/// once, in the middle of the adopt - a millisecond after the client's view was pruned of nearly a
/// thousand replicables and before it is finished being rebuilt.
///
/// The client-side handler is conditional on state that may not be there yet at that instant:
///
/// <code>
/// if (!justUpdateClientCache &amp;&amp; MyEntities.TryGetEntityById(entityId, out entity))
///     Sync.Players.SetControlledEntityInternal(playerId, entity);
/// </code>
///
/// If the entity is not resolvable on the client when the message lands, the assignment is dropped on
/// the floor. Nothing re-sends it, and the only other route to control - <c>MyCharacter</c> calling
/// <c>MyPlayerCollection.UpdateControl</c> as it is added to the scene - has already run for a
/// character the client retained rather than recreated. The player is left holding a character the
/// server agrees it controls, and no way to move it, until they take control of something else. That
/// is why sitting in a cockpit cured it: <c>MyShipController</c> calls <c>UpdateControl</c> too.
///
/// The re-assert is therefore SERVER-SIDE ONLY (2026-07-26). The last paragraph of this comment used
/// to argue the opposite - "re-assigning the entity a client already controls is what vanilla does, so
/// the extra attempts are harmless" - and that was wrong, and cost a player their controls twice.
/// On the client, the assignment runs:
///
/// <code>
/// RemoveControlledEntityInternal(entity);              // deletes the CLIENT's own map entry
/// playerById.Controller.TakeControl(entity);           // returns at once if ControlledEntity == entity
/// </code>
///
/// and <c>MyClientState.Write</c> walks that same client-side map to decide what to send. A client that
/// kept its character across the handover - which is every client, ours are not recreated - already
/// holds it, so <c>TakeControl</c> no-ops, the entry stays deleted, and the client starts reporting that
/// it controls nothing. Every input dies at once, silently, while every server-side field stays perfect.
/// Nothing on the client can put the entry back either: every route funnels into <c>TakeControl</c>,
/// which refuses to act on an entity the controller already holds. Only sitting in a cockpit (a
/// different entity, so <c>TakeControl</c> runs) or a reconnect clears it.
///
/// So there is no safe automatic re-send, and a spectating client is indistinguishable from a broken one
/// on the wire - vanilla stops reporting control while the spectator camera is active. The client half
/// is reported in the character census as <c>clientControl</c>; when it reads <c>none</c> for a player
/// who is not spectating, <see cref="RepairClient"/> is the deliberate, human-initiated repair.
///
/// ponytail: deletion, not another heuristic. The only automatic half left is the one that is provably
/// idempotent - the server's own map.
/// </remarks>
public static class ControlReassertService
{
	private static readonly double[] AttemptDelays = { 1000, 3000, 6000 };
	private static readonly List<Pending> Queue = new List<Pending>();
	private static double m_nextRepair;

	/// <summary>
	/// Partitioning.md §6.6 control-liveness window: how long a client may hold an entity it demonstrably
	/// cannot move before the session is forced through a reconnect instead of left silently stuck.
	/// </summary>
	private const double LivenessWindowMilliseconds = 5000;
	private static readonly Dictionary<ulong, double> UnhealthySince = new Dictionary<ulong, double>();
	private static readonly System.Net.Http.HttpClient Gateway = ClusterHttp.Client(TimeSpan.FromSeconds(10));
	private static readonly List<string> Alarms = new List<string>();

	public static object LivenessStatus()
	{
		return new
		{
			unhealthy = UnhealthySince.Keys.ToArray(),
			alarms = Alarms.ToArray()
		};
	}

	private sealed class Pending
	{
		public ulong Client;
		public long EntityId;
		public int Attempt;
		public double Due;
	}

	public static void Schedule(ulong client, long entityId)
	{
		if (entityId == 0) return;
		Queue.RemoveAll(pending => pending.Client == client);
		Queue.Add(new Pending
		{
			Client = client,
			EntityId = entityId,
			Attempt = 0,
			Due = ClusterRuntime.LocalMilliseconds + AttemptDelays[0]
		});
	}

	public static void Forget(ulong client) => Queue.RemoveAll(pending => pending.Client == client);

	/// <summary>
	/// Re-send a live player the assignment for whatever they already hold, once, by hand.
	/// </summary>
	/// <remarks>
	/// Deliberately manual, and deliberately not on any schedule: the message is destructive to a client
	/// that still holds its character (it deletes the client's own control-map entry and the client then
	/// reports controlling nothing), and a spectating client is indistinguishable on the wire from one
	/// that lost the assignment. Nothing automatic can tell those apart, so a human decides.
	///
	/// Check <c>clientControl</c> in the character census first: <c>none</c> on a player who is not
	/// spectating is the state this repairs.
	/// </remarks>
	public static object RepairClient(ulong client)
	{
		if (MySession.Static?.Players == null) return new { notified = false, reason = "no session" };
		if (!MySession.Static.Players.TryGetPlayerBySteamId(client, out MyPlayer player))
			return new { notified = false, reason = "no online player with that steam id" };
		if (player.Controller?.ControlledEntity?.Entity is not MyEntity held || held.MarkedForClose)
			return new { notified = false, reason = "the player holds no entity to re-assert" };
		MySession.Static.Players.ClusterEnsureControlledEntity(player.Id, held);
		MySession.Static.Players.ClusterNotifyControlledEntity(player.Id, held.EntityId);
		MyLog.Default?.WriteLine($"control_repair_requested client={client} entity={held.EntityId}");
		return new { notified = true, client, entityId = held.EntityId };
	}

	public static void Update()
	{
		if (MySession.Static?.Ready != true) return;
		RepairControlMap();
		if (Queue.Count == 0) return;
		double now = ClusterRuntime.LocalMilliseconds;
		for (int index = Queue.Count - 1; index >= 0; index--)
		{
			Pending pending = Queue[index];
			if (now < pending.Due) continue;
			// Update owns every queue mutation; Reassert only reports whether the pending is still
			// live. Reassert used to RemoveAll its own entry on the drop paths, and a drop on the
			// FINAL attempt then ran RemoveAt(index) against the shrunk list - the seated cell's
			// post-seat stale drop hit exactly that and took the whole node down (2026-08-01).
			bool keep = Reassert(pending);
			pending.Attempt++;
			if (!keep || pending.Attempt >= AttemptDelays.Length) Queue.RemoveAt(index);
			else pending.Due = now + (AttemptDelays[pending.Attempt] - AttemptDelays[pending.Attempt - 1]);
		}
	}

	/// <summary>
	/// Put back any controlling-player mapping that has gone missing under a player who still holds the
	/// character.
	/// </summary>
	/// <remarks>
	/// Scheduling a repair at the adopt site only fixes the handovers that go through the adopt site. A
	/// player re-attached by any other route - the balancer moving a partition, a player rebind, a
	/// respawn landing on a different node - loses the mapping just as silently, and the symptom is the
	/// same every time: input dropped without a word while the character keeps falling under gravity.
	///
	/// The inconsistency is trivially detectable and the repair is idempotent, so check it rather than
	/// enumerate the ways in. This is the one place that has to be right; every call site upstream can
	/// then be wrong without costing the player their controls.
	///
	/// ponytail: a sweep over online players once a second, not an event. There is no event for "the map
	/// disagrees with the controller" - that is the whole defect.
	/// </remarks>
	private static void RepairControlMap()
	{
		if (ClusterRuntime.LocalMilliseconds < m_nextRepair) return;
		double now = ClusterRuntime.LocalMilliseconds;
		m_nextRepair = now + 1000;
		HashSet<ulong> seen = new HashSet<ulong>();
		foreach (MyPlayer player in MySession.Static.Players.GetOnlinePlayers())
		{
			if (player?.Identity == null || player.Controller?.ControlledEntity?.Entity is not MyEntity held
				|| held.MarkedForClose) continue;
			seen.Add(player.Id.SteamId);
			// P1-CHR-03 breaks the rebinding for one client on purpose (ClusterSeams.SuppressControlRebind,
			// set only by the test plugin) to prove the §6.6 fallback below converts a stuck character into
			// a forced reconnect. Unset in every release deployment, so the repair always runs.
			bool broken = ClusterSeams.SuppressControlRebind?.Invoke(player.Id.SteamId) == true;
			if (!broken && MySession.Static.Players.GetControllingPlayer(held) == null)
			{
				MySession.Static.Players.ClusterEnsureControlledEntity(player.Id, held);
				MyLog.Default?.WriteLine($"control_map_repaired client={player.Id.SteamId} entity={held.EntityId} "
					+ $"controllingSteamId={MySession.Static.Players.GetControllingPlayer(held)?.Client?.SteamUserId ?? 0}");
			}
			// Partitioning.md §10 invariant 8 / §6.6: the three server-side conditions under which the
			// client's input cannot reach the entity it holds. A client that can see the world but cannot
			// move fails validation exactly like a protocol error does, instead of being left stuck.
			// Invariant 8 wants "an active (non-frozen, physics-enabled) controlled entity". An entity
			// with NO physics is not physics-enabled: that is the exact shape of the stuck character -
			// the client keeps its camera and its ability to look around, and its input reaches a body
			// with no proxy in the world. Reading a missing Physics as healthy made the one condition
			// the invariant exists to catch the one condition it could not see.
			// Resolve to the top-most parent for the physics test, exactly as the frozen test above
			// already does. A seated player's controlled entity is the COCKPIT BLOCK, not the grid, and
			// a block's Physics is null - so testing `held` directly marked every seated player
			// unhealthy and force-reconnected them five seconds later. That kicked the client out of
			// four lanes (automatic, proximity, save-lineage, pod-spawn) before they could assert
			// anything, and it looked exactly like the client dying of an unexplained SIGKILL.
			//
			// And liveness is "the body is in the world", NOT "the body is awake". Havok deactivates
			// anything at rest, so IsActive reads false for every parked grid and every character
			// standing still - which is a healthy client, not a stuck one. IsInWorld is the condition
			// invariant 8 actually wants: input reaching a body that no longer has a place to move in.
			MyEntity root = held.GetTopMostParent() ?? held;
			// A frozen root is a handover in flight: expected, self-resolving unavailability, not the
			// stuck state invariant 8 hunts. Counting it toward the kick window turned any transfer
			// slower than the window into a forced reconnect (P1-HND-05 under sweep load: the prune/
			// re-seed handover ran ~6 s and the watchdog killed the client mid-lane). Frozen is
			// indeterminate - reset the clock; if the entity comes back genuinely stuck, the clock
			// starts fresh from the unfreeze, and a wedged transfer is the handover machinery's own
			// timeout to catch, not this watchdog's.
			if (ClusterRuntime.IsFrozen(root.EntityId)) { UnhealthySince.Remove(player.Id.SteamId); continue; }
			bool healthy = MySession.Static.Players.GetControllingPlayer(held) != null
				&& root.Physics != null && root.Physics.Enabled && root.Physics.IsInWorld;
			if (healthy) { UnhealthySince.Remove(player.Id.SteamId); continue; }
			if (!UnhealthySince.TryGetValue(player.Id.SteamId, out double since))
			{
				UnhealthySince[player.Id.SteamId] = now;
				continue;
			}
			if (now - since < LivenessWindowMilliseconds) continue;
			UnhealthySince.Remove(player.Id.SteamId);
			ForceReconnect(player.Id.SteamId, held.EntityId);
		}
		foreach (ulong gone in UnhealthySince.Keys.Where(client => !seen.Contains(client)).ToArray())
			UnhealthySince.Remove(gone);
	}

	/// <summary>
	/// The §6.6 fallback: put the client through the normal join flow, which rebinds control the vanilla
	/// way. Never corruption - worst case the player re-joins and re-downloads.
	/// </summary>
	/// <remarks>
	/// The Gateway's kick verb already IS a forced reconnect through the join flow, so this reuses it
	/// rather than inventing a second path a node can use to move a client.
	/// </remarks>
	private static void ForceReconnect(ulong client, long entityId)
	{
		string alarm = $"control_liveness_failed client={client} entity={entityId} action=forced-reconnect";
		MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] ALARM " + alarm);
		Alarms.Add(alarm);
		if (Alarms.Count > 256) Alarms.RemoveAt(0);
		string gateway = Environment.GetEnvironmentVariable("CLUSTER_GATEWAY_REGISTRY")?.TrimEnd('/');
		if (string.IsNullOrEmpty(gateway)) return;
		// Off the game thread: a reconnect round trip must never sit inside the update loop.
		new System.Threading.Thread(() =>
		{
			try
			{
				ClusterRuntime.AuthorizeRegistryClient(Gateway);
				using System.Net.Http.HttpResponseMessage response =
					// reason=reconnect: this is a rescue, not a punishment, so it must not arm the admission
					// cooldown that would keep the stuck player out for minutes (Gateway KickTimeout).
					Gateway.PostAsync(gateway + "/admission/kick?reason=reconnect&user=" + client, null)
						.GetAwaiter().GetResult();
				response.EnsureSuccessStatusCode();
			}
			catch (Exception error)
			{
				MyLog.Default?.WriteLineAndConsole("[ClusterPlugin] forced reconnect failed client=" + client
					+ " error=" + SecretRedactor.Redact(error.Message));
			}
		}) { IsBackground = true, Name = "ClusterControlLivenessReconnect" }.Start();
	}

	/// <summary>Runs one attempt; false means the pending is dead and Update must drop it (Reassert
	/// never touches the Queue itself - Schedule keeps at most one entry per client).</summary>
	private static bool Reassert(Pending pending)
	{
		// A client that left, or a body that was closed or taken over by someone else, is not a case for
		// re-asserting anything - drop it rather than push a stale assignment at whoever is there now.
		if (!MySession.Static.Players.TryGetPlayerBySteamId(pending.Client, out MyPlayer player)
			|| !MyEntities.TryGetEntityById(pending.EntityId, out MyEntity entity)
			|| entity.MarkedForClose
			|| entity is not IMyControllableEntity)
		{
			return false;
		}
		MyPlayer controlling = MySession.Static.Players.GetControllingPlayer(entity);
		if (controlling != null && controlling.Id != player.Id)
		{
			return false;
		}
		// Server side only, deliberately. A client that kept its character across our handover always
		// already holds it, and telling it to take the character again is not a no-op there, it is
		// destructive - see ClusterNotifyControlledEntity. So repair whichever server half is missing
		// (controller via the no-sync attach, map via ensure) and never broadcast.
		bool alreadyHeld = player.Controller?.ControlledEntity?.Entity?.EntityId == entity.EntityId;
		if (!alreadyHeld && player.Controller?.ControlledEntity?.Entity != null)
		{
			// The player legitimately moved on to another entity after this was scheduled - a
			// server-side seat between the adopt and the first attempt is the observed case, and
			// re-taking the old entity here tore the fresh cockpit control down (P3-HND-02 seated
			// cell, 2026-08-01). A pending assignment for an entity the controller no longer holds,
			// while it holds something else, is stale: drop it rather than fight the newer state.
			MyLog.Default?.WriteLine($"control_reassert_stale client={pending.Client} entity={pending.EntityId} "
				+ $"held={player.Controller.ControlledEntity.Entity.EntityId}");
			return false;
		}
		if (alreadyHeld) MySession.Static.Players.ClusterEnsureControlledEntity(player.Id, entity);
		else MySession.Static.Players.ClusterTakeControlNoSync(player.Id, entity);
		MyLog.Default?.WriteLine($"control_reassert client={pending.Client} entity={pending.EntityId} "
			+ $"attempt={pending.Attempt + 1}/{AttemptDelays.Length} held={alreadyHeld} "
			+ $"controllingSteamId={MySession.Static.Players.GetControllingPlayer(entity)?.Client?.SteamUserId ?? 0}");
		return true;
	}
}
