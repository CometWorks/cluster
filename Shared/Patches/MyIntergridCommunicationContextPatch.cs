using System;
using System.Collections.Generic;
using System.Reflection;
using ClusterPlugin;
using HarmonyLib;
using Sandbox.Game.GameSystems.IntergridCommunication;
using Sandbox.Game.SessionComponents;
using VRageMath;

namespace Shared.Patches;

/// <summary>
/// Prefix
/// on the explicit interface implementation
/// <c>IMyIntergridCommunicationSystem.SendUnicastMessage&lt;TData&gt;</c>. Vanilla resolves the
/// addressee with GetContextForPB, which knows only blocks loaded on this node, and returns false
/// for anything else - an addressee one partition away is indistinguishable from one that does not
/// exist. The prefix steps in exactly there (a locally resolvable addressee never reaches the hook)
/// and hands the send to <see cref="MyIntergridCommunicationContextSeams.ClusterSendRemoteUnicast"/>,
/// which answers false for both cases it cannot route - vanilla's own answer.
///
/// The target is a generic method. Harmony 2.4 on .NET 10 refuses to patch a generic method
/// definition (NotSupportedException, verified), so <see cref="TargetMethods"/> patches closed
/// instantiations: <c>&lt;string&gt;</c> owns the runtime's shared canonical code and therefore
/// covers every reference-type TData at once (verified: object and List&lt;int&gt; hit the prefix),
/// and every value type <c>IgcDeliveryCodec.TryEncode</c> can carry is patched individually. A value
/// type outside that set stays vanilla, which returns false - the same result as a payload the
/// codec rejects.
///
/// Boxing through <c>BoxMessage(data)</c> would throw for a disallowed TData. Harmony hands the prefix the already-boxed argument, so a
/// disallowed reference type reaches the hook instead of throwing; the codec rejects it and the
/// send returns false, which is what vanilla returns for an unknown addressee anyway.
/// </summary>
[HarmonyPatch]
public static class MyIntergridCommunicationContextPatch
{
	private const string SendUnicastMessageName = "Sandbox.ModAPI.Ingame.IMyIntergridCommunicationSystem.SendUnicastMessage";

	/// <summary>Value types IgcDeliveryCodec.TryEncode can put on the wire; keep in sync with it.</summary>
	private static readonly Type[] EncodableValueTypes =
	{
		typeof(bool), typeof(byte), typeof(sbyte), typeof(short), typeof(ushort), typeof(int), typeof(uint),
		typeof(long), typeof(ulong), typeof(float), typeof(double), typeof(Vector3D), typeof(Vector3), typeof(Vector3I)
	};

	[HarmonyTargetMethods]
	private static IEnumerable<MethodBase> TargetMethods()
	{
		MethodInfo definition = AccessTools.Method(typeof(MyIntergridCommunicationContext), SendUnicastMessageName)
			?? throw new InvalidOperationException("MyIntergridCommunicationContext." + SendUnicastMessageName + " not found");
		if (!definition.IsGenericMethodDefinition || definition.GetGenericArguments().Length != 1)
		{
			throw new InvalidOperationException(SendUnicastMessageName + " is not the expected generic method definition");
		}
		// Reference types share one canonical instantiation; patching <string> patches all of them.
		yield return definition.MakeGenericMethod(typeof(string));
		foreach (Type valueType in EncodableValueTypes)
		{
			yield return definition.MakeGenericMethod(valueType);
		}
	}

	[HarmonyPrefix]
	private static bool SendUnicastMessagePrefix(MyIntergridCommunicationContext __instance, long addressee, string tag,
		object data, ref bool __result)
	{
		Func<Sandbox.Game.Entities.Blocks.MyProgrammableBlock, long, string, object, bool> hook =
			MyIntergridCommunicationContextSeams.ClusterSendRemoteUnicast;
		if (hook == null)
		{
			return true;
		}
		MyIGCSystemSessionComponent context = MyIGCSystemSessionComponent.Static;
		if (context == null || context.GetContextForPB(addressee) != null)
		{
			// Locally resolvable (or no session component at all): the vanilla path is untouched.
			return true;
		}
		// ClusterPlugin patch point: MyIntergridCommunicationContextPatch.SendRemoteUnicast
		// GetContextForPB only resolves blocks loaded on this node, so an addressee one partition away is
		// indistinguishable from one that does not exist. The hook answers false for both cases it cannot
		// route, which is exactly what vanilla already returns here.
		__result = hook(__instance.ProgrammableBlock, addressee, tag, data);
		return false;
	}
}
