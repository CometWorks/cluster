using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using VRage;
using VRage.Game;
using VRage.ObjectBuilders.Private;

namespace ClusterPlugin;

/// <summary>
/// One broadcaster in the cluster-wide antenna graph. <paramref name="GridEntityId"/> is what lets a node ask
/// "can I reach that grid": the proxy antenna it installs is a standalone entity, so without the source grid
/// recorded there is no way back from a reachable broadcaster to the grid it belongs to.
/// </summary>
// The Laser* fields carry the far side of the cross-node laser handshake (MyLaserAntenna.ClusterLaserPeer):
// the peer's target, state-machine state, power/rotation status, and head position. Zero/false for non-lasers.
public sealed record AntennaGraphEntry(string Origin, long AntennaEntityId, string Builder, long GridEntityId = 0,
	bool IsLaser = false, long LaserTargetId = 0, byte LaserState = 0, bool LaserPowered = false,
	bool LaserRotationFinished = false, double LaserHeadX = 0, double LaserHeadY = 0, double LaserHeadZ = 0);
/// <summary>
/// One antenna-graph message, in either of its two shapes. FULL: <paramref name="Antennas"/> is the
/// whole set (a node's whole local set, or the authority's whole merged set). DELTA: Antennas is null
/// and <paramref name="Upserts"/>/<paramref name="Removed"/> are the entries that changed since the
/// state at <paramref name="Base"/>. Both carry the <paramref name="Revision"/> the message produces
/// and the <see cref="AntennaGraphCodec.Digest"/> of the whole set AFTER it is applied, so a receiver
/// that drifted (a report lost on a link flap, a delta applied to the wrong base) finds out on the
/// spot instead of relaying a wrong graph until the next full message.
/// </summary>
/// <remarks>
/// T-0154 (2026-09-15): before the delta form every node re-sent its whole local set (~1500
/// builders, ~4 MB, from the busiest node) and the authority re-published the whole merge (1992
/// entries, 5.8 MB, to every replica) on every change, i.e. continuously once anything flew:
/// 16 MB/s out of the World Authority through the gateway, and both at 14-15 GB RSS in 50 minutes.
/// A builder string changes only when its antenna moves or is re-configured, so a delta carries the
/// ships in flight (tens of entries) rather than the lattice (thousands).
/// </remarks>
public sealed record AntennaGraphMessage(AntennaGraphEntry[] Antennas = null, AntennaGraphEntry[] Upserts = null,
	long[] Removed = null, long Base = 0, long Revision = 0, long Digest = 0)
{
	public bool IsFull => Antennas != null;
}

/// <summary>The FULL shape alone; what <see cref="AntennaGraphCodec.Read"/> returns for callers that only ever want the whole set.</summary>
public sealed record AntennaGraphSnapshot(AntennaGraphEntry[] Antennas);

public static class AntennaGraphCodec
{
	public const int MaxAntennas = 4096;

	public static string Write(IEnumerable<AntennaGraphEntry> antennas) => WriteFull(antennas, 0);

	/// <summary>The FULL message: every entry, ordered by id, with its revision and digest.</summary>
	public static string WriteFull(IEnumerable<AntennaGraphEntry> antennas, long revision)
	{
		AntennaGraphEntry[] ordered = antennas.OrderBy(x => x.AntennaEntityId).ToArray();
		return JsonSerializer.Serialize(new AntennaGraphMessage(ordered, null, null, 0, revision, Digest(ordered)));
	}

	/// <summary>
	/// The DELTA message from the state at <paramref name="baseRevision"/> to <paramref name="revision"/>;
	/// <paramref name="digest"/> is of the whole set after the delta, not of the delta.
	/// </summary>
	public static string WriteDelta(IEnumerable<AntennaGraphEntry> upserts, IEnumerable<long> removed, long baseRevision, long revision, long digest)
		=> JsonSerializer.Serialize(new AntennaGraphMessage(null, upserts.OrderBy(x => x.AntennaEntityId).ToArray(),
			removed.OrderBy(x => x).ToArray(), baseRevision, revision, digest));

