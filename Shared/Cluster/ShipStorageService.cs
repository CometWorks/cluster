using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using PluginSdk.Clustering;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using SpaceEngineers.Game.SessionComponents;
using VRage.Game;
using VRage.Network;
using VRage.ObjectBuilders.Private;
using VRageMath;
using MyGridRetrievalOrder = Sandbox.ModAPI.MyGridRetrievalOrder;
using MyGridStorageRequestResult = Sandbox.ModAPI.MyGridStorageRequestResult;
using static ClusterPlugin.ShipStorageRecord;
using Index = ClusterPlugin.ShipStorageRecord.Index;

namespace ClusterPlugin;

/// <summary>
/// SE1-0233: ship storage (the Services terminal, <see cref="MyGridsStorageSessionComponent"/>) is World Authority
/// state. Vanilla keeps the index in the session component and the stored blueprints as <c>gs-&lt;id&gt;*.sbc</c> files
/// in the session folder, so on a cluster both stayed on the node that stored the ship: a handover hid it and a
/// restart of that node lost it. Stored ships belong to a player, not to a terminal (any terminal lists them), so
/// they cannot travel with one partition.
/// <para>
/// The World Authority keeps every stored ship (its index entry and its blueprint, gzipped) and persists them in
/// the global save. It publishes the index without blueprints, and each node writes it into its session component,
/// so the vanilla listing, limits and validation run unchanged. A store uploads the blueprint vanilla just wrote and
/// is undone when the World Authority does not take it. A retrieval first takes the ship at the World Authority, which
/// only one caller can, fetches the blueprint into the files vanilla loads, and then consumes or releases it.
/// </para>
/// <para>
/// Crash windows. A node that dies between a store and the next save of the stored grid's partition brings the grid
/// back from that save: every node closes a grid whose entity id belongs to a stored (or recently retrieved or
/// deleted) ship. A node that dies during a retrieval leaves the ship taken; the node releases what it held when it
/// starts again.
/// </para>
/// <para>
/// #1269 (race F27): a take must not outlive its retrieval. A node whose Take got no answer releases it, since the
/// World Authority may have admitted it. A node that dies first, or a Release that overtakes its Take, leaves a take
/// whose blueprint nobody read: the World Authority releases it after <see cref="UnfetchedTakeLife"/>, which is safe
/// whatever the node does, as the fetch and the release exclude each other under the lock. Once the blueprint was read,
/// the ship may be pasted and in a partition save, and releasing it could duplicate the ship, so a take held past
/// <see cref="FetchedTakeReport"/> stays taken and is reported once for an admin to decide. #1316: the admin lists the
/// held takes and settles a stranded one (<see cref="SettleTake"/>) through the Gateway admin API.
/// </para>
/// <para>
/// #1317: a command the player waits for (Store, Delete, Share, Order) is sent once, under an operation id of its
/// own. An answer that does not come back (a timeout, a new World Authority generation, the link down) says nothing
/// about whether the World Authority applied it, so the node no longer reads it as a refusal. Delete, Share and Order
/// go to the outbox under the same operation id, so the World Authority's command ledger answers from the first
/// attempt if it was applied and applies it if not. A Store's grids stay, as before, and the outbox carries an Unstore
/// of the ship: the World Authority drops it without a tombstone, and refuses a Store that arrives after its Unstore
/// (commands go on the unordered channel). Until then the node's sweep spares those grids, which the index lists as a
/// stored ship's. A node that restarts before its Unstore is delivered loses it, and the sweep closes the grids: the
/// ship is stored, once.
/// </para>
/// <para>
/// SE1-0248: the delayed retrieval orders and the auto-deploy switch of each player live next to the ships and ride
/// the same index, so every node shows an ordered ship in transition and only its order can take it. Only the node
/// that holds an order's terminal runs it; a terminal removed from its grid cancels its orders, as in vanilla, while a
/// grid that closes may only have moved, so its orders stay until the player cancels them. The combat cooldown of a
/// grid travels with the grid in its handover; a restart or an offline round trip still drops it.
/// </para>
/// </summary>
internal static class ShipStorageService
{
	private const string Subsystem = "ship-storage";
	private const string BlueprintSubsystem = "ship-storage-blueprint";
	private const string NotStored = "ship is not stored or already taken";
	private const int MaxBlueprintChars = 48 * 1024 * 1024;
	private const int CommandTimeoutMilliseconds = 10000;
	private static readonly TimeSpan TombstoneLife = TimeSpan.FromDays(1);
	internal static readonly TimeSpan UnfetchedTakeLife = TimeSpan.FromMinutes(2);
	internal static readonly TimeSpan FetchedTakeReport = TimeSpan.FromMinutes(10);

	/// <summary>Ordered: a take that runs an order. AutoDeploy: the switch's new state.</summary>
	public sealed record Request(string Op, Guid Id, Ship Ship = null, Guid Take = default, int Kind = 0, long Identity = 0,
		Order Order = null, bool Ordered = false, bool AutoDeploy = false, long Terminal = 0);
	internal sealed record Pending(Request Request, Guid Operation);

	private static readonly object Sync = new object();

	// World Authority
	private static readonly Dictionary<Guid, Ship> Ships = new Dictionary<Guid, Ship>();
	private static readonly List<Tombstone> Tombstones = new List<Tombstone>();
	private static readonly Dictionary<long, User> Users = new Dictionary<long, User>();
	private static readonly HashSet<Guid> ReportedTakes = new HashSet<Guid>();
	/// <summary>#1317: ships unstored, until when; a Store of one is refused.</summary>
	private static readonly Dictionary<Guid, DateTime> Unstored = new Dictionary<Guid, DateTime>();
	private static bool s_migrate;

