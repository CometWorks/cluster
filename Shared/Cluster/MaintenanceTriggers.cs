using System;
using System.Collections.Generic;
using System.Linq;

namespace ClusterPlugin;

/// <summary>
/// Orchestration.md 11.2: the node half of <c>POST /trigger/&lt;task&gt;</c> - run a scheduled maintenance
/// task now so a scenario never has to wait out a cadence.
/// </summary>
/// <remarks>
/// Every entry calls exactly the routine the scheduler calls; there is no test-only variant, which is the
/// contract P1-INF-02 asserts by comparing a scheduled run's event log against a triggered one. Policy is
/// therefore honored by construction: the split sweep still skips locked partitions because
/// <see cref="PartitionMembership"/> skips them, and a save still goes through the bounded queue.
///
/// Runs on the game thread (the caller marshals), which is also the idempotence guarantee: a second
/// trigger cannot interleave with the first.
/// </remarks>
internal static class MaintenanceTriggers
{
	internal static readonly string[] Tasks = { "split-sweep", "membership-eval", "save", "conceal-eval", "compact" };

	internal static object Run(string task, PartitionManager partitions, PartitionPersistence persistence, ulong? partitionFilter)
	{
		switch (task)
		{
			// One routine, two names: the membership pass IS the sweep - it re-clusters every partition's
			// members from scratch at the D + 2m split radius on each call (Partitioning.md 3.4). The names
			// stay distinct because the plan's trigger table and the scenarios name them separately.
			case "split-sweep":
			case "membership-eval":
			{
				partitions.Evaluate();
				PartitionMembership.Change[] changes = partitions.LastChanges;
				return new
				{
					task,
					splits = changes.Where(change => change.Split && !change.Transfer).Select(change => new
						{ input = change.Inputs.Single(), children = change.Outputs.Select(output => output.Id).ToArray() }).ToArray(),
					// T-0339 (design C): Inputs = [source, target], both kept.
					transfers = changes.Where(change => change.Transfer).Select(change => new
						{ source = change.Inputs[0], target = change.Inputs[1], moved = change.Moved }).ToArray(),
					merges = changes.Where(change => !change.Split).Select(change => new
						{ inputs = change.Inputs, output = change.Outputs.Single().Id }).ToArray(),
					partitions = partitions.Evaluate().Select(partition => partition.Id).ToArray()
				};
			}
			case "save":
			{
				IEnumerable<ulong> targets = partitionFilter is ulong only
					? new[] { only }
					: partitions.Evaluate().Select(partition => partition.Id).ToArray();
				var queued = targets.Select(id => new { partition = id, accepted = persistence.QueueTestCapture(id) }).ToArray();
				return new { task, queued };
			}
			case "conceal-eval":
				return new { task, concealment = persistence.TriggerConcealEval() };
			case "compact":
				return new { task, compacted = VoxelJournalService.Instance?.Compact() };
			default:
				throw new KeyNotFoundException("Unknown maintenance task " + task);
		}
	}
}
