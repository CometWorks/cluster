using System;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using VRageMath;

namespace ClusterPlugin;

/// <summary>
/// Seam holder for <see cref="MyIdentity"/>. The static seam is raised by Shared/Patches/MyIdentityPatch.cs
/// from postfixes on ChangeCharacter, OnCharacterDied and SetDead; the two apply helpers
/// are extension methods here.
/// </summary>
public static class MyIdentitySeams
{
	/// <summary>
	/// Cluster seam: raised whenever this identity's death state or last-death position changes locally
	/// (SetDead, a character death, or a character change syncing IsDead). Death facts are otherwise
	/// node-local - only the node hosting the character learns them.
	/// </summary>
	public static Action<MyIdentity> ClusterStateChanged;

	/// <summary>
	/// Cluster apply: takes a loaded body back into the saved set, the same bookkeeping
	/// <see cref="MyIdentity.ChangeCharacter"/> does, without claiming control of it. Vanilla restores this set
	/// from the identity's own object builder because identity and character travel in one save; in the
	/// cluster the World Authority writes the identities and hosts no character, so the set arrives
	/// empty and MySpaceRespawnComponent.TryFindExistingCharacter never finds the rejoining player's
	/// body.
	/// </summary>
	public static void ClusterAdoptSavedCharacter(this MyIdentity self, MyCharacter character)
	{
		if (character == null || !self.SavedCharacters.Add(character.EntityId)) return;
		// character.OnClosing += OnSavedCharacterClosing; see PublicizedEvents.
		PublicizedEvents.AddOnClosing(character, self.OnSavedCharacterClosing);
	}

	/// <summary>
	/// Cluster apply: sets replicated death facts directly, bypassing the seam so applications never
	/// report back.
	/// </summary>
	public static void ClusterApplyPlayerState(this MyIdentity self, bool dead, Vector3D? lastDeathPosition)
	{
		self.IsDead = dead;
		self.LastDeathPosition = lastDeathPosition;
	}
}