	// Regular node
	private static string s_index, s_applied;
	private static HashSet<long> s_origins = new HashSet<long>();
	private static readonly Dictionary<Guid, MyObjectBuilder_SavedGridDetails> DetailsCache = new Dictionary<Guid, MyObjectBuilder_SavedGridDetails>();
	private static readonly Dictionary<Guid, Guid> Takes = new Dictionary<Guid, Guid>();
	private static readonly List<Pending> Outbox = new List<Pending>();
	/// <summary>#1317: the grids of a store whose answer was lost, which the sweep spares until the Unstore is through.</summary>
	private static readonly Dictionary<Guid, long[]> Unstoring = new Dictionary<Guid, long[]>();
	private static bool s_registered;
	private static double s_nextRegister, s_nextFlush, s_nextSweep;

	private static bool Active => ClusterRuntime.IsClustered && !ClusterRuntime.IsWorldAuthority;

	internal static void Install()
	{
		GlobalStateChannel.RegisterCommandHandler(Subsystem, Handle, durable: true);
		GlobalStateChannel.RegisterQueryHandler(BlueprintSubsystem, Blueprint);
		GlobalStateChannel.RegisterReplicaHandler(Subsystem, ReceiveIndex);
		GlobalStateChannel.RegisterSnapshot(Subsystem, Snapshot, ReceiveIndex);
		PluginHandoverService.Register("cluster.storage-combat", CombatHandover);
	}

	internal static void Update()
	{
		if (!ClusterRuntime.IsClustered || MySession.Static?.Ready != true) return;
		double now = ClusterRuntime.LocalMilliseconds;
		if (ClusterRuntime.IsWorldAuthority)
		{
			if (s_migrate) Migrate();
			if (now >= s_nextSweep)
			{
				s_nextSweep = now + 5000;
				if (ExpireTakes(DateTime.UtcNow).Released > 0) Changed();
			}
			return;
		}
		if (!s_registered && now >= s_nextRegister && ClusterRuntime.AuthorityGeneration > 0) Register(now);
		if (now >= s_nextFlush) Flush();
		ApplyIndex();
		if (now >= s_nextSweep)
		{
			s_nextSweep = now + 5000;
			Sweep();
		}
	}

	// -- regular node: hooks called by Shared.Patches.MyGridsStorageSessionComponentPatch -------------------------

	/// <summary>Session component Init prefix: a node's world file index is not the cluster's, the WA's is.</summary>
	internal static void Initializing(MyObjectBuilder_GridsStorageSessionComponent builder)
	{
		if (!Active) return;
		builder?.GridsData?.Dictionary?.Clear();
		builder?.UserData?.Dictionary?.Clear();
	}

	/// <summary>StoreRequest.Dispatch postfix: upload what vanilla stored, or undo the store.</summary>
	internal static void Stored(MyGridsStorageSessionComponent storage, MyGridsStorageSessionComponent.StoreRequest request)
	{
		if (!Active) return;
		KeyValuePair<Guid, MyStoredGridData> entry = storage.m_storedGridsData.FirstOrDefault(pair => ReferenceEquals(pair.Value, request.GridData));
		if (entry.Value == null) return;   // vanilla failed and told the caller
		Guid id = entry.Key;
		long[] origins = storage.m_gridsToClose.Select(grid => grid.EntityId).ToArray();
		try
		{
			Ship ship = FromData(id, entry.Value, Pack(File.ReadAllBytes(SbcPath(id))), origins);
			Submit(new Request("Store", id, ship), Command, Outbox.Add, () => Unstoring[id] = origins);
			Console.WriteLine("[ClusterPlugin] ship storage stored id=" + id + " owner=" + ship.Owner
				+ " grids=" + string.Join(",", origins) + " blueprint=" + ship.Blueprint.Length);
		}
		catch (Exception error)
		{
			// The grids stay, the ship is not stored and the caller hears the vanilla failure.
			storage.m_storedGridsData.Remove(id);
			storage.m_gridsToClose.Clear();
			var others = storage.m_requestsToConclude.Where(other => other != request).ToArray();
			storage.m_requestsToConclude.Clear();
			foreach (var other in others) storage.m_requestsToConclude.Enqueue(other);
			request.Notify(MyGridStorageRequestResult.UnspecifiedStorageIssue);
			Console.WriteLine("[ClusterPlugin] ship storage store refused id=" + id + ": " + SecretRedactor.Redact(error.Message)
				+ (IsLostAnswer(error) ? " (answer lost: an Unstore is queued)" : ""));
		}
		finally { MyGridStorageHelper.Instance.Delete(id); }
	}

	/// <summary>RetrieveRequest.Dispatch prefix: take the ship at the WA and put its files where vanilla loads them.</summary>
	internal static bool Retrieving(MyGridsStorageSessionComponent.RetrieveRequest request)
	{
		if (!Active) return true;
		Guid id = request.Id, take = Guid.NewGuid();
		try
		{
			if (!s_registered) throw new InvalidOperationException("ship storage is not registered with the World Authority yet");
			// A take for an order this node runs (ProcessOrder marked it); any other take of an ordered ship is refused.
			bool ordered = MySession.Static.GetComponent<MyGridsStorageSessionComponent>()?.m_clientOrders.Values
				.Any(user => user.Orders.Any(order => order.Id == id && order.InProgress)) == true;
			TakeAtAuthority(new Request("Take", id, Take: take, Ordered: ordered), request => Command(request, Guid.NewGuid()), Send);
		}
		catch (Exception error)
		{
			Console.WriteLine("[ClusterPlugin] ship storage take refused id=" + id + ": " + SecretRedactor.Redact(error.Message));
			request.Notify(error.Message.Contains(NotStored) ? MyGridStorageRequestResult.GridNotFound
				: MyGridStorageRequestResult.UnspecifiedStorageIssue);
			return false;
		}
		try
		{
			string blueprint = GlobalStateChannel.SendCommand(BlueprintSubsystem, id.ToString(), CommandTimeoutMilliseconds);
			MyGridStorageHelper.Instance.Delete(id);
			File.WriteAllBytes(SbcPath(id), Unpack(blueprint));
			if (!MyObjectBuilderSerializerKeen.SerializeXML(Path.Combine(MySession.Static.CurrentPath, "gs-" + id + "-detail.sbc"),
				false, request.Data.SavedGridDetails))
				throw new IOException("the grid details did not serialize");
		}
		catch (Exception error)
		{
			Console.WriteLine("[ClusterPlugin] ship storage fetch failed id=" + id + ": " + SecretRedactor.Redact(error.Message));
			Finish(id, take, consumed: false);
			request.Notify(MyGridStorageRequestResult.UnspecifiedStorageIssue);
			return false;
		}
		Takes[id] = take;
		return true;
	}

