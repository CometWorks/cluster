using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Weapons;
using SpaceEngineers.Game.Entities.Blocks;
using SpaceEngineers.Game.Entities.Weapons;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.ObjectBuilders;
using VRage.ObjectBuilders.Private;
using VRageMath;

using ClusterPlugin;

namespace ClusterTestPlugin;

/// <summary>
/// P3-HND-01/02 and P3-HND-DUMMY fixture seam. Spawns content-rich and interaction-state fixtures whose serialized
/// object builders exercise the Partitioning §6.10 round-trip gate the way real worlds do: loaded
/// turrets (GunBase RemainingMagazines), inventories with items, landing gear near a lock candidate
/// (ReadyToLock), connectors in range, merge blocks held apart, tools mid-operation and subgrids
/// mid-motion. All fixtures are DYNAMIC grids: a static member anchors its partition
/// (PartitionManager) and an anchored partition never crosses the node boundary at all.
/// </summary>
public static class RichTestHarness
{
	private static int m_fixtureCounter;

	public static object Spawn(string kind, double x, double y, double z, long builtBy = 0)
	{
		var origin = new Vector3D(x, y, z);
		List<MyEntity> spawned = kind switch
		{
			"rich" => SpawnRich(origin, builtBy),
			"gearpair" => SpawnGearPair(origin, builtBy),
			"connectorpair" => SpawnConnectorPair(origin, builtBy),
			"mergepair" => SpawnMergePair(origin, builtBy),
			"grinderrig" => SpawnGrinderRig(origin, builtBy),
			"rotorrig" => SpawnRotorRig(origin, builtBy),
			"pistonrig" => SpawnPistonRig(origin, builtBy),
			"antennaship" => SpawnAntennaShip(origin, builtBy),
			"targetdummy" => SpawnTargetDummy(origin, builtBy),
			"lounge" => SpawnLounge(origin, builtBy),
			_ => throw new ArgumentException($"Unknown fixture kind '{kind}'"),
		};
		return new
		{
			kind,
			grids = spawned.Select(entity => new
			{
				grid = entity.EntityId,
				name = entity.DisplayName,
				blocks = (entity as MyCubeGrid)?.BlocksCount ?? 0,
			}).ToArray(),
		};
	}

	// -- fixture builders ---------------------------------------------------

	/// <summary>One dynamic grid carrying every content family from the three live findings:
	/// a loaded gatling turret (GunBase magazines), an interior turret, a cockpit and a cargo
	/// container with inventory items, power blocks, a connector, a decoy and a landing gear.</summary>
	private static List<MyEntity> SpawnRich(Vector3D origin, long builtBy)
	{
		GridBuilder grid = new GridBuilder("cluster-rich-fixture", origin, builtBy);
		// Armor bed the functional blocks stand on: every one of them mounts on its base face.
		for (int x = 0; x <= 11; x++)
			for (int z = 0; z <= 2; z++)
				grid.Armor(x, -1, z);
		grid.Add<MyObjectBuilder_Cockpit>("LargeBlockCockpit", 0, 0, 1);
		grid.Add<MyObjectBuilder_CargoContainer>("LargeBlockSmallContainer", 1, 0, 1);
		var battery = grid.Add<MyObjectBuilder_BatteryBlock>("LargeBlockBatteryBlock", 2, 0, 1);
		battery.CurrentStoredPower = 3f;
		grid.Add<MyObjectBuilder_Reactor>("LargeBlockSmallGenerator", 3, 0, 1);
		grid.Add<MyObjectBuilder_Gyro>("LargeBlockGyro", 4, 0, 1);
		grid.Add<MyObjectBuilder_ShipConnector>("Connector", 5, 0, 1);
		grid.Add<MyObjectBuilder_Decoy>("LargeDecoy", 6, 0, 1);
		var gatling = grid.Add<MyObjectBuilder_LargeGatlingTurret>(null, 7, 0, 0);
		gatling.GunBase = new MyObjectBuilder_GunBase
		{
			CurrentAmmoMagazineName = "NATO_25x184mm",
			RemainingAmmo = 61,
			RemainingMagazines = 3,
		};
		var interior = grid.Add<MyObjectBuilder_InteriorTurret>("LargeInteriorTurret", 10, 0, 1);
		interior.GunBase = new MyObjectBuilder_GunBase
		{
			CurrentAmmoMagazineName = "NATO_5p56x45mm",
			RemainingAmmo = 17,
			RemainingMagazines = 2,
		};
		var gear = grid.Add<MyObjectBuilder_LandingGear>("LargeBlockLandingGear", 12, -1, 0);
		gear.AutoLock = false;
		MyEntity built = grid.Build();
		// Inventory content goes in through the live inventories: the modern OBs carry them in
		// component containers, and the game builds those for us on spawn.
		Fill<MyCargoContainer>(built,
			(NewItem<MyObjectBuilder_Component>("SteelPlate"), 120),
			(NewItem<MyObjectBuilder_Component>("Construction"), 60),
			(NewItem<MyObjectBuilder_Ingot>("Uranium"), 40),
			(NewItem<MyObjectBuilder_AmmoMagazine>("NATO_25x184mm"), 6));
		Fill<MyReactor>(built, (NewItem<MyObjectBuilder_Ingot>("Uranium"), 25));
		Fill<MyLargeGatlingTurret>(built, (NewItem<MyObjectBuilder_AmmoMagazine>("NATO_25x184mm"), 4));
		Fill<MyLargeInteriorTurret>(built, (NewItem<MyObjectBuilder_AmmoMagazine>("NATO_5p56x45mm"), 3));
		return new List<MyEntity> { built };
	}

