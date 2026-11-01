using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using Sandbox;
using Sandbox.Game.Contracts;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Game.ObjectBuilders.Components;
using VRage.Game.ObjectBuilders.Components.Contracts;
using VRage.ObjectBuilder;
using VRage.ObjectBuilders.Private;

namespace ClusterPlugin;

/// <summary>
/// SE1-0232: the contracts offered at a contract block travel inside the block's object builder.
/// <para>
/// Vanilla keeps them in <see cref="MySessionComponentContractSystem"/>, which is per node and filled
/// from the world at load. A posted contract therefore stayed on the node it was posted on: a handover
/// or a restart of that node dropped it, and with it the reward a player-made one had already taken from
/// the poster. Like a store block's items (WorldAuthority.md §6.2), the offers are partition state, so
/// they are written into the block's builder as a mod storage entry (<see cref="Key"/>, through
/// <see cref="BlockBuilderEntry"/>). Handover, partition saves, offline partitions and recovery all move
/// the builder, so they all carry the offers with no change to any of them.
/// </para>
/// <para>
/// Life of the entry: Init reads it from the arriving builder and keeps it off the block's own storage
/// component. When the block enters the scene, the node adopts it: the entry replaces the local offers
/// for that block (a block without an entry, from before this change, keeps what the node loaded from the
/// world). From then on every builder of the block carries the entry, an empty one when nothing is
/// offered. An arrived offer the node knows as accepted or settled is not adopted, and the entry is rewritten
/// without it: it comes from a save older than the acceptance (F30, #1272). A close the cluster makes
/// (<see cref="ClusterCloseScope"/>) is not a destruction: the offers are neither refunded nor removed, and
/// the copies left behind are replaced when the block comes back.
/// Grinding or deleting the block stays vanilla.
/// </para>
/// Accepted contracts and the mod API's OnContractAcquired/Failed/Succeeded callbacks do not travel (the
/// callbacks are delegates of the posting node).
/// </summary>
internal static class ContractOffers
{
	internal static readonly Guid Key = new Guid("5c1a0232-0ffe-4c2e-9a0e-3c0b7a1d5e52");

	private static readonly ConditionalWeakTable<MyContractBlock, State> States = new ConditionalWeakTable<MyContractBlock, State>();

	/// <summary>Value is the entry the block's builders carry (null: none yet). Ids are the local offers it encodes.</summary>
	private sealed class State
	{
		public string Value;
		public long[] Ids;
		public bool Adopted;
	}

	private static bool Active => ClusterRuntime.IsClustered && !ClusterRuntime.IsWorldAuthority;

	private static bool OnGameThread => MySandboxGame.Static != null && Thread.CurrentThread == MySandboxGame.Static.UpdateThread;

	/// <summary>MyContractBlock.Init postfix, any thread: take the entry off the builder's storage component.</summary>
	internal static void Initialized(MyContractBlock block, MyObjectBuilder_CubeBlock builder)
	{
		if (!Active) return;
		States.AddOrUpdate(block, new State { Value = BlockBuilderEntry.Read(builder, Key) });
		BlockBuilderEntry.Forget(block, Key);
	}

	/// <summary>Block entered the scene: replace the local offers with the arrived ones (game thread).</summary>
	internal static void AddedToScene(MyContractBlock block)
	{
		if (!Active) return;
		if (!OnGameThread || MySession.Static?.Ready != true)
		{
			MySandboxGame.Static?.Invoke(() => AddedToScene(block), "SE1-0232 contract offers");
			return;
		}
		State state = States.GetOrCreateValue(block);
		var system = MySession.Static.GetComponent<MySessionComponentContractSystem>();
		if (state.Adopted || block.MarkedForClose || system == null) return;
		try
		{
			Adopt(system, block, state);
			state.Adopted = true;
		}
		catch (Exception error)
		{
			// Not adopted: the block keeps carrying the entry it arrived with, it just offers nothing here.
			Console.WriteLine("[ClusterPlugin] contract offers not adopted block=" + block.EntityId + ": " + error.GetType().Name + ": " + error.Message);
		}
	}

