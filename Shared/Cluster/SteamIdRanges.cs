using System.Collections.Generic;
using System.Globalization;

namespace ClusterPlugin;

/// <summary>SE-1316: a comma list of Steam IDs and first-last ranges, as <c>cleanup.botSteamIds</c> holds it.</summary>
internal sealed class SteamIdRanges
{
	private readonly List<(ulong First, ulong Last)> m_ranges = new List<(ulong, ulong)>();

	private SteamIdRanges() { }

	public bool IsEmpty => m_ranges.Count == 0;

	public bool Contains(ulong steamId)
	{
		if (steamId == 0) return false;
		foreach ((ulong first, ulong last) in m_ranges)
			if (steamId >= first && steamId <= last) return true;
		return false;
	}

	/// <summary>The ranges of <paramref name="text"/>, null when an item is not an id or a first-last range.</summary>
	public static SteamIdRanges Parse(string text)
	{
		var ranges = new SteamIdRanges();
		foreach (string raw in (text ?? string.Empty).Split(','))
		{
			string item = raw.Trim();
			if (item.Length == 0) continue;
			string[] ends = item.Split('-');
			if (ends.Length > 2
				|| !ulong.TryParse(ends[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out ulong first)
				|| first == 0)
				return null;
			ulong last = first;
			if (ends.Length == 2 && (!ulong.TryParse(ends[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out last) || last < first))
				return null;
			ranges.m_ranges.Add((first, last));
		}
		return ranges;
	}

	/// <summary>The config form of <paramref name="text"/> ("a,b-c", blanks dropped), null when it does not parse.</summary>
	public static string Normalize(string text)
	{
		SteamIdRanges ranges = Parse(text);
		if (ranges == null) return null;
		var items = new List<string>();
		foreach ((ulong first, ulong last) in ranges.m_ranges)
			items.Add(first == last
				? first.ToString(CultureInfo.InvariantCulture)
				: first.ToString(CultureInfo.InvariantCulture) + "-" + last.ToString(CultureInfo.InvariantCulture));
		return string.Join(",", items);
	}
}
