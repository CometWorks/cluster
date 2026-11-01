using Sandbox.Game.Entities;
using Sandbox.Game.GameSystems;

namespace ClusterPlugin;

public static class MyAntennaSystemPatch
{
	public static void Install()
	{
		MyAntennaSystemSeams.ClusterRegisterPostfix = RegisterAntennaPostfix;
		MyAntennaSystemSeams.ClusterUnregisterPostfix = UnregisterAntennaPostfix;
	}

	public static void RegisterAntennaPostfix(MyDataBroadcaster broadcaster)
		=> AntennaGraphService.Register(broadcaster);

	public static void UnregisterAntennaPostfix(MyDataBroadcaster broadcaster)
		=> AntennaGraphService.Unregister(broadcaster);
}
