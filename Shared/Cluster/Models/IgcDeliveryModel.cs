using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using VRageMath;

namespace ClusterPlugin;

/// <summary>
/// One IGC message crossing a node boundary. <paramref name="Addressee"/> is 0 for a broadcast.
/// <paramref name="OperationId"/> is the exactly-once key and <paramref name="Sequence"/> the per-sender order,
/// so a retry during handover neither duplicates a message nor delivers it out of order.
/// <paramref name="TargetNode"/> names the node the sender numbered this stream for: a message buffered through
/// a handover is forwarded to the partition's new owner still carrying the old stream identity, and without the
/// target in the stream key the forwarded sequences would collide with the fresh stream the sender starts
/// against the new owner.
/// </summary>
public sealed record ClusterIgcDelivery(
	Guid OperationId,
	string Origin,
	long SourceAddress,
	long Addressee,
	string Tag,
	string PayloadType,
	string Payload,
	int Distance,
	long Sequence,
	string TargetNode = null,
	// For a broadcast copy held across a partition handover: the single programmable block whose listeners this
	// copy may reach. 0 for ordinary deliveries. The original broadcast already reached every other listener, so
	// a forwarded copy that re-fanned would duplicate.
	long ListenerScope = 0);

public sealed record ClusterIgcDeliveryResult(bool Delivered, string Error);

/// <summary>
/// Reports which broadcast tags a node currently has listeners for, so a broadcast is only fanned out to nodes
/// that can consume it. Vanilla drops a broadcast with no matching listener on the floor; sending it across the
/// cluster anyway would turn every script's heartbeat into cluster-wide traffic.
/// </summary>
public sealed record ClusterIgcListeners(string Origin, string[] Tags);

public sealed record ClusterIgcListenerSnapshot(ClusterIgcListeners[] Nodes);

public static class IgcDeliveryCodec
{
	public const int MaxTagLength = 256;
	public const int MaxPayloadLength = 64 * 1024;
	public const int MaxRequestLength = 8 * 1024 * 1024;

	public static string WriteRequest(IReadOnlyList<ClusterIgcDelivery> deliveries)
	{
		foreach (ClusterIgcDelivery delivery in deliveries) Validate(delivery);
		return JsonSerializer.Serialize(deliveries);
	}

	public static ClusterIgcDelivery[] ReadRequest(string payload)
	{
		if (payload == null || payload.Length > MaxRequestLength)
			throw new InvalidOperationException("Invalid IGC delivery payload");
		ClusterIgcDelivery[] deliveries = JsonSerializer.Deserialize<ClusterIgcDelivery[]>(payload)
			?? throw new InvalidOperationException("Invalid IGC delivery payload");
		foreach (ClusterIgcDelivery delivery in deliveries) Validate(delivery);
		return deliveries;
	}

	public static void ValidateDecision(ClusterIgcDelivery delivery)
	{
		if (delivery == null || delivery.OperationId == Guid.Empty
			|| delivery.SourceAddress == 0 || delivery.Sequence < 0
			|| string.IsNullOrEmpty(delivery.Tag) || delivery.Tag.Length > MaxTagLength
			|| string.IsNullOrEmpty(delivery.PayloadType) || delivery.PayloadType.Length > 64
			|| delivery.Payload == null || delivery.Payload.Length > MaxPayloadLength
			|| delivery.Origin?.Length > 128
			|| delivery.ListenerScope < 0 || delivery.ListenerScope != 0 && delivery.Addressee != 0)
			throw new InvalidOperationException("Invalid IGC delivery payload");
	}

	public static void Validate(ClusterIgcDelivery delivery)
	{
		ValidateDecision(delivery);
		if (string.IsNullOrEmpty(delivery.Origin))
			throw new InvalidOperationException("Invalid IGC delivery payload");
	}

	public static string WriteListeners(IEnumerable<ClusterIgcListeners> nodes)
		=> JsonSerializer.Serialize(new ClusterIgcListenerSnapshot(
			nodes.OrderBy(node => node.Origin, StringComparer.Ordinal).ToArray()));

	public static ClusterIgcListenerSnapshot ReadListeners(string payload)
	{
		if (payload == null || payload.Length > 1024 * 1024)
			throw new InvalidOperationException("Invalid IGC listener payload");
		ClusterIgcListenerSnapshot snapshot = JsonSerializer.Deserialize<ClusterIgcListenerSnapshot>(payload)
			?? throw new InvalidOperationException("Invalid IGC listener payload");
		if (snapshot.Nodes == null
			|| snapshot.Nodes.Any(node => node == null || string.IsNullOrEmpty(node.Origin) || node.Origin.Length > 128
				|| node.Tags == null || node.Tags.Length > 4096
				|| node.Tags.Any(tag => string.IsNullOrEmpty(tag) || tag.Length > MaxTagLength)))
			throw new InvalidOperationException("Invalid IGC listener payload");
		return snapshot;
	}

