using System;

namespace ClusterPlugin;

internal sealed class SaveSchedulePolicy
{
	private double m_lowSeconds;
	private double m_highSeconds;

	public int Level { get; private set; } = 1;

	public void Sample(double simulationRatio, double elapsedSeconds)
	{
		if (elapsedSeconds < 0) throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
		if (simulationRatio < 0.9)
		{
			m_highSeconds = 0;
			m_lowSeconds += elapsedSeconds;
			if (Level == 1 && m_lowSeconds >= 10) Level = 2;
			if (Level == 2 && m_lowSeconds >= 40) Level = 4;
		}
		else if (simulationRatio > 0.97)
		{
			m_lowSeconds = 0;
			m_highSeconds += elapsedSeconds;
			while (Level > 1 && m_highSeconds >= 30)
			{
				Level /= 2;
				m_highSeconds -= 30;
			}
		}
		else
		{
			m_lowSeconds = 0;
			m_highSeconds = 0;
		}
	}

	/// <summary>
	/// Maximum rollback: no partition may go longer than this between catalogued saves
	/// (Viktor, 2026-08-19 - see Docs/Gateway/StateDurability.md). The bound is the
	/// governing metric for the whole cluster, so it caps the adaptive backoff rather than the
	/// backoff capping it. Override with CLUSTER_MAX_ROLLBACK_SECONDS.
	/// </summary>
	public static readonly int MaxRollbackSeconds =
		int.TryParse(Environment.GetEnvironmentVariable("CLUSTER_MAX_ROLLBACK_SECONDS"), out int seconds)
		&& seconds > 0 ? seconds : 60;

	/// <summary>
	/// How long this partition may wait for its next save.
	///
	/// The adaptive Level still decides how hard to back off when the simulation is struggling,
	/// but it can no longer breach the rollback bound. Before 2026-08-19 a clientless partition
	/// degraded to 300 s, five times the bound - and "clientless" does not mean "idle" here: the
	/// regression fleet's Visitors fly on autopilot in partitions with no player attached, so
	/// five minutes of lost flight was a real rollback that the metric forbids.
	///
	/// This does mean a lagging node now saves more often than it used to, which is the opposite
	/// of what the backoff was for. That trade is deliberate and is affordable because the saves
	/// themselves got much cheaper: state is compressed now, and the gateway no longer floods the
	/// same disk with 99 MB/s of registry writes while the nodes are trying to save.
	/// </summary>
	public int EffectiveIntervalSeconds(bool hasPlayer) => Math.Min(
		hasPlayer ? 30 * Math.Min(Level, 2) : 60 * Level, MaxRollbackSeconds);
	public bool IsLagAlarm(double lagSeconds, bool hasPlayer) => lagSeconds > (hasPlayer ? 90 : 180);
}
