using System;

namespace ClusterPlugin;

/// <summary>
/// Seam holder for <see cref="Sandbox.Game.Entities.Blocks.MyStoreBlock"/>. The transpilers in
/// Shared/Patches/MyStoreBlockPatch.cs call its four delegates through small static helpers.
/// </summary>
public static class MyStoreBlockSeams
{
	// Reserves the payer debit at the World Authority before the first inventory mutation of a synchronous
	// settlement path; the scope refunds when disposed without a commit.
	public static Func<long, long, (IDisposable Scope, bool Approved)> ClusterBeginPayment;
	// Commits the observed vanilla debit/credit pair before the success result is sent.
	public static Action<IDisposable> ClusterCommitPayment;
	// Reserves without making the scope ambient (prefab purchase: settlement runs in a spawn callback).
	public static Func<long, long, (IDisposable Scope, bool Approved)> ClusterBeginDeferredPayment;
	// Runs the vanilla settlement inside the deferred scope and commits it.
	public static Action<IDisposable, Action> ClusterSettlePayment;
}
