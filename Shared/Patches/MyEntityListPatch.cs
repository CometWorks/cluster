using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Game.World;
using VRage.Game.Entity;

namespace Shared.Patches;

/// <summary>
/// T-0180, Entity List half: show the entities vanilla drops, and every player's GPS markers.
///
/// <c>MyEntityList.GetEntityList</c> is a switch whose cases each call one of the
/// <c>GenerateInfo_*</c> builders and DISCARD the entity when none of them returns a row - so a
/// missile, a meteor, a safe zone or a voxel map is simply absent from every type in the combo.
/// The client half needs nothing: <c>EntityListResponse</c> only sorts and adds rows, a row click
/// attaches the camera by id or falls back to the server-sent <c>Position</c>, and the row actions
/// go by id to <c>ProceedEntitiesAction_Implementation</c>.
///
/// TWO ADDITIONS, both under <c>NotReplicated</c>.
///
/// 1. THE MISSING ENTITIES. Rather than reimplement the builders' kind tests - which would be a
///    second copy of a vanilla decision, free to drift from it - this compares the ids vanilla
///    returned against the entities it was given and adds a row for each one it dropped. That is
///    correct by construction whatever the builders do, including after a game update that changes
///    them.
///
/// 2. THE GPS MARKERS. The server holds every identity's markers in
///    <c>MyGpsCollection.m_playerGpss</c>; a client only ever receives its OWN, because the world
///    snapshot strips other identities' GPS (<c>MyMultiplayerServerBase</c>), and the <c>Gps</c>
///    cycling order never reaches the server at all - the Admin screen handles it client-locally
///    over the admin's own list. So the only way an admin sees another player's marker is if the
///    server puts it in a list the client already renders. A synthetic row with the marker's coords
///    as <c>Position</c> gives a working list entry, spectator jump and Teleport here.
///
/// WHY NotReplicated IS THE RIGHT HOME for both: it is the one type whose name is already honest
/// about the rows being things the client does not hold, and neither addition is replicated. A GPS
/// marker is not an entity at all, so no other type could be truthful about it.
///
/// SYNTHETIC IDS ARE NEGATIVE AND NEVER COLLIDE with a real EntityId, which is what keeps a Remove
/// or Stop aimed at a GPS row from hitting an unrelated entity: <c>MyEntities</c> ids are positive,
/// <c>ProceedEntityAction</c> looks the id up and does nothing when it misses. The row is therefore
/// inert for every action except the two that use <c>Position</c>, which are the two that make
/// sense for a marker.
///
/// CLUSTER_ADMIN_WIDEN=0 restores vanilla behaviour.
/// </summary>
public static class MyEntityListPatch
{
	/// <summary>Rows per list, so a world with thousands of markers cannot make the screen unusable.</summary>
	private const int MaxGpsRows = 500;

	[HarmonyPatch(typeof(MyEntityList), nameof(MyEntityList.GetEntityList),
		new[] { typeof(MyEntityList.MyEntityTypeEnum), typeof(IEnumerable<MyEntity>) })]
	private static class GetEntityList
	{
		[HarmonyPostfix]
		private static void Postfix(MyEntityList.MyEntityTypeEnum selectedType,
			IEnumerable<MyEntity> entities, List<MyEntityList.MyEntityListInfoItem> __result)
		{
			if (!MyEntityCyclingPatch.Enabled || __result == null) return;
			if (selectedType != MyEntityList.MyEntityTypeEnum.NotReplicated) return;
			try
			{
				AddDroppedEntities(entities, __result);
				AddGpsMarkers(__result);
			}
			catch (Exception exception)
			{
				// This runs inside a [Server(SpaceMasterOnly)] event handler. A throw here would
				// lose the admin's whole list, and an incomplete list beats no list at all.
				Console.WriteLine("[ClusterPlugin] admin entity list widening failed: " + exception);
			}
		}
	}

	private static void AddDroppedEntities(IEnumerable<MyEntity> entities,
		List<MyEntityList.MyEntityListInfoItem> rows)
	{
		if (entities == null) return;
		var present = new HashSet<long>(rows.Select(row => row.EntityId));
		foreach (MyEntity entity in entities)
		{
			if (entity == null || entity.IsReplicated || !present.Add(entity.EntityId)) continue;
			rows.Add(new MyEntityList.MyEntityListInfoItem
			{
				DisplayName = Describe(entity),
				EntityId = entity.EntityId,
				Position = entity.PositionComp?.GetPosition() ?? default,
				IsGrid = false,
				IsReplicated = false,
			});
		}
	}

	/// <summary>Type name plus the entity's own name when it has one - "MyMeteor" alone is useless
	/// in a list of forty of them, and DisplayName is the only column a synthetic row can speak
	/// through.</summary>
	private static string Describe(MyEntity entity)
	{
		string kind = entity.GetType().Name;
		string name = entity.DisplayName;
		return string.IsNullOrEmpty(name) || name == kind ? kind : kind + ": " + name;
	}

	private static void AddGpsMarkers(List<MyEntityList.MyEntityListInfoItem> rows)
	{
		MyGpsCollection gpss = MySession.Static?.Gpss;
		if (gpss == null) return;
		long synthetic = -1L;
		int added = 0;
		foreach (KeyValuePair<long, Dictionary<int, MyGps>> owner in gpss.m_playerGpss)
		{
			string who = MySession.Static.Players.TryGetIdentity(owner.Key)?.DisplayName
				?? owner.Key.ToString();
			foreach (MyGps gps in owner.Value.Values)
			{
				if (gps == null) continue;
				if (added++ >= MaxGpsRows)
				{
					rows.Add(new MyEntityList.MyEntityListInfoItem
					{
						DisplayName = "GPS: ... list truncated at " + MaxGpsRows + " markers",
						EntityId = synthetic,
						IsGrid = false,
						IsReplicated = false,
					});
					return;
				}
				rows.Add(new MyEntityList.MyEntityListInfoItem
				{
					DisplayName = "GPS " + who + ": " + gps.Name,
					EntityId = synthetic--,
					Position = gps.Coords,
					IsGrid = false,
					IsReplicated = false,
				});
			}
		}
	}
}