	/// <summary>T-0392: a small ship as ShipAntennaService (T-0186) sees a pilot's ship - DYNAMIC, a cockpit
	/// and a radio antenna that is ON (Enabled, broadcasting) - the shape of the 7 'Visitor Ion' ships whose
	/// antennas the reconcile switched off between activation slices (residuals=7, 2026-10-05 00:22:56).</summary>
	private static List<MyEntity> SpawnAntennaShip(Vector3D origin, long builtBy)
	{
		GridBuilder ship = new GridBuilder("cluster-antenna-ship", origin, builtBy);
		for (int x = 0; x <= 4; x++)
			for (int z = 0; z <= 1; z++)
				ship.Armor(x, -1, z);
		ship.Add<MyObjectBuilder_Cockpit>("LargeBlockCockpit", 0, 0, 1);
		ship.Add<MyObjectBuilder_BatteryBlock>("LargeBlockBatteryBlock", 1, 0, 1).CurrentStoredPower = 3f;
		// The large radio antenna is a tall block: give it a column of its own.
		var antenna = ship.Add<MyObjectBuilder_RadioAntenna>("LargeBlockRadioAntenna", 4, 0, 0);
		antenna.Enabled = true;
		antenna.EnableBroadcasting = true;
		antenna.BroadcastRadius = 500f;
		return new List<MyEntity> { ship.Build() };
	}

	/// <summary>SE-1083: two seats on one grid, like the lounge of a station where a crew of traders waits.</summary>
	private static List<MyEntity> SpawnLounge(Vector3D origin, long builtBy)
	{
		GridBuilder lounge = new GridBuilder("cluster-lounge", origin, builtBy);
		for (int x = 0; x <= 3; x++)
			for (int z = 0; z <= 1; z++)
				lounge.Armor(x, -1, z);
		lounge.Add<MyObjectBuilder_Cockpit>("LargeBlockCockpit", 0, 0, 1);
		lounge.Add<MyObjectBuilder_Cockpit>("LargeBlockCockpit", 2, 0, 1);
		lounge.Add<MyObjectBuilder_BatteryBlock>("LargeBlockBatteryBlock", 3, 0, 0).CurrentStoredPower = 3f;
		return new List<MyEntity> { lounge.Build() };
	}