	public static AntennaGraphSnapshot Read(string payload)
	{
		AntennaGraphMessage message = ReadMessage(payload);
		if (!message.IsFull) throw new InvalidOperationException("Invalid antenna graph payload: a full snapshot is required");
		return new AntennaGraphSnapshot(message.Antennas);
	}

	public static AntennaGraphMessage ReadMessage(string payload)
	{
		if (payload == null || payload.Length > 16 * 1024 * 1024)
			throw new InvalidOperationException("Invalid antenna graph payload");
		AntennaGraphMessage message = JsonSerializer.Deserialize<AntennaGraphMessage>(payload)
			?? throw new InvalidOperationException("Invalid antenna graph payload");
		if (message.IsFull)
		{
			if (message.Upserts != null || message.Removed != null || message.Base != 0 || !ValidEntries(message.Antennas))
				throw new InvalidOperationException("Invalid antenna graph payload");
			return message;
		}
		if (message.Upserts == null || message.Removed == null || message.Removed.Length > MaxAntennas
			|| message.Base < 0 || message.Revision <= message.Base || !ValidEntries(message.Upserts)
			|| message.Removed.Any(x => x <= 0) || message.Removed.Distinct().Count() != message.Removed.Length
			|| message.Upserts.Any(x => Array.IndexOf(message.Removed, x.AntennaEntityId) >= 0))
			throw new InvalidOperationException("Invalid antenna graph delta");
		return message;
	}

	private static bool ValidEntries(AntennaGraphEntry[] entries)
		=> entries != null && entries.Length <= MaxAntennas
			&& !entries.Any(x => x == null || x.AntennaEntityId <= 0 || x.Origin == null || x.Origin.Length > 128
				|| string.IsNullOrEmpty(x.Builder) || x.Builder.Length > 1024 * 1024)
			&& entries.Select(x => x.AntennaEntityId).Distinct().Count() == entries.Length;

	/// <summary>
	/// The set after <paramref name="delta"/> is applied to <paramref name="current"/>: upserts replace or
	/// add by antenna id, removals of unknown ids are no-ops. Returned ordered by id. Throws when the
	/// result's digest is not the one the sender computed, which is the "wrong base" signal: the caller
	/// then re-synchronises from a full message rather than relaying a graph nobody else has.
	/// </summary>
	public static AntennaGraphEntry[] ApplyDelta(IEnumerable<AntennaGraphEntry> current, AntennaGraphMessage delta, Func<AntennaGraphEntry, AntennaGraphEntry> map = null)
	{
		if (delta == null || delta.IsFull) throw new ArgumentException("A delta is required", nameof(delta));
		var state = current.ToDictionary(x => x.AntennaEntityId);
		foreach (long removed in delta.Removed) state.Remove(removed);
		foreach (AntennaGraphEntry entry in delta.Upserts) state[entry.AntennaEntityId] = map == null ? entry : map(entry);
		if (state.Count > MaxAntennas) throw new InvalidOperationException("Antenna graph delta exceeds the entry cap");
		AntennaGraphEntry[] result = state.Values.OrderBy(x => x.AntennaEntityId).ToArray();
		long digest = Digest(result);
		if (digest != delta.Digest)
			throw new InvalidOperationException("Antenna graph delta digest mismatch: base=" + delta.Base + " revision=" + delta.Revision
				+ " expected=" + delta.Digest + " got=" + digest);
		return result;
	}

	/// <summary>
	/// Order-independent digest of a set: the unchecked sum of the per-entry hashes, so a sender can keep
	/// it incrementally and a receiver can recompute it from the set alone. Stable across processes
	/// (FNV-1a, never string.GetHashCode) and independent of <see cref="AntennaGraphEntry.Origin"/>,
	/// which the authority overwrites with the reporting node's id on the way through.
	/// </summary>
	public static long Digest(IEnumerable<AntennaGraphEntry> entries)
	{
		long sum = 0;
		foreach (AntennaGraphEntry entry in entries) sum = unchecked(sum + EntryDigest(entry));
		return sum;
	}

