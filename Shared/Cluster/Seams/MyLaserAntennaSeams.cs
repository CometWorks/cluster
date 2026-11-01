using Sandbox;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.GameSystems;
using System.Collections.Generic;
using System.Linq;
using VRage.Game.Entity;
using VRageMath;

namespace ClusterPlugin;

/// <summary>
/// Seam holder for the cluster members of <c>MyLaserAntenna</c>.
/// The connection state machine's peer
/// checks read the real MyLaserAntenna, which is null for a cross-node peer present here only as a
/// proxy; the cluster supplies the same facts through <see cref="ClusterLaserLink.Peer"/> from its
/// 1 Hz antenna graph reports instead. The seam delegate stays on <see cref="ClusterLaserLink"/>:
/// assigning a static on MyLaserAntenna would run its static constructor, which dereferences
/// MySession.Static long before a session exists at cluster install time.
///
/// The proxy-peer branches of ConnectTo / DoConnectTo / the
/// UpdateAfterSimulation100 state machine / SetupLaseTargetCoords live here as helpers and are
/// wired in by <c>Shared.Patches.MyLaserAntennaPatch</c>. Every helper short-circuits to vanilla
/// while the seam is null.
/// </summary>
public static class MyLaserAntennaSeams
{
	extension(MyLaserAntenna self)
	{
		/// <summary>The local facts a remote node needs to run its side of the cross-node handshake.</summary>
		public ClusterLaserLink.PeerInfo ClusterLaserSnapshot => new ClusterLaserLink.PeerInfo
		{
			TargetId = self.m_targetId ?? 0L,
			State = (byte)self.State,
			Powered = self.Enabled && self.IsFunctional
				&& self.ResourceSink.SuppliedRatioByType(MyResourceDistributorComponent.ElectricityId) > 0.99f,
			RotationFinished = self.m_rotationFinished,
			HeadPos = self.HeadPos
		};

		/// <summary>ClusterPlugin entry to the internal pairing flow (test harness and cluster tooling).</summary>
		public bool ClusterConnectTo(long targetAntennaId) => self.ConnectTo(targetAntennaId);
	}

	/// <summary>Null when the antenna is a real local block (vanilla handles it) or no remote peer is known.</summary>
	public static ClusterLaserLink.PeerInfo? ClusterPeerFor(long id)
	{
		if (MyLaserAntenna.GetLaserById(id) != null)
		{
			return null;
		}
		return ClusterLaserLink.Peer?.Invoke(id);
	}

	/// <summary>
	/// The proxy branch of <c>ConnectTo</c>, taken only where vanilla had already decided
	/// <c>GetLaserById(DestId) == null</c> and would return false. Returns the method's result.
	/// </summary>
	internal static bool ConnectToProxy(MyLaserAntenna self, long destId)
	{
		// Cluster: the target may live on another node, present here only as a proxy. Pair the local
		// side against the proxy; the remote side is paired on its own node - there is no object here
		// to call ConnectToRec on, which is exactly why the state machine reads ClusterLaserPeer. That
		// node points it back when it sees this link request in the antenna graph (PointBack).
		MyLaserBroadcaster proxyBroadcaster = MyLaserAntenna.GetLaserBroadcasterById(destId);
		if (proxyBroadcaster == null || ClusterPeerFor(destId) == null)
		{
			return false;
		}
		if (!proxyBroadcaster.CanBeUsedByPlayer(self.OwnerId) || !self.Broadcaster.CanBeUsedByPlayer(proxyBroadcaster.Owner))
		{
			return false;
		}
		self.IdleOther();
		self.DoConnectTo(destId);
		return true;
	}

	/// <summary>
	/// SE1-0235: the <c>target.ConnectToRec(this)</c> that vanilla's <c>ConnectTo</c> ends with, run on the
	/// target's node once the antenna graph shows <paramref name="callerId"/>, a laser on another node,
	/// linking to <paramref name="self"/>. <c>ConnectToRec</c> takes the proxy branch
	/// (<see cref="ConnectToProxy"/>), with its ownership checks, and tells the attached clients.
	/// Unlike vanilla only an idle laser is taken: one that is linked, linking or searching keeps its
	/// target, so no link is stolen and two callers cannot take turns with one laser.
	/// </summary>
	internal static bool PointBack(MyLaserAntenna self, long callerId)
	{
		if (self.State != MyLaserAntenna.StateEnum.idle)
		{
			return false;
		}
		self.ConnectToRec(callerId);
		return self.m_targetId == callerId;
	}