	/// <summary>RetrieveRequest.Notify prefix: the outcome of a retrieval this node took.</summary>
	internal static void Retrieved(MyGridsStorageSessionComponent.RetrieveRequest request, MyGridStorageRequestResult result)
	{
		if (!Active || !Takes.Remove(request.Id, out Guid take)) return;
		Finish(request.Id, take, result == MyGridStorageRequestResult.Success);
		MyGridStorageHelper.Instance.Delete(request.Id);
	}

	/// <summary>DeleteGrid prefix: the player discards a stored ship.</summary>
	internal static bool Delete(MyGridsStorageSessionComponent storage, Guid id, ref bool result)
	{
		if (!Active) return true;
		try { result = Submit(new Request("Delete", id), Command, Outbox.Add) == "true"; }
		catch (Exception error)
		{
			Console.WriteLine("[ClusterPlugin] ship storage delete failed id=" + id + ": " + SecretRedactor.Redact(error.Message));
			result = false;
		}
		if (result) storage.m_storedGridsData.Remove(id);
		return false;
	}

	/// <summary>SetOwnershipKindEndpoint postfix: vanilla changed the replica, the WA keeps the change.</summary>
	internal static void Shared(Guid id, MyStoredGridOwnershipKind kind)
	{
		if (!Active) return;
		try { Submit(new Request("Share", id, Kind: (int)kind), Command, Outbox.Add); }
		catch (Exception error)
		{
			Console.WriteLine("[ClusterPlugin] ship storage share failed id=" + id + ": " + SecretRedactor.Redact(error.Message));
		}
	}

	/// <summary>EnqueueOrder postfix: the WA keeps the order vanilla just queued, or the last index takes it back.</summary>
	internal static void Enqueued(MyGridsStorageSessionComponent storage, long identity, Guid id, int queuedBefore)
	{
		if (!Active || storage.GetCurrentClientQueueLength(identity) <= queuedBefore) return;   // vanilla refused
		MyGridRetrievalOrder order = storage.m_clientOrders[identity].Orders.Last();
		try
		{
			Submit(new Request("Order", id, Identity: identity, Order: new Order(order.Id, order.DisplayName, order.CountdownMs,
				order.CreditsPaid, order.CallerEndpointId, order.CallerEntityId)), Command, Outbox.Add);
		}
		catch (Exception error) when (IsLostAnswer(error))
		{
			// The order stays queued here; the outbox delivers it, or takes it back if the World Authority refuses it.
			Console.WriteLine("[ClusterPlugin] ship storage order id=" + id + " queued for retry: " + SecretRedactor.Redact(error.Message));
		}
		catch (Exception error)
		{
			Console.WriteLine("[ClusterPlugin] ship storage order refused id=" + id + ": " + SecretRedactor.Redact(error.Message));
			lock (Sync) s_index ??= s_applied;
		}
	}

	/// <summary>RemoveOrder prefix: the player cancelled the head of the queue, or a node delivered it.</summary>
	internal static void Dequeuing(MyGridsStorageSessionComponent storage, long identity)
	{
		if (Active && storage.GetCurrentOrder(identity) is MyGridRetrievalOrder head)
			Send(new Request("Dequeue", head.Id, Identity: identity));
	}

	/// <summary>SetAutoDeploySwitch postfix: the player's switch, or vanilla turning it off when an order fails.</summary>
	internal static void AutoDeploySet(long identity, bool value)
	{
		if (Active) Send(new Request("AutoDeploy", Guid.Empty, Identity: identity, AutoDeploy: value));
	}

	/// <summary>ExpediteCurrentClientOrderEndpoint postfix: the player paid to shorten the head order's countdown.</summary>
	internal static void Expedited(MyGridsStorageSessionComponent storage)
	{
		if (!Active) return;
		long identity = MySession.Static.Players.TryGetIdentityId(MyEventContext.Current.Sender.Value);
		if (storage.GetCurrentOrder(identity) is { CreditsPaid: > 0 } order)
			Send(new Request("Expedite", order.Id, Identity: identity, Order: new Order(order.Id, order.DisplayName, order.CountdownMs,
				order.CreditsPaid, order.CallerEndpointId, order.CallerEntityId)));
	}

	/// <summary>
	/// ProcessOrder prefix: only the node that holds the order's terminal runs it. Vanilla elsewhere would find no
	/// terminal and drop the order.
	/// </summary>
	internal static bool Processing(MyGridRetrievalOrder order) => !Active || MyEntities.EntityExists(order.CallerEntityId);

	/// <summary>RemoveOrdersForTerminal prefix: <see cref="TerminalClosing"/> decides on a cluster node.</summary>
	internal static bool RemovingTerminalOrders() => !Active;

	/// <summary>
	/// The terminal's OnBlockEntityClosing prefix. A terminal removed from its grid (ground down, destroyed) cancels its
	/// orders, as in vanilla. A whole grid that closes may have moved to another node or gone offline, so those orders
	/// stay until the player cancels them. <see cref="ClusterCloseScope"/> could tell a deletion apart (SE1-0256).
	/// </summary>
	internal static void TerminalClosing(VRage.ModAPI.IMyEntity terminal)
	{
		if (Active && !MySession.Static.IsUnloading && terminal is MyCubeBlock { CubeGrid.MarkedForClose: false } block)
			Send(new Request("Unqueue", Guid.Empty, Terminal: block.EntityId));
	}