	/// <summary>SE1-0225: a target dummy on an armor bed. The dummy writes its destroyed state only once
	/// its subparts exist, which a freshly imported block has not reached yet.</summary>
	private static List<MyEntity> SpawnTargetDummy(Vector3D origin, long builtBy)
	{
		GridBuilder grid = new GridBuilder("cluster-target-dummy", origin, builtBy);
		grid.Armor(0, -1, 0);
		grid.Armor(1, -1, 0);
		grid.Add<SpaceEngineers.ObjectBuilders.ObjectBuilders.MyObjectBuilder_TargetDummyBlock>("TargetDummy", 0, 0, 0);
		MyEntity built = grid.Build();
		// A new dummy starts destroyed and, in Survival, restores only from steel plates in its inventory.
		Fill<MyTargetDummyBlock>(built, (NewItem<MyObjectBuilder_Component>("SteelPlate"), 5));
		return new List<MyEntity> { built };
	}

	/// <summary>A ship whose unlocked magnetic gear hangs just above a separate dynamic slab:
	/// the runtime drives the gear into ReadyToLock, the state the landing-gear finding lives in.
	/// The slab sits close enough for the gear's detection box and far enough not to collide.</summary>
	private static List<MyEntity> SpawnGearPair(Vector3D origin, long builtBy)
	{
		GridBuilder ship = new GridBuilder("cluster-gear-ship", origin, builtBy);
		for (int z = 0; z <= 2; z++)
			ship.Armor(0, 0, z);
		var gear = ship.Add<MyObjectBuilder_LandingGear>("LargeBlockLandingGear", 0, -2, 0);
		gear.AutoLock = false;
		GridBuilder slab = new GridBuilder("cluster-gear-slab", origin + new Vector3D(0, -7.0, 0), builtBy);
		for (int x = -1; x <= 1; x++)
			for (int z = -1; z <= 3; z++)
				slab.Armor(x, 0, z);
		return new List<MyEntity> { ship.Build(), slab.Build() };
	}

	/// <summary>Two dynamic grids with connectors facing each other inside connect range but not
	/// connected. Act("connect") on either side completes the lock post-handover.</summary>
	private static List<MyEntity> SpawnConnectorPair(Vector3D origin, long builtBy)
	{
		// Connector connect face is the block's Forward face (-Z by default). Grid B is offset
		// along +Z with its connector facing back (-Z toward A? no: A faces -Z, so B sits at -Z
		// of A and faces +Z = Backward orientation).
		GridBuilder a = new GridBuilder("cluster-connector-a", origin, builtBy);
		a.Armor(0, 0, 1);
		a.Add<MyObjectBuilder_ShipConnector>("Connector", 0, 0, 0);
		a.Add<MyObjectBuilder_Reactor>("LargeBlockSmallGenerator", 0, 1, 1);
		// The pair: one 2.5 m block plus a 0.3 m gap between the connector front faces -
		// inside connect range, outside collision.
		GridBuilder b = new GridBuilder("cluster-connector-b", origin + new Vector3D(0, 0, -2.8), builtBy);
		b.Armor(0, 0, -1);
		b.Add<MyObjectBuilder_ShipConnector>("Connector", 0, 0, 0,
			new SerializableBlockOrientation(Base6Directions.Direction.Backward, Base6Directions.Direction.Up));
		b.Add<MyObjectBuilder_Reactor>("LargeBlockSmallGenerator", 0, 1, -1);
		MyEntity builtA = a.Build(), builtB = b.Build();
		Fill<MyReactor>(builtA, (NewItem<MyObjectBuilder_Ingot>("Uranium"), 10));
		Fill<MyReactor>(builtB, (NewItem<MyObjectBuilder_Ingot>("Uranium"), 10));
		return new List<MyEntity> { builtA, builtB };
	}

