using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;

namespace ClusterPlugin;

/// <summary>
/// SE-1146: the World Authority's clean-up of drop container GPS markers ("Unknown Signal", "Strong Unknown Signal"). The
/// node holding a drop deletes its marker at the WA (GpsAuthorityPatch.DeleteContainerPrefix), but a drop can leave without
/// that: a node that got it by a handover or a reload never counted its marker down, and markers from before the fix stayed
/// on the WA for good. Octahedron collected ~30 of them in a day, several stuck at "T - 0sec". So the WA deletes a marker
/// once it has had it for longer than vanilla keeps any drop (its grid timeout, plus a minute of slack), and deletes the
/// oldest markers while more drops are marked than drops.maxActive allows. Markers are not saved with the WA's checkpoint
/// (vanilla saves only GPS whose entity is on the saving server), so a WA restart starts them all fresh.
/// </summary>
internal static class ContainerGpsSweep
{
	private const double SweepIntervalMilliseconds = 5000;
	private const int SlackSeconds = 60;
	private static readonly Dictionary<long, double> s_firstSeen = new();
	private static double s_nextSweep;

	public static void Update()
	{
		if (!ClusterRuntime.IsClustered || !ClusterRuntime.IsWorldAuthority || MySession.Static?.Ready != true) return;
		double now = ClusterRuntime.LocalMilliseconds;
		if (now < s_nextSweep) return;
		s_nextSweep = now + SweepIntervalMilliseconds;
		var definition = MySession.Static.GetComponent<MySessionComponentContainerDropSystem>()?.m_definition;
		if (definition == null) return;

		var marked = new HashSet<long>(MySession.Static.Gpss.m_playerGpss.Values
			.SelectMany(entries => entries.Values)
			.Where(gps => gps.IsContainerGPS && gps.EntityId != 0)
			.Select(gps => gps.EntityId));
		foreach (long gone in s_firstSeen.Keys.Where(id => !marked.Contains(id)).ToArray())
			s_firstSeen.Remove(gone);
		foreach (long id in marked)
			s_firstSeen.TryAdd(id, now);

		double lifetime = (Math.Max(definition.PersonalContainerGridTimeOut, definition.CompetetiveContainerGridTimeOut) + SlackSeconds) * 1000.0;
		List<long> expired = s_firstSeen.Where(pair => now - pair.Value > lifetime).Select(pair => pair.Key).ToList();
		int cap = ClusterConfig.GetInt("drops.maxActive");
		int overCap = cap > 0 ? s_firstSeen.Count - expired.Count - cap : 0;
		List<long> oldest = overCap > 0
			? s_firstSeen.Where(pair => !expired.Contains(pair.Key)).OrderBy(pair => pair.Value).Take(overCap).Select(pair => pair.Key).ToList()
			: new List<long>();
		if (expired.Count == 0 && oldest.Count == 0) return;

		foreach (long id in expired.Concat(oldest))
		{
			MySession.Static.Gpss.SendDeleteContainerGpsRequest(id, 0);
			s_firstSeen.Remove(id);
		}
		Console.WriteLine("[ClusterPlugin] drop_container gps sweep expired=" + expired.Count + " overCap=" + oldest.Count
			+ " cap=" + cap + " remaining=" + s_firstSeen.Count);
	}

	/// <summary>
	/// This node's drop container markers, one row per container with the identities holding it, and on the WA the
	/// seconds since it first saw each. Game thread.
	/// </summary>
	public static object Status()
	{
		double now = ClusterRuntime.LocalMilliseconds;
		var markers = MySession.Static.Gpss.m_playerGpss
			.SelectMany(entries => entries.Value.Values.Where(gps => gps.IsContainerGPS).Select(gps => (Identity: entries.Key, Gps: gps)))
			.GroupBy(entry => entry.Gps.EntityId)
			.Select(group => new
			{
				entity = group.Key,
				name = group.First().Gps.Name,
				identities = group.Count(),
				remaining = group.First().Gps.ContainerRemainingTime,
				seenSeconds = s_firstSeen.TryGetValue(group.Key, out double seen) ? Math.Round((now - seen) / 1000) : -1
			})
			.ToArray();
		return new { node = Environment.GetEnvironmentVariable("CLUSTER_NODE_ID") ?? string.Empty, count = markers.Length, markers };
	}
}
