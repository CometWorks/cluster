using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

namespace ClusterRegistry;

internal static partial class SelfTest
{
    private static void TestOfflineMetadataBoundary()
    {
        static void Read(byte[] bytes, long? declared = null)
        {
            using var content = new StreamContent(new MemoryStream(bytes));
            content.Headers.ContentLength = declared;
            DirectGateway.ReadOfflineMetadata(content, CancellationToken.None).GetAwaiter().GetResult();
        }
        static void Reject(Action read, string code)
        {
            try { read(); throw new Exception("metadata unexpectedly accepted: " + code); }
            catch (OfflineAdminException e) { Assert(e.Code == code); }
        }
        int limit = DirectGateway.OfflineMetadataLimit;
        Read(Encoding.UTF8.GetBytes("{\"value\":\"" + new string('x', limit - 12) + "\"}"));
        Reject(() => Read("{}"u8.ToArray(), limit + 1L), "node_response_too_large");
        Reject(() => Read(new byte[limit + 1]), "node_response_too_large");
        try { Read("{"u8.ToArray()); throw new Exception("malformed JSON accepted"); }
        catch (JsonException) { }
        foreach (bool trickle in new[] { false, true })
        {
            using var stream = new OfflineSlowStream(trickle);
            using var content = new StreamContent(stream);
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            var watch = Stopwatch.StartNew();
            try
            {
                DirectGateway.ReadOfflineMetadata(content, deadline.Token).GetAwaiter().GetResult();
                throw new Exception("stalled metadata accepted");
            }
            catch (OperationCanceledException) { }
            Assert(watch.Elapsed < TimeSpan.FromSeconds(2) && stream.Disposed && (!trickle || stream.Reads > 1));
        }
        using (var content = new StringContent("{}"))
        using (var deadline = new CancellationTokenSource())
        {
            deadline.Cancel();
            try { DirectGateway.ReadOfflineMetadata(content, deadline.Token).GetAwaiter().GetResult(); throw new Exception("cancel ignored"); }
            catch (OperationCanceledException) { }
        }
        // Reopening the registry must keep the identity and honest uncertainty. An
        // interrupted Running clear is likewise recoverable by its original key.
        string directory = Directory.CreateTempSubdirectory("offline-clear-restart-").FullName;
        string id, running;
        using (var registry = new GatewayRegistry(directory, Admission))
        {
            id = registry.BeginAdminOperation("offline-cache-clear", "request", "admin", "key", "cache", out _).OperationId;
            registry.FailAdminOperation(id, "offline_clear_uncertain", "lost reply");
            running = registry.BeginAdminOperation("offline-cache-clear", "other", "admin", "interrupted", "cache", out _).OperationId;
        }
        using (var registry = new GatewayRegistry(directory, Admission))
        {
            Assert(registry.BeginAdminOperation("offline-cache-clear", "request", "admin", "key", "cache", out bool created).OperationId == id && !created);
            Assert(registry.ResumeOfflineCacheClear(id));
            registry.CompleteAdminOperation(id);
            Assert(!registry.ResumeOfflineCacheClear(id));
            Assert(registry.BeginAdminOperation("offline-cache-clear", "other", "admin", "interrupted", "cache", out created).OperationId == running && !created);
            registry.FailAdminOperation(running, "offline_clear_uncertain", "reconcile under original GUID");
        }
        Console.WriteLine("offline metadata: near-8MiB valid, oversized declared/no-length, malformed JSON, stalled/token-ignoring stream, trickle, cancellation/disposal, durable clear retry passed");
    }

    private sealed class OfflineSlowStream(bool trickle) : Stream
    {
        private readonly TaskCompletionSource<int> _stalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Disposed;
        internal int Reads;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reads++;
            if (!trickle) return await _stalled.Task; // Deliberately ignores the token.
            await Task.Delay(20); // Each byte arrives, but the total deadline still applies.
            buffer.Span[0] = (byte)' ';
            return 1;
        }
        protected override void Dispose(bool disposing) { Disposed = true; _stalled.TrySetCanceled(); base.Dispose(disposing); }
        public override int Read(byte[] buffer, int offset, int count) => throw new Exception("synchronous body read");
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