	/// <summary>OnIdentityCleanedUp postfix: identity trash runs on the World Authority only.</summary>
	internal static void IdentityRemoved(long identityId)
	{
		if (!ClusterRuntime.IsClustered || !ClusterRuntime.IsWorldAuthority) return;
		int removed;
		lock (Sync)
		{
			Guid[] owned = Ships.Values.Where(ship => ship.Owner == identityId && ship.TakenBy == null).Select(ship => ship.Id).ToArray();
			foreach (Guid id in owned) Remove(id);
			removed = owned.Length;
			if (Users.Remove(identityId)) removed++;
		}
		if (removed == 0) return;
		Changed();
		Console.WriteLine("[ClusterPlugin] ship storage removed " + removed + " ship(s) and queue(s) of trashed identity " + identityId);
	}

	private static void Register(double now)
	{
		try
		{
			string released = Command(new Request("Hello", Guid.Empty), Guid.NewGuid());
			s_registered = true;
			Console.WriteLine("[ClusterPlugin] ship storage registered; the WA released " + released + " ship(s) this node held before");
		}
		catch (Exception error)
		{
			s_nextRegister = now + 10000;
			Console.WriteLine("[ClusterPlugin] ship storage registration failed: " + SecretRedactor.Redact(error.Message));
		}
	}

	/// <summary>
	/// The Take of a retrieval. Only a refusal says the World Authority did not admit it; a lost answer (a timeout, a
	/// new World Authority generation) may follow an admitted take, so the node queues its Release, which the World
	/// Authority ignores for a take it does not hold (#1269).
	/// </summary>
	internal static void TakeAtAuthority(Request take, Func<Request, string> command, Action<Request> send)
	{
		try { command(take); }
		catch (Exception error) when (!error.Message.Contains(NotStored))
		{
			send(new Request("Release", take.Id, Take: take.Take));
			throw;
		}
	}

	/// <summary>
	/// #1317: a command the player waits for, sent once under an operation id of its own. A refusal throws as it is. A lost
	/// answer throws too, and first queues what makes the World Authority agree with the node: the same command under
	/// the same operation id (the ledger answers from the first attempt if that was applied), or for a Store, whose grids
	/// the node keeps, an Unstore of the ship (<paramref name="unstoring"/> runs first).
	/// </summary>
	internal static string Submit(Request request, Func<Request, Guid, string> command, Action<Pending> queue, Action unstoring = null)
	{
		var pending = new Pending(request, Guid.NewGuid());
		try { return command(request, pending.Operation); }
		catch (Exception error) when (IsLostAnswer(error))
		{
			if (request.Op == "Store")
			{
				unstoring?.Invoke();
				queue(new Pending(new Request("Unstore", request.Id), Guid.NewGuid()));
			}
			else queue(pending);
			throw;
		}
	}

	/// <summary>
	/// No answer, which may follow an applied command: a timeout, a new World Authority generation, the link down. A
	/// refusal by the World Authority (a handler error, a reused operation id) is not one.
	/// </summary>
	internal static bool IsLostAnswer(Exception error)
		=> error is TimeoutException || error.Message.Contains("World Authority") || error.Message.Contains("node link");

	/// <summary>Consume or release a take.</summary>
	private static void Finish(Guid id, Guid take, bool consumed) => Send(new Request(consumed ? "Consume" : "Release", id, Take: take));

	/// <summary>
	/// A change the WA must get eventually: sent in order, retried with the same operation id until it answers. One the
	/// World Authority refuses (#1317: a retried Order whose ship was taken meanwhile) is dropped, and the last index
	/// undoes it here.
	/// </summary>
	private static void Send(Request request)
	{
		Outbox.Add(new Pending(request, Guid.NewGuid()));
		if (ClusterRuntime.LocalMilliseconds >= s_nextFlush) Flush();
	}

	private static void Flush()
	{
		if (FlushOutbox(Outbox, Command, () => { lock (Sync) s_index ??= s_applied; }))
			s_nextFlush = ClusterRuntime.LocalMilliseconds + 5000;
	}

	/// <summary>Sends the outbox in order; true when the World Authority could not be reached and the head stays queued.</summary>
	internal static bool FlushOutbox(List<Pending> outbox, Func<Request, Guid, string> command, Action refused)
	{
		while (outbox.Count > 0)
		{
			Pending next = outbox[0];
			try { command(next.Request, next.Operation); }
			catch (Exception error) when (IsLostAnswer(error))
			{
				Console.WriteLine("[ClusterPlugin] ship storage " + next.Request.Op + " id=" + next.Request.Id + " deferred: "
					+ SecretRedactor.Redact(error.Message));
				return true;
			}
			catch (Exception error)
			{
				Console.WriteLine("[ClusterPlugin] ship storage " + next.Request.Op + " id=" + next.Request.Id + " refused: "
					+ SecretRedactor.Redact(error.Message));
				refused();
			}
			outbox.RemoveAt(0);
		}
		return false;
	}

	private static string Command(Request request, Guid operation)
		=> GlobalStateChannel.SendCommand(Subsystem, JsonSerializer.Serialize(request), operation, CommandTimeoutMilliseconds);

	private static void ReceiveIndex(string payload)
	{
		lock (Sync) s_index = payload;
	}

