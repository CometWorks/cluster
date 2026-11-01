using System;
using Sandbox.Game.Entities.Blocks;

namespace ClusterPlugin;

public static class MyStoreBlockPatch
{
	[ThreadStatic] private static StorePaymentScope m_payment;

	public static void Install()
	{
		MyStoreBlockSeams.ClusterBeginPayment = SeamBeginPayment;
		MyStoreBlockSeams.ClusterCommitPayment = SeamCommitPayment;
		MyStoreBlockSeams.ClusterBeginDeferredPayment = SeamBeginDeferredPayment;
		MyStoreBlockSeams.ClusterSettlePayment = SeamSettlePayment;
	}

	private static (IDisposable Scope, bool Approved) SeamBeginPayment(long payerId, long amount)
	{
		StorePaymentScope payment = BeginPayment(payerId, amount);
		return (payment, payment.Approved);
	}

	private static void SeamCommitPayment(IDisposable scope) => ((StorePaymentScope)scope).Commit();

	private static (IDisposable Scope, bool Approved) SeamBeginDeferredPayment(long payerId, long amount)
	{
		StorePaymentScope payment = BeginDeferredPayment(payerId, amount);
		return (payment, payment.Approved);
	}

	private static void SeamSettlePayment(IDisposable scope, Action settle) => ((StorePaymentScope)scope).Settle(settle);

	public static StorePaymentScope BeginPayment(long payerId, long amount)
	{
		// Reject nesting before reserving: a throw after the debit would strand it with nothing left to refund it.
		if (m_payment != null && ClusterRuntime.IsClustered && !ClusterRuntime.IsWorldAuthority && amount != 0)
			throw new InvalidOperationException("Nested store payments are not supported");
		StorePaymentScope payment = BeginDeferredPayment(payerId, amount);
		if (payment.Approved && payment.Deferrable) m_payment = payment;
		return payment;
	}

	/// <summary>
	/// Reserves the payer debit without making the scope ambient, for a purchase whose vanilla settlement runs in a
	/// later callback (the asynchronous prefab spawn). <see cref="StorePaymentScope.Settle"/> makes it ambient for
	/// exactly the duration of that callback; disposing without settling refunds.
	/// </summary>
	public static StorePaymentScope BeginDeferredPayment(long payerId, long amount)
	{
		if (!ClusterRuntime.IsClustered || ClusterRuntime.IsWorldAuthority || amount == 0)
			return new StorePaymentScope(null, payerId, amount);
		return new StorePaymentScope(MyBankingSystemPatch.ChangeBalanceWithOperation, payerId, amount);
	}

	internal static bool TryObserveBalanceChange(long ownerId, long amount, ref bool result)
	{
		StorePaymentScope payment = m_payment;
		if (payment == null) return false;
		result = payment.Observe(ownerId, amount);
		return true;
	}

	public sealed class StorePaymentScope : IDisposable
	{
		private readonly Func<long, long, Guid, bool> m_change;
		private readonly Guid m_reserveOperation = Guid.NewGuid();
		private readonly Guid m_refundOperation = Guid.NewGuid();
		private readonly Guid m_creditOperation = Guid.NewGuid();
		private readonly long m_payerId;
		private readonly long m_amount;
		private long m_payeeId;
		private long m_credit;
		private bool m_debitObserved;
		private bool m_committed;
		private bool m_disposed;

		internal StorePaymentScope(Func<long, long, Guid, bool> change, long payerId, long amount)
		{
			if (payerId == 0) throw new ArgumentOutOfRangeException(nameof(payerId));
			if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));
			m_change = change;
			m_payerId = payerId;
			m_amount = amount;
			if (change == null) Approved = true;
			else
			{
				// Journal BEFORE reserving: a crash between the reservation and its commit/refund must
				// leave a record a restarting node can replay to net zero (see StorePaymentJournal).
				StorePaymentJournal.Reserve(m_reserveOperation, m_refundOperation, payerId, amount);
				Approved = change(payerId, -amount, m_reserveOperation);
				if (!Approved) StorePaymentJournal.Close(m_reserveOperation);
			}
		}

		public bool Approved { get; }

		/// <summary>True when this scope arbitrates through the World Authority rather than passing vanilla through.</summary>
		internal bool Deferrable => m_change != null;

		/// <summary>
		/// Runs the vanilla settlement inside this scope so its balance calls are observed, then commits. Used by the
		/// asynchronous prefab purchase, whose settlement happens in a spawn callback rather than inline.
		/// </summary>
		internal void Settle(Action settle)
		{
			if (settle == null) throw new ArgumentNullException(nameof(settle));
			if (!Approved || m_change == null) { settle(); return; }
			if (m_disposed) throw new InvalidOperationException("Store payment was already refunded");
			StorePaymentScope previous = m_payment;
			m_payment = this;
			try
			{
				settle();
				Commit();
			}
			finally { m_payment = previous; }
		}

		internal bool Observe(long ownerId, long amount)
		{
			if (!Approved || m_disposed) return false;
			if (ownerId == m_payerId && amount == -m_amount && !m_debitObserved)
			{
				m_debitObserved = true;
				return true;
			}
			if (ownerId != 0 && amount >= 0 && m_payeeId == 0)
			{
				m_payeeId = ownerId;
				m_credit = amount;
				return true;
			}
			throw new InvalidOperationException("Store payment did not match the prepared debit and credit");
		}

		public void Commit()
		{
			if (!Approved || m_committed || m_change == null) return;
			if (!m_debitObserved) throw new InvalidOperationException("Store payment did not apply its prepared debit");
			if (m_payeeId != 0 && m_credit != 0 && !m_change(m_payeeId, m_credit, m_creditOperation))
				throw new InvalidOperationException("Store payment credit was rejected");
			m_committed = true;
			StorePaymentJournal.Close(m_reserveOperation);
		}

		public void Dispose()
		{
			if (m_disposed) return;
			m_disposed = true;
			if (ReferenceEquals(m_payment, this)) m_payment = null;
			if (Approved && !m_committed && m_change != null)
			{
				if (!m_change(m_payerId, m_amount, m_refundOperation))
					throw new InvalidOperationException("Store payment refund was rejected");
				StorePaymentJournal.Close(m_reserveOperation);
			}
		}
	}
}
