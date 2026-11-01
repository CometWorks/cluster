using System.Collections.Generic;
using System.Globalization;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using VRage.Utils;

namespace ClusterPlugin;

/// <summary>
/// MEASUREMENT ONLY - counts client-frames in the one replication state that forces an
/// outermost-layer rebuild every frame without ever clearing the flag that causes it.
///
/// The state, from reading MyReplicationServer.SendUpdate:
///
///   :442  if (State.AreCharacterReplicableDependenciesDirty()) UpdateLayer.UpdateTimer = 0;
///   :492      if (controlledReplicable != null)                       // guard
///   :506          characterReplicable.GetDependencies(forPlayer: true) // the ONLY clearing site
///
/// Forcing is gated on CharacterReplicable; clearing is gated on ControlledReplicable. The two
/// properties differ in exactly one term - ControlledReplicable resolves the character's TOP-MOST
/// PARENT, CharacterReplicable the character itself. So a seated pilot whose GRID has no registered
/// replicable on this node forces on every frame and never clears.
///
/// The three-condition form (CharacterReplicable != null AND dirty AND ControlledReplicable == null)
/// collapses to two: AreCharacterReplicableDependenciesDirty() RETURNS FALSE when CharacterReplicable
/// is not a MyCharacterReplicable, so dirty == true already implies it is non-null. That also means
/// the characterless population - the 91% one from findings/no-local-character-is-not-an-ejection-
/// marker.md - cannot enter this state: it has BOTH properties null, so forcing is blocked before
/// clearing is.
///
/// Registered falsifier, before any data: ZERO latched client-frames over an hour kills the whole
/// line, and the 428 ms adopt-ready->adopted residual is left unexplained by anything in that method.
///
/// See findings/broadcaster-invalidation-scale-and-the-within-transfer-refutation.md and
/// findings/adopt-is-half-queue-wait-and-not-driven-by-view-size.md.
/// </summary>
public static class BroadcasterLatchProbe
{
	private const int ReportEveryFrames = 3600;   // ~60 s at 60 fps; a heartbeat even when zero

	private static long _frames;
	private static long _observedClientFrames;
	private static long _latchedClientFrames;
	private static readonly Dictionary<ulong, int> Streaks = new();
	// Reused, not allocated per frame: this runs on the game thread 60x/s, and the point of the
	// probe is to measure a cost, not to add one. Copying the roster also avoids iterating a
	// collection that a join or leave could mutate underneath us.
	private static readonly List<MyPlayer> Roster = new();
	private static int _worstStreak;
	private static ulong _worstClient;
	// Split by whether the latched character is SEATED. A bare latch count is uninterpretable
	// against the hypothesis: the state is supposed to be a seated pilot whose GRID has no
	// replicable, so a latch on an unseated character would mean something else entirely and
	// would send us after the wrong thing. (factory-6c, 2026-08-22.)
	private static long _latchedSeatedClientFrames;

	public static void Update()
	{
		if (MySession.Static?.Ready != true) return;
		_frames++;

		Roster.Clear();
		Roster.AddRange(MySession.Static.Players.GetOnlinePlayers());
		foreach (MyPlayer player in Roster)
		{
			ulong client = player.Id.SteamId;
			MyClientState state = ReplicationServerPatch.StateFor(client);
			if (state == null) continue;
			_observedClientFrames++;

			// dirty == true already implies CharacterReplicable is a MyCharacterReplicable.
			bool latched = state.AreCharacterReplicableDependenciesDirty() && state.ControlledReplicable == null;
			if (!latched)
			{
				Streaks.Remove(client);
				continue;
			}

			_latchedClientFrames++;
			MyCharacter character = player.Character;
			if (character != null && !ReferenceEquals(character.GetTopMostParent(), character))
				_latchedSeatedClientFrames++;
			Streaks.TryGetValue(client, out int streak);
			streak++;
			Streaks[client] = streak;
			if (streak > _worstStreak) { _worstStreak = streak; _worstClient = client; }
		}

		if (_frames % ReportEveryFrames != 0) return;
		// Emitted even when the count is zero: a silent probe is indistinguishable from one that
		// never ran, and this measurement's whole value is in being able to report a clean zero.
		MyLog.Default?.WriteLineAndConsole(string.Format(CultureInfo.InvariantCulture,
			"[ClusterPlugin] broadcaster_latch frames={0} observedClientFrames={1} latchedClientFrames={2}"
			+ " latchedSeatedClientFrames={3}"
			+ " latchedSharePct={4:F3} worstStreakFrames={5} worstClient={6} currentlyLatched={7}",
			_frames, _observedClientFrames, _latchedClientFrames, _latchedSeatedClientFrames,
			_observedClientFrames == 0 ? 0.0 : 100.0 * _latchedClientFrames / _observedClientFrames,
			_worstStreak, _worstClient, Streaks.Count));
	}
}