	/// <summary>Two dynamic grids with merge blocks aligned inside merge range, held apart by one
	/// side being disabled. Act("merge-enable") post-handover completes the merge (grid count 2 -> 1).
	/// The merge plate sits on the block's RIGHT face (model dummy), so the pair is laid out along
	/// X with the far side yawed 180 degrees - plates 0.1 m apart, inside merge range.</summary>
	private static List<MyEntity> SpawnMergePair(Vector3D origin, long builtBy)
	{
		GridBuilder a = new GridBuilder("cluster-merge-a", origin, builtBy);
		a.Armor(0, 0, 1);
		a.Add<MyObjectBuilder_MergeBlock>("LargeShipMergeBlock", 0, 0, 0);
		a.Add<MyObjectBuilder_Reactor>("LargeBlockSmallGenerator", 0, 1, 1);
		GridBuilder b = new GridBuilder("cluster-merge-b", origin + new Vector3D(2.6, 0, 0), builtBy);
		b.Armor(0, 0, -1);
		var merge = b.Add<MyObjectBuilder_MergeBlock>("LargeShipMergeBlock", 0, 0, 0,
			new SerializableBlockOrientation(Base6Directions.Direction.Backward, Base6Directions.Direction.Up));
		merge.Enabled = false;
		b.Add<MyObjectBuilder_Reactor>("LargeBlockSmallGenerator", 0, 1, -1);
		MyEntity builtA = a.Build(), builtB = b.Build();
		Fill<MyReactor>(builtA, (NewItem<MyObjectBuilder_Ingot>("Uranium"), 10));
		Fill<MyReactor>(builtB, (NewItem<MyObjectBuilder_Ingot>("Uranium"), 10));
		return new List<MyEntity> { builtA, builtB };
	}

	/// <summary>A powered grinder rig facing a separate victim grid: the grinder runs while the
	/// partition crosses the boundary, the grinder-mid-operation cell of the §6.10 matrix.</summary>
	private static List<MyEntity> SpawnGrinderRig(Vector3D origin, long builtBy)
	{
		GridBuilder rig = new GridBuilder("cluster-grinder-rig", origin, builtBy);
		rig.Armor(0, 0, 2);
		var grinder = rig.Add<MyObjectBuilder_ShipGrinder>("LargeShipGrinder", 0, 0, 0);
		grinder.Enabled = true;
		rig.Add<MyObjectBuilder_Reactor>("LargeBlockSmallGenerator", 0, 1, 2);
		// The grind sphere is grid-local (0,0,-0.33) r=1.4 (probed via DetectorSphere): only a
		// block whose near face is within ~1.07 m of the grinder front can ever be ground. Heavy
		// armor keeps that one block alive long enough to cross mid-grind.
		GridBuilder victim = new GridBuilder("cluster-grinder-victim", origin + new Vector3D(0, 0, -2.6), builtBy);
		for (int y = -1; y <= 1; y++)
			victim.Armor(0, y, 0, "LargeHeavyBlockArmorBlock");
		MyEntity builtRig = rig.Build();
		Fill<MyReactor>(builtRig, (NewItem<MyObjectBuilder_Ingot>("Uranium"), 10));
		return new List<MyEntity> { builtRig, victim.Build() };
	}

	/// <summary>A powered rotor spinning under velocity: Act("top") creates the subgrid head,
	/// giving the partition a mechanical subgrid mid-motion.</summary>
	private static List<MyEntity> SpawnRotorRig(Vector3D origin, long builtBy)
	{
		GridBuilder rig = new GridBuilder("cluster-rotor-rig", origin, builtBy);
		rig.Armor(0, -1, 0);
		rig.Armor(1, -1, 0);
		var stator = rig.Add<MyObjectBuilder_MotorAdvancedStator>("LargeAdvancedStator", 0, 0, 0);
		stator.TargetVelocity = 0.5f;
		stator.LimitsActive = false;
		rig.Add<MyObjectBuilder_Reactor>("LargeBlockSmallGenerator", 1, 0, 0);
		MyEntity builtRig = rig.Build();
		Fill<MyReactor>(builtRig, (NewItem<MyObjectBuilder_Ingot>("Uranium"), 10));
		return new List<MyEntity> { builtRig };
	}

