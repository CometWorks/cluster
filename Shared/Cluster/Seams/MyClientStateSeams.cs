using System.Runtime.CompilerServices;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using VRage.Game.Entity;

namespace ClusterPlugin;

/// <summary>
/// Seam holder for <see cref="MyClientState"/>: two diagnostic fields that
/// make the silent input gate of <c>MyClientState.Read</c> visible. Every branch of that gate discards the
/// client's controls without a log line. Extension properties backed by a ConditionalWeakTable;
/// Shared/Patches/MyClientStatePatch.cs maintains them (prefix on Read + a one-call transpiler).
/// </summary>
public static class MyClientStateSeams
{
	private sealed class Diagnostics
	{
		public int ReadFrame = -1;
		public string Gate = "never";
	}

	private static readonly ConditionalWeakTable<MyClientState, Diagnostics> Table = new ConditionalWeakTable<MyClientState, Diagnostics>();

	private static Diagnostics Of(MyClientState state) => Table.GetValue(state, static _ => new Diagnostics());

	extension(MyClientState self)
	{
		/// <summary>The last frame this client's state packet was read at all.</summary>
		public int ClusterLastReadFrame
		{
			get => Table.TryGetValue(self, out Diagnostics d) ? d.ReadFrame : -1;
			set => Of(self).ReadFrame = value;
		}

		/// <summary>
		/// "applied", or the name of the branch that silently discarded the controls: "unresolved" (the
		/// client's claimed controlled entity did not resolve in ReadShared), "no-controlling-player",
		/// "steam-mismatch". The ReadShared bail sub-reason is not recorded.
		/// </summary>
		public string ClusterLastGate
		{
			get => Table.TryGetValue(self, out Diagnostics d) ? d.Gate : "never";
			set => Of(self).Gate = value;
		}
	}

	/// <summary>Prefix body for Read: stamps the frame and assumes "unresolved" until the gate says otherwise.</summary>
	public static void BeginRead(MyClientState self)
	{
		if (!ClusterRuntime.IsClustered) return;
		Diagnostics d = Of(self);
		d.ReadFrame = MySession.Static?.GameplayFrameCounter ?? -1;
		d.Gate = "unresolved";
	}

	/// <summary>
	/// Transpiler target: stands in for the single <c>GetControllingPlayer</c> call inside Read, so the gate
	/// verdict is recorded from exactly the values vanilla decides on. Vanilla behaviour is unchanged.
	/// </summary>
	public static MyPlayer GateControllingPlayer(MyPlayerCollection players, MyEntity controlledEntity, MyClientState self)
	{
		MyPlayer controllingPlayer = players.GetControllingPlayer(controlledEntity);
		if (ClusterRuntime.IsClustered)
		{
			Of(self).Gate = controllingPlayer == null ? "no-controlling-player"
				: controllingPlayer.Client.SteamUserId != self.EndpointId.Id.Value ? "steam-mismatch"
				: "applied";
		}
		return controllingPlayer;
	}
}
