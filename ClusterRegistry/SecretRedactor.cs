using System.Text;

namespace ClusterRegistry;

internal static class SecretRedactor
{
    private static string[] _values = [];

    public static void ConfigureFromEnvironment() => Configure([
        Environment.GetEnvironmentVariable("CLUSTER_JOIN_TOKEN_CURRENT"),
        Environment.GetEnvironmentVariable("CLUSTER_JOIN_TOKEN_PREVIOUS"),
        Environment.GetEnvironmentVariable("CLUSTER_ADMIN_TOKEN"),
    ]);

    internal static void Configure(IEnumerable<string?> values) => _values = values
        .Where(value => !string.IsNullOrEmpty(value))
        .SelectMany(value => new[] { value!, Uri.EscapeDataString(value!) })
        .Distinct(StringComparer.Ordinal).OrderByDescending(value => value.Length).ToArray();

    /// <summary>T-0321: adds secrets learnt after start (the persisted start credentials).</summary>
    internal static void Add(IEnumerable<string?> values) => Configure(_values.Concat(values));

    public static string Redact(string value)
    {
        foreach (string secret in _values) value = value.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        return value;
    }

    public static byte[] Redact(byte[] value) => Encoding.UTF8.GetBytes(Redact(Encoding.UTF8.GetString(value)));

    public static TextWriter Wrap(TextWriter writer) => new RedactingWriter(writer);

    private sealed class RedactingWriter(TextWriter inner) : TextWriter
    {
        public override Encoding Encoding => inner.Encoding;
        public override void Write(char value) => inner.Write(value);
        public override void Write(string? value) => inner.Write(value == null ? null : Redact(value));
        public override void WriteLine(string? value) => inner.WriteLine(value == null ? null : Redact(value));
    }
}