	/// <summary>A powered piston extending under velocity: Act("top") creates the head subgrid.</summary>
	private static List<MyEntity> SpawnPistonRig(Vector3D origin, long builtBy)
	{
		GridBuilder rig = new GridBuilder("cluster-piston-rig", origin, builtBy);
		rig.Armor(0, -1, 0);
		rig.Armor(1, -1, 0);
		var piston = rig.Add<MyObjectBuilder_ExtendedPistonBase>("LargePistonBase", 0, 0, 0);
		piston.Velocity = 0.4f;
		rig.Add<MyObjectBuilder_Reactor>("LargeBlockSmallGenerator", 1, 0, 0);
		MyEntity builtRig = rig.Build();
		Fill<MyReactor>(builtRig, (NewItem<MyObjectBuilder_Ingot>("Uranium"), 10));
		return new List<MyEntity> { builtRig };
	}

	// -- actions --------------------------------------------------------------

	/// <summary>Drives an interaction-state transition on a fixture grid. The target selector is
	/// the block type, so the scenario never needs block entity ids.</summary>
	public static object Act(long gridId, string action, long targetId = 0)
	{
		if (!MyEntities.TryGetEntityById(gridId, out MyEntity entity))
			throw new InvalidOperationException($"Entity {gridId} is not on this node");
		// The character-coupling cells (§3.2 attachment table) address the CHARACTER, not a grid.
		if (action == "dampen" || action == "undampen")
		{
			var character = entity as Sandbox.Game.Entities.Character.MyCharacter
				?? throw new InvalidOperationException($"Entity {gridId} is not a character");
			if (action == "dampen")
			{
				if (!MyEntities.TryGetEntityById(targetId, out MyEntity dampener))
					throw new InvalidOperationException($"Dampening target {targetId} is not on this node");
				character.RelativeDampeningEntity = dampener;
			}
			else
				character.RelativeDampeningEntity = null;
			return new { entity = gridId, action, dampenedTo = character.RelativeDampeningEntity?.EntityId ?? 0 };
		}
		if (entity is not MyCubeGrid grid)
			throw new InvalidOperationException($"Grid {gridId} is not on this node");
		switch (action)
		{
			case "seat":
			case "unseat":
			{
				var cockpit = Fat<MyCockpit>(grid);
				if (action == "seat")
				{
					// The first free seat, so a second rider takes the lounge's second seat (SE-1083).
					cockpit = grid.GetFatBlocks().OfType<MyCockpit>().FirstOrDefault(seat => seat.Pilot == null) ?? cockpit;
					if (!MyEntities.TryGetEntityById(targetId, out MyEntity rider)
						|| rider is not Sandbox.Game.Entities.Character.MyCharacter character)
						throw new InvalidOperationException($"Seat rider {targetId} is not a character on this node");
					cockpit.AttachPilot(character, 0);
				}
				else
					cockpit.RemovePilot();
				return new { grid = gridId, action, pilot = cockpit.Pilot?.EntityId ?? 0 };
			}
			case "gear-lock":
			case "gear-unlock":
			{
				var gear = Fat<SpaceEngineers.Game.ModAPI.IMyLandingGear>(grid);
				if (action == "gear-lock") gear.Lock();
				else gear.Unlock();
				return new { grid = gridId, action, lockMode = gear.LockMode.ToString() };
			}
			case "connect":
			case "disconnect":
			{
				var connector = Fat<MyShipConnector>(grid);
				if (action == "connect") connector.TryConnect();
				else connector.TryDisconnect();
				return new { grid = gridId, action, status = ((Sandbox.ModAPI.Ingame.IMyShipConnector)connector).Status.ToString() };
			}
			case "merge-enable":
			{
				var merge = Fat<MyShipMergeBlock>(grid);
				merge.Enabled = true;
				return new { grid = gridId, action, enabled = merge.Enabled };
			}
			case "top":
			{
				// RecreateTop is protected on MyMechanicalConnectionBlockBase; this is a test seam,
				// and reflection beats forking the block class for one call.
				var mechanical = (MyMechanicalConnectionBlockBase)(MyCubeBlock)Fat<MyMechanicalConnectionBlockBase>(grid);
				MethodInfo recreate = typeof(MyMechanicalConnectionBlockBase).GetMethod(
					"RecreateTop", BindingFlags.Instance | BindingFlags.NonPublic)
					?? throw new InvalidOperationException("RecreateTop not found");
				ParameterInfo[] parameters = recreate.GetParameters();
				object[] arguments = parameters.Select(parameter =>
					parameter.ParameterType == typeof(bool) ? (object)true : parameter.DefaultValue).ToArray();
				if (parameters.Length > 0 && parameters[0].ParameterType == typeof(long?))
					arguments[0] = null;
				recreate.Invoke(mechanical, arguments);
				return new { grid = gridId, action, hasTop = mechanical.TopBlock != null };
			}
			case "stale-limits":
			{
				// SE-1416: the stale LimitsActive a headless stator keeps on a live node. The game only
				// recomputes the private flag on a limit change or while a top is attached, so flipping it
				// here leaves the value a load re-derives differently, as an earlier attach left it live.
				MyMotorStator stator = Fat<MyMotorStator>(grid);
				FieldInfo field = typeof(MyMotorStator).GetField("m_limitsActive", BindingFlags.Instance | BindingFlags.NonPublic)
					?? throw new InvalidOperationException("m_limitsActive not found");
				field.SetValue(stator, !(bool)field.GetValue(stator));
				return new { grid = gridId, action, limitsActive = ((MyObjectBuilder_MotorStator)stator.GetObjectBuilderCubeBlock()).LimitsActive };
			}
			case "track":
			{
				if (!MyEntities.TryGetEntityById(targetId, out MyEntity target))
					throw new InvalidOperationException($"Track target {targetId} is not on this node");
				var turret = (Sandbox.ModAPI.IMyLargeTurretBase)(MyCubeBlock)Fat<MyLargeTurretBase>(grid);
				turret.TrackTarget((VRage.ModAPI.IMyEntity)target);
				return new { grid = gridId, action, target = targetId };
			}
			case "dummy-destroy":
			{
				// Restoration off, so the dummy stays destroyed across the handovers that follow.
				var dummy = Fat<MyTargetDummyBlock>(grid);
				dummy.EnableRestoration = false;
				dummy.DestroyAllSubparts();
				return new { grid = gridId, action, destroyed = dummy.m_isDestroyed };
			}
			default:
				throw new ArgumentException($"Unknown action '{action}'");
		}
	}

