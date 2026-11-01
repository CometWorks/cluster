using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using static ClusterPlugin.ShipStorageRecord;
using Index = ClusterPlugin.ShipStorageRecord.Index;

namespace ClusterPlugin;

internal static partial class ClusterSelfTest
{
	/// <summary>
	/// #1269 (race F27): a ship take must not outlive its retrieval. Runs the real World Authority command and blueprint
	/// handlers and the take expiry of <see cref="ShipStorageService"/> on a regular node, whose World Authority state is
	/// otherwise unused, with a ship of the test's own. (1) Node N's Take is admitted and its answer dropped: N queues a
	/// Release, and once it is delivered the ship is listed again and node M can take it. KNOWN POSITIVE: without the
	/// Release (the code before #1269) the ship stays unlisted and M's Take is refused. A refused Take queues nothing.
	/// (2) N dies between its Take and the blueprint fetch: the take is released after the unfetched life, not before.
	/// (3) The fetch and the expiry exclude each other: a released take cannot be fetched, and a fetched take is never
	/// released, only reported once. (4) A take restored from the global save gets a fresh life; one saved before #1269
	/// counts as fetched. (5) #1316: the admin list shows a fetched take as stranded once it is held past the report time,
	/// an unfetched one never; an admin settles only the exact stranded take, release lists the ship again, consume drops
	/// it, and the node's late Consume of a settled take changes nothing.
	/// </summary>
	private static void ShipStorageTakes()
	{
		void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("ship-storage-takes: " + message); }
		Check(!ClusterRuntime.IsWorldAuthority, "runs on a regular node: on the World Authority the expiry would touch the live takes");
		const string N = "self-test-node-n", M = "self-test-node-m";
		Guid id = Guid.Parse("7e570000-1269-4000-8000-000000000001");
		var takes = new List<Guid>();   // in issue order: takes[^1] is the latest

		Ship Seed()
		{
			var ship = new Ship(id, 1269, 0, new float[] { 1, 2, 3 }, new double[] { 10, 20, 30 }, new float[] { 0, 0, 0, 1 },
				"<MyObjectBuilder_SavedGridDetails><DisplayName>Self-test ship</DisplayName></MyObjectBuilder_SavedGridDetails>",
				Array.Empty<long>(), "blueprint");
			ShipStorageService.Apply(N, new ShipStorageService.Request("Store", id, ship), out _);
			return ship;
		}
		Ship Current() => JsonSerializer.Deserialize<Record>(ShipStorageService.PersistedRecord()).Ships.Single(ship => ship.Id == id);
		bool Listed() => JsonSerializer.Deserialize<Index>(ShipStorageService.Snapshot()).Ships.Any(ship => ship.Id == id);
		ShipStorageService.Request Take(Guid take) { takes.Add(take); return new ShipStorageService.Request("Take", id, Take: take); }
		bool Refused(Action action)
		{
			try { action(); return false; }
			catch (InvalidOperationException error) { return error.Message.Contains("not stored or already taken"); }
		}
		bool SettleRefused(Guid take, bool consume, DateTime at)
		{
			try { ShipStorageService.SettleTake(id, take, consume, at); return false; }
			catch (InvalidOperationException) { return true; }
		}
		ShipStorageService.HeldTake Held(DateTime at) => ShipStorageService.HeldTakes(at).Single(take => take.Id == id);
		DateTime Fetch()
		{
			ShipStorageService.Apply(N, Take(Guid.NewGuid()), out _);
			ShipStorageService.Blueprint(N, id.ToString());
			return Current().Taken + ShipStorageService.FetchedTakeReport;
		}
		void Clear()
		{
			// Delete refuses a taken ship; Hello from the holder releases it first.
			foreach (string node in new[] { N, M }) ShipStorageService.Apply(node, new ShipStorageService.Request("Hello", Guid.Empty), out _);
			ShipStorageService.Apply(N, new ShipStorageService.Request("Delete", id), out _);
		}

		bool DroppedAnswer(bool release)
		{
			Seed();
			var outbox = new List<ShipStorageService.Request>();
			bool timedOut = false;
			try
			{
				ShipStorageService.TakeAtAuthority(Take(Guid.NewGuid()), request =>
				{
					ShipStorageService.Apply(N, request, out _);
					throw new TimeoutException("World Authority command timed out");
				}, release ? outbox.Add : _ => { });
			}
			catch (TimeoutException) { timedOut = true; }
			Check(timedOut, "N hears the timeout");
			Check(Current().TakenBy == N && !Listed(), "the World Authority admitted N's take and unlisted the ship");
			if (release)
			{
				Check(outbox.Count == 1 && outbox[0].Op == "Release" && outbox[0].Take == takes[^1], "N queued a Release of its take");
				foreach (ShipStorageService.Request queued in outbox) ShipStorageService.Apply(N, queued, out _);
			}
			bool listed = Listed() && !Refused(() => ShipStorageService.Apply(M, Take(Guid.NewGuid()), out _));
			Clear();
			return listed;
		}

