using Sandbox.Game.Entities.Character;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;

namespace ClusterPlugin;

public static class MyCharacterPatch
{
	public static void Install() => MyCharacterSeams.ClusterCharacterLoaded = CharacterLoaded;

	/// <summary>
	/// Puts a loaded body back into its owner's saved set. Vanilla carries that set in the identity's
	/// own object builder, but a cluster checkpoint is written by the World Authority, which hosts no
	/// character: the set arrives empty, MySpaceRespawnComponent.TryFindExistingCharacter finds nothing
	/// and the rejoining player spawns a second body next to the one they left. The body itself names
	/// its owner (OwningPlayerIdentityId) and its entity id survives both saves and partition imports,
	/// so the node that loads it can restore the link on its own.
	/// </summary>
	public static void CharacterLoaded(MyCharacter character, long ownerIdentityId)
	{
		if (!Sync.IsServer || ownerIdentityId == 0) return;
		MySession.Static?.Players?.TryGetIdentity(ownerIdentityId)?.ClusterAdoptSavedCharacter(character);
	}
}