	/// <summary>
	/// The <c>DoConnectTo</c> body with a proxy-peer branch in place of the vanilla
	/// "???" fallback. Only called when that branch would have been taken (no real target, no explicit
	/// coords+name, a known remote peer), so the vanilla ordering of the side effects is preserved.
	/// </summary>
	internal static void DoConnectToProxy(MyLaserAntenna self, long destId, ClusterLaserLink.PeerInfo clusterPeer)
	{
		self.State = MyLaserAntenna.StateEnum.rot_Rec;
		if (Sync.IsServer)
		{
			self.m_permanentConnection.Value = false;
		}
		self.m_targetId = destId;
		self.Broadcaster.RaiseChangeSuccessfullyContacting();
		self.m_rotationInterval_ms = MySandboxGame.TotalGamePlayTimeInMilliseconds;
		self.m_targetCoords = clusterPeer.HeadPos;
		self.m_lastKnownTargetName.Clear().Append(MyLaserAntenna.GetLaserBroadcasterById(destId)?.Entity is MyProxyAntenna proxy
			? proxy.Name : "???");
		self.ResourceSink.Update();
		self.Receiver.UpdateBroadcastersInRange();
		MyLaserAntenna.UpdateVisuals();
		self.SetDetailedInfoDirty();
		self.RaisePropertiesChanged();
		self.UpdateEmissivity();
		self.UpdateMyStateText();
	}

	/// <summary>
	/// An added <c>else if</c> in <c>SetupLaseTargetCoords</c>: vanilla left
	/// m_canLaseTargetCoords false because the target is not a real local antenna; a powered remote peer
	/// aimed at us through a usable proxy lases just the same.
	/// </summary>
	internal static void SetupLaseTargetCoordsProxy(MyLaserAntenna self)
	{
		if (!self.m_rotationFinished || !self.m_wasVisible || !self.m_targetId.HasValue)
		{
			return;
		}
		long targetId = self.m_targetId.Value;
		if (MyLaserAntenna.GetLaserById(targetId) != null)
		{
			return;
		}
		if (ClusterPeerFor(targetId) is ClusterLaserLink.PeerInfo clusterPeer
			&& MyLaserAntenna.GetLaserBroadcasterById(targetId) is MyLaserBroadcaster clusterProxy
			&& clusterPeer.TargetId == self.EntityId && clusterPeer.Powered
			&& self.IsInRange(clusterProxy.Entity as MyEntity)
			&& clusterProxy.CanBeUsedByPlayer(self.OwnerId) && self.Broadcaster.CanBeUsedByPlayer(clusterProxy.Owner))
		{
			self.m_canLaseTargetCoords.Value = true;
		}
	}

	/// <summary>
	/// SE1-0226: what the vanilla <c>search_GPS</c> loop iterates instead of <c>LaserAntennas</c>. The loop
	/// reads <c>RealAntenna.Enabled</c> of every entry, and a proxy laser has no real antenna, so on a node
	/// holding any laser proxy a GPS search threw a NullReferenceException every 100 frames. Vanilla never
	/// met a proxy there: only clients had them, and clients leave before the loop.
	/// </summary>
	public static Dictionary<long, MyLaserBroadcaster> RealLasers(MyAntennaSystem antennas)
	{
		Dictionary<long, MyLaserBroadcaster> lasers = antennas.LaserAntennas;
		return lasers.Values.All(laser => laser.RealAntenna != null) ? lasers
			: lasers.Where(pair => pair.Value.RealAntenna != null).ToDictionary(pair => pair.Key, pair => pair.Value);
	}

