using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using ClusterPlugin;
using HarmonyLib;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using VRageMath;

namespace Shared.Patches;

/// <summary>
/// Proxy-peer branches for MyLaserAntenna. A cross-node laser peer exists locally only as a proxy, and
/// every vanilla peer check reads the real MyLaserAntenna (GetLaserById), which is null for
/// proxies - a cross-node link could never reach <c>connected</c>. Each patch below activates only
/// where vanilla had already found <c>GetLaserById(..) == null</c>, and only while
/// <see cref="ClusterLaserLink.Peer"/> is installed; the branch bodies are in
/// <see cref="MyLaserAntennaSeams"/>. The GPS search (SE1-0226) differs: its loop skips proxies
/// unconditionally, since vanilla would dereference their null real antenna, and the proxy search runs
/// after it when vanilla found nothing.
/// </summary>
[HarmonyPatch(typeof(MyLaserAntenna))]
public static class MyLaserAntennaPatch
{
	/// <summary>Vanilla: GetLaserById null → return false. Cluster: pair against the proxy instead.</summary>
	[HarmonyPrefix]
	[HarmonyPatch(nameof(MyLaserAntenna.ConnectTo))]
	private static bool ConnectToPrefix(MyLaserAntenna __instance, long DestId, ref bool __result)
	{
		if (ClusterLaserLink.Peer == null || MyLaserAntenna.GetLaserById(DestId) != null)
		{
			return true;
		}
		__result = MyLaserAntennaSeams.ConnectToProxy(__instance, DestId);
		return false;
	}

	/// <summary>
	/// Vanilla falls to "coords = Zero, name = ???" when the target is neither a real local antenna nor given
	/// explicitly; an added <c>else if</c> takes the remote peer's head position and the proxy's
	/// name there. The whole body runs from the seam helper so the side-effect order stays vanilla's.
	/// </summary>
	[HarmonyPrefix]
	[HarmonyPatch(nameof(MyLaserAntenna.DoConnectTo))]
	private static bool DoConnectToPrefix(MyLaserAntenna __instance, long DestId, Vector3D? targetCoords, string name)
	{
		if (ClusterLaserLink.Peer == null || MyLaserAntenna.GetLaserById(DestId) != null || (targetCoords.HasValue && name != null))
		{
			return true;
		}
		if (MyLaserAntennaSeams.ClusterPeerFor(DestId) is not ClusterLaserLink.PeerInfo clusterPeer)
		{
			return true;
		}
		MyLaserAntennaSeams.DoConnectToProxy(__instance, DestId, clusterPeer);
		return false;
	}

	/// <summary>Vanilla resets m_canLaseTargetCoords and only sets it for a real local target; the proxy peer is additive.</summary>
	[HarmonyPostfix]
	[HarmonyPatch(nameof(MyLaserAntenna.SetupLaseTargetCoords))]
	private static void SetupLaseTargetCoordsPostfix(MyLaserAntenna __instance)
	{
		if (ClusterLaserLink.Peer == null)
		{
			return;
		}
		MyLaserAntennaSeams.SetupLaseTargetCoordsProxy(__instance);
	}

	/// <summary>SE1-0226: the <c>search_GPS</c> case found no real local laser; look for one hosted on another node.</summary>
	[HarmonyPostfix]
	[HarmonyPatch(nameof(MyLaserAntenna.UpdateAfterSimulation100))]
	private static void UpdateAfterSimulation100Postfix(MyLaserAntenna __instance)
	{
		if (ClusterLaserLink.Peer == null)
		{
			return;
		}
		MyLaserAntennaSeams.SearchGpsProxy(__instance);
	}