	public static long EntryDigest(AntennaGraphEntry entry)
	{
		const ulong Prime = 1099511628211UL;
		ulong hash = 14695981039346656037UL;
		void Mix(ulong value)
		{
			for (int shift = 0; shift < 64; shift += 8) hash = unchecked((hash ^ ((value >> shift) & 0xFF)) * Prime);
		}
		Mix((ulong)entry.AntennaEntityId);
		Mix((ulong)entry.GridEntityId);
		Mix(entry.IsLaser ? 1UL : 0UL);
		Mix((ulong)entry.LaserTargetId);
		Mix(entry.LaserState);
		Mix((entry.LaserPowered ? 1UL : 0UL) | (entry.LaserRotationFinished ? 2UL : 0UL));
		Mix((ulong)BitConverter.DoubleToInt64Bits(entry.LaserHeadX));
		Mix((ulong)BitConverter.DoubleToInt64Bits(entry.LaserHeadY));
		Mix((ulong)BitConverter.DoubleToInt64Bits(entry.LaserHeadZ));
		foreach (char character in entry.Builder) hash = unchecked((hash ^ character) * Prime);
		return unchecked((long)hash);
	}

	public static string WriteBuilder(MyObjectBuilder_ProxyAntenna builder)
	{
		ValidateBuilder(builder, builder.AntennaEntityId);
		// Every display string here comes from a name a player or a platform chose, and XmlWriter refuses the
		// C0 controls outright: a 0x18 in one killed a live node from AntennaGraphService.Update.
		// Serialization has to survive any name.
		builder.InfoName = XmlText(builder.InfoName);
		builder.StateText = XmlText(builder.StateText);
		if (builder.HudParams != null)
			foreach (MyObjectBuilder_HudEntityParams hud in builder.HudParams)
				if (hud != null) hud.Text = XmlText(hud.Text);
		using var stream = new MemoryStream();
		if (!MyObjectBuilderSerializerKeen.SerializeXML(stream, builder))
			throw new InvalidOperationException("Cannot serialize antenna proxy");
		return Convert.ToBase64String(stream.ToArray());
	}

