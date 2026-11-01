using Sandbox.Engine.Multiplayer;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using VRage.Game.Entity;
using VRageMath;

namespace ClusterTestPlugin;

/// <summary>
/// T-0292 bench harness: drive a grid group (or a character) at a fixed speed for a fixed time, so the
/// fast-partition rules can be exercised without mods.
/// - mode=teleport: the warp-drive shape (STC 4.0 Warp Drive moves the grid ahead every tick): every
///   frame the whole physical group is moved by v*dt with SetPosition, physics velocity zeroed.
/// - pulse=ON,OFF (teleport only, SE1-0084): move for ON frames, stand still for OFF frames, repeat; a member
///   that keeps entering and leaving the fast state.
/// - mode=velocity: a real physics velocity, re-applied every frame. Above the vanilla ship limit
///   (100 m/s) this needs /fast-test-speed-limit first, which raises the environment definition's
///   ship and character limits - what speed mods do through the same definition.
/// </summary>
internal static class FastTestHarness
{
    private sealed class Drive
    {
        public long Entity;
        public Vector3D Velocity;
        public bool Teleport;
        public double Until;
        public double Last;
        public double Travelled;
        public int PulseOn, PulseOff;
        public long Frame;
    }

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly Dictionary<long, Drive> Drives = new Dictionary<long, Drive>();