		try
		{
			// (1) the F27 interleaving, forced.
			Check(DroppedAnswer(release: true), "after N's Release the ship is listed again and M takes it");
			Check(!DroppedAnswer(release: false), "KNOWN POSITIVE: without the Release the ship stays unlisted and M's Take is refused");
			Seed();
			ShipStorageService.Apply(M, Take(Guid.NewGuid()), out _);
			var refusedOutbox = new List<ShipStorageService.Request>();
			Check(Refused(() => ShipStorageService.TakeAtAuthority(Take(Guid.NewGuid()),
				request => ShipStorageService.Apply(N, request, out _), refusedOutbox.Add)), "N's Take of M's ship is refused");
			Check(refusedOutbox.Count == 0, "a refused Take queues no Release");
			Clear();

			// (2) N dies between its Take and the blueprint fetch.
			Seed();
			ShipStorageService.Apply(N, Take(Guid.NewGuid()), out _);
			DateTime taken = Current().Taken;
			Check(taken != default && !Current().Fetched, "the take carries its time and is not fetched");
			Check(ShipStorageService.ExpireTakes(taken + ShipStorageService.UnfetchedTakeLife - TimeSpan.FromSeconds(1)).Released == 0
				&& Current().TakenBy == N, "an unfetched take is kept within its life");
			Check(ShipStorageService.ExpireTakes(taken + ShipStorageService.UnfetchedTakeLife).Released == 1 && Listed()
				&& Current().TakenBy == null && Current().Taken == default, "an unfetched take is released after its life and the ship listed");
			// (3) the expiry released it: N's late fetch is refused, so N cannot paste a ship that is stored again.
			bool fetchRefused = false;
			try { ShipStorageService.Blueprint(N, id.ToString()); }
			catch (InvalidOperationException) { fetchRefused = true; }
			Check(fetchRefused, "the fetch of a released take is refused");
			ShipStorageService.Apply(N, Take(Guid.NewGuid()), out _);
			Check(ShipStorageService.Blueprint(N, id.ToString()) == "blueprint" && Current().Fetched, "the fetch marks the take");
			DateTime late = Current().Taken + ShipStorageService.FetchedTakeReport;
			Check(ShipStorageService.ExpireTakes(late) == (0, 1) && Current().TakenBy == N && !Listed(),
				"a fetched take is kept past the report time and reported");
			Check(ShipStorageService.ExpireTakes(late + TimeSpan.FromDays(1)) == (0, 0) && Current().TakenBy == N, "and reported once");
			ShipStorageService.Apply(N, new ShipStorageService.Request("Consume", id, Take: takes[^1]), out _);
			Check(!JsonSerializer.Deserialize<Record>(ShipStorageService.PersistedRecord()).Ships.Any(ship => ship.Id == id),
				"N's Consume settles the fetched take");

			// (4) the global save round trip.
			DateTime now = DateTime.UtcNow;
			Ship stored = Seed() with { TakenBy = N, Take = Guid.NewGuid(), Taken = now - TimeSpan.FromHours(1) };
			Check(ShipStorageService.Restored(stored, now) == stored with { Taken = now }, "a restored unfetched take gets a fresh life");
			Check(ShipStorageService.Restored(stored with { Fetched = true }, now) == stored with { Taken = now, Fetched = true },
				"a restored fetched take stays fetched");
			Check(ShipStorageService.Restored(stored with { Taken = default }, now) == stored with { Taken = now, Fetched = true },
				"a take saved before #1269 counts as fetched");
			Check(ShipStorageService.Restored(stored with { TakenBy = null, Take = default, Taken = default }, now)
				== stored with { TakenBy = null, Take = default, Taken = default }, "a ship that is not taken is restored as it was");
			Clear();

			// (5) #1316: the admin settles a stranded take.
			Seed();
			ShipStorageService.Apply(N, Take(Guid.NewGuid()), out _);
			Check(!Held(Current().Taken + TimeSpan.FromDays(1)).Stranded, "an unfetched take is never stranded (the expiry releases it)");
			ShipStorageService.Apply(N, new ShipStorageService.Request("Release", id, Take: takes[^1]), out _);
			DateTime stranded = Fetch();
			ShipStorageService.HeldTake held = Held(stranded);
			Check(!Held(stranded - TimeSpan.FromSeconds(1)).Stranded && held.Stranded && held.Fetched && held.TakenBy == N && held.Take == takes[^1]
				&& held.Owner == 1269 && held.Name == "Self-test ship" && held.Position.SequenceEqual(new double[] { 11, 22, 33 }),
				"the list shows the held take, stranded once fetched and held past the report time");
			Check(SettleRefused(takes[^1], false, stranded - TimeSpan.FromSeconds(1)) && Current().TakenBy == N && !Listed(),
				"a take that is not stranded yet is not settled; the ship stays unlisted");
			Check(SettleRefused(Guid.NewGuid(), false, stranded) && Current().TakenBy == N, "nor another take than the one held");
			ShipStorageService.SettleTake(id, takes[^1], consume: false, stranded);
			Check(Listed() && Current().TakenBy == null && !ShipStorageService.HeldTakes(stranded).Any(take => take.Id == id),
				"release lists the ship again");
			ShipStorageService.Apply(N, new ShipStorageService.Request("Consume", id, Take: takes[^1]), out bool lateConsume);
			Check(!lateConsume && Listed(), "N's late Consume of the released take changes nothing");
			Check(SettleRefused(takes[^1], true, stranded), "the settled take cannot be settled again");
			stranded = Fetch();
			ShipStorageService.SettleTake(id, takes[^1], consume: true, stranded);
			Check(!JsonSerializer.Deserialize<Record>(ShipStorageService.PersistedRecord()).Ships.Any(ship => ship.Id == id) && !Listed(),
				"consume drops the ship");
		}
		finally { Clear(); }
	}
}