	/// <summary>
	/// The state machine. Both proxy branches sit right after <c>laserById = GetLaserById(m_targetId.Value)</c>
	/// in the <c>contact_Rec</c> and <c>connected</c> cases - the only two GetLaserById calls in the method.
	/// After each one the transpiler inserts <c>if (StateMachineHook(laserById, this)) break;</c>, where
	/// break is a jump to the method's final ret (the switch is the method's last statement). The local is
	/// duplicated on the stack ahead of the original stloc, so the transpiler never has to decode which
	/// local slot the compiler chose.
	///
	/// SE1-0226: the one <c>LaserAntennas</c> read, the <c>search_GPS</c> loop, goes through
	/// <see cref="MyLaserAntennaSeams.RealLasers"/>, which leaves the proxies out. Same stack shape: the
	/// MyAntennaSystem instance in, the dictionary out.
	/// </summary>
	[HarmonyTranspiler]
	[HarmonyPatch(nameof(MyLaserAntenna.UpdateAfterSimulation100))]
	private static IEnumerable<CodeInstruction> UpdateAfterSimulation100Transpiler(IEnumerable<CodeInstruction> instructions,
		ILGenerator generator, MethodBase original)
	{
		MethodInfo getLaserById = AccessTools.Method(typeof(MyLaserAntenna), nameof(MyLaserAntenna.GetLaserById), new[] { typeof(long) })
			?? throw new InvalidOperationException("MyLaserAntenna.GetLaserById(long) not found");
		MethodInfo hook = AccessTools.Method(typeof(MyLaserAntennaPatch), nameof(StateMachineHook));
		List<CodeInstruction> codes = new List<CodeInstruction>(instructions);
		if (codes.Count == 0 || codes[codes.Count - 1].opcode != OpCodes.Ret)
		{
			throw new InvalidOperationException(original.Name + ": expected the method to end with ret");
		}
		Label breakLabel = generator.DefineLabel();
		codes[codes.Count - 1].labels.Add(breakLabel);
		int anchors = 0;
		for (int i = 0; i + 1 < codes.Count; i++)
		{
			if (!codes[i].Calls(getLaserById))
			{
				continue;
			}
			if (!codes[i + 1].opcode.Name.StartsWith("stloc", StringComparison.Ordinal))
			{
				throw new InvalidOperationException(original.Name + ": GetLaserById call not followed by stloc");
			}
			anchors++;
			codes.Insert(i + 1, new CodeInstruction(OpCodes.Dup));
			codes.InsertRange(i + 3, new[]
			{
				new CodeInstruction(OpCodes.Ldarg_0),
				new CodeInstruction(OpCodes.Call, hook),
				new CodeInstruction(OpCodes.Brtrue, breakLabel)
			});
			i += 4;
		}
		if (anchors != 2)
		{
			throw new InvalidOperationException(original.Name + ": expected exactly 2 GetLaserById anchors, found " + anchors);
		}
		MethodInfo laserAntennas = AccessTools.PropertyGetter(typeof(MyAntennaSystem), nameof(MyAntennaSystem.LaserAntennas));
		List<CodeInstruction> reads = codes.FindAll(code => code.Calls(laserAntennas));
		if (reads.Count != 1)
		{
			throw new InvalidOperationException(original.Name + ": expected exactly 1 LaserAntennas read, found " + reads.Count);
		}
		reads[0].opcode = OpCodes.Call;
		reads[0].operand = AccessTools.Method(typeof(MyLaserAntennaSeams), nameof(MyLaserAntennaSeams.RealLasers));
		return codes;
	}

	/// <summary>True: the current switch case breaks here (the proxy branch ran).</summary>
	private static bool StateMachineHook(MyLaserAntenna laserById, MyLaserAntenna self)
	{
		if (ClusterLaserLink.Peer == null || laserById != null || !self.m_targetId.HasValue)
		{
			return false;
		}
		long targetId = self.m_targetId.Value;
		switch (self.State)
		{
			case MyLaserAntenna.StateEnum.contact_Rec:
				return MyLaserAntennaSeams.ContactWithProxyPeer(self, targetId);
			case MyLaserAntenna.StateEnum.connected:
				return MyLaserAntennaSeams.HoldWithProxyPeer(self, targetId);
			default:
				return false;
		}
	}
}
