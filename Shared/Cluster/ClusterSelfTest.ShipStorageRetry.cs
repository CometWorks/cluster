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
	/// #1317: a ship storage command whose answer is lost must not leave the node and the World Authority disagreeing.
	/// Runs <see cref="ShipStorageService.Submit"/> and <see cref="ShipStorageService.FlushOutbox"/> against the real
	/// World Authority handler behind a command ledger, on a regular node whose World Authority state is otherwise unused.
	/// (1) A Store applied with its answer dropped: the node keeps the grids and queues an Unstore, after which the ship
	/// is stored nowhere and its grids are not listed for the sweep. KNOWN POSITIVE: without the Unstore (the code before
	/// #1317) the ship is stored while its grids stay in the world, a duplicate. (2) An Unstore that overtakes its Store
	/// makes the World Authority refuse the Store; a refusal queues nothing. (3) An Order applied with its answer dropped
	/// is retried under its operation id and the ledger answers from the first attempt. KNOWN POSITIVE: a fresh id is
	/// refused (the ship is ordered already) and the node would take back an order the World Authority keeps. (4) A Delete
	/// likewise. (5) Which errors are a lost answer.
	/// </summary>
	private static void ShipStorageRetry()
	{
		void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("ship-storage-retry: " + message); }
		Check(!ClusterRuntime.IsWorldAuthority, "runs on a regular node: on the World Authority it would touch the live ships");
		const string N = "self-test-node-n";
		const string Ledger = "self-test-ship-storage";
		long nextOrigin = 0x7E57_0000_1317_0000;
		var used = new List<Guid>();

		Ship NewShip(Guid id, int origins) => new Ship(id, 1317, 0, new float[3], new double[3], new float[] { 0, 0, 0, 1 }, "<details/>",
			Enumerable.Range(0, origins).Select(_ => ++nextOrigin).ToArray(), "blueprint");
		Guid NewId() { Guid id = Guid.NewGuid(); used.Add(id); return id; }
		bool Stored(Guid id) => JsonSerializer.Deserialize<Record>(ShipStorageService.PersistedRecord()).Ships.Any(ship => ship.Id == id);
		bool Swept(long[] origins) => JsonSerializer.Deserialize<Index>(ShipStorageService.Snapshot()).Origins.Any(origins.Contains);
		int Orders(Guid id) => JsonSerializer.Deserialize<Record>(ShipStorageService.PersistedRecord()).Users?
			.Sum(user => user.Orders.Count(order => order.Id == id)) ?? 0;
		Func<ShipStorageService.Request, Guid, string> WorldAuthority(bool dropAnswer, bool freshId = false) => (request, operation) =>
		{
			GlobalCommandResult result = GlobalCommandLedger.Execute(Ledger, freshId ? Guid.NewGuid() : operation, JsonSerializer.Serialize(request),
				() => new GlobalCommandResult(true, ShipStorageService.Apply(N, request, out _) ?? string.Empty, string.Empty));
			if (!result.Success) throw new InvalidOperationException(result.Error);
			if (dropAnswer) throw new TimeoutException("World Authority command timed out");
			return result.Payload;
		};
		bool Throws<T>(Func<string> action) where T : Exception
		{
			try { action(); return false; }
			catch (T) { return true; }
		}

		// (1) and its known positive: true when the node and the World Authority agree after the lost answer.
		bool StoreLost(bool unstore)
		{
			Guid id = NewId();
			Ship ship = NewShip(id, 2);
			var outbox = new List<ShipStorageService.Pending>();
			bool spared = false;
			Check(Throws<TimeoutException>(() => ShipStorageService.Submit(new ShipStorageService.Request("Store", id, ship),
				WorldAuthority(dropAnswer: true), outbox.Add, () => spared = true)), "the node hears the timeout");
			Check(Stored(id) && Swept(ship.Origins), "the World Authority stored the ship and lists its grids for the sweep");
			Check(spared && outbox.Count == 1 && outbox[0].Request.Op == "Unstore" && outbox[0].Request.Id == id,
				"the node spares its grids and queued an Unstore of the ship");
			if (unstore)
				Check(!ShipStorageService.FlushOutbox(outbox, WorldAuthority(dropAnswer: false), () => Check(false, "the Unstore is not refused")),
					"the Unstore is delivered");
			return !Stored(id) && !Swept(ship.Origins);
		}

		// (3) and its known positive: true when the retried order is the World Authority's one order of the ship.
		bool OrderLost(bool freshId)
		{
			Guid id = NewId();
			ShipStorageService.Apply(N, new ShipStorageService.Request("Store", id, NewShip(id, 0)), out _);
			var order = new ShipStorageService.Request("Order", id, Identity: 1317, Order: new Order(id, "self-test", 60000, 0, 0, 0));
			var outbox = new List<ShipStorageService.Pending>();
			Check(Throws<TimeoutException>(() => ShipStorageService.Submit(order, WorldAuthority(dropAnswer: true), outbox.Add)), "the node hears the timeout");
			Check(Orders(id) == 1 && outbox.Count == 1 && outbox[0].Request == order, "the World Authority keeps the order; the node queued it again");
			bool refused = false;
			Check(!ShipStorageService.FlushOutbox(outbox, WorldAuthority(dropAnswer: false, freshId), () => refused = true) && outbox.Count == 0,
				"the retry is answered");
			return !refused && Orders(id) == 1;
		}

		try
		{
			// (1)
			Check(StoreLost(unstore: true), "after the Unstore the ship is stored nowhere and its grids are not swept");
			Check(!StoreLost(unstore: false), "KNOWN POSITIVE: without the Unstore the ship is stored and its grids stay listed (duplicate)");

			// (2) the Unstore overtakes its Store on the unordered channel.
			Guid late = NewId();
			ShipStorageService.Apply(N, new ShipStorageService.Request("Unstore", late), out bool changed);
			Check(!changed, "an Unstore of a ship never stored changes nothing");
			var refusedOutbox = new List<ShipStorageService.Pending>();
			Exception refusal = null;
			try { ShipStorageService.Submit(new ShipStorageService.Request("Store", late, NewShip(late, 1)), WorldAuthority(dropAnswer: false), refusedOutbox.Add); }
			catch (Exception error) { refusal = error; }
			Check(refusal is InvalidOperationException && refusal.Message.Contains("was unstored") && !Stored(late),
				"the World Authority refuses a Store after its Unstore");
			Check(refusedOutbox.Count == 0 && !ShipStorageService.IsLostAnswer(refusal), "a refusal queues nothing");

			// (3)
			Check(OrderLost(freshId: false), "a retried Order keeps its operation id and the ledger answers from the first attempt");
			Check(!OrderLost(freshId: true), "KNOWN POSITIVE: a fresh id is refused, the node would take back the World Authority's order");

			// (4)
			Guid deleted = NewId();
			ShipStorageService.Apply(N, new ShipStorageService.Request("Store", deleted, NewShip(deleted, 0)), out _);
			var deleteOutbox = new List<ShipStorageService.Pending>();
			Check(Throws<TimeoutException>(() => ShipStorageService.Submit(new ShipStorageService.Request("Delete", deleted),
				WorldAuthority(dropAnswer: true), deleteOutbox.Add)) && !Stored(deleted), "the Delete applied, its answer lost");
			Check(deleteOutbox.Count == 1 && deleteOutbox[0].Request.Op == "Delete", "the node queued the Delete again");
			string replayed = null;
			ShipStorageService.FlushOutbox(deleteOutbox, (request, id) => replayed = WorldAuthority(dropAnswer: false)(request, id),
				() => Check(false, "the Delete is not refused"));
			Check(replayed == "true" && deleteOutbox.Count == 0, "the retried Delete is answered from the first attempt: deleted");

			// (5)
			foreach (Exception lost in new Exception[]
			{
				new TimeoutException("World Authority command timed out"), new InvalidOperationException("World Authority generation changed"),
				new InvalidOperationException("World Authority is unavailable"), new InvalidOperationException("Global-state node link is disconnected"),
			})
				Check(ShipStorageService.IsLostAnswer(lost), "a lost answer: " + lost.Message);
			foreach (string refused in new[]
			{
				"ship is not stored or already taken", "Ship x is stored already", "Ship x was unstored: its node lost the answer",
				"Operation ID was reused with a different payload", "Invalid stored ship x",
			})
				Check(!ShipStorageService.IsLostAnswer(new InvalidOperationException(refused)), "a refusal: " + refused);
		}
		finally
		{
			foreach (Guid id in used) ShipStorageService.Apply(N, new ShipStorageService.Request("Unstore", id), out _);
		}
	}
}