	/// <summary>
	/// The parsed builder for an entry, reusing the last parse of an IDENTICAL builder string.
	/// READ-ONLY callers only - the instance is shared, so never hand it to anything that mutates it
	/// (MyEntities.CreateFromObjectBuilderAndAdd stamps an EntityId on it); use ReadBuilder for those.
	/// </summary>
	/// <remarks>
	/// The antenna graph is re-applied on every node once a second and the builder strings are almost always
	/// the same ones as the second before, but each MyObjectBuilderSerializerKeen.DeserializeXML call
	/// costs a fixed ~0.2 ms no matter how small the payload: 73% of it is the generated
	/// XmlSerializationReader's InitIDs, which re-interns its whole id table per call. Measured on the w22
	/// regression cluster 2026-08-22, node-3 (372 entities): AntennaGraphService.ApplySnapshot ran 25x in 30 s
	/// at a 16.5 ms median with 89% of that inside DeserializeXML, landing one over-budget frame per second on
	/// a node whose median frame is 9.2 ms. Keying the parse on the exact string turns the repeat applies, the
	/// authority's per-report validation and the /cluster/antenna-graph endpoint into dictionary lookups.
	/// </remarks>
	public static MyObjectBuilder_ProxyAntenna ReadBuilderShared(AntennaGraphEntry entry)
	{
		string key = entry?.Builder ?? throw new InvalidDataException("Invalid antenna proxy builder");
		lock (ParsedBuilders)
		{
			if (ParsedBuilders.TryGetValue(key, out MyObjectBuilder_ProxyAntenna cached)
				&& cached.AntennaEntityId == entry.AntennaEntityId)
				return cached;
		}
		// The exact-string cache above cannot hit for anything that MOVES, and in this world every
		// antenna is on a flying ship: the builder carries Position, so a moving antenna presents a new
		// key every snapshot and pays a full DeserializeXML. Measured on the w22 cluster 2026-08-22
		// (node-2, 90 s): ApplySnapshot held the game thread for 2196 ms of the window, 1972 ms of it
		// inside DeserializeXML and 1441 ms of THAT inside the generated reader's InitIDs - which is
		// fixed per call and has nothing to do with the payload.
		// So when the only things that changed are positions, patch them instead of re-parsing.
		var attributes = new List<VRageMath.Vector3D>(1);
		var elements = new List<VRageMath.Vector3D>();
		string masked = MaskPositions(DecodeXml(entry.Builder), attributes, elements);
		lock (ParsedBuilders)
		{
			if (!LastDecoded.TryGetValue(entry.AntennaEntityId, out Decoded last)
				|| last.Builder.AntennaEntityId != entry.AntennaEntityId)
			{
				Missed("no-memo", null);
			}
			else if (PositionsOnlyChange(last, key, masked, attributes.Count, elements.Count))
			{
				// The instance is shared and documented read-only, so patching it in place is the same
				// contract the exact-string hit already has: two callers holding it both want the newest
				// position anyway. Done under the lock so a reader never sees a torn vector.
				last.Builder.Position = attributes[0];
				for (int index = 0; index < elements.Count; index++) last.Builder.HudParams[index].Position = elements[index];
				ValidateBuilder(last.Builder, entry.AntennaEntityId);
				// The old key must go: it would otherwise hand a later caller this same instance carrying
				// a position that no longer belongs to that string.
				ParsedBuilders.Remove(last.Key);
				if (ParsedBuilders.Count >= MaxParsedBuilders) ParsedBuilders.Clear();
				ParsedBuilders[key] = last.Builder;
				LastDecoded[entry.AntennaEntityId] = new Decoded(key, masked, last.Builder);
				return last.Builder;
			}
		}
		MyObjectBuilder_ProxyAntenna parsed = ReadBuilder(entry);
		lock (ParsedBuilders)
		{
			// The graph is bounded (Read caps it at 4096 antennas) but the strings turn over as antennas move,
			// so drop the whole table rather than grow without bound. Refilling it costs one apply.
			if (ParsedBuilders.Count >= MaxParsedBuilders) ParsedBuilders.Clear();
			ParsedBuilders[key] = parsed;
			if (LastDecoded.Count >= MaxParsedBuilders) LastDecoded.Clear();
			LastDecoded[entry.AntennaEntityId] = new Decoded(key, masked, parsed);
		}
		return parsed;
	}

	/// <param name="Masked">The decoded document with its positions cut out (<see cref="MaskPositions"/>); null when they did not read.</param>
	private sealed record Decoded(string Key, string Masked, MyObjectBuilder_ProxyAntenna Builder);

	/// <summary>Keyed by antenna id, so a moving antenna's successive builders can be diffed against each other.</summary>
	private static readonly Dictionary<long, Decoded> LastDecoded = new Dictionary<long, Decoded>();

	private static string DecodeXml(string builder)
	{
		try { return Encoding.UTF8.GetString(Convert.FromBase64String(builder)); }
		catch (FormatException exception) { throw new InvalidDataException("Invalid antenna proxy builder", exception); }
	}

