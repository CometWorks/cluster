using System;
using ClusterPlugin;
using HarmonyLib;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.SessionComponents;

namespace Shared.Patches;

/// <summary>
/// MyIntergridCommunicationContextPatch.SendRemoteBroadcast. The
/// ledger prescribes a postfix on the generic
/// <c>IMyIntergridCommunicationSystem.SendBroadcastMessage&lt;TData&gt;</c>; Harmony 2.4 on .NET 10
/// cannot patch a generic definition and a value-type TData gets its own native code, so the hook
/// sits one call down instead: <c>MyIGCSystemSessionComponent.EnqueueMessage(Message)</c>, the
/// non-generic method SendBroadcastMessage calls with the already-boxed payload. That is the same
/// point in the flow (right after the local enqueue, sharing the one boxing)
/// and covers every TData. Unicast messages also pass through EnqueueMessage and are skipped -
/// their remote path is the unicast prefix in <c>MyIntergridCommunicationContextPatch</c>.
/// </summary>
[HarmonyPatch(typeof(MyIGCSystemSessionComponent), nameof(MyIGCSystemSessionComponent.EnqueueMessage))]
public static class MyIGCSystemSessionComponentPatch
{
	[HarmonyPostfix]
	private static void EnqueueMessagePostfix(MyIGCSystemSessionComponent.Message message)
	{
		Action<MyProgrammableBlock, string, object, int> hook = MyIntergridCommunicationContextSeams.ClusterSendRemoteBroadcast;
		if (hook == null || message.IsUnicast || message.Source == null)
		{
			return;
		}
		// ClusterPlugin patch point: MyIntergridCommunicationContextPatch.SendRemoteBroadcast
		// Listeners on other nodes are invisible to the local dispatch loop, so without this a broadcast stops at
		// the partition boundary. The hook only reaches nodes that report a listener for this tag.
		hook(message.Source.ProgrammableBlock, message.Tag, message.Data, (int)message.TransmissionDistance);
	}
}
