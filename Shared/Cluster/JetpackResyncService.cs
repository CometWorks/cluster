using System.Collections.Generic;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using VRage.Game;
using VRage.ObjectBuilders;
using VRage.Game.Entity;
using VRage.Utils;

namespace ClusterPlugin;

/// <summary>
/// Give an imported character back the jetpack it arrived with, once its suit has power again.
/// </summary>
/// <remarks>
/// <c>MyCharacterJetpackComponent</c> turns itself off, permanently and one-way, on any momentary loss
/// of suit power:
///
/// <code>
/// private void PowerChangedInternal(bool obj)
/// {
///     if (!m_isPowered &amp;&amp; TurnedOn) TurnOnJetpack(newState: false);
/// }
/// </code>
///
/// An imported character rebuilds its thrust component, its resource sink and its distributor
/// connection, and that sequence goes through exactly that transition before power is available
/// again. The jetpack ends up off on the server while the client still has it on, and nothing
/// reconciles them: a client sends jetpack *changes*, never its current state, so it has no reason to
/// say anything and the two disagree for the rest of the session.
///
/// The consequence is not a cosmetic HUD difference. The server simulates a character it believes is
/// standing: the player's movement input arrives and is applied as walking, on ground they are not
/// on, so they do not move - while jump still registers, the camera still turns, and every diagnostic
/// says the character is controlled, powered and healthy. That is the "I can jump whilst jetpacking
/// but not move normally" report, and it survives everything except a new body.
///
/// Only the off-to-on direction is repaired. A jetpack wrongly on is visible to the player and one
/// keypress from correct; a jetpack wrongly off strands them with no way to ask for it back.
///
/// ponytail: a short window rather than an event, because the power transition that clears the flag
/// happens somewhere inside the import/thaw sequence and there is no callback for "the suit is
/// connected again". Twenty seconds is a handover, generously.
/// </remarks>
public static class JetpackResyncService
{
	private const double WindowMilliseconds = 20000;
	private static readonly Dictionary<long, double> Wanted = new Dictionary<long, double>();

	public static void Remember(MyEntity entity, MyObjectBuilder_EntityBase builder)
	{
		if (entity is not MyCharacter || builder is not MyObjectBuilder_Character character || !character.JetpackEnabled) return;
		Wanted[entity.EntityId] = ClusterRuntime.LocalMilliseconds + WindowMilliseconds;
	}

	public static void Forget(long entityId) => Wanted.Remove(entityId);

	public static void Update()
	{
		if (Wanted.Count == 0 || MySession.Static?.Ready != true) return;
		double now = ClusterRuntime.LocalMilliseconds;
		List<long> done = null;
		foreach ((long entityId, double expires) in Wanted)
		{
			if (now > expires)
			{
				(done ??= new List<long>()).Add(entityId);
				continue;
			}
			if (!MyEntities.TryGetEntityById(entityId, out MyEntity entity) || entity is not MyCharacter character
				|| character.MarkedForClose || character.IsDead || character.JetpackComp == null)
			{
				(done ??= new List<long>()).Add(entityId);
				continue;
			}
			// Wait for the power, do not fight it: turning the jetpack on while the suit is still
			// unpowered just runs PowerChangedInternal again and turns it straight back off.
			if (!character.JetpackComp.IsPowered || character.JetpackComp.TurnedOn) continue;
			character.JetpackComp.TurnOnJetpack(true);
			(done ??= new List<long>()).Add(entityId);
			MyLog.Default?.WriteLine($"jetpack_resynced entity={entityId} turnedOn={character.JetpackComp.TurnedOn} "
				+ $"powered={character.JetpackComp.IsPowered} state={character.GetCurrentMovementState()}");
		}
		if (done != null) foreach (long entityId in done) Wanted.Remove(entityId);
	}
}
