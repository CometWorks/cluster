using System;
using VRage.Library.Utils;

namespace ClusterPlugin;

/// <summary>
/// Seam holder for <see cref="Sandbox.Engine.Multiplayer.MyMultiplayerServerBase"/>. Harmony side:
/// Shared/Patches/MyMultiplayerServerBasePatch.cs (ReplicationClockPatch postfix on GetUpdateTime).
/// </summary>
public static class MyMultiplayerServerBaseSeams
{
	public static Func<MyTimeSpan, MyTimeSpan> ClusterAdjustUpdateTime;
}
