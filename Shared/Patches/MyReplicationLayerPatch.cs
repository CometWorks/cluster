using System.Threading;
using ClusterPlugin;
using HarmonyLib;
using VRage.Library.Algorithms;
using VRage.Network;

namespace Shared.Patches;

/// <summary>
/// RegistryNetworkIdRangePatch, replication-layer half:
/// <c>m_networkIdGenerator</c> should be
/// <c>ReplicationNetworkIdGeneratorPatch.Replacement() ?? SequenceIdGenerator.CreateWithStopwatch(...)</c>.
/// The stock constructor always builds the stopwatch generator, so this postfix swaps the field to
/// the registry-issued range generator when the runtime has installed one; without cluster startup
/// the vanilla generator stays. NextId/Return dispatch to the range generator is
/// <see cref="SequenceIdGeneratorPatch"/>.
/// </summary>
[HarmonyPatch(typeof(MyReplicationLayer), MethodType.Constructor, typeof(bool), typeof(EndpointId), typeof(Thread))]
public static class MyReplicationLayerPatch
{
	[HarmonyPostfix]
	private static void ConstructorPostfix(ref SequenceIdGenerator ___m_networkIdGenerator)
	{
		SequenceIdGenerator replacement = ReplicationNetworkIdGeneratorPatch.Replacement();
		if (replacement != null)
			___m_networkIdGenerator = replacement;
	}
}
