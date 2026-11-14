using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Sandbox.Game.Contracts;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using VRage.Game.ObjectBuilders.Components;
using VRage.Game.ObjectBuilders.Components.Contracts;
using VRage.ObjectBuilder;
using VRage.ObjectBuilders.Private;

namespace ClusterPlugin;

/// <summary>
/// SE-1511: the contracts a faction station offers, posted on the World Authority (by a mod or plugin, such as
/// Octahedron's station contracts), show at the station's contract blocks on every regular node.
/// <para>
/// A station offer has no start block, so it can't travel in a block's builder the way <see cref="ContractOffers"/>
/// do. It lived in the World Authority's contract system only, and a regular node's contract block, which lists
/// the offers of its grid's station from the local system, showed none. The World Authority publishes its station
/// offers (inactive, with a start station and no start block) whenever their ids or rewards change, and every
/// regular node mirrors them into its own contract system: it adds the missing ones and drops the ones the World
/// Authority no longer offers. Acceptance on a node already goes through <see cref="ContractLedger"/>, whose World
/// Authority side takes its own offer out as it accepts. An offer that is active here or that the ledger knows as
/// settled is not added again.
/// </para>
/// </summary>
internal static class StationContractOffers
{
	private const string Subsystem = "contract-offers";
	private const double PublishCheckMilliseconds = 1000;

	private static readonly object Sync = new();
	private static string m_snapshot;
	private static string m_signature;
	private static double m_nextCheck;
	// Regular node: a payload that arrived before the session was ready.
	private static string m_pending;

	public static void Install()
	{
		GlobalStateChannel.RegisterReplicaHandler(Subsystem, Apply);
		GlobalStateChannel.RegisterSnapshot(Subsystem, CurrentSnapshot, Apply);
	}

	private static MySessionComponentContractSystem System => MySession.Static?.GetComponent<MySessionComponentContractSystem>();

	private static bool IsStationOffer(MyContract contract)
		=> contract.State == MyContractStateEnum.Inactive && contract.StartBlock == 0 && contract.StartStation != 0;

	/// <summary>Game thread. World Authority: publishes the station offers when they changed, checked once a second.</summary>
	public static void Update()
	{
		if (!ClusterRuntime.IsClustered || MySession.Static?.Ready != true) return;
		if (!ClusterRuntime.IsWorldAuthority)
		{
			string pending;
			lock (Sync)
			{
				pending = m_pending;
				m_pending = null;
			}
			if (pending != null) Apply(pending);
			return;
		}
		double now = ClusterRuntime.LocalMilliseconds;
		if (now < m_nextCheck) return;
		m_nextCheck = now + PublishCheckMilliseconds;
		MySessionComponentContractSystem system = System;
		if (system == null) return;
		MyContract[] offers = system.m_inactiveContracts.Values.Where(IsStationOffer).OrderBy(contract => contract.Id).ToArray();
		string signature = Signature(offers.Select(contract => (contract.Id, contract.RewardMoney)));
		if (signature == m_signature) return;
		string snapshot = Serialize(offers.Select(contract => contract.GetObjectBuilder()));
		lock (Sync) m_snapshot = snapshot;
		m_signature = signature;
		GlobalStateChannel.Publish(Subsystem, snapshot);
		Console.WriteLine("[ClusterPlugin] station_contract_offers_published offers=" + offers.Length + " bytes=" + snapshot.Length);
	}

	internal static string Signature(IEnumerable<(long Id, long Reward)> offers)
		=> string.Join(";", offers.Select(offer => offer.Id + ":" + offer.Reward));

	private static string CurrentSnapshot()
	{
		lock (Sync) return m_snapshot;
	}

