using System;
using System.Runtime.CompilerServices;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Replication.ClientStates;
using Sandbox.Game.World;
using VRage.Library.Collections;
using VRageMath;

namespace ClusterPlugin;

/// <summary>
/// Seam holder for <see cref="MyCharacter"/>: MyCharacterPatch.CharacterLoaded plus the ClusterLast*
/// diagnostic fields.
/// The diagnostic instance fields the engine wrote are extension properties backed by a
/// ConditionalWeakTable; Shared/Patches/MyCharacterPatch.cs maintains them from the same sites.
/// </summary>
public static class MyCharacterSeams
{
	/// <summary>
	/// Cluster seam: raised after a character loads from its object builder, with the owning identity
	/// id the builder carries (0 when it names none). A loaded body is the only place the cluster can
	/// still see who it belongs to - the identity itself comes from the World Authority, which hosts no
	/// character.
	/// </summary>
	public static Action<MyCharacter, long> ClusterCharacterLoaded;

	private sealed class Diagnostics
	{
		public int ControlsFrame = -1;
		public Vector3 ControlsMove;
		public int InputFrame = -1;
		public int UpdateFrame = -1;
	}

	private static readonly ConditionalWeakTable<MyCharacter, Diagnostics> Table = new ConditionalWeakTable<MyCharacter, Diagnostics>();

	private static Diagnostics Of(MyCharacter character) => Table.GetValue(character, static _ => new Diagnostics());

	/// <summary>The current gameplay frame, or -1 before a session exists - the stamp every field below carries.</summary>
	public static int CurrentFrame => MySession.Static?.GameplayFrameCounter ?? -1;

	extension(MyCharacter self)
	{
		/// <summary>ClusterPlugin diagnostic seam: the last frame a controls payload for this character
		/// arrived from its client. Splits "the server's input gate drops the packet" from "the client
		/// sends zeros".</summary>
		public int ClusterLastControlsFrame
		{
			get => Table.TryGetValue(self, out Diagnostics d) ? d.ControlsFrame : -1;
			set => Of(self).ControlsFrame = value;
		}

		/// <summary>ClusterPlugin diagnostic seam: the move indicator of the last received controls payload.</summary>
		public Vector3 ClusterLastControlsMove
		{
			get => Table.TryGetValue(self, out Diagnostics d) ? d.ControlsMove : Vector3.Zero;
			set => Of(self).ControlsMove = value;
		}

		/// <summary>ClusterPlugin diagnostic seam: the last frame nonzero movement input reached this
		/// character's MoveAndRotate. MoveIndicator sampled between frames lies (it read zero through a
		/// confirmed 38 m move); a stamp on arrival does not.</summary>
		public int ClusterLastInputFrame
		{
			get => Table.TryGetValue(self, out Diagnostics d) ? d.InputFrame : -1;
			set => Of(self).InputFrame = value;
		}

		/// <summary>ClusterPlugin diagnostic seam (P1/P2-CHR-02 inert character): the last frame this
		/// character's UpdateBeforeSimulation actually ran. NeedsUpdate says what is registered;
		/// this says what executes.</summary>
		public int ClusterLastUpdateFrame
		{
			get => Table.TryGetValue(self, out Diagnostics d) ? d.UpdateFrame : -1;
			set => Of(self).UpdateFrame = value;
		}
	}

	/// <summary>
	/// Transpiler target (MyCharacterPatch, DeserializeControls): called right after vanilla constructed the
	/// client state from the stream, before the out-of-order check, which is where the two controls
	/// fields are stamped.
	/// </summary>
	public static void RecordControls(MyCharacter self, MyCharacterClientState state)
	{
		if (!ClusterRuntime.IsClustered) return;
		Diagnostics d = Of(self);
		d.ControlsFrame = CurrentFrame;
		d.ControlsMove = state.MoveIndicator;
	}
}
