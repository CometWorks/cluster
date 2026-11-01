using System;

namespace ClusterPlugin;

public sealed class ClusterClock
{
	private const double MaximumRoundTripMilliseconds = 10000;
	private const double MaximumDrift = 0.001;

	/// <summary>
	/// Slew small corrections, step large ones - the rule every clock synchroniser uses. A 0.1% slew hides
	/// jitter, but it needs a thousand seconds per second of error: an admin moving the time of day by hours
	/// would never arrive. Anything past this is a deliberate jump of the authority's game time.
	/// </summary>
	private const double StepThresholdMilliseconds = 2000;
	private readonly object m_lock = new object();
	private bool m_initialized;
	private double m_offset;
	private double m_drift;
	private double m_lastSampleMidpoint;
	private double m_lastObservedOffset;
	private double m_lastLocal;
	private double m_lastOutput;

	private double? m_steppedTo;

	public bool IsInitialized { get { lock (m_lock) return m_initialized; } }

	/// <summary>
	/// The output of the last step, once. Whoever follows this clock has to tell its own listeners about a
	/// jump: a slewed correction is invisible, a step is not.
	/// </summary>
	public double? TakeStep()
	{
		lock (m_lock)
		{
			double? stepped = m_steppedTo;
			m_steppedTo = null;
			return stepped;
		}
	}

	public double OffsetMilliseconds { get { lock (m_lock) return m_offset; } }
	public double Drift { get { lock (m_lock) return m_drift; } }

	public bool Sample(double localSendMilliseconds, double localReceiveMilliseconds, double gatewayMilliseconds)
	{
		if (!double.IsFinite(localSendMilliseconds) || !double.IsFinite(localReceiveMilliseconds) || !double.IsFinite(gatewayMilliseconds)
			|| localSendMilliseconds < 0 || gatewayMilliseconds < 0 || localReceiveMilliseconds < localSendMilliseconds
			|| localReceiveMilliseconds - localSendMilliseconds > MaximumRoundTripMilliseconds)
			return false;

		double midpoint = (localSendMilliseconds + localReceiveMilliseconds) / 2;
		double observedOffset = gatewayMilliseconds - midpoint;
		lock (m_lock)
		{
			if (m_initialized && midpoint <= m_lastSampleMidpoint) return false;
			if (!m_initialized)
			{
				m_initialized = true;
				m_offset = observedOffset;
				m_lastOutput = localReceiveMilliseconds + observedOffset;
				m_lastLocal = localReceiveMilliseconds;
			}
			else
			{
				double elapsed = midpoint - m_lastSampleMidpoint;
				double observedDrift = Math.Clamp((observedOffset - m_lastObservedOffset) / elapsed, -MaximumDrift, MaximumDrift);
				m_drift = m_drift * 0.9 + observedDrift * 0.1;
				double predictedOffset = m_offset + m_drift * elapsed;
				m_offset = predictedOffset * 0.875 + observedOffset * 0.125;
			}
			m_lastSampleMidpoint = midpoint;
			m_lastObservedOffset = observedOffset;
			return true;
		}
	}

	public double Now(double localMilliseconds, double vanillaMilliseconds)
	{
		lock (m_lock)
		{
			if (!m_initialized)
			{
				m_lastLocal = localMilliseconds;
				m_lastOutput = vanillaMilliseconds;
				return vanillaMilliseconds;
			}
			double elapsed = Math.Max(0, localMilliseconds - m_lastLocal);
			double unslewed = m_lastOutput + elapsed;
			double target = localMilliseconds + m_offset + m_drift * (localMilliseconds - m_lastSampleMidpoint);
			double output;
			if (Math.Abs(target - unslewed) > StepThresholdMilliseconds)
			{
				output = target;
				m_steppedTo = target;
			}
			else output = unslewed + Math.Clamp(target - unslewed, -elapsed * 0.001, elapsed * 0.001);
			if (output <= m_lastOutput) output = Math.BitIncrement(m_lastOutput);
			m_lastLocal = Math.Max(m_lastLocal, localMilliseconds);
			m_lastOutput = output;
			return output;
		}
	}
}
