using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using Sandbox.Common.ObjectBuilders;

namespace ClusterPlugin;

internal static partial class ClusterSelfTest
{
	/// <summary>
	/// #1273 (race F31): node A's Set(Z) is applied at the World Authority but its answer is lost, node B's Remove(Z)
	/// follows, then A's outbox retries. The World Authority side is the real command ledger in front of the real set
	/// change, on a set of the test's own. Green: the retry goes under the queued operation id, the ledger answers it
	/// from the first attempt, and Z stays removed. KNOWN POSITIVE: the same sequence with a new id per attempt (the
	/// code before #1273) brings Z back. The answer is offered while the change is still queued, so the game thread
	/// cannot apply an older set in between.
	/// </summary>
	private static void AdminSafeZoneRetry()
	{
		void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("admin-safezone-retry: " + message); }
		const long Z = 0x7E57_0000_0127_3001;
		string zoneXml = AdminSafeZones.ToXml(new MyObjectBuilder_SafeZone { EntityId = Z, SafeZoneBlockId = 0, Radius = 50 });

		bool Run(bool freshIdPerAttempt, out List<Guid> attempts)
		{
			var zones = new SortedDictionary<long, string>();
			long version = 0;
			string WorldAuthority(AdminSafeZones.Request request, Guid operationId)
			{
				GlobalCommandResult result = GlobalCommandLedger.Execute("self-test-admin-safezones", operationId, JsonSerializer.Serialize(request), () =>
				{
					if (AdminSafeZones.Change(zones, request)) version++;
					return new GlobalCommandResult(true, version.ToString(), string.Empty);
				});
				if (!result.Success) throw new InvalidOperationException(result.Error);
				return result.Payload;
			}

			var outbox = new List<AdminSafeZones.Queued> { new AdminSafeZones.Queued(new AdminSafeZones.Request("Set", Z, zoneXml), Guid.NewGuid()) };
			var sent = new List<Guid>();
			bool dropAnswer = true;
			string Send(AdminSafeZones.Request request, Guid operationId)
			{
				Guid id = freshIdPerAttempt ? Guid.NewGuid() : operationId;
				sent.Add(id);
				string answer = WorldAuthority(request, id);
				if (dropAnswer) throw new TimeoutException("World Authority command timed out");
				return answer;
			}
			var offered = new List<string>();
			void Offer(string payload)
			{
				Check(Monitor.IsEntered(AdminSafeZones.Gate) && outbox.Count == 1, "the answer is offered while the change is still queued");
				offered.Add(payload);
			}

			Check(AdminSafeZones.FlushOutbox(outbox, Send, Offer), "a lost answer defers the flush");
			Check(outbox.Count == 1 && offered.Count == 0 && zones.ContainsKey(Z), "A's Set applied at the World Authority, still queued at A");
			WorldAuthority(new AdminSafeZones.Request("Remove", Z), Guid.NewGuid());
			Check(!zones.ContainsKey(Z) && version == 2, "B's Remove applied after A's Set");
			dropAnswer = false;
			Check(!AdminSafeZones.FlushOutbox(outbox, Send, Offer), "the retry is answered");
			Check(outbox.Count == 0 && offered.Count == 1, "the answered change leaves the outbox once");
			attempts = sent;
			if (!freshIdPerAttempt) Check(offered[0] == "1", "the ledger answers the retry from the first attempt (version 1), got " + offered[0]);
			return zones.ContainsKey(Z);
		}

		Check(!Run(false, out List<Guid> attempts), "Z stays removed after A's retry");
		Check(attempts.Count == 2 && attempts[0] == attempts[1], "both attempts go under the queued operation id");
		Check(Run(true, out attempts), "KNOWN POSITIVE: a new operation id per attempt brings Z back");
	}
}
