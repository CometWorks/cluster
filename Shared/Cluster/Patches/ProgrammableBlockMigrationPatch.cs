using System;
using System.Reflection;
using Sandbox.Game.Entities.Blocks;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Ingame;

namespace ClusterPlugin;

internal static class ProgrammableBlockMigrationPatch
{
	public static void Install() => MyProgrammableBlockSeams.ClusterPrepareProgram = PrepareProgram;

	public static void PrepareProgram(MyProgrammableBlock __instance, IMyGridProgram program)
	{
		Type type = typeof(MyProgrammableBlock).Assembly.GetType("Sandbox.Game.SessionComponents.MyIGCSystemSessionComponent", throwOnError: true);
		object component = type.GetProperty("Static", BindingFlags.Public | BindingFlags.Static).GetValue(null);
		object context = type.GetMethod("GetOrMakeContextFor", BindingFlags.Public | BindingFlags.Instance).Invoke(component, new object[] { __instance });
		program.IGC_ContextGetter = () => (IMyIntergridCommunicationSystem)context;
	}
}