	/// <summary>
	/// SE1-0226: the <c>search_GPS</c> case for a laser hosted on another node, run after vanilla's search
	/// of the real local lasers found nothing (it would have left <c>search_GPS</c> or set an error). The
	/// same rules as vanilla's loop, with the far side's facts from its antenna graph entry: powered, head
	/// within 10 m of the coordinates, usable both ways; an idle one wins, otherwise the nearest. Pairing
	/// goes through <c>ConnectToRec</c>, whose <c>ConnectTo</c> takes the proxy branch
	/// (<see cref="ConnectToProxy"/>): this side is pointed here, and the far side by its own node if it
	/// is idle (SE1-0235, <see cref="PointBack"/>), as with a link made by id. Vanilla's permanent-link
	/// preference is not applied, the graph entry does not carry that flag.
	/// </summary>
	internal static void SearchGpsProxy(MyLaserAntenna self)
	{
		if (!Sync.IsServer || self.State != MyLaserAntenna.StateEnum.search_GPS || !self.m_rotationFinished
			|| self.m_connectionError.Value != MyLaserAntenna.ConnectionError.None || self.m_OnlyPermanentExists
			|| !self.Enabled || !self.IsFunctional
			|| self.ResourceSink.SuppliedRatioByType(MyResourceDistributorComponent.ElectricityId) <= 0.99f)
		{
			return;
		}
		MyLaserBroadcaster found = null;
		ClusterLaserLink.PeerInfo foundPeer = default;
		double nearest = double.MaxValue;
		foreach (MyLaserBroadcaster laser in MyAntennaSystem.Static.LaserAntennas.Values)
		{
			if (laser.RealAntenna != null || ClusterPeerFor(laser.AntennaEntityId) is not ClusterLaserLink.PeerInfo peer
				|| !peer.Powered)
			{
				continue;
			}
			double distance = Vector3D.DistanceSquared(peer.HeadPos, self.m_targetCoords);
			if (distance >= 100.0 || !laser.CanBeUsedByPlayer(self.OwnerId) || !self.Broadcaster.CanBeUsedByPlayer(laser.Owner))
			{
				continue;
			}
			if (peer.State == (byte)MyLaserAntenna.StateEnum.idle)
			{
				found = laser;
				foundPeer = peer;
				break;
			}
			if (distance < nearest)
			{
				nearest = distance;
				found = laser;
				foundPeer = peer;
			}
		}
		if (found == null)
		{
			return;
		}
		if (!self.IsInRange(found.Entity as MyEntity))
		{
			self.m_connectionError.Value = MyLaserAntenna.ConnectionError.OutOfRange;
		}
		else if (!self.LosTest(foundPeer.HeadPos))
		{
			self.m_connectionError.Value = MyLaserAntenna.ConnectionError.NoLineOfSight;
		}
		else
		{
			self.ConnectToRec(found.AntennaEntityId);
		}
	}

	/// <summary>
	/// <c>contact_Rec</c> case of the state machine with <c>laserById == null</c>. True means the case
	/// breaks here (the proxy branch was taken); false lets vanilla continue, which for a null
	/// laserById is its own break.
	/// </summary>
	internal static bool ContactWithProxyPeer(MyLaserAntenna self, long targetId)
	{
		if (Sync.IsServer
			&& ClusterPeerFor(targetId) is ClusterLaserLink.PeerInfo clusterPeer
			&& MyLaserAntenna.GetLaserBroadcasterById(targetId) is MyLaserBroadcaster clusterProxy
			&& clusterPeer.TargetId == self.EntityId
			&& clusterPeer.State >= (byte)MyLaserAntenna.StateEnum.rot_Rec
			&& clusterPeer.Powered
			&& self.IsInRange(clusterProxy.Entity as MyEntity)
			&& clusterProxy.CanBeUsedByPlayer(self.OwnerId) && self.Broadcaster.CanBeUsedByPlayer(clusterProxy.Owner)
			&& !(Vector3D.DistanceSquared(clusterPeer.HeadPos, self.m_targetCoords) > 100.0)
			&& self.LosTest(clusterPeer.HeadPos))
		{
			// Cross-node peer: its own node runs the same dance against this antenna's proxy, so there is
			// nothing to write into here - once the peer has rotated onto us and reached contact, connect.
			if (clusterPeer.RotationFinished && clusterPeer.State >= (byte)MyLaserAntenna.StateEnum.contact_Rec)
			{
				self.m_canLaseTargetCoords.Value = true;
				self.ShiftModeSync(MyLaserAntenna.StateEnum.connected);
			}
			return true;
		}
		return false;
	}

	/// <summary>
	/// <c>connected</c> case of the state machine with <c>laserById == null</c>. True means the link is
	/// held through the proxy and the case breaks without falling back to contact_Rec; false lets vanilla
	/// run its own <c>ShiftModeSync(contact_Rec); break;</c>.
	/// </summary>
	internal static bool HoldWithProxyPeer(MyLaserAntenna self, long targetId)
	{
		// Cross-node peer held through its proxy: accept contact_Rec as well as connected - the two
		// sides cross into connected a report cycle apart, and demanding connected on both would
		// flap this side back to contact just for observing the peer mid-transition.
		if (ClusterPeerFor(targetId) is ClusterLaserLink.PeerInfo holdPeer
			&& MyLaserAntenna.GetLaserBroadcasterById(targetId) is MyLaserBroadcaster holdProxy
			&& holdPeer.TargetId == self.EntityId
			&& holdPeer.State >= (byte)MyLaserAntenna.StateEnum.contact_Rec
			&& holdPeer.Powered && holdPeer.RotationFinished
			&& self.IsInRange(holdProxy.Entity as MyEntity)
			&& holdProxy.CanBeUsedByPlayer(self.OwnerId) && self.Broadcaster.CanBeUsedByPlayer(holdProxy.Owner)
			&& self.LosTest(holdPeer.HeadPos))
		{
			self.m_targetCoords = holdPeer.HeadPos;
			self.m_canLaseTargetCoords.Value = true;
			return true;
		}
		return false;
	}
}