	/// <summary>The latest index into the session component, once its own loading is done (game thread).</summary>
	private static void ApplyIndex()
	{
		string payload;
		lock (Sync) payload = s_index;
		if (payload == null) return;
		MyGridsStorageSessionComponent storage = MySession.Static.GetComponent<MyGridsStorageSessionComponent>();
		if (storage == null || !storage.m_dataReady) return;
		Index index = JsonSerializer.Deserialize<Index>(payload) ?? throw new InvalidDataException("Invalid ship storage index");
		// The orders come from the WA too; one this node is running stays in progress here.
		var running = storage.m_clientOrders.Values.SelectMany(user => user.Orders).Where(order => order.InProgress)
			.Select(order => order.Id).ToHashSet();
		storage.m_clientOrders.Clear();
		foreach (User user in index.Users ?? Array.Empty<User>())
		{
			var data = new MyGridsStorageSessionComponent.UserData { AutoDeploy = user.AutoDeploy };
			foreach (Order order in user.Orders)
				data.Orders.Enqueue(new MyGridRetrievalOrder
				{
					Id = order.Id,
					DisplayName = order.DisplayName,
					CountdownMs = order.RemainingMs,
					CreditsPaid = order.CreditsPaid,
					CallerEndpointId = order.CallerEndpointId,
					CallerEntityId = order.CallerEntityId,
					InProgress = running.Contains(order.Id),
				});
			storage.m_clientOrders[user.Identity] = data;
		}
		var queued = storage.m_clientOrders.Values.SelectMany(user => user.Orders).Select(order => order.Id).ToHashSet();
		storage.m_storedGridsData.Clear();
		var seen = new HashSet<Guid>();
		foreach (Ship ship in index.Ships ?? Array.Empty<Ship>())
		{
			if (!DetailsCache.TryGetValue(ship.Id, out MyObjectBuilder_SavedGridDetails details))
				DetailsCache[ship.Id] = details = ReadDetails(ship.Details);
			storage.m_storedGridsData[ship.Id] = ToData(ship, details, queued.Contains(ship.Id));
			seen.Add(ship.Id);
		}
		foreach (Guid gone in DetailsCache.Keys.Where(id => !seen.Contains(id)).ToArray()) DetailsCache.Remove(gone);
		s_origins = (index.Origins ?? Array.Empty<long>()).ToHashSet();
		// An Unstore that is through, with an index that no longer lists the grids, releases them to the sweep.
		foreach (Guid unstored in Unstoring.Where(pair => !pair.Value.Any(s_origins.Contains)
			&& !Outbox.Any(pending => pending.Request.Op == "Unstore" && pending.Request.Id == pair.Key)).Select(pair => pair.Key).ToArray())
			Unstoring.Remove(unstored);
		lock (Sync)
		{
			s_applied = payload;
			if (ReferenceEquals(s_index, payload)) s_index = null;
		}
	}

	/// <summary>Closes grids that are stored ships: a recovery or an import brought back a grid stored after its last save.</summary>
	private static void Sweep()
	{
		if (s_origins.Count == 0) return;
		MyGridsStorageSessionComponent storage = MySession.Static.GetComponent<MyGridsStorageSessionComponent>();
		var closing = storage?.m_gridsToClose.Select(grid => grid.EntityId).ToHashSet() ?? new HashSet<long>();
		closing.UnionWith(Unstoring.Values.SelectMany(origins => origins));
		var doomed = MyEntities.GetEntities().OfType<MyCubeGrid>()
			.Where(grid => s_origins.Contains(grid.EntityId) && !grid.MarkedForClose && !closing.Contains(grid.EntityId)
				&& !ClusterRuntime.IsFrozen(grid.EntityId) && !ImportActivationGuard.IsOpenFor(GuardUser.ShipStorage, grid))
			.ToArray();
		foreach (MyCubeGrid grid in doomed)
		{
			Console.WriteLine("[ClusterPlugin] ship storage closed grid " + grid.EntityId + " '" + grid.DisplayName
				+ "': it is a stored ship, brought back from a save older than its store");
			grid.Close();
		}
	}

	// -- World Authority --------------------------------------------------------------------------------------------

	private static string Handle(string origin, string payload)
	{
		Request request = JsonSerializer.Deserialize<Request>(payload) ?? throw new InvalidDataException("Invalid ship storage request");
		string result = Apply(origin, request, out bool changed);
		if (changed)
		{
			Changed();
			Console.WriteLine("[ClusterPlugin] ship storage " + request.Op + " id=" + request.Id + " from " + origin);
		}
		return result;
	}