	// -- state ----------------------------------------------------------------

	/// <summary>The fixture's live interaction state plus the facts of its CURRENT serialized OB -
	/// the same serialization a handover ships - so a scenario can assert both behavior (the gear is
	/// ReadyToLock) and content (RemainingMagazines-bearing blocks are in the transfer set).</summary>
	public static object State(long gridId)
	{
		if (!MyEntities.TryGetEntityById(gridId, out MyEntity entity) || entity.MarkedForClose)
			return new { exists = false };
		// A character id answers with its coupling state (§3.2): the dampening reference the gate
		// must carry across as ground truth, resolved (or not) on THIS node after import.
		if (entity is Sandbox.Game.Entities.Character.MyCharacter character)
			return new
			{
				exists = true,
				character = true,
				relativeDampeningEntity = character.RelativeDampeningEntity?.EntityId ?? 0,
				parent = character.Parent?.EntityId ?? 0,
				magneticBootsEnabled = character.IsMagneticBootsEnabled,
				magneticBootsActive = character.IsMagneticBootsActive,
			};
		if (entity is not MyCubeGrid grid)
			return new { exists = false };

		var gears = new List<object>();
		var connectors = new List<object>();
		var merges = new List<object>();
		var rotors = new List<object>();
		var pistons = new List<object>();
		var turrets = new List<object>();
		var grinders = new List<object>();
		var cockpits = new List<object>();
		var dummies = new List<object>();
		foreach (MyCubeBlock block in grid.GetFatBlocks())
		{
			switch (block)
			{
				case MyTargetDummyBlock dummy:
					dummies.Add(new
					{
						initialized = dummy.m_areSubpartsInitialized,
						destroyed = dummy.m_isDestroyed,
						subparts = dummy.m_subpartStates.Values.Select(state => state.IntegrityCurrent).ToList(),
					});
					break;
				case MyCockpit cockpit:
					cockpits.Add(new { pilot = cockpit.Pilot?.EntityId ?? 0 });
					break;
				case SpaceEngineers.Game.ModAPI.IMyLandingGear gear:
					gears.Add(new { lockMode = gear.LockMode.ToString(), autoLock = gear.AutoLock });
					break;
				case MyShipConnector connector:
					connectors.Add(new { status = ((Sandbox.ModAPI.Ingame.IMyShipConnector)connector).Status.ToString() });
					break;
				case MyShipMergeBlock merge:
					merges.Add(new
					{
						enabled = merge.Enabled,
						inConstraint = merge.InConstraint,
						// The merge face comes from the model's "merge" dummy, not the block OB -
						// exposed so the fixture geometry can be verified instead of guessed.
						mergeFace = typeof(MyShipMergeBlock)
							.GetField("m_forward", BindingFlags.Instance | BindingFlags.NonPublic)
							?.GetValue(merge)?.ToString(),
						blockForward = merge.Orientation.Forward.ToString(),
					});
					break;
				case MyMotorStator stator:
					rotors.Add(new { angle = stator.CurrentAngle, hasTop = stator.TopBlock != null, topGrid = stator.TopBlock?.CubeGrid?.EntityId ?? 0, working = stator.IsWorking,
						limitsActive = ((MyObjectBuilder_MotorStator)stator.GetObjectBuilderCubeBlock()).LimitsActive });
					break;
				case MyPistonBase piston:
					pistons.Add(new { position = piston.CurrentPosition, hasTop = piston.TopBlock != null, topGrid = piston.TopBlock?.CubeGrid?.EntityId ?? 0, working = piston.IsWorking });
					break;
				case MyLargeTurretBase turret:
					turrets.Add(new { target = turret.Target?.EntityId ?? 0, working = turret.IsWorking });
					break;
				case MyShipGrinder grinder:
					// The detector sphere is grid-local; expose it so tests can place victims exactly.
					BoundingSphere detector = grinder.DetectorSphere;
					grinders.Add(new
					{
						enabled = grinder.Enabled,
						working = grinder.IsWorking,
						detectorLocal = new { detector.Center.X, detector.Center.Y, detector.Center.Z },
						detectorRadius = detector.Radius,
					});
					break;
			}
		}

