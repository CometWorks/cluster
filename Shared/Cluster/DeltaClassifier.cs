using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ClusterPlugin;

/// <summary>
/// Sorts the differing paths of one entity (ImportRoundTrip.DiffPaths) into the applier tiers of
/// HandoverDelta.md §3. The classification is shared by the offline harness (CodecTest
/// --delta-saves: how much of the measured drift each tier would absorb) and, later, by the source
/// that builds the patch and the target that applies it - one table, one truth.
///
/// Paths look like
///   MyObjectBuilder_Sector/SectorObjects/MyObjectBuilder_EntityBase#ID/CubeBlocks/
///     MyObjectBuilder_CubeBlock#ID/ComponentContainer/Components/MyObjectBuilder_ComponentContainer...
/// Entities and blocks are id-addressed; anonymous repeated siblings carry [index].
/// </summary>
internal static class DeltaClassifier
{
	internal enum Tier { Unchanged, Derived, Pose, Component, Scalar, Rebuild }

	// Derived from the pose and re-computed by the engine on the target: a cockpit's cached
	// PilotRelativeWorld follows the ship; a connector's/landing gear's MasterToSlaveTransform is
	// re-derived from the two bodies. Nothing to apply; the round-trip gate already tolerates
	// them (epsilons) or the target re-emits the same values once the pose is applied.
	private static readonly Regex Derived = new Regex(Entity + Block + @"/(PilotRelativeWorld|MasterToSlaveTransform)(/|$)", RegexOptions.Compiled);
	// A character's feet offset is re-derived from the ground/animation each frame (no setter and
	// no handover-visible effect); a cockpit's embedded Pilot carries it too.
	private static readonly Regex DerivedEntity = new Regex(Entity + @"(" + Block + @"/Pilot)?/CharacterFeetOffset$", RegexOptions.Compiled);

	private const string Entity = @"^MyObjectBuilder_Sector/SectorObjects/MyObjectBuilder_EntityBase#-?\d+";
	private const string Block = @"/CubeBlocks/MyObjectBuilder_CubeBlock(#-?\d+|\[\d+\])?";

	// Tier A: the entity's own dynamic record. A block's or a pilot's pose is NOT the entity's.
	private static readonly Regex Pose = new Regex(Entity + @"/(PositionAndOrientation|LinearVelocity|AngularVelocity)(/|$)", RegexOptions.Compiled);

	// Tier B: anything inside a component's sub-document, on the entity or on one of its blocks.
	// The applier re-deserialises the whole component, so every leaf under it is covered; a
	// component that APPEARS or VANISHES shows up as a child-count diff on Components itself and
	// is not under a component - that stays tier R.
	private static readonly Regex Component = new Regex(Entity + @"(" + Block + @")?/ComponentContainer/Components/[^/]+\[?\d*\]?/.+", RegexOptions.Compiled);

	// Tier C: per-class scalar setters that exist server-side today. Keyed by the element name
	// relative to the entity or block; the class check happens in the applier (a FilledRatio on
	// a block that is not a MyGasTank falls through to rebuild at apply time, logged).
	private static readonly HashSet<string> BlockScalars = new HashSet<string>
	{
		"FilledRatio", "CurrentStoredPower", "CustomName", "ShowOnHUD", "ShowInTerminal",
		"ShowInToolbarConfig", "ShowInInventory", "Enabled", "Owner", "ShareMode", "TradingEnabled",
		"AutoUnlockTime", "IsLocked", "ConnectedEntityId", "TimeOfConnection",
		"SemiautoEnabled", "ProducerEnabled", "MaxStoredPower", "ChargeMode", "OnlyRecharge", "OnlyDischarge",
		"OnlyDischargeEnabled", "IsParkingEnabled", "IsPowerTransferOverrideEnabled", "PilotJetpackEnabled", "OxygenLevel", "IsMaster",
	};
	private static readonly HashSet<string> EntityScalars = new HashSet<string> { "DisplayName", "Name" };
	private static readonly HashSet<string> CharacterScalars = new HashSet<string>
	{
		"EnvironmentOxygenLevel", "MovementState", "OxygenLevel",
		"HydrogenLevel", "Health", "JetpackEnabled", "DampenersEnabled", "LightEnabled",
	};
	private static readonly Regex BlockScalar = new Regex(Entity + Block + @"/([A-Za-z]+)$", RegexOptions.Compiled);
	private static readonly Regex EntityScalar = new Regex(Entity + @"/([A-Za-z]+)$", RegexOptions.Compiled);
	private static readonly Regex PilotScalar = new Regex(Entity + Block + @"/Pilot/([A-Za-z]+)$", RegexOptions.Compiled);

	internal static Tier Classify(string residual)
	{
		int split = residual.IndexOf(": ", StringComparison.Ordinal);
		string path = split < 0 ? residual : residual.Substring(0, split);
		if (Pose.IsMatch(path)) return Tier.Pose;
		if (Derived.IsMatch(path)) return Tier.Derived;
		if (Component.IsMatch(path)) return Tier.Component;
		Match m = PilotScalar.Match(path);
		if (m.Success) return CharacterScalars.Contains(m.Groups[m.Groups.Count - 1].Value) ? Tier.Scalar : Tier.Rebuild;
		m = BlockScalar.Match(path);
		if (m.Success) return BlockScalars.Contains(m.Groups[m.Groups.Count - 1].Value) ? Tier.Scalar : Tier.Rebuild;
		m = EntityScalar.Match(path);
		if (m.Success) return CharacterScalars.Contains(m.Groups[1].Value) || EntityScalars.Contains(m.Groups[1].Value) ? Tier.Scalar : Tier.Rebuild;
		return Tier.Rebuild;
	}

	/// <summary>The tier an entity needs: the worst tier among its diffs (Rebuild dominates).</summary>
	internal static Tier ClassifyEntity(IEnumerable<string> residuals, out Dictionary<Tier, int> counts)
	{
		counts = new Dictionary<Tier, int>();
		Tier worst = Tier.Unchanged;
		foreach (string residual in residuals)
		{
			Tier tier = Classify(residual);
			counts[tier] = counts.TryGetValue(tier, out int n) ? n + 1 : 1;
			if (tier > worst) worst = tier;
		}
		return worst;
	}

	/// <summary>The leaf element name of a residual path, for the rebuild-cause histogram.</summary>
	internal static string Leaf(string residual)
	{
		int split = residual.IndexOf(": ", StringComparison.Ordinal);
		string path = split < 0 ? residual : residual.Substring(0, split);
		string leaf = path.Substring(path.LastIndexOf('/') + 1);
		int hash = leaf.IndexOf('#');
		return hash < 0 ? leaf : leaf.Substring(0, hash);
	}
}
