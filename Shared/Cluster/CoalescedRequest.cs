using System;
using System.Threading;

namespace ClusterPlugin;

/// <summary>
/// SE1-0084: a request that may be asked for from any thread and is sent by one worker at a time; asks while one is out
/// or waiting coalesce into one more send after it. <see cref="ClusterRuntime"/>'s Gateway pass request (POST
/// /registry/report-now) is the one user.
///
/// F40 (#1285): the worker used to leave on <c>wanted == 0</c> and then reset <c>posting</c>. An ask between the two
/// found <c>posting</c> still set, left its <c>wanted</c> for the worker and returned; the worker had already looked, so
/// nobody sent it. The worker now looks at <c>wanted</c> again after the reset (the classic double-check) and goes on
/// if it can take <c>posting</c> back. The reset is a full fence (<see cref="Interlocked.Exchange(ref int, int)"/>):
/// a plain store could pass the read after it, which is the same lost ask again.
/// </summary>
internal sealed class CoalescedRequest
{
	private readonly Action m_send;
	private readonly Action<Exception> m_failed;
	private readonly Action<Action> m_start;
	private int m_wanted, m_posting;

	/// <summary>Self-test hook: runs on the worker between leaving the send loop and resetting <c>posting</c>, the
	/// window F40 lost an ask in.</summary>
	internal Action AfterDrain;

	/// <param name="send">One request; throws when it failed. Runs on the worker only, never two at once.</param>
	/// <param name="failed">A send's exception. The worker stops on it; an ask made during the failed send still gets
	/// its own send, the rest wait for the next ask.</param>
	/// <param name="start">Runs the worker; the thread pool by default.</param>
	public CoalescedRequest(Action send, Action<Exception> failed, Action<Action> start = null)
	{
		m_send = send;
		m_failed = failed;
		m_start = start ?? (work => ThreadPool.QueueUserWorkItem(_ => work()));
	}

	/// <summary>Asks for one send after this call. Returns whether this call started the worker.</summary>
	public bool Request()
	{
		Volatile.Write(ref m_wanted, 1);
		if (Interlocked.CompareExchange(ref m_posting, 1, 0) != 0) return false;
		m_start(Drain);
		return true;
	}

	private void Drain()
	{
		do
		{
			try
			{
				try
				{
					while (Interlocked.Exchange(ref m_wanted, 0) == 1) m_send();
				}
				catch (Exception exception)
				{
					m_failed(exception);
				}
				AfterDrain?.Invoke();
			}
			finally
			{
				Interlocked.Exchange(ref m_posting, 0);
			}
		}
		while (Volatile.Read(ref m_wanted) == 1 && Interlocked.CompareExchange(ref m_posting, 1, 0) == 0);
	}
}