	/// <summary>A command's change to the World Authority state; the caller publishes it when <paramref name="changed"/>.</summary>
	internal static string Apply(string origin, Request request, out bool changed)
	{
		string result = string.Empty;
		changed = true;
		lock (Sync)
		{
			Ships.TryGetValue(request.Id, out Ship ship);
			switch (request.Op)
			{
				case "Store":
					Ship stored = request.Ship ?? throw new InvalidDataException("A store carries the ship");
					if (stored.Id != request.Id || stored.Id == Guid.Empty || string.IsNullOrEmpty(stored.Details)
						|| string.IsNullOrEmpty(stored.Blueprint) || stored.Blueprint.Length > MaxBlueprintChars)
						throw new InvalidDataException("Invalid stored ship " + request.Id);
					if (ship != null) throw new InvalidOperationException("Ship " + request.Id + " is stored already");
					if (Unstored.ContainsKey(request.Id)) throw new InvalidOperationException("Ship " + request.Id + " was unstored: its node lost the answer");
					Ships.Add(stored.Id, Untaken(stored));
					break;
				case "Unstore":
					// #1317: the node lost the Store's answer and kept the grids. No tombstone: they are in the world. A
					// Store this overtook is refused. A different Guid for every store, so the id cannot come back.
					foreach (Guid expired in Unstored.Where(pair => pair.Value < DateTime.UtcNow).Select(pair => pair.Key).ToArray())
						Unstored.Remove(expired);
					Unstored[request.Id] = DateTime.UtcNow + TombstoneLife;
					changed = ship != null;
					if (!changed) break;
					if (ship.TakenBy != null)
						Console.WriteLine("[ClusterPlugin] ship storage unstore of a taken ship id=" + request.Id + " take=" + ship.Take + " by "
							+ ship.TakenBy + " fetched=" + ship.Fetched + (ship.Fetched ? ": its node may paste it, a duplicate of the grids kept by "
							+ origin : ": its fetch is refused now"));
					Remove(ship.Id, tombstone: false);
					break;
				case "Take":
					if (ship == null || ship.TakenBy != null || request.Take == Guid.Empty || IsOrdered(ship.Id) != request.Ordered)
						throw new InvalidOperationException(NotStored);
					Ships[ship.Id] = ship with { TakenBy = origin, Take = request.Take, Taken = DateTime.UtcNow, Fetched = false };
					break;
				case "Consume":
				case "Release":
					// Never an error: the node retries until it gets an answer, and a take the WA no longer
					// knows (released by the node's own restart) has nothing left to settle.
					if (ship == null || ship.Take != request.Take || ship.TakenBy != origin)
					{
						Console.WriteLine("[ClusterPlugin] ship storage " + request.Op + " of an unknown take id=" + request.Id + " from " + origin);
						changed = false;
					}
					else if (request.Op == "Consume") Remove(ship.Id);
					else Ships[ship.Id] = Untaken(ship);
					break;
				case "Delete":
					changed = ship != null && ship.TakenBy == null;
					if (changed) Remove(ship.Id);
					result = changed ? "true" : "false";
					break;
				case "Share":
					changed = ship != null;
					if (changed) Ships[ship.Id] = ship with { Kind = request.Kind };
					break;
				case "Order":
					Order order = request.Order ?? throw new InvalidDataException("An order carries the order");
					if (ship == null || ship.TakenBy != null || order.Id != ship.Id || IsOrdered(ship.Id))
						throw new InvalidOperationException(NotStored);
					User user = UserOf(request.Identity);
					SetUser(user with { Orders = user.Orders.Append(order with { Due = DueAt(order) }).ToArray() });
					break;
				case "Expedite":
					Order paid = request.Order ?? throw new InvalidDataException("An expedite carries the order");
					User payer = UserOf(request.Identity);
					changed = SetUser(payer with { Orders = payer.Orders.Select(each => each.Id == request.Id
						? each with { Due = DueAt(paid), CreditsPaid = paid.CreditsPaid } : each).ToArray() });
					break;
				case "Dequeue":
					User owner = UserOf(request.Identity);
					changed = SetUser(owner with { Orders = owner.Orders.Where(each => each.Id != request.Id).ToArray() });
					break;
				case "Unqueue":
					changed = false;
					foreach (User each in Users.Values.ToArray())
						changed |= SetUser(each with { Orders = each.Orders.Where(queued => queued.CallerEntityId != request.Terminal).ToArray() });
					break;
				case "AutoDeploy":
					changed = SetUser(UserOf(request.Identity) with { AutoDeploy = request.AutoDeploy });
					break;
				case "Hello":
					Ship[] held = Ships.Values.Where(each => each.TakenBy == origin).ToArray();
					foreach (Ship each in held) Ships[each.Id] = Untaken(each);
					changed = held.Length > 0;
					result = held.Length.ToString();
					break;
				default:
					throw new InvalidDataException("Unknown ship storage operation " + request.Op);
			}
		}
		return result;
	}

	/// <summary>
	/// The blueprint of a ship the asking node has taken; a read, outside the command ledger. It marks the take fetched:
	/// from here on the node may paste the ship, so <see cref="ExpireTakes"/> no longer releases it.
	/// </summary>
	internal static string Blueprint(string origin, string payload)
	{
		Guid id = Guid.Parse(payload);
		bool first;
		string blueprint;
		lock (Sync)
		{
			if (!Ships.TryGetValue(id, out Ship ship) || ship.TakenBy != origin)
				throw new InvalidOperationException("Ship " + id + " is not taken by " + origin);
			first = !ship.Fetched;
			if (first) Ships[id] = ship with { Fetched = true };
			blueprint = ship.Blueprint;
		}
		if (first) ClusterRuntime.RequestGlobalSave();
		return blueprint;
	}

	/// <summary>
	/// #1269: releases the takes whose blueprint was not read within <see cref="UnfetchedTakeLife"/> and reports, once
	/// each, those still held <see cref="FetchedTakeReport"/> after the take. The caller publishes when any was released.
	/// </summary>
	internal static (int Released, int Reported) ExpireTakes(DateTime now)
	{
		var released = new List<Ship>();
		var reported = new List<Ship>();
		lock (Sync)
		{
			foreach (Ship ship in Ships.Values.Where(each => each.TakenBy != null).ToArray())
			{
				if (!ship.Fetched && now - ship.Taken >= UnfetchedTakeLife)
				{
					Ships[ship.Id] = Untaken(ship);
					released.Add(ship);
				}
				else if (ship.Fetched && now - ship.Taken >= FetchedTakeReport && ReportedTakes.Add(ship.Take)) reported.Add(ship);
			}
			ReportedTakes.IntersectWith(Ships.Values.Where(ship => ship.TakenBy != null).Select(ship => ship.Take));
		}
		foreach (Ship ship in released)
			Console.WriteLine("[ClusterPlugin] ship storage released take id=" + ship.Id + " of " + ship.TakenBy + " taken "
				+ ship.Taken.ToString("O") + ": its blueprint was never fetched");
		foreach (Ship ship in reported)
			Console.WriteLine("[ClusterPlugin] ship storage take stranded id=" + ship.Id + " owner=" + ship.Owner + " take=" + ship.Take
				+ " by " + ship.TakenBy + " taken " + ship.Taken.ToString("O") + ": its blueprint was fetched, so the ship may exist"
				+ " as a grid; it stays taken (unlisted) until that node's Hello or an admin settles it"
				+ " (GET /admin/v1/ship-storage/takes, POST /admin/v1/ship-storage/takes/" + ship.Id + "/settle)");
		return (released.Count, reported.Count);
	}

