using System;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using Shared.Patches;
using VRage;
using VRage.Game;
using VRage.Game.Entity;
using VRage.ModAPI;
using VRage.ObjectBuilders.Private;
using VRageMath;

namespace ClusterPlugin;

internal static partial class ClusterSelfTest
{
    /// <summary>
    /// SE-1583: the two halves of the fix for the import that stopped seven nodes on 2026-11-15.
    /// (1) A character created the way the handover import creates it, owned by an identity this node does not hold and
    /// with no player controlling it, goes through <c>MyCharacterWeaponPositionComponent.Update</c> without the
    /// NullReferenceException vanilla throws there on a dedicated server. (2) An entity that throws out of its parallel
    /// update is caught by <see cref="MyEntitySimCostPatch"/> and counted instead of failing the parallel task. Runs on
    /// the game thread; the character is never added to the scene and is closed again.
    /// </summary>
    private static void CharacterWithoutIdentity()
    {
        Exception failure = null;
        using var done = new System.Threading.ManualResetEventSlim();
        Sandbox.MySandboxGame.Static.Invoke(
            () =>
            {
                try
                {
                    CharacterWithoutIdentityOnGameThread();
                }
                catch (Exception error)
                {
                    failure = error;
                }
                finally
                {
                    done.Set();
                }
            },
            "self-test character-without-identity"
        );
        Assert(
            done.Wait(TimeSpan.FromSeconds(30)),
            "the game thread did not run the section within 30 s"
        );
        if (failure != null)
            throw failure;
    }

    private static void CharacterWithoutIdentityOnGameThread()
    {
        const long unknownOwner = 0x7FFF_0000_0000_062F; // 1583; any id the identity collection does not hold
        Assert(
            MySession.Static.Players.TryGetIdentity(unknownOwner) == null,
            "the fixture owner id is a real identity here"
        );
        var builder = MyObjectBuilderSerializerKeen.CreateNewObject<MyObjectBuilder_Character>();
        builder.CharacterModel = "Default_Astronaut";
        builder.SubtypeName = "Default_Astronaut";
        builder.OwningPlayerIdentityId = unknownOwner;
        builder.PositionAndOrientation = new MyPositionAndOrientation(
            new Vector3D(0, 0, -1e7),
            Vector3.Forward,
            Vector3.Up
        );

        var character = (MyCharacter)MyEntities.CreateFromObjectBuilder(builder, fadeIn: false);
        Assert(character != null, "the character was not created");
        try
        {
            // The known positive: exactly what vanilla dereferences on a dedicated server.
            Assert(
                character.Definition != null && character.WeaponPosition != null,
                "the character has no definition or weapon position"
            );
            Assert(
                character.GetIdentity() == null,
                "the character resolved an identity, so this section tests nothing"
            );
            long skips = MyCharacterWeaponPositionComponentPatch.Skips;
            character.WeaponPosition.Update();
            Assert(
                MyCharacterWeaponPositionComponentPatch.Skips == skips + 1,
                "the update of a character without an identity was not skipped"
            );
        }
        finally
        {
            character.Close();
        }

        long faults = MyEntitySimCostPatch.ParallelUpdateFaults;
        var thrower = new ThrowingParallelEntity();
        MyEntitySimCostPatch.UpdateAfterSimulationParallel(thrower);
        MyEntitySimCostPatch.UpdateBeforeSimulationParallel(thrower);
        Assert(thrower.Calls == 2, "the throwing entity was not updated");
        Assert(
            MyEntitySimCostPatch.ParallelUpdateFaults == faults + 2,
            "the parallel update faults were not counted"
        );
    }

    private sealed class ThrowingParallelEntity : MyEntity, IMyParallelUpdateable
    {
        public int Calls;
        public MyParallelUpdateFlags UpdateFlags => MyParallelUpdateFlags.NONE;

        public void UpdateBeforeSimulationParallel()
        {
            Calls++;
            throw new NullReferenceException("self-test");
        }

        public void UpdateAfterSimulationParallel()
        {
            Calls++;
            throw new NullReferenceException("self-test");
        }
    }
}