	/// <summary>
	/// True when the document behind <paramref name="key"/> differs from <paramref name="last"/>'s ONLY in the
	/// values of its Position elements, so the memoised builder becomes the new one by patching them.
	/// </summary>
	/// <remarks>
	/// Deliberately text rather than a parse: the whole point is to answer the question without paying for a
	/// parse. It is conservative in the only direction that matters - anything it does not recognise falls
	/// through to the full deserialize, so a false NEGATIVE costs what the code costs without it and a false
	/// POSITIVE is the only real hazard. Hence: both documents are compared whole with every position value cut
	/// out, so nothing but those values may differ, and every value must land in a known field.
	///
	/// A ship antenna's document carries TWO kinds of moving position (#1290). Its own Position is a
	/// SerializableVector3D, written as x/y/z ATTRIBUTES (VRage/SerializableVector3D.cs); each HudParams entry's
	/// Position is a VRageMath.Vector3D, written as X/Y/Z CHILD ELEMENTS. Both follow the ship. The first
	/// version recognised the attribute form only, so on the live #1191 cluster every ship antenna fell
	/// through (antenna_fastpath hits=0, diff-outside-position ~97 % of 4096 lookups, the span running from
	/// the antenna Position into HudParams).
	///
	/// The element name is NOT unique in this document type, which is the trap here (factory-fa, 2026-08-22).
	/// MyObjectBuilder_ProxyAntenna declares exactly one attribute-form Position, but MyObjectBuilder_EntityBase
	/// adds PositionAndOrientation and LocalPositionAndOrientation, and MyPositionAndOrientation serializes its
	/// own member as [XmlElement("Position")] - same name, same type, same attribute shape, and a HUD entry's
	/// Waypoints are the same type again. Today all of them are null on a captured proxy (AntennaGraphService
	/// builds `new MyObjectBuilder_ProxyAntenna { EntityId = 0 }` and never sets them), so this is latent - but
	/// it is latent on fields that any future path can populate, and a wrong mapping would fail silently. Hence
	/// the counts: exactly one attribute-form Position (the antenna's own, a struct always written), and exactly
	/// one element-form Position per HUD entry (a struct always written, so with the count equal no other
	/// element-form one exists and the i-th belongs to HudParams[i]). Anything else re-parses.
	/// </remarks>
	private static bool PositionsOnlyChange(Decoded last, string key, string masked, int attributes, int elements)
	{
		if (string.Equals(last.Key, key, StringComparison.Ordinal)) { Missed("identical", null); return false; }   // the caller already hit
		if (masked == null || last.Masked == null) { Missed("vector-parse", null); return false; }
		if (attributes != 1 || elements != (last.Builder.HudParams?.Count ?? 0) || last.Builder.HudParams?.Any(x => x == null) == true)
		{
			Missed("ambiguous-position-count", null);
			return false;
		}
		if (!string.Equals(last.Masked, masked, StringComparison.Ordinal))
		{
			// The class that matters: the documents differ somewhere OTHER than a position. Carry a sample of
			// the differing span so the cause is readable from the log without a second deployment -
			// factory-fa measured two serialisations of the SAME entities differing by dropped empty
			// <SubtypeName /> elements and float re-serialisation precision (2026-08-22), which would land
			// exactly here and would otherwise show up only as a count.
			Missed("diff-outside-position", Sample(last.Masked, masked));
			return false;
		}
		Hit();
		return true;
	}

	/// <summary>
	/// MEASUREMENT ONLY. The fast path above measured as NOTHING on its first deployment - median
	/// ApplySnapshot 31.45 ms at 254 stable proxies against a 28.9-30.9 ms pre-deploy baseline, where
	/// "well below 10" was predicted (findings/the-antenna-fast-path-measured-as-nothing.md). A count of
	/// misses alone would only confirm that it misses; the REASON is the whole question, so each miss is
	/// classified and the dominant class carries one sample of the offending span.
	/// </summary>
	private static readonly object FastPathLock = new object();
	private static long m_fastPathHits;
	private static long m_fastPathHitsTotal;
	private static readonly Dictionary<string, long> FastPathMisses = new Dictionary<string, long>();
	private static readonly Dictionary<string, string> FastPathSamples = new Dictionary<string, string>();
	private static readonly Dictionary<string, long> FastPathMissesTotal = new Dictionary<string, long>();
	private static long m_fastPathLookups;
	/// <summary>Report period, in lookups. ~250 proxies per snapshot at a few snapshots a second.</summary>
	private const int FastPathReportEvery = 4096;

