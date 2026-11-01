using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Multiplayer;
using VRage.ObjectBuilders;

namespace ClusterPlugin;

/// <summary>
/// #1381: a meteor keeps its vanilla lifetime across loads.
/// <para>
/// Vanilla <c>MyMeteorGameLogic</c> stamps <c>m_timeCreated</c> with the node's game clock at Init and closes the
/// meteor once it is older than min(MAX_TRAJECTORY_LENGTH / SPEED, MAX_TRAJECTORY_LENGTH / |v|) s, ~111 s. A
/// handover, an offline partition's load and a node start all run Init again, so on a cluster every hop started
/// the meteor's life over, and one that kept moving between nodes or went offline never closed (98 online at
/// T+90 on octahedron-1191c). Meteors are partition members: they hold partitions online and link them.
/// </para>
/// <para>
/// The meteor's birth (Unix milliseconds, wall clock, the same on every node) travels in its builder as a
/// <see cref="BlockBuilderEntry"/> (<see cref="Key"/>), so the value is constant and a round trip compares equal
/// at the handover gate. Init sets the vanilla stamp back by the age the birth gives, so the vanilla check closes
/// the meteor on schedule; time spent offline counts, as it would have in one process. A loaded meteor keeps at
/// least <see cref="LoadGraceMilliseconds"/>: one that closed between its import and the round-trip gate would
/// fail the handover ("Imported entity is missing"). A builder without the entry (a fresh spawn, a save from a
/// build before this one) starts a full life, as vanilla does.
/// </para>
/// </summary>
internal static class MeteorLifetime
{
	internal static readonly Guid Key = new Guid("5c1a0233-3e7e-4a2e-9d0e-b10c2e7e0381");

	/// <summary>The least life a loaded meteor keeps: longer than an import takes to reach the round-trip gate.</summary>
	internal const int LoadGraceMilliseconds = 30000;

	private sealed class State
	{
		public long Birth;
	}

	private static readonly ConditionalWeakTable<MyMeteor.MyMeteorGameLogic, State> States = new ConditionalWeakTable<MyMeteor.MyMeteorGameLogic, State>();

	private static bool Active => ClusterRuntime.IsClustered && !ClusterRuntime.IsWorldAuthority && Sync.IsServer;

	private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

	/// <summary>MyMeteorGameLogic.Init postfix, any thread (vanilla spawns meteors in parallel).</summary>
	internal static void Initialized(MyMeteor.MyMeteorGameLogic logic, MyObjectBuilder_EntityBase builder)
	{
		if (!Active || builder is not MyObjectBuilder_Meteor meteor) return;
		long now = Now;
		long birth = long.TryParse(BlockBuilderEntry.Read(builder, Key), NumberStyles.Integer, CultureInfo.InvariantCulture, out long carried)
			? carried : now;
		States.AddOrUpdate(logic, new State { Birth = birth });
		logic.m_timeCreated -= CarriedAge(now - birth, LifetimeMilliseconds(meteor.LinearVelocity.Length()));
	}

	/// <summary>MyMeteorGameLogic.GetObjectBuilder postfix: write the birth into the builder.</summary>
	internal static void Saving(MyMeteor.MyMeteorGameLogic logic, MyObjectBuilder_EntityBase builder)
	{
		if (!Active || builder == null) return;
		long birth = States.GetValue(logic, _ => new State { Birth = Now }).Birth;
		BlockBuilderEntry.Write(builder, Key, birth.ToString(CultureInfo.InvariantCulture));
	}

	/// <summary>The meteor's carried birth (Unix ms), null when it has none (test probe).</summary>
	internal static long? Birth(MyMeteor meteor)
		=> meteor?.GameLogic is MyMeteor.MyMeteorGameLogic logic && States.TryGetValue(logic, out State state) ? state.Birth : null;

	/// <summary>Vanilla's lifetime of a meteor at this speed (UpdateBeforeSimulation100).</summary>
	internal static double LifetimeMilliseconds(double speed)
		=> Math.Min(MyMeteor.MAX_TRAJECTORY_LENGTH / MyMeteor.SPEED, MyMeteor.MAX_TRAJECTORY_LENGTH / speed) * 1000;

	/// <summary>The age a loaded meteor resumes at: its real age, but never so old it has under the grace left.</summary>
	internal static int CarriedAge(long ageMilliseconds, double lifetimeMilliseconds)
		=> (int)Math.Max(0, Math.Min(ageMilliseconds, lifetimeMilliseconds - LoadGraceMilliseconds));
}
