using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using ClusterProtocol;

namespace ClusterRegistry;

/// <summary>T-0340 step 5 (Shared/Cluster/SaveFileServing.cs): the node control listener stays responsive while a save
/// file is being read by a slow requester, and the streamed bytes are the file's.</summary>
internal static partial class SelfTest
{
    // A control listener shaped like the node's (ClusterControlServer.ListenLoop): one thread, each request handled
    // inline unless the save-file worker takes it. A requester asks for a 50 MB save and reads nothing; a second caller
    // asks for the node's state. Inline (the shape before this step) the second caller waits behind the copy - red;
    // with the worker it is answered at once. Then the slow requester reads on and gets the file's bytes, a resumed
    // fetch gets the rest from its offset, and a file that ends early is an error, never a short answer.
    private static void TestSaveFileWorker()
    {
        static void Check(bool value, string what) { if (!value) throw new InvalidOperationException("save-file-worker: " + what); }
        const int Size = 50 * 1024 * 1024;
        string directory = Directory.CreateTempSubdirectory("save-file-worker-").FullName;
        string path = Path.Combine(directory, "g00000001-s00000001.mps");
        byte[] content = new byte[Size];
        new Random(340).NextBytes(content);
        File.WriteAllBytes(path, content);
        string expected = Convert.ToHexString(SHA256.HashData(content));
        try
        {
            double inline = ListenerLatencyDuringRead(path, expected, Size, inline: true, Check);
            double worker = ListenerLatencyDuringRead(path, expected, Size, inline: false, Check);
            Check(inline >= 900, $"earned red: handled inline, the listener answered the second caller only after {inline:F0} ms (expected to wait out the 1 s the reader held)");
            Check(worker < 500, $"with the worker the listener answers the second caller in {worker:F0} ms while the 50 MB read is held");

            using (FileStream resumed = SaveFileStream.Open(path, Size - 1000))
            {
                var rest = new MemoryStream();
                SaveFileStream.Copy(resumed, rest, 1000);
                Check(rest.ToArray().AsSpan().SequenceEqual(content.AsSpan(Size - 1000)), "a resumed fetch gets the bytes from its offset");
            }
            bool refused = false, early = false;
            try { SaveFileStream.Open(path, Size + 1).Dispose(); } catch (ArgumentOutOfRangeException) { refused = true; }
            Check(refused, "an offset past the end is refused");
            using (FileStream whole = SaveFileStream.Open(path, Size - 10))
                try { SaveFileStream.Copy(whole, Stream.Null, 11); } catch (EndOfStreamException) { early = true; }
            Check(early, "a file that ends before the announced length is an error");
            // The open file is the one served even when its name is replaced meanwhile (a damaged copy replaced by a fetched one).
            using (FileStream held = SaveFileStream.Open(path, 0))
            {
                File.WriteAllBytes(path + ".new", new byte[10]);
                File.Move(path + ".new", path, true);
                var all = new MemoryStream();
                SaveFileStream.Copy(held, all, Size);
                Check(Convert.ToHexString(SHA256.HashData(all.ToArray())) == expected, "a file replaced while it is served is served as it was opened");
            }
            Check(!SaveFileWorker.Serves("POST", "/cluster/save-file") && !SaveFileWorker.Serves("GET", "/cluster/global-save-file")
                && SaveFileWorker.Serves("GET", "/cluster/save-file/"), "only a save file read goes to the worker");
            Console.WriteLine($"save-file-worker: second caller answered in {worker:F0} ms during a held 50 MB read (inline: {inline:F0} ms); "
                + "streamed bytes match, resumed and replaced files too");
        }
        finally
        {
            SaveFileWorker.Inline = false;
            Directory.Delete(directory, true);
        }
    }

    /// <summary>How long the listener took to answer a second caller while a requester held a save-file read for a second.</summary>
    private static double ListenerLatencyDuringRead(string path, string expected, int size, bool inline, Action<bool, string> check)
    {
        SaveFileWorker.Inline = inline;
        int port = FreeTcpPort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/cluster/");
        listener.Start();
        using var copying = new ManualResetEventSlim();
        using var worker = new SaveFileWorker(2, error => Console.WriteLine("save-file-worker: handler failed: " + error.Message));
        var loop = new Thread(() =>
        {
            while (true)
            {
                HttpListenerContext context;
                try { context = listener.GetContext(); }
                catch (Exception) { return; }
                void Handle()
                {
                    try
                    {
                        if (SaveFileWorker.Serves(context.Request.HttpMethod, context.Request.Url!.AbsolutePath))
                        {
                            using FileStream file = SaveFileStream.Open(path, 0);
                            context.Response.ContentLength64 = file.Length;
                            copying.Set();
                            SaveFileStream.Copy(file, context.Response.OutputStream, file.Length);
                        }
                        else context.Response.OutputStream.Write("ok"u8);
                        context.Response.Close();
                    }
                    catch (Exception) { context.Response.Abort(); }
                }
                if (worker.Dispatch(context.Request.HttpMethod, context.Request.Url!.AbsolutePath, Handle)) continue;
                Handle();
            }
        }) { IsBackground = true };
        loop.Start();

        // The slow requester: asks, then reads nothing for a second. 50 MB does not fit the loopback socket buffers, so the
        // server's copy is blocked in Write for that long.
        using var reader = new TcpClient();
        reader.Connect(IPAddress.Loopback, port);
        NetworkStream stream = reader.GetStream();
        stream.Write(System.Text.Encoding.ASCII.GetBytes($"GET /cluster/save-file HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nConnection: close\r\n\r\n"));
        check(copying.Wait(5000), "the save-file request reached its handler");
        var held = Stopwatch.StartNew();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        Task<double> second = Task.Run(() =>
        {
            var clock = Stopwatch.StartNew();
            string answer = http.GetStringAsync($"http://127.0.0.1:{port}/cluster/node-state").GetAwaiter().GetResult();
            return answer == "ok" ? clock.Elapsed.TotalMilliseconds : throw new InvalidOperationException("second caller got '" + answer + "'");
        });
        Thread.Sleep(TimeSpan.FromSeconds(1) - held.Elapsed is { Ticks: > 0 } wait ? wait : TimeSpan.Zero);
        check(inline || second.IsCompleted, "with the worker the second caller is answered while the reader still holds");
        // The reader reads on: headers, then exactly the file.
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[1 << 16];
        var head = new List<byte>();
        long body = 0;
        bool inBody = false;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            int from = 0;
            if (!inBody)
            {
                for (; from < read && !inBody; from++)
                {
                    head.Add(buffer[from]);
                    int n = head.Count;
                    inBody = n >= 4 && head[n - 4] == '\r' && head[n - 3] == '\n' && head[n - 2] == '\r' && head[n - 1] == '\n';
                }
                if (!inBody) continue;
            }
            hash.AppendData(buffer, from, read - from);
            body += read - from;
        }
        check(body == size && Convert.ToHexString(hash.GetHashAndReset()) == expected, $"the slow reader got the whole file ({body} of {size} bytes, inline={inline})");
        double latency = second.GetAwaiter().GetResult();
        listener.Stop();
        loop.Join(2000);
        return latency;
    }
}