	/// <summary>
	/// #1316: a take the World Authority holds, as an admin sees it. Name, Owner and Position (the terminal plus the
	/// ship's offset, near where vanilla pastes it) are what to look for in the world; only a Stranded take can be settled.
	/// </summary>
	public sealed record HeldTake(Guid Id, long Owner, string Name, double[] Position, string TakenBy, Guid Take,
		DateTime Taken, bool Fetched, bool Stranded);

	internal static HeldTake[] HeldTakes(DateTime now)
	{
		lock (Sync)
			return Ships.Values.Where(ship => ship.TakenBy != null).OrderBy(ship => ship.Taken).Select(ship => new HeldTake(ship.Id,
				ship.Owner, DisplayName(ship.Details), Enumerable.Range(0, 3).Select(i => ship.Terminal[i] + ship.Relative[i]).ToArray(),
				ship.TakenBy, ship.Take, ship.Taken, ship.Fetched, IsStranded(ship, now))).ToArray();
	}

	private static bool IsStranded(Ship ship, DateTime now) => ship.TakenBy != null && ship.Fetched && now - ship.Taken >= FetchedTakeReport;

	/// <summary>
	/// #1316: an admin settles a stranded take after looking for its grid in the world. Release lists the ship again (no
	/// grid was pasted), consume drops it (the grid exists). Only the exact take and only once it is stranded: a younger
	/// take may still be pasting. A Consume or Release its node sends later is of a take the World Authority no longer
	/// knows, so it changes nothing. The caller publishes.
	/// </summary>
	internal static Ship SettleTake(Guid id, Guid take, bool consume, DateTime now)
	{
		lock (Sync)
		{
			if (!Ships.TryGetValue(id, out Ship ship) || take == Guid.Empty || ship.Take != take)
				throw new InvalidOperationException("Ship " + id + " is not held under take " + take);
			if (!IsStranded(ship, now))
				throw new InvalidOperationException("Take " + take + " of ship " + id + " is not stranded: only a take whose blueprint was"
					+ " fetched and that is held " + FetchedTakeReport.TotalMinutes + " min can be settled");
			if (consume) Remove(id);
			else Ships[id] = Untaken(ship);
			return ship;
		}
	}

	/// <summary>The admin route's settle (ClusterControlServer): <see cref="SettleTake"/>, published and logged.</summary>
	internal static void Settle(Guid id, Guid take, bool consume)
	{
		Ship ship = SettleTake(id, take, consume, DateTime.UtcNow);
		Changed();
		Console.WriteLine("[ClusterPlugin] ship storage admin " + (consume ? "consumed" : "released") + " stranded take id=" + id
			+ " owner=" + ship.Owner + " take=" + take + " of " + ship.TakenBy + " taken " + ship.Taken.ToString("O"));
	}

	private static string DisplayName(string details)
	{
		try { return System.Xml.Linq.XElement.Parse(details.TrimStart('﻿')).Element("DisplayName")?.Value; }
		catch (System.Xml.XmlException) { return null; }
	}

	private static Ship Untaken(Ship ship) => ship with { TakenBy = null, Take = default, Taken = default, Fetched = false };

	/// <summary>
	/// A take restored from the global save gets a fresh <see cref="Ship.Taken"/>, so its node has a full life to settle
	/// it with the new World Authority. A take saved before #1269 has no time and no fetch mark, so it counts as fetched.
	/// </summary>
	internal static Ship Restored(Ship ship, DateTime now) => ship.TakenBy == null ? ship
		: ship with { Taken = now, Fetched = ship.Fetched || ship.Taken == default };

	private static User UserOf(long identity) =>
		Users.TryGetValue(identity, out User user) ? user : new User(identity, true, Array.Empty<Order>());

	private static bool IsOrdered(Guid id) => Users.Values.Any(user => user.Orders.Any(order => order.Id == id));

	private static DateTime DueAt(Order order) => DateTime.UtcNow.AddMilliseconds(Math.Max(0, order.RemainingMs));

	/// <summary>Stores a player's queue and switch; vanilla forgets a player whose switch is on and queue empty. True if it changed.</summary>
	private static bool SetUser(User user)
	{
		bool idle = user.AutoDeploy && user.Orders.Length == 0;
		bool changed = Users.TryGetValue(user.Identity, out User old)
			? idle || old.AutoDeploy != user.AutoDeploy || !old.Orders.SequenceEqual(user.Orders)
			: !idle;
		if (idle) Users.Remove(user.Identity);
		else Users[user.Identity] = user;
		return changed;
	}

	private static void Remove(Guid id, bool tombstone = true)
	{
		Ship ship = Ships[id];
		Ships.Remove(id);
		foreach (User user in Users.Values.ToArray()) SetUser(user with { Orders = user.Orders.Where(order => order.Id != id).ToArray() });
		if (tombstone && ship.Origins?.Length > 0) Tombstones.Add(new Tombstone(ship.Origins, DateTime.UtcNow + TombstoneLife));
	}

	private static void Changed()
	{
		// Under the lock, so two changes cannot publish their snapshots in the opposite order.
		lock (Sync) GlobalStateChannel.Publish(Subsystem, Snapshot());
		ClusterRuntime.RequestGlobalSave();
	}

	internal static string Snapshot()
	{
		lock (Sync)
		{
			Tombstones.RemoveAll(tombstone => tombstone.Until < DateTime.UtcNow);
			return JsonSerializer.Serialize(new Index(
				Ships.Values.Where(ship => ship.TakenBy == null).OrderBy(ship => ship.Id)
					.Select(ship => ship with { Blueprint = null, Origins = null }).ToArray(),
				Ships.Values.SelectMany(ship => ship.Origins ?? Array.Empty<long>())
					.Concat(Tombstones.SelectMany(tombstone => tombstone.Origins)).Distinct().ToArray(),
				Users.Values.OrderBy(user => user.Identity).Select(user => user with
				{
					Orders = user.Orders.Select(order => order with
					{
						RemainingMs = Math.Max(0, (long)(order.Due - DateTime.UtcNow).TotalMilliseconds),
						Due = default,
					}).ToArray(),
				}).ToArray()));
		}
	}