	private static void Hit()
	{
		lock (FastPathLock) { m_fastPathHits++; m_fastPathHitsTotal++; ReportFastPathIfDue(); }
	}

	/// <summary>Every hit since the process started, never reset (the report's hits= is per period); for the self-test.</summary>
	internal static long FastPathHitsTotal { get { lock (FastPathLock) return m_fastPathHitsTotal; } }

	/// <summary>
	/// Hits and misses by class since the process started, for GET /cluster/antenna-graph: the log report comes
	/// every 4096 lookups, which a bench with a handful of antennas does not reach in a run.
	/// </summary>
	internal static object FastPathTotals()
	{
		lock (FastPathLock)
			return new { hits = m_fastPathHitsTotal, misses = new SortedDictionary<string, long>(FastPathMissesTotal, StringComparer.Ordinal) };
	}

	private static void Missed(string reason, string sample)
	{
		lock (FastPathLock)
		{
			FastPathMisses.TryGetValue(reason, out long count);
			FastPathMisses[reason] = count + 1;
			FastPathMissesTotal.TryGetValue(reason, out long total);
			FastPathMissesTotal[reason] = total + 1;
			// First sample of each class per report period only: one is evidence, thousands are a flood.
			if (sample != null && !FastPathSamples.ContainsKey(reason)) FastPathSamples[reason] = sample;
			ReportFastPathIfDue();
		}
	}

	private static void ReportFastPathIfDue()
	{
		if (++m_fastPathLookups % FastPathReportEvery != 0) return;
		System.Text.StringBuilder line = new System.Text.StringBuilder("[ClusterPlugin] antenna_fastpath hits=")
			.Append(m_fastPathHits);
		foreach (KeyValuePair<string, long> miss in FastPathMisses) line.Append(' ').Append(miss.Key).Append('=').Append(miss.Value);
		foreach (KeyValuePair<string, string> sample in FastPathSamples)
			line.Append(" sample[").Append(sample.Key).Append("]=").Append(sample.Value);
		Console.WriteLine(line.ToString());
		m_fastPathHits = 0;
		FastPathMisses.Clear();
		FastPathSamples.Clear();
	}

	/// <summary>The differing span from both documents, bounded and with newlines flattened, for one log line.</summary>
	private static string Sample(string before, string after)
	{
		const int Max = 120;
		int limit = Math.Min(before.Length, after.Length);
		int prefix = 0;
		while (prefix < limit && before[prefix] == after[prefix]) prefix++;
		int suffix = 0;
		while (suffix < limit - prefix && before[before.Length - 1 - suffix] == after[after.Length - 1 - suffix]) suffix++;
		string a = Slice(before, prefix, before.Length - suffix, Max);
		string b = Slice(after, prefix, after.Length - suffix, Max);
		return "at" + prefix + " {" + a + "} -> {" + b + "}";
	}

	private static string Slice(string xml, int start, int end, int max)
	{
		if (start < 0) start = 0;
		if (end > xml.Length) end = xml.Length;
		if (end <= start) return "";
		string cut = xml.Substring(start, Math.Min(end - start, max)).Replace('\n', ' ').Replace('\r', ' ');
		return end - start > max ? cut + "..." : cut;
	}