		// The serialized truth: what a handover of this grid would actually carry.
		MyObjectBuilder_CubeGrid builder = (MyObjectBuilder_CubeGrid)grid.GetObjectBuilder();
		XDocument document;
		using (MemoryStream stream = new MemoryStream())
		{
			if (!MyObjectBuilderSerializerKeen.SerializeXML(stream, builder))
				throw new InvalidOperationException($"Could not serialize grid {gridId}");
			stream.Position = 0;
			document = XDocument.Load(stream);
		}
		int magazineBearing = document.Descendants("GunBase").Count(gun => gun.Element("RemainingMagazines") != null);
		int inventoryItems = document.Descendants("Items").SelectMany(items => items.Elements()).Count();

		return new
		{
			exists = true,
			blocks = grid.BlocksCount,
			// Sum of slim-block integrity: drops the moment a tool starts eating the grid,
			// long before the first block disappears (the grinder cell's mid-grind marker).
			integrity = grid.GetBlocks().Sum(slim => slim.Integrity),
			isStatic = grid.IsStatic,
			gears,
			connectors,
			merges,
			rotors,
			pistons,
			turrets,
			grinders,
			cockpits,
			dummies,
			// Authoritative terminal names, keyed by block min: lets a lane separate "the queued
			// custom-name edit never reached this node" from "the client's replicated view is stale"
			// (P1-HND-04 counters, sweep 14: client stuck on the old name with no way to tell which).
			blockNames = grid.GetFatBlocks().OfType<MyTerminalBlock>()
				.Select(block => new
				{
					min = new[] { block.Min.X, block.Min.Y, block.Min.Z },
					name = block.CustomName?.ToString() ?? "",
				}).ToList(),
			serialized = new { magazineBearing, inventoryItems },
		};
	}

