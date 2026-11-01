using System.Text.Json;

namespace ClusterRegistry;

/// <summary>Bounded local evidence only. Host collects it; this process has no external uploader.</summary>
internal static class DiagnosticReports
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static DateTime window;
    private static int count;
    private static readonly string RunId = Guid.NewGuid().ToString("N");

    internal static void Write(string kind, string message, Exception? exception = null,
        string? transferId = null, ulong? partition = null, string? phase = null)
    {
        bool fatal = kind == "unhandled_exception";
        if (!Monitor.TryEnter(Gate, fatal ? 100 : 0)) return;
        string? temporary = null;
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (now.UtcDateTime - window >= TimeSpan.FromMinutes(1)) { window = now.UtcDateTime; count = 0; }
            if (!fatal && ++count > 32) return;
            string directory = Environment.GetEnvironmentVariable("ERROR_REPORTING_DIRECTORY")
                ?? Path.Combine(AppContext.BaseDirectory, "Diagnostics");
            Directory.CreateDirectory(directory);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var files = Directory.EnumerateFiles(directory, "*.json")
                .Where(path => Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _)).Take(257).ToArray();
            if (files.Length >= 256)
            {
                if (!fatal) return;
                File.Delete(files.OrderBy(File.GetLastWriteTimeUtc).First());
            }
            var context = new Dictionary<string, string>();
            string? json = Environment.GetEnvironmentVariable("ERROR_REPORTING_CONTEXT");
            if (json is { Length: <= 16384 })
                foreach (var pair in JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [])
                    if (context.Count < 32) context[Limit(pair.Key, 80)!] = Limit(pair.Value, 1024)!;
            context.TryAdd("runId", RunId);
            context.TryAdd("role", "registry");
            if (transferId != null) context["transferId"] = Limit(transferId, 128)!;
            if (partition != null) context["partitionId"] = partition.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (phase != null) context["phase"] = Limit(phase, 128)!;
            string id = Guid.NewGuid().ToString("N");
            var report = new
            {
                SchemaVersion = 1, ReportId = id, CapturedAtUtc = now,
                Source = "ClusterRegistry", Kind = kind, Severity = fatal ? "Critical" : "Error",
                Message = Limit(SecretRedactor.Redact(message), 8192),
                Exception = exception == null ? null : Limit(SecretRedactor.Redact(exception.ToString()), 32768), Context = context,
            };
            temporary = Path.Combine(directory, id + ".tmp");
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, report, JsonOptions);
                stream.Flush(true);
            }
            File.Move(temporary, Path.Combine(directory, id + ".json"));
        }
        catch { /* Reporting cannot alter handover or recovery semantics. */ }
        finally
        {
            if (temporary != null) try { File.Delete(temporary); } catch { }
            Monitor.Exit(Gate);
        }
    }

    private static string? Limit(string? value, int length) => value?.Length > length ? value[..length] : value;
}
