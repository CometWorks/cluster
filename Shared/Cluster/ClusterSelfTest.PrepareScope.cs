using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ClusterPlugin;

internal static partial class ClusterSelfTest
{
	/// <summary>
	/// T-0404 import.prepareScope: the transferred root ids are the text before the first '/' of each decoded identity
	/// (a root replicable's own identity is its bare EntityId); malformed entries are skipped here (the entry loop
	/// reports them). The comparator counts an identity whose replicable differs between the maps, including present
	/// in one and absent in the other. Live equivalence on real trees is prepare_scope_check (mode check).
	/// </summary>
	private static void PrepareScopeRules()
	{
		void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("prepare-scope: " + message); }
		string Entry(string identity) => Convert.ToBase64String(Encoding.UTF8.GetBytes(identity)) + ":Some.Type, Asm:1,2";
		string state = string.Join(";", Entry("72057594037927936"), Entry("72057594037927936/QUJD.REVG"), Entry("144115188075855873/eHl6.MTIz"),
			"not-base64!:x:1", ";", Entry("garbage/abc"));
		HashSet<long> roots = ReplicationNetworkIdPatch.TransferredRoots(state);
		Check(roots.SetEquals(new long[] { 72057594037927936, 144115188075855873 }), "roots: " + string.Join(",", roots));
		Check(ReplicationNetworkIdPatch.TransferredRoots("").Count == 0, "empty state, no roots");
		object a = new object(), b = new object();
		var full = new Dictionary<string, object>();
		var scoped = new Dictionary<string, object>();
		string[] ids = { "1", "1/x", "2" };
		Check(ReplicationNetworkIdPatch.Mismatches(ids, full, scoped) == 0, "both empty: 0");
		full["1"] = a; scoped["1"] = a; full["2"] = b; scoped["2"] = b; full["9"] = b;   // "9" is not a transferred identity
		Check(ReplicationNetworkIdPatch.Mismatches(ids, full, scoped) == 0, "same replicables, extra untransferred identities ignored");
		scoped.Remove("2");
		Check(ReplicationNetworkIdPatch.Mismatches(ids, full, scoped) == 1, "KNOWN POSITIVE: a present identity missing from the scoped map counts");
		scoped["2"] = a;
		Check(ReplicationNetworkIdPatch.Mismatches(ids, full, scoped) == 1, "KNOWN POSITIVE: another replicable for the same identity counts");
		scoped["1/x"] = b;
		Check(ReplicationNetworkIdPatch.Mismatches(ids, full, scoped) == 2, "present only in the scoped map counts too");
	}
}