	/// <summary>
	/// Encodes an IGC payload for the wire. IGC data is a boxed object whose permitted shapes vanilla restricts to
	/// primitives, strings, immutable arrays and tuples of those. Only the flat shapes are encoded here: an
	/// unsupported one returns false so the send fails the same way an unreachable addressee already does, rather
	/// than crossing the boundary as something the far side would have to guess at.
	/// </summary>
	public static bool TryEncode(object data, out string type, out string payload)
	{
		type = null;
		payload = null;
		switch (data)
		{
			case null: return false;
			case string value: type = "string"; payload = value; break;
			case bool value: type = "bool"; payload = value ? "1" : "0"; break;
			case byte value: type = "byte"; payload = value.ToString(CultureInfo.InvariantCulture); break;
			case sbyte value: type = "sbyte"; payload = value.ToString(CultureInfo.InvariantCulture); break;
			case short value: type = "short"; payload = value.ToString(CultureInfo.InvariantCulture); break;
			case ushort value: type = "ushort"; payload = value.ToString(CultureInfo.InvariantCulture); break;
			case int value: type = "int"; payload = value.ToString(CultureInfo.InvariantCulture); break;
			case uint value: type = "uint"; payload = value.ToString(CultureInfo.InvariantCulture); break;
			case long value: type = "long"; payload = value.ToString(CultureInfo.InvariantCulture); break;
			case ulong value: type = "ulong"; payload = value.ToString(CultureInfo.InvariantCulture); break;
			case float value: type = "float"; payload = value.ToString("R", CultureInfo.InvariantCulture); break;
			case double value: type = "double"; payload = value.ToString("R", CultureInfo.InvariantCulture); break;
			case Vector3D value: type = "Vector3D"; payload = JsonSerializer.Serialize(new[] { value.X, value.Y, value.Z }); break;
			case Vector3 value: type = "Vector3"; payload = JsonSerializer.Serialize(new[] { value.X, value.Y, value.Z }); break;
			case Vector3I value: type = "Vector3I"; payload = JsonSerializer.Serialize(new[] { value.X, value.Y, value.Z }); break;
			case string[] value: type = "string[]"; payload = JsonSerializer.Serialize(value); break;
			case long[] value: type = "long[]"; payload = JsonSerializer.Serialize(value); break;
			case int[] value: type = "int[]"; payload = JsonSerializer.Serialize(value); break;
			case double[] value: type = "double[]"; payload = JsonSerializer.Serialize(value); break;
			case float[] value: type = "float[]"; payload = JsonSerializer.Serialize(value); break;
			default: return false;
		}
		return payload.Length <= MaxPayloadLength;
	}

	/// <summary>Decodes a payload written by <see cref="TryEncode"/>. Throws on anything it did not write.</summary>
	public static object Decode(string type, string payload) => type switch
	{
		"string" => payload,
		"bool" => payload == "1",
		"byte" => byte.Parse(payload, CultureInfo.InvariantCulture),
		"sbyte" => sbyte.Parse(payload, CultureInfo.InvariantCulture),
		"short" => short.Parse(payload, CultureInfo.InvariantCulture),
		"ushort" => ushort.Parse(payload, CultureInfo.InvariantCulture),
		"int" => int.Parse(payload, CultureInfo.InvariantCulture),
		"uint" => uint.Parse(payload, CultureInfo.InvariantCulture),
		"long" => long.Parse(payload, CultureInfo.InvariantCulture),
		"ulong" => ulong.Parse(payload, CultureInfo.InvariantCulture),
		"float" => float.Parse(payload, CultureInfo.InvariantCulture),
		"double" => double.Parse(payload, CultureInfo.InvariantCulture),
		"Vector3D" => ReadVector3D(payload),
		"Vector3" => ReadVector3(payload),
		"Vector3I" => ReadVector3I(payload),
		"string[]" => JsonSerializer.Deserialize<string[]>(payload),
		"long[]" => JsonSerializer.Deserialize<long[]>(payload),
		"int[]" => JsonSerializer.Deserialize<int[]>(payload),
		"double[]" => JsonSerializer.Deserialize<double[]>(payload),
		"float[]" => JsonSerializer.Deserialize<float[]>(payload),
		_ => throw new InvalidOperationException("Unsupported IGC payload type " + type)
	};

	private static Vector3D ReadVector3D(string payload)
	{
		double[] values = JsonSerializer.Deserialize<double[]>(payload);
		if (values == null || values.Length != 3) throw new InvalidOperationException("Invalid Vector3D payload");
		return new Vector3D(values[0], values[1], values[2]);
	}

	private static Vector3 ReadVector3(string payload)
	{
		float[] values = JsonSerializer.Deserialize<float[]>(payload);
		if (values == null || values.Length != 3) throw new InvalidOperationException("Invalid Vector3 payload");
		return new Vector3(values[0], values[1], values[2]);
	}

	private static Vector3I ReadVector3I(string payload)
	{
		int[] values = JsonSerializer.Deserialize<int[]>(payload);
		if (values == null || values.Length != 3) throw new InvalidOperationException("Invalid Vector3I payload");
		return new Vector3I(values[0], values[1], values[2]);
	}
}