	/// <summary>
	/// <paramref name="xml"/> with every Position element replaced by a placeholder, its value added to
	/// <paramref name="attributes"/> (x/y/z attribute form) or <paramref name="elements"/> (X/Y/Z child form)
	/// in document order. Null when a Position element of either form does not read as a finite vector.
	/// </summary>
	/// <remarks>
	/// Every "&lt;Position " and "&lt;Position&gt;" of the input is consumed (masked, or the whole call returns
	/// null), and both placeholders are text no serializer writes (an unquoted attribute value; a "#" that is not
	/// a number), so two masked documents are equal only when the inputs differ in position values alone.
	/// </remarks>
	private static string MaskPositions(string xml, List<VRageMath.Vector3D> attributes, List<VRageMath.Vector3D> elements)
	{
		const string Open = "<Position", ElementClose = "</Position>";
		var masked = new StringBuilder(xml.Length);
		int copied = 0;
		for (int open = xml.IndexOf(Open, StringComparison.Ordinal); open >= 0; open = xml.IndexOf(Open, copied, StringComparison.Ordinal))
		{
			int after = open + Open.Length;
			char next = after < xml.Length ? xml[after] : '\0';
			int end;
			VRageMath.Vector3D value;
			if (next == ' ')
			{
				end = xml.IndexOf('>', after);
				if (end < 0 || xml[end - 1] != '/' || xml.IndexOf('<', after, end - after) >= 0
					|| !TryReadVector(xml.Substring(open, end - open + 1), out value)) return null;
				attributes.Add(value);
				masked.Append(xml, copied, open - copied).Append("<Position x=# />");
			}
			else if (next == '>')
			{
				int close = xml.IndexOf(ElementClose, after, StringComparison.Ordinal);
				if (close < 0) return null;
				System.Text.RegularExpressions.Match match = ElementVector.Match(xml, after + 1, close - after - 1);
				if (!match.Success || match.Index != after + 1 || match.Length != close - after - 1
					|| !TryReadNumber(match.Groups[1].Value, out double x) || !TryReadNumber(match.Groups[2].Value, out double y)
					|| !TryReadNumber(match.Groups[3].Value, out double z)) return null;
				elements.Add(new VRageMath.Vector3D(x, y, z));
				end = close + ElementClose.Length - 1;
				masked.Append(xml, copied, open - copied).Append("<Position>#</Position>");
			}
			else
			{
				// PositionAndOrientation and the like: ordinary text, compared as is.
				masked.Append(xml, copied, after - copied);
				copied = after;
				continue;
			}
			copied = end + 1;
		}
		return masked.Append(xml, copied, xml.Length - copied).ToString();
	}

