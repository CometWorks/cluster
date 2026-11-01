using System;
using Sandbox.Game.Entities.Blocks;
using Sandbox.ModAPI;

namespace ClusterPlugin;

/// <summary>
/// Seams of Sandbox.Game.Entities.Blocks.MyProgrammableBlock.
/// Invoked from CreateInstance by Shared.Patches.MyProgrammableBlockPatch.
/// </summary>
public static class MyProgrammableBlockSeams
{
	// ClusterPlugin seam adapter for ProgrammableBlockMigrationPatch.PrepareProgram: called right
	// after the vanilla lazy IGC_ContextGetter assignment and before the script constructor runs.
	public static Action<MyProgrammableBlock, IMyGridProgram> ClusterPrepareProgram;
}