	/// <summary>Regular node, game thread: mirrors the published station offers into the local contract system.</summary>
	private static void Apply(string payload)
	{
		if (!ClusterRuntime.IsClustered || ClusterRuntime.IsWorldAuthority || payload == null) return;
		MySessionComponentContractSystem system = System;
		if (MySession.Static?.Ready != true || system == null)
		{
			lock (Sync) m_pending = payload;
			return;
		}
		Dictionary<long, MyObjectBuilder_Contract> wanted = Parse(payload)
			.Where(builder => builder.Id > 0 && builder.StartBlock == 0 && builder.StartStation != 0)
			.ToDictionary(builder => builder.Id);
		Dictionary<long, MyContract> local = system.m_inactiveContracts.Values.Where(IsStationOffer).ToDictionary(contract => contract.Id);
		(List<long> remove, List<long> add) = Plan(
			local.Values.Select(contract => (contract.Id, contract.RewardMoney)),
			wanted.Values.Select(builder => (builder.Id, builder.RewardMoney)),
			id => system.m_activeContracts.ContainsKey(id) || ContractLedger.IsSettled(id)
				|| (system.m_inactiveContracts.ContainsKey(id) && !local.ContainsKey(id)));
		foreach (long id in remove) system.RemoveInactiveContract(id);
		foreach (long id in add) system.AddContract(MyContractFactory.CreateInstance(wanted[id]));
		// Vanilla drains these in the update a regular node does not run.
		system.m_pendingContractChanges.Clear();
		if (add.Count + remove.Count > 0)
			Console.WriteLine("[ClusterPlugin] station_contract_offers_applied offers=" + wanted.Count + " added=" + add.Count
				+ " removed=" + remove.Count);
	}

	/// <summary>
	/// What a node changes to mirror the World Authority's offers. Local station offers it no longer offers, or offers
	/// at another reward, go. Wanted ones that aren't here come, unless blocked here (active, settled, or the id taken
	/// by an offer that is no station's). A changed reward is a remove and an add.
	/// </summary>
	internal static (List<long> Remove, List<long> Add) Plan(
		IEnumerable<(long Id, long Reward)> local, IEnumerable<(long Id, long Reward)> wanted, Func<long, bool> blocked)
	{
		Dictionary<long, long> want = wanted.ToDictionary(offer => offer.Id, offer => offer.Reward);
		List<long> remove = local.Where(offer => !want.TryGetValue(offer.Id, out long reward) || reward != offer.Reward)
			.Select(offer => offer.Id).OrderBy(id => id).ToList();
		HashSet<long> kept = local.Select(offer => offer.Id).Except(remove).ToHashSet();
		List<long> add = want.Keys.Where(id => !kept.Contains(id) && (remove.Contains(id) || !blocked(id))).OrderBy(id => id).ToList();
		return (remove, add);
	}

	private static string Serialize(IEnumerable<MyObjectBuilder_Contract> contracts)
	{
		var builder = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_SessionComponentContractSystem>();
		builder.InactiveContracts = new MySerializableList<MyObjectBuilder_Contract>(contracts);
		using var xml = new MemoryStream();
		if (!MyObjectBuilderSerializerKeen.SerializeXML(xml, builder))
			throw new InvalidDataException("Station contract offers did not serialize");
		// Deflated: a few hundred offers are half a megabyte of XML.
		using var packed = new MemoryStream();
		using (var deflate = new DeflateStream(packed, CompressionLevel.Optimal, leaveOpen: true)) xml.WriteTo(deflate);
		return Convert.ToBase64String(packed.ToArray());
	}

	private static List<MyObjectBuilder_Contract> Parse(string value)
	{
		if (string.IsNullOrEmpty(value)) return new List<MyObjectBuilder_Contract>();
		using var xml = new MemoryStream();
		using (var deflate = new DeflateStream(new MemoryStream(Convert.FromBase64String(value)), CompressionMode.Decompress))
			deflate.CopyTo(xml);
		xml.Position = 0;
		if (!MyObjectBuilderSerializerKeen.DeserializeXML(xml, out MyObjectBuilder_SessionComponentContractSystem builder))
			throw new InvalidDataException("Unreadable station contract offers");
		return builder.InactiveContracts?.ToList() ?? new List<MyObjectBuilder_Contract>();
	}
}
