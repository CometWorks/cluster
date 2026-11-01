using System;
using System.Collections.Generic;
using SpaceEngineers.Game.GUI;
using SpaceEngineers.Game.World;
using VRage.Network;
using VRage.Utils;

namespace ClusterPlugin;

/// <summary>
/// Seam holder for <see cref="MyGuiScreenMedicals"/>. Shared/Patches/MyGuiScreenMedicalsPatch.cs transpiles
/// <c>RefreshRespawnPointsRequest</c> to call <see cref="AppendRespawnPoints"/> right after vanilla collected
/// the local list.
/// </summary>
public static class MyGuiScreenMedicalsSeams
{
	public static Action<ulong, long, List<MySpaceRespawnComponent.MyRespawnPointInfo>> ClusterAppendRespawnPoints;

	/// <summary>Transpiler target: (sender, identity, local list).</summary>
	public static void AppendRespawnPoints(long identityId, ClearToken<MySpaceRespawnComponent.MyRespawnPointInfo> availableRespawnPoints)
	{
		Action<ulong, long, List<MySpaceRespawnComponent.MyRespawnPointInfo>> seam = ClusterAppendRespawnPoints;
		if (seam == null) return;
		try
		{
			seam(MyEventContext.Current.Sender.Value, identityId, availableRespawnPoints.List);
		}
		catch (Exception exception)
		{
			// The call sits before vanilla's try/finally that recycles the list; never let a registry
			// hiccup take the medical screen down with it.
			MyLog.Default?.WriteLine("[ClusterPlugin] respawn-points append failed: " + exception);
		}
	}
}