	/// <summary>The global save section; captured with the command ledger, so the two agree.</summary>
	internal static string PersistedRecord()
	{
		lock (Sync)
			return JsonSerializer.Serialize(new Record(Ships.Values.OrderBy(ship => ship.Id).ToArray(), Tombstones.ToArray(),
				Users.Values.OrderBy(user => user.Identity).ToArray()));
	}

	/// <summary>
	/// World Authority load. A cluster's first World Authority (no catalogued global save) takes over the ships the
	/// converted world stored; any later one has its global save, and the world file's index is stale by then.
	/// </summary>
	internal static void RestoreRecord(string payload, bool firstLoad)
	{
		s_migrate = firstLoad;
		if (string.IsNullOrEmpty(payload)) return;
		Record record = JsonSerializer.Deserialize<Record>(payload) ?? throw new InvalidDataException("Invalid ship storage record");
		lock (Sync)
		{
			Ships.Clear();
			Tombstones.Clear();
			Users.Clear();
			foreach (Ship ship in record.Ships ?? Array.Empty<Ship>()) Ships[ship.Id] = Restored(ship, DateTime.UtcNow);
			Tombstones.AddRange(record.Tombstones ?? Array.Empty<Tombstone>());
			foreach (User user in record.Users ?? Array.Empty<User>()) Users[user.Identity] = user;
		}
		Console.WriteLine("[ClusterPlugin] ship storage restored ships=" + record.Ships?.Length
			+ " taken=" + record.Ships?.Count(ship => ship.TakenBy != null) + " queues=" + record.Users?.Length);
	}

	private static void Migrate()
	{
		MyGridsStorageSessionComponent storage = MySession.Static.GetComponent<MyGridsStorageSessionComponent>();
		if (storage == null || MySession.Static.Settings.GridStorageMaxPerPlayer == 0)
		{
			s_migrate = false;
			return;
		}
		if (!storage.m_dataReady) return;
		s_migrate = false;
		int taken = 0;
		foreach (KeyValuePair<Guid, MyStoredGridData> pair in storage.m_storedGridsData.ToArray())
		{
			string path = SbcPath(pair.Key);
			if (pair.Value.SavedGridDetails == null || !File.Exists(path)) continue;
			Ship ship = FromData(pair.Key, pair.Value, Pack(File.ReadAllBytes(path)), Array.Empty<long>());
			lock (Sync)
				if (Ships.TryAdd(ship.Id, ship)) taken++;
		}
		if (taken == 0) return;
		Changed();
		Console.WriteLine("[ClusterPlugin] ship storage took over " + taken + " ship(s) stored in the world file");
	}

	// -- combat cooldown ---------------------------------------------------------------------------------------------

	/// <summary>
	/// A grid's storage combat cooldown through its handover. Vanilla keeps the time of the last damage per node, by
	/// the mechanical root's entity id, so a grid that just fought could be stored right after its partition moved.
	/// The timestamp is UTC ticks, like the trash grace's expiry.
	/// </summary>
	internal static readonly IPluginGridHandover CombatHandover = new CombatCooldownHandover();

	private sealed class CombatCooldownHandover : IPluginGridHandover
	{
		public byte[] Leaving(long gridId)
		{
			MyGridsStorageSessionComponent storage = MySession.Static?.GetComponent<MyGridsStorageSessionComponent>();
			return storage != null && storage.GetCombatCooldownRemainingMs(gridId) > 0
				&& storage.m_gridCombatTimestamps.TryGetValue(gridId, out long ticks) ? BitConverter.GetBytes(ticks) : null;
		}

		public void Left(long gridId) { }

		public void Stayed(long gridId) { }

		public void Arrived(long gridId, byte[] state)
		{
			MyGridsStorageSessionComponent storage = MySession.Static?.GetComponent<MyGridsStorageSessionComponent>();
			if (storage == null || state?.Length != sizeof(long)) return;
			long ticks = BitConverter.ToInt64(state, 0);
			if (!storage.m_gridCombatTimestamps.TryGetValue(gridId, out long local) || local < ticks)
				storage.m_gridCombatTimestamps[gridId] = ticks;
		}
	}

	// -- conversions ---------------------------------------------------------------------------------------------------

	private static string SbcPath(Guid id) => Path.Combine(MySession.Static.CurrentPath, "gs-" + id + ".sbc");

	private static Ship FromData(Guid id, MyStoredGridData data, string blueprint, long[] origins)
	{
		using var stream = new MemoryStream();
		if (!MyObjectBuilderSerializerKeen.SerializeXML(stream, data.SavedGridDetails))
			throw new InvalidDataException("The details of stored ship " + id + " did not serialize");
		Vector3 relative = data.RelativePosition;
		Vector3D terminal = data.StorageEntityPosition;
		Quaternion orientation = data.Orientation;
		return new Ship(id, data.OwnerId, (int)data.OwnershipKind, new[] { relative.X, relative.Y, relative.Z },
			new[] { terminal.X, terminal.Y, terminal.Z }, new[] { orientation.X, orientation.Y, orientation.Z, orientation.W },
			Encoding.UTF8.GetString(stream.ToArray()), origins, blueprint);
	}

	private static MyObjectBuilder_SavedGridDetails ReadDetails(string xml)
	{
		using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml), writable: false);
		return MyObjectBuilderSerializerKeen.DeserializeXML(stream, out MyObjectBuilder_SavedGridDetails details)
			? details : throw new InvalidDataException("Unreadable stored ship details");
	}
}
