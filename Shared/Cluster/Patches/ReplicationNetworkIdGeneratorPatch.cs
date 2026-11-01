using System;
using VRage.Library.Algorithms;

namespace ClusterPlugin;

public static class ReplicationNetworkIdGeneratorPatch
{
	public static Func<SequenceIdGenerator> Factory { get; set; }
	public static SequenceIdGenerator Replacement() => Factory?.Invoke();
}