	// -- plumbing ---------------------------------------------------------------

	private static T Fat<T>(MyCubeGrid grid) where T : class
	{
		return grid.GetFatBlocks().OfType<T>().FirstOrDefault()
			?? throw new InvalidOperationException($"No {typeof(T).Name} on grid {grid.EntityId}");
	}

	private static void Fill<TBlock>(MyEntity grid, params (MyObjectBuilder_PhysicalObject content, int amount)[] items)
		where TBlock : MyCubeBlock
	{
		MyCubeBlock block = ((MyCubeGrid)grid).GetFatBlocks().OfType<TBlock>().FirstOrDefault()
			?? throw new InvalidOperationException($"No {typeof(TBlock).Name} on grid {grid.EntityId}");
		var inventory = block.GetInventory() as Sandbox.Game.MyInventory
			?? throw new InvalidOperationException($"{typeof(TBlock).Name} on grid {grid.EntityId} has no inventory");
		foreach ((MyObjectBuilder_PhysicalObject content, int amount) in items)
			inventory.AddItems(amount, content);
		// The gun refreshed its magazine count while the inventory was still empty, and nothing
		// re-refreshes it until the turret fires - the Survival RemainingMagazines assertion
		// (P3-HND-01) reads the serialized truth, so bring it up to date now.
		if (block is MyLargeTurretBase turret)
			turret.GunBase?.RefreshAmmunitionAmount(forceUpdate: true);
	}

	private static MyObjectBuilder_PhysicalObject NewItem<T>(string subtype)
		where T : MyObjectBuilder_PhysicalObject, new()
	{
		return MyObjectBuilderSerializerKeen.CreateNewObject<T>(subtype);
	}

	/// <summary>Accumulates block builders for one dynamic large grid and spawns it.</summary>
	private sealed class GridBuilder
	{
		private readonly MyObjectBuilder_CubeGrid m_grid;
		private readonly long m_builtBy;

		public GridBuilder(string name, Vector3D position, long builtBy)
		{
			m_builtBy = builtBy;
			m_grid = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_CubeGrid>();
			m_grid.GridSizeEnum = MyCubeSize.Large;
			m_grid.IsStatic = false;
			m_grid.CreatePhysics = true;
			m_grid.PersistentFlags = MyPersistentEntityFlags2.InScene | MyPersistentEntityFlags2.CastShadows;
			m_grid.PositionAndOrientation = new MyPositionAndOrientation(position, Vector3D.Forward, Vector3D.Up);
			m_grid.DisplayName = name + "-" + ++m_fixtureCounter;
		}

		public void Armor(int x, int y, int z, string subtype = "LargeBlockArmorBlock")
		{
			var armor = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_CubeBlock>(subtype);
			Place(armor, x, y, z, null);
			m_grid.CubeBlocks.Add(armor);
		}

		public T Add<T>(string subtype, int x, int y, int z, SerializableBlockOrientation? orientation = null)
			where T : MyObjectBuilder_CubeBlock, new()
		{
			T block = MyObjectBuilderSerializerKeen.CreateNewObject<T>(subtype);
			Place(block, x, y, z, orientation);
			m_grid.CubeBlocks.Add(block);
			return block;
		}

		private void Place(MyObjectBuilder_CubeBlock block, int x, int y, int z,
			SerializableBlockOrientation? orientation)
		{
			block.Min = new SerializableVector3I(x, y, z);
			if (orientation.HasValue)
				block.BlockOrientation = orientation.Value;
			if (m_builtBy != 0)
			{
				block.BuiltBy = m_builtBy;
				block.Owner = m_builtBy;
			}
		}

		public MyEntity Build()
		{
			return MyEntities.CreateFromObjectBuilderAndAdd(m_grid, fadeIn: false)
				?? throw new InvalidOperationException($"Could not spawn fixture grid {m_grid.DisplayName}");
		}
	}
}
