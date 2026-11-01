using System;
using System.Collections.Generic;
using System.Linq;

namespace ClusterPlugin;

internal static partial class ClusterSelfTest
{
	/// T-0296 r3: a handover import KEEPS an entity a connected client controls on this node (T-0169, and the
	/// live-controlled rule next to it) and the import's Adopt moves it into the arriving layout. When the handover
	/// is then discarded, that layout is not owned here: it is locked, its saves are refused as fenced, and after
	/// three refusals the fenced-orphan purge closes "the leftover incarnation" - the player's live body. The discard
	/// must hand the kept member back. The control runs the discard as it was and must show the defect.
	/// Plain membership data, no game types: Tools and the offline harness run it as well as the node.
	private static void MembershipDiscard()
	{
		const ulong Arriving = 900;
		void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("membership-discard: " + message); }
		PartitionMembership.Member At(long id, double x) => new PartitionMembership.Member(id, x, 0, 0, 0, 0, 0);
		string Shape(PartitionMembership membership) => string.Join(" | ", membership.Layouts.OrderBy(layout => layout.Id)
			.Select(layout => layout.Id + ":" + string.Join(",", layout.Members.OrderBy(id => id))));

		(PartitionMembership Membership, ulong Home, HashSet<ulong> Owned) Run(bool handBack, bool homeGone)
		{
			HashSet<ulong> owned = new HashSet<ulong>();
			ulong next = 1;
			PartitionMembership membership = new PartitionMembership(3000, 300, count =>
			{
				ulong[] ids = Enumerable.Range(0, count).Select(_ => next++).ToArray();
				owned.UnionWith(ids);   // the live allocator claims every id it hands out
				return ids;
			}, id => !owned.Contains(id));   // T-0307: a layout this node does not own is locked
			// The live body 1 beside its ship 2 (one owned layout), something else far away.
			membership.Evaluate(new[] { At(1, 0), At(2, 100), At(3, 50000) }, 0);
			ulong home = membership.Layouts.Single(layout => layout.Members.Contains(1)).Id;
			// The import of partition 900 arrives with a stale copy of 1 (kept: the live one stays) and creates 10, 11.
			membership.Adopt(Arriving, new long[] { 1, 10, 11 }, 10, mature: false);
			Check(membership.Layouts.Single(layout => layout.Id == Arriving).Members.Contains(1), "the import adopts the kept live body into the arriving layout");
			if (homeGone) membership.Remove(home);   // its ship left meanwhile
			// The handover is discarded: the created 10 and 11 are closed; the arriving layout was never owned here.
			if (handBack) membership.Return(Arriving, new Dictionary<long, ulong> { [1] = home });
			PartitionMembership.Member[] census = homeGone ? new[] { At(1, 0), At(3, 50000) } : new[] { At(1, 0), At(2, 100), At(3, 50000) };
			membership.Evaluate(census, 20);
			membership.Evaluate(census, 400);
			return (membership, home, owned);
		}

		var control = Run(handBack: false, homeGone: false);
		PartitionMembership.Layout stranded = control.Membership.Layouts.SingleOrDefault(layout => layout.Members.Contains(1));
		Check(stranded != null && stranded.Id == Arriving && !control.Owned.Contains(stranded.Id),
			"control: without the hand-back the live body must be left in the unowned arriving layout (the defect): " + Shape(control.Membership));

		var fixedRun = Run(handBack: true, homeGone: false);
		PartitionMembership.Layout holder = fixedRun.Membership.Layouts.SingleOrDefault(layout => layout.Members.Contains(1));
		Check(holder != null && holder.Id == fixedRun.Home && holder.Members.Contains(2),
			"after the discard the live body is back in the layout that held it, with its ship: " + Shape(fixedRun.Membership));
		Check(fixedRun.Membership.Layouts.All(layout => layout.Id != Arriving), "the discarded arriving layout is gone: " + Shape(fixedRun.Membership));

		var orphaned = Run(handBack: true, homeGone: true);
		holder = orphaned.Membership.Layouts.SingleOrDefault(layout => layout.Members.Contains(1));
		Check(holder != null && holder.Id != Arriving && orphaned.Owned.Contains(holder.Id),
			"a kept body whose previous layout is gone is placed in an owned layout by the next pass: " + Shape(orphaned.Membership));
	}
}