	private static void Adopt(MySessionComponentContractSystem system, MyContractBlock block, State state)
	{
		long blockId = block.EntityId;
		int restored = 0, dropped = 0, foreign = 0, skipped = 0;
		if (state.Value != null)
		{
			List<MyObjectBuilder_Contract> arrived = Parse(state.Value);
			foreign = arrived.RemoveAll(contract => contract.StartBlock != blockId);   // a pasted copy of the block
			var keep = arrived.Select(contract => contract.Id).ToHashSet();
			foreach (long stale in LocalIds(system, blockId).Where(id => !keep.Contains(id)))
				if (system.RemoveContract(stale)) dropped++;
			foreach (MyObjectBuilder_Contract contract in arrived)
			{
				if (system.m_inactiveContracts.ContainsKey(contract.Id)) continue;
				// An entry from before the acceptance (a partition rolled back to an older save, F30): the offer is
				// accepted or settled already, and the entry is rewritten without it below.
				bool active = system.m_activeContracts.ContainsKey(contract.Id);
				if (active || ContractLedger.IsSettled(contract.Id))
				{
					Console.WriteLine("[ClusterPlugin] contract offer not restored, already " + (active ? "active" : "settled")
						+ " here: block=" + blockId + " contract=" + contract.Id);
					skipped++;
					continue;
				}
				system.AddContract(MyContractFactory.CreateInstance(contract));
				restored++;
			}
		}
		state.Ids = LocalIds(system, blockId);
		if (state.Value == null || foreign + skipped > 0) state.Value = Serialize(system, blockId);
		if (restored + dropped + foreign + skipped > 0)
			Console.WriteLine("[ClusterPlugin] contract offers adopted block=" + blockId + " offered=" + state.Ids.Length
				+ " restored=" + restored + " dropped=" + dropped + " foreign=" + foreign + " skipped=" + skipped);
	}

	/// <summary>MyContractBlock.GetObjectBuilderCubeBlock postfix: write the entry into the builder.</summary>
	internal static void Saving(MyContractBlock block, MyObjectBuilder_CubeBlock builder)
	{
		if (!Active || builder == null || !States.TryGetValue(block, out State state)) return;
		// Off the game thread (the gate's re-serialization) the last value stands; it is refreshed on the
		// game thread only when the offered ids changed, so an unchanged arrival round-trips byte for byte.
		if (state.Adopted && OnGameThread)
		{
			var system = MySession.Static?.GetComponent<MySessionComponentContractSystem>();
			long[] ids = system == null ? state.Ids : LocalIds(system, block.EntityId);
			if (system != null && !ids.SequenceEqual(state.Ids))
			{
				try
				{
					state.Value = Serialize(system, block.EntityId);
					state.Ids = ids;
				}
				catch (Exception error)
				{
					// The previous entry stands; a save must not fail over the offers.
					Console.WriteLine("[ClusterPlugin] contract offers not serialized block=" + block.EntityId + ": " + error.Message);
				}
			}
		}
		if (state.Value != null) BlockBuilderEntry.Write(builder, Key, state.Value);
	}

	/// <summary>ContractBlockDestroyed prefix: true keeps the offers (a cluster close), false runs vanilla.</summary>
	internal static bool KeepOnClose(long blockId)
	{
		if (!Active || !ClusterCloseScope.Active) return false;
		var system = MySession.Static?.GetComponent<MySessionComponentContractSystem>();
		int offered = system == null ? 0 : LocalIds(system, blockId).Length;
		if (offered > 0) Console.WriteLine("[ClusterPlugin] contract offers kept on cluster close block=" + blockId + " offered=" + offered);
		return true;
	}

	private static long[] LocalIds(MySessionComponentContractSystem system, long blockId) =>
		system.m_inactiveContractsForBlock.TryGetValue(blockId, out var offers) ? offers.Select(offer => offer.Id).ToArray() : Array.Empty<long>();

	private static string Serialize(MySessionComponentContractSystem system, long blockId)
	{
		if (!system.m_inactiveContractsForBlock.TryGetValue(blockId, out var offers) || offers.Count == 0) return string.Empty;
		var builder = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_SessionComponentContractSystem>();
		builder.InactiveContracts = new MySerializableList<MyObjectBuilder_Contract>(offers.OfType<MyContract>().Select(offer => offer.GetObjectBuilder()));
		using var stream = new MemoryStream();
		if (!MyObjectBuilderSerializerKeen.SerializeXML(stream, builder))
			throw new InvalidDataException("Contract offers of block " + blockId + " did not serialize");
		return Encoding.UTF8.GetString(stream.ToArray());
	}

	private static List<MyObjectBuilder_Contract> Parse(string value)
	{
		if (value.Length == 0) return new List<MyObjectBuilder_Contract>();
		using var stream = new MemoryStream(Encoding.UTF8.GetBytes(value), writable: false);
		if (!MyObjectBuilderSerializerKeen.DeserializeXML(stream, out MyObjectBuilder_SessionComponentContractSystem builder))
			throw new InvalidDataException("Unreadable contract offers entry");
		return builder.InactiveContracts?.ToList() ?? new List<MyObjectBuilder_Contract>();
	}
}