    public static void Register()
    {
        // POST /fast-test-drive?entity=&vx=&vy=&vz=&seconds=&mode=teleport|velocity[&pulse=ON,OFF]
        TestRoutes.Add("POST", "/fast-test-drive", context =>
        {
            long entity = long.Parse(TestRoutes.Query(context, "entity"), CultureInfo.InvariantCulture);
            Vector3D velocity = new Vector3D(TestRoutes.Double(context, "vx"), TestRoutes.Double(context, "vy"), TestRoutes.Double(context, "vz"));
            double seconds = TestRoutes.Double(context, "seconds");
            bool teleport = (TestRoutes.Query(context, "mode", "teleport") ?? "teleport") != "velocity";
            int[] pulse = (TestRoutes.Query(context, "pulse", "0,0") ?? "0,0").Split(',').Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() =>
            {
                if (!MyEntities.TryGetEntityById(entity, out MyEntity found))
                    throw new ArgumentException("Unknown entity " + entity);
                double now = Clock.Elapsed.TotalSeconds;
                Drives[entity] = new Drive { Entity = entity, Velocity = velocity, Teleport = teleport, Until = now + seconds, Last = now,
                    PulseOn = pulse[0], PulseOff = pulse[1] };
                return (object)new { entity, speed = velocity.Length(), seconds, mode = teleport ? "teleport" : "velocity",
                    position = Vec(found.PositionComp.GetPosition()) };
            }));
        });
        // POST /fast-test-stop?entity=
        TestRoutes.Add("POST", "/fast-test-stop", context =>
        {
            long entity = long.Parse(TestRoutes.Query(context, "entity"), CultureInfo.InvariantCulture);
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() => (object)new { stopped = Stop(entity) }));
        });
        // GET /fast-test-state: active drives and each driven entity's position.
        TestRoutes.Add("GET", "/fast-test-state", context =>
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() => (object)new
            {
                drives = Drives.Values.Select(drive => new
                {
                    entity = drive.Entity, speed = drive.Velocity.Length(), mode = drive.Teleport ? "teleport" : "velocity",
                    remaining = Math.Max(0, drive.Until - Clock.Elapsed.TotalSeconds), travelled = drive.Travelled,
                    position = MyEntities.TryGetEntityById(drive.Entity, out MyEntity e) ? Vec(e.PositionComp.GetPosition()) : null,
                }).ToArray(),
            })));
        // POST /fast-test-velocity?entity=&vx=&vy=&vz=[&wx=&wy=&wz=] - T-0361: ONE write of a real physics velocity
        // (linear m/s, angular rad/s); nothing re-applies it, so an unpowered grid in space coasts on it.
        TestRoutes.Add("POST", "/fast-test-velocity", context =>
        {
            long entity = long.Parse(TestRoutes.Query(context, "entity"), CultureInfo.InvariantCulture);
            Vector3 linear = new Vector3((float)TestRoutes.Double(context, "vx"), (float)TestRoutes.Double(context, "vy"), (float)TestRoutes.Double(context, "vz"));
            Vector3 angular = new Vector3(Optional(context, "wx"), Optional(context, "wy"), Optional(context, "wz"));
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() =>
            {
                if (!MyEntities.TryGetEntityById(entity, out MyEntity found))
                    throw new ArgumentException("Unknown entity " + entity);
                if (found.Physics == null || !found.Physics.Enabled)
                    throw new InvalidOperationException("Entity " + entity + " has no enabled physics");
                found.Physics.LinearVelocity = linear;
                found.Physics.AngularVelocity = angular;
                return Motion(entity);
            }));
        });
        // POST /fast-test-dampeners?grid=&enabled= - T-0401 velocity gate: switch a grid's inertial dampeners (vanilla
        // MyCubeGrid.EnableDampingInternal, internal: by reflection) so a written velocity is not braked away by the grid's
        // own thrusters; the state is in the grid's builder (DampenersEnabled), so it travels with a handover.
        TestRoutes.Add("POST", "/fast-test-dampeners", context =>
        {
            long id = long.Parse(TestRoutes.Query(context, "grid"), CultureInfo.InvariantCulture);
            bool enabled = string.Equals(TestRoutes.Query(context, "enabled"), "true", StringComparison.OrdinalIgnoreCase);
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() =>
            {
                if (!MyEntities.TryGetEntityById(id, out MyEntity found) || found is not MyCubeGrid grid)
                    throw new ArgumentException("Unknown grid " + id);
                typeof(MyCubeGrid).GetMethod("EnableDampingInternal", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    .Invoke(grid, new object[] { enabled, false });
                return (object)new { grid = id, dampeners = grid.DampenersEnabled };
            }));
        });
        // GET /fast-test-grid-state?grid= - T-0401 follow-up: a grid's mechanical state after a handover thaw, to compare a
        // deferred import (import.deferPhysics) with the control: blocks, its body (enabled, in the Havok world, constraints),
        // its mechanical/physical group sizes, and every block subpart body (doors, pistons, ...) enabled / in world.
        TestRoutes.Add("GET", "/fast-test-grid-state", context =>
        {
            long id = long.Parse(TestRoutes.Query(context, "grid"), CultureInfo.InvariantCulture);
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() => GridState(id)));
        });
        // POST /fast-test-grid-fault?grid=&kind=subpart|constraint - T-0401 follow-up (CH 07:02): the KNOWN POSITIVE for the
        // post-thaw state compare. Breaks one grid on purpose: kind=subpart disables the first block subpart body that is
        // enabled; kind=constraint removes one constraint from the grid's body. Bench grids only.
        TestRoutes.Add("POST", "/fast-test-grid-fault", context =>
        {
            long id = long.Parse(TestRoutes.Query(context, "grid"), CultureInfo.InvariantCulture);
            string kind = TestRoutes.Query(context, "kind", "subpart") ?? "subpart";
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() => GridFault(id, kind)));
        });
        // GET /fast-test-velocity?entity= - T-0361: the entity's position and velocities as its physics reports them
        // (found=false when the entity is not on this node: a polled read, never an error).
        TestRoutes.Add("GET", "/fast-test-velocity", context =>
        {
            long entity = long.Parse(TestRoutes.Query(context, "entity"), CultureInfo.InvariantCulture);
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() => Motion(entity)));
        });
        // GET /world-name-test - the name the session and its multiplayer server carry (SE-788).
        TestRoutes.Add("GET", "/world-name-test", context =>
        {
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() =>
                (object)new { session = MySession.Static.Name, multiplayer = MyMultiplayer.Static?.WorldName }));
        });
        // POST /fast-test-speed-limit?mps= - raise (or restore) the environment's ship and character speed limits.
        TestRoutes.Add("POST", "/fast-test-speed-limit", context =>
        {
            float mps = (float)TestRoutes.Double(context, "mps");
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() =>
            {
                var env = MySector.EnvironmentDefinition;
                var before = new { large = env.LargeShipMaxSpeed, small = env.SmallShipMaxSpeed };
                env.LargeShipMaxSpeed = mps;
                env.SmallShipMaxSpeed = mps;
                // A rigid body takes its Havok MaxLinearVelocity from these limits when it is CREATED
                // (MyGridPhysics.SetDefaultRigidBodyMaxVelocities): a speed mod sets them at load, before
                // any grid exists. Re-apply them to the grids already here, or they stay capped.
                int applied = 0;
                foreach (MyCubeGrid grid in MyEntities.GetEntities().OfType<MyCubeGrid>())
                    if (grid.Physics?.RigidBody != null && !grid.IsStatic) { grid.Physics.SetDefaultRigidBodyMaxVelocities(); applied++; }
                return (object)new { before, after = new { large = env.LargeShipMaxSpeed, small = env.SmallShipMaxSpeed }, gridsApplied = applied };
            }));
        });
    }

    private static double[] Vec(Vector3D v) => new[] { v.X, v.Y, v.Z };

    private static float Optional(System.Net.HttpListenerContext context, string name) =>
        float.Parse(TestRoutes.Query(context, name, "0"), CultureInfo.InvariantCulture);

    /// <summary>Game thread. T-0361: where an entity is and how it moves, read from its physics body.</summary>
    private static object Motion(long entity)
    {
        if (!MyEntities.TryGetEntityById(entity, out MyEntity found))
            return new { found = false, entity };
        Vector3 linear = found.Physics?.LinearVelocity ?? Vector3.Zero;
        Vector3 angular = found.Physics?.AngularVelocity ?? Vector3.Zero;
        return new
        {
            found = true, entity, name = found.DisplayName, isStatic = found is MyCubeGrid grid && grid.IsStatic,
            physics = found.Physics != null, physicsEnabled = found.Physics?.Enabled == true,
            position = Vec(found.PositionComp.GetPosition()),
            linear = Vec(linear), angular = Vec(angular), speed = (double)linear.Length(), spin = (double)angular.Length(),
        };
    }

    /// <summary>Game thread. T-0401 follow-up: see GET /fast-test-grid-state.</summary>
    private static object GridState(long id)
    {
        if (!MyEntities.TryGetEntityById(id, out MyEntity found) || found is not MyCubeGrid grid)
            return new { found = false, grid = id };
        int subparts = 0, subpartBodies = 0, subpartEnabled = 0, subpartInWorld = 0;
        Dictionary<string, int> bodyTypes = new Dictionary<string, int>();
        void Walk(MyEntity owner, string type)
        {
            if (owner.Subparts == null) return;
            foreach (var subpart in owner.Subparts.Values)
            {
                subparts++;
                if (subpart.Physics != null)
                {
                    subpartBodies++;
                    if (subpart.Physics.Enabled) subpartEnabled++;
                    if (subpart.Physics.IsInWorld) subpartInWorld++;
                    bodyTypes[type] = bodyTypes.TryGetValue(type, out int n) ? n + 1 : 1;
                }
                Walk(subpart, type);
            }
        }
        foreach (var block in grid.GetFatBlocks())
            Walk(block, block.BlockDefinition.Id.TypeId.ToString().Replace("MyObjectBuilder_", ""));
        var physics = grid.Physics as Sandbox.Engine.Physics.MyPhysicsBody;
        return new
        {
            found = true, grid = id, name = grid.DisplayName, blocks = grid.BlocksCount, isStatic = grid.IsStatic,
            physicsEnabled = grid.Physics?.Enabled == true, inWorld = grid.Physics?.IsInWorld == true,
            constraints = physics?.Constraints.Count ?? -1,
            mechanicalGroup = MyCubeGridGroups.Static.Mechanical.GetGroup(grid)?.Nodes.Count ?? 1,
            physicalGroup = MyCubeGridGroups.Static.Physical.GetGroup(grid)?.Nodes.Count ?? 1,
            subparts, subpartBodies, subpartEnabled, subpartInWorld, subpartBodyTypes = bodyTypes,
        };
    }

    /// <summary>Game thread. T-0401 follow-up: see POST /fast-test-grid-fault.</summary>
    private static object GridFault(long id, string kind)
    {
        if (!MyEntities.TryGetEntityById(id, out MyEntity found) || found is not MyCubeGrid grid)
            throw new ArgumentException("Unknown grid " + id);
        if (kind == "constraint")
        {
            var physics = grid.Physics as Sandbox.Engine.Physics.MyPhysicsBody;
            var constraint = physics?.Constraints.FirstOrDefault();
            if (physics == null || constraint == null)
                return new { grid = id, kind, applied = false, reason = "no constraint on the grid body" };
            physics.RemoveConstraint(constraint);
            return new { grid = id, kind, applied = true, constraints = physics.Constraints.Count };
        }
        // The same walk as GridState (subparts of subparts too), so a grid whose state counts an enabled subpart body has one to break.
        MyEntity Enabled(MyEntity owner)
        {
            if (owner.Subparts == null) return null;
            foreach (var subpart in owner.Subparts.Values)
            {
                if (subpart.Physics != null && subpart.Physics.Enabled) return subpart;
                MyEntity deeper = Enabled(subpart);
                if (deeper != null) return deeper;
            }
            return null;
        }
        foreach (var block in grid.GetFatBlocks())
        {
            MyEntity subpart = Enabled(block);
            if (subpart == null) continue;
            subpart.Physics.Enabled = false;
            return new { grid = id, kind, applied = true, block = block.BlockDefinition.Id.ToString() };
        }
        return new { grid = id, kind, applied = false, reason = "no enabled subpart body" };
    }

    private static bool Stop(long entity)
    {
        if (!Drives.Remove(entity, out Drive drive)) return false;
        foreach (MyEntity member in Group(entity))
            if (member.Physics != null) member.Physics.LinearVelocity = Vector3.Zero;
        return true;
    }

    /// <summary>The entity and every grid physically attached to it: they fly as one.</summary>
    private static IEnumerable<MyEntity> Group(long entity)
    {
        if (!MyEntities.TryGetEntityById(entity, out MyEntity root)) return Array.Empty<MyEntity>();
        if (root is MyCubeGrid grid)
            return MyCubeGridGroups.Static.Physical.GetGroup(grid)?.Nodes.Select(node => (MyEntity)node.NodeData).ToArray()
                ?? new MyEntity[] { grid };
        return new[] { root };
    }

    /// <summary>Game thread, every frame (Plugin.Update).</summary>
    public static void Update()
    {
        if (Drives.Count == 0) return;
        double now = Clock.Elapsed.TotalSeconds;
        foreach (Drive drive in Drives.Values.ToArray())
        {
            if (now >= drive.Until || !MyEntities.EntityExists(drive.Entity)) { Stop(drive.Entity); continue; }
            // One game step per frame, like the STC warp's per-tick teleport: the node measures speed over
            // GAME time, so a wall-clock step would read v / sim-speed.
            const double dt = 1.0 / 60.0;
            drive.Last = now;
            if (drive.Teleport && drive.PulseOn > 0 && drive.Frame++ % (drive.PulseOn + drive.PulseOff) >= drive.PulseOn) continue;
            foreach (MyEntity member in Group(drive.Entity))
            {
                if (drive.Teleport)
                {
                    member.PositionComp.SetPosition(member.PositionComp.GetPosition() + drive.Velocity * dt);
                    if (member.Physics != null) { member.Physics.LinearVelocity = Vector3.Zero; member.Physics.AngularVelocity = Vector3.Zero; }
                }
                else if (member.Physics != null)
                    member.Physics.LinearVelocity = drive.Velocity;
            }
            drive.Travelled += drive.Velocity.Length() * dt;
        }
    }
}
