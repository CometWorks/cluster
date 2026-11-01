using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClusterRegistry;

/// <summary>
/// A JSON document fetched from a node and relayed inside a registry response, kept as the node's own bytes in
/// small chunks and copied token by token into the response - through the secret redactor - when it is written.
///
/// Two things this replaces, both measured on w32 2026-09-25: (1) /diag parsed every node's section into a
/// JsonDocument, whose metadata table and the buffered body are large-object allocations - ~365 MB in two
/// minutes at cluster-monitor's poll rate, most of the Registry's LOH fragmentation; (2) the JsonElement it
/// produced was written through JsonElement's own converter, so the per-string redactor (4fb59f6) never saw
/// a node section's strings: /diag and trigger fan-outs went out unredacted. Chunks are 64 KB, below the
/// 85,000-byte LOH threshold, and the document is validated when read, so a bad node body is reported as that
/// node's error exactly as a parse failure was before, never as a half-written response.
/// </summary>
[JsonConverter(typeof(RelayedJsonConverter))]
internal sealed class RelayedJson
{
    internal const int ChunkSize = 64 * 1024;
    private readonly List<byte[]> _chunks;
    private readonly int _lastLength;

    private RelayedJson(List<byte[]> chunks, int lastLength)
    {
        _chunks = chunks;
        _lastLength = lastLength;
    }

    public long Length => _chunks.Count == 0 ? 0 : (long)(_chunks.Count - 1) * ChunkSize + _lastLength;

    /// <summary>Reads the whole stream into chunks and validates it as one JSON value; throws JsonException if it is not.</summary>
    public static RelayedJson Read(Stream stream)
    {
        var chunks = new List<byte[]>();
        int last = ChunkSize;
        while (true)
        {
            if (last == ChunkSize)
            {
                chunks.Add(new byte[ChunkSize]);
                last = 0;
            }
            int read = stream.Read(chunks[^1], last, ChunkSize - last);
            if (read == 0) break;
            last += read;
        }
        if (last == 0 && chunks.Count > 0)
        {
            chunks.RemoveAt(chunks.Count - 1);
            last = chunks.Count == 0 ? 0 : ChunkSize;
        }
        var relayed = new RelayedJson(chunks, last);
        var reader = new Utf8JsonReader(relayed.Sequence(), new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
        if (!reader.Read()) throw new JsonException("empty JSON body");
        reader.Skip();
        if (reader.Read()) throw new JsonException("trailing data after the JSON value");
        return relayed;
    }

    internal ReadOnlySequence<byte> Sequence()
    {
        if (_chunks.Count == 0) return ReadOnlySequence<byte>.Empty;
        if (_chunks.Count == 1) return new ReadOnlySequence<byte>(_chunks[0], 0, _lastLength);
        var first = new Segment(_chunks[0], ChunkSize, 0);
        Segment tail = first;
        for (int i = 1; i < _chunks.Count; i++)
            tail = tail.Append(_chunks[i], i == _chunks.Count - 1 ? _lastLength : ChunkSize);
        return new ReadOnlySequence<byte>(first, 0, tail, tail.Memory.Length);
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(byte[] chunk, int length, long runningIndex)
        {
            Memory = new ReadOnlyMemory<byte>(chunk, 0, length);
            RunningIndex = runningIndex;
        }

        public Segment Append(byte[] chunk, int length)
        {
            var next = new Segment(chunk, length, RunningIndex + Memory.Length);
            Next = next;
            return next;
        }
    }

    /// <summary>Copies the document token by token; strings and property names go through the redactor when <paramref name="redact"/>.</summary>
    internal void WriteTo(Utf8JsonWriter writer, bool redact)
    {
        var reader = new Utf8JsonReader(Sequence(), new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject: writer.WriteStartObject(); break;
                case JsonTokenType.EndObject: writer.WriteEndObject(); break;
                case JsonTokenType.StartArray: writer.WriteStartArray(); break;
                case JsonTokenType.EndArray: writer.WriteEndArray(); break;
                case JsonTokenType.PropertyName:
                    string name = reader.GetString()!;
                    writer.WritePropertyName(redact ? SecretRedactor.Redact(name) : name);
                    break;
                case JsonTokenType.String:
                    string value = reader.GetString()!;
                    writer.WriteStringValue(redact ? SecretRedactor.Redact(value) : value);
                    break;
                case JsonTokenType.Number:
                    if (reader.HasValueSequence) writer.WriteRawValue(reader.ValueSequence, skipInputValidation: true);
                    else writer.WriteRawValue(reader.ValueSpan, skipInputValidation: true);
                    break;
                case JsonTokenType.True: writer.WriteBooleanValue(true); break;
                case JsonTokenType.False: writer.WriteBooleanValue(false); break;
                case JsonTokenType.Null: writer.WriteNullValue(); break;
            }
        }
    }
}

/// <summary>Writes a <see cref="RelayedJson"/>, redacting only when the options carry the registry's redacting string converter.</summary>
internal sealed class RelayedJsonConverter : JsonConverter<RelayedJson>
{
    public override RelayedJson Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("RelayedJson is write-only");

    public override void Write(Utf8JsonWriter writer, RelayedJson value, JsonSerializerOptions options) =>
        value.WriteTo(writer, redact: ReferenceEquals(options, DirectGateway.RedactingJson));
}

/// <summary>
/// JsonElement's own converter writes its raw text, bypassing the per-string redactor; this one walks the
/// element so every string and property name of an embedded element is redacted like the rest of the body.
/// </summary>
internal sealed class RedactingElementConverter : JsonConverter<JsonElement>
{
    public override JsonElement Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        JsonElement.ParseValue(ref reader);

    public override void Write(Utf8JsonWriter writer, JsonElement value, JsonSerializerOptions options)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in value.EnumerateObject())
                {
                    writer.WritePropertyName(SecretRedactor.Redact(property.Name));
                    Write(writer, property.Value, options);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in value.EnumerateArray()) Write(writer, item, options);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(SecretRedactor.Redact(value.GetString()!));
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }
}