	/// <summary>The inside of a Vector3D element as XmlSerializer writes it (VRage.Game.XmlSerializers: X, Y, Z children).</summary>
	private static readonly System.Text.RegularExpressions.Regex ElementVector = new System.Text.RegularExpressions.Regex(
		@"\G\s*<X>([^<]*)</X>\s*<Y>([^<]*)</Y>\s*<Z>([^<]*)</Z>\s*", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

	private static bool TryReadNumber(string text, out double value)
		=> double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

	private static bool TryReadVector(string element, out VRageMath.Vector3D value)
	{
		value = default;
		if (!TryReadAttribute(element, "x", out double x) || !TryReadAttribute(element, "y", out double y)
			|| !TryReadAttribute(element, "z", out double z)) return false;
		value = new VRageMath.Vector3D(x, y, z);
		return double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(z);
	}

	private static bool TryReadAttribute(string element, string name, out double value)
	{
		value = 0;
		int at = element.IndexOf(" " + name + "=\"", StringComparison.Ordinal);
		if (at < 0) return false;
		int from = at + name.Length + 3;
		int to = element.IndexOf('"', from);
		// NumberStyles.Float also accepts leading whitespace and a sign, which the serializer never emits.
		// Accepting a shape that cannot exist is harmless; narrowing it would only add a way to fall through.
		return to > 0 && double.TryParse(element.AsSpan(from, to - from), NumberStyles.Float,
			CultureInfo.InvariantCulture, out value);
	}

	private static readonly Dictionary<string, MyObjectBuilder_ProxyAntenna> ParsedBuilders =
		new Dictionary<string, MyObjectBuilder_ProxyAntenna>(StringComparer.Ordinal);
	private const int MaxParsedBuilders = 8192;

	/// <summary>Parses a FRESH builder instance. Use for anything that hands the builder to the engine.</summary>
	/// <remarks>
	/// #1257: MyObjectBuilderSerializerKeen.DeserializeXML does exactly this, except that every call gets a
	/// new XmlReader with a new name table, and the generated reader's InitIDs adds every element and
	/// attribute name its assembly knows to it (about 7000 NameTable.Add calls): 421 KB of garbage per
	/// parse, whatever the payload (self-test antenna-parse-alloc; 53 KB with the table below). The fast
	/// path in ReadBuilderShared misses for every moving antenna (#1191, 2026-10-21: antenna_fastpath
	/// hits=0, diff-outside-position ~97 %), so a node parses ~3000-6000 builders a minute: 1.3-2.4 GB/min
	/// of allocation per node. A name table kept per thread is filled once; later readers find every name
	/// already there and allocate nothing for it. A name table only atomizes names, so it is bounded by
	/// the schema, not by the payloads.
	/// </remarks>
	public static MyObjectBuilder_ProxyAntenna ReadBuilder(AntennaGraphEntry entry)
	{
		byte[] xml;
		try { xml = Convert.FromBase64String(entry.Builder); }
		catch (FormatException exception) { throw new InvalidDataException("Invalid antenna proxy builder", exception); }
		using var stream = new MemoryStream(xml, writable: false);
		MyObjectBuilder_ProxyAntenna builder;
		try
		{
			XmlReaderSettings settings = new XmlReaderSettings { CheckCharacters = false, NameTable = t_names ??= new NameTable() };
			builder = MyXmlSerializerManager.GetSerializer(typeof(MyObjectBuilder_ProxyAntenna))
				.Deserialize(new MyXmlTextReader(stream, settings)) as MyObjectBuilder_ProxyAntenna;
		}
		catch (Exception exception)
		{
			throw new InvalidDataException("Invalid antenna proxy builder", exception);
		}
		ValidateBuilder(builder, entry.AntennaEntityId);
		return builder;
	}

	[ThreadStatic] private static NameTable t_names;

	private static void ValidateBuilder(MyObjectBuilder_ProxyAntenna builder, long expectedId)
	{
		if (builder == null || builder.EntityId != 0 || builder.AntennaEntityId != expectedId || expectedId <= 0
			|| builder.InfoEntityId <= 0 || builder.Owner < 0 || !Finite(builder.Position)
			|| builder.BroadcastRadius < 0f || !float.IsFinite(builder.BroadcastRadius)
			|| builder.HudParams?.Count > 1024 || builder.InfoName?.Length > 1024 || builder.StateText?.Length > 1024
			|| builder.MainRemoteOwner.HasValue && builder.MainRemoteOwner.Value < 0
			|| builder.MainRemoteId.HasValue && builder.MainRemoteId.Value < 0)
			throw new InvalidDataException("Invalid antenna proxy builder");
	}

	/// <summary>
	/// The input with every character XML 1.0 cannot carry dropped, surrogate pairs kept whole so an emoji in a
	/// player name survives. Null passes through: the fields are optional.
	/// </summary>
	internal static string XmlText(string text)
	{
		if (text == null) return null;
		StringBuilder kept = null;
		for (int index = 0; index < text.Length; index++)
		{
			char character = text[index];
			bool pair = index + 1 < text.Length && XmlConvert.IsXmlSurrogatePair(text[index + 1], character);
			if (pair || XmlConvert.IsXmlChar(character))
			{
				kept?.Append(character);
				if (pair)
				{
					kept?.Append(text[index + 1]);
					index++;
				}
				continue;
			}
			kept ??= new StringBuilder(text.Length).Append(text, 0, index);
		}
		return kept?.ToString() ?? text;
	}

	private static bool Finite(VRageMath.Vector3D value)
		=> double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);
}
