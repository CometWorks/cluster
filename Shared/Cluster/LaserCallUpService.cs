using System;
using System.Collections.Generic;
using System.Linq;
using VRage.Game.Entity;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;

namespace ClusterPlugin;

/// <summary>An explicit ConnectTo (including GPS proxy resolution) wakes the far side; holding an
/// established proxy link never does. Vanilla pairs both real blocks after the directed load.</summary>
internal static class LaserCallUpService
{
	private sealed record Pending(long Target, long Started) { public long NextLoad; }
	private static readonly Dictionary<long, Pending> Requests = new();
	private static readonly Dictionary<long, long> Last = new();
	private static long m_next;
	private static readonly string Node = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID") ?? string.Empty;

	internal static void Request(MyLaserAntenna source, long target)
	{
		if (!ClusterConfig.GetBool("offline.callUp")) return;
		AntennaGraphEntry peer = AntennaGraphService.Entries().FirstOrDefault(x => x.AntennaEntityId == target);
		if (peer == null || !OfflinePartitionService.IsOfflineOrigin(peer.Origin)) return;
		long now = Environment.TickCount64;
		if (Last.TryGetValue(source.EntityId, out long last) && now - last < 60000) return;
		Last[source.EntityId] = now;
		Requests[source.EntityId] = new(target, now);
	}

	internal static void Update()
	{
		long now = Environment.TickCount64;
		if (now < m_next) return;
		m_next = now + 1000;
		foreach (var pair in Requests.ToArray())
		{
			MyLaserAntenna source = MyLaserAntenna.GetLaserById(pair.Key);
			Pending request = pair.Value;
			if (source == null || source.MarkedForClose || source.m_targetId != request.Target || !source.Enabled
				|| now - request.Started >= Math.Clamp(ClusterConfig.GetInt("offline.callUpSeconds"), 1, 1200) * 1000L)
			{ Requests.Remove(pair.Key); continue; }
			if (MyLaserAntenna.GetLaserById(request.Target) != null)
			{
				source.ConnectTo(request.Target); // includes vanilla ownership, ConnectToRec and sync
				Requests.Remove(pair.Key);
				continue;
			}
			var peer = AntennaGraphService.Entries().FirstOrDefault(x => x.AntennaEntityId == request.Target);
			if (peer != null && OfflinePartitionService.IsOfflineOrigin(peer.Origin) && now >= request.NextLoad)
			{
				var proxy = MyLaserAntenna.GetLaserBroadcasterById(request.Target);
				if (proxy == null || !proxy.CanBeUsedByPlayer(source.OwnerId) || !source.Broadcaster.CanBeUsedByPlayer(proxy.Owner)
					|| !source.IsInRange(proxy.Entity as MyEntity)) { Requests.Remove(pair.Key); continue; }
				OfflinePartitionService.RequestLoad(OfflinePartitionService.PartitionOfOrigin(peer.Origin), Node, "laser");
				request.NextLoad = now + 15000;
			}
		}
		foreach (long old in Last.Where(x => now - x.Value >= 60000 && !Requests.ContainsKey(x.Key)).Select(x => x.Key).ToArray()) Last.Remove(old);
	}
}
