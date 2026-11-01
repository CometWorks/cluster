#nullable enable
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace ClusterProtocol;

/// <summary>
/// T-0340 step 5: the save-file route off the control listener's thread. The node control listener is ONE thread that
/// handles each request inline; a save fetch validated its grant with a synchronous registry call and then wrote the
/// whole file to the requester on that thread, so every other control request of the node (handover phases, probes,
/// the registry's own calls) waited behind a copy of up to tens of MB - and with the per-host store any live node of
/// a host serves that host's files, so the reads no longer follow the node that wrote them. The listener hands a
/// save-file request to this worker (<see cref="Dispatch"/>) and goes back to accepting; the worker's threads run the
/// same handler, grant validation included. Shared with the registry, whose self-test measures the listener's latency
/// during a slow 50 MB read (the plugin's own sections need a live node).
/// </summary>
public sealed class SaveFileWorker : IDisposable
{
    /// <summary>Self-test switch that restores the shape before this step - every request inline on the listener - to earn the red.</summary>
    public static bool Inline;

    private readonly BlockingCollection<Action> m_work = new BlockingCollection<Action>();

    /// <param name="threads">How many save files are served at once; further requests wait in line, off the listener.</param>
    /// <param name="failed">Told what a handler threw (a handler answers its own request errors; this is the rest).</param>
    public SaveFileWorker(int threads, Action<Exception> failed)
    {
        for (int i = 0; i < threads; i++)
            new Thread(() =>
            {
                foreach (Action work in m_work.GetConsumingEnumerable())
                {
                    try { work(); }
                    catch (Exception error) { failed(error); }
                }
            }) { Name = "ClusterSaveFile-" + i, IsBackground = true }.Start();
    }

    /// <summary>Whether the request is one this worker serves: a save file read.</summary>
    public static bool Serves(string method, string path) =>
        method == "GET" && path.TrimEnd('/').EndsWith("/save-file", StringComparison.OrdinalIgnoreCase);

    /// <summary>Queues <paramref name="handle"/> when the request is a save file read (true); else the caller handles it inline.</summary>
    public bool Dispatch(string method, string path, Action handle)
    {
        if (Inline || !Serves(method, path)) return false;
        try { m_work.Add(handle); }
        catch (InvalidOperationException) { return false; }   // disposed: the listener is stopping
        return true;
    }

    public void Dispose() => m_work.CompleteAdding();
}

/// <summary>T-0340 step 5: a save file streamed to its requester instead of read whole into memory first.</summary>
public static class SaveFileStream
{
    /// <summary>
    /// Opens <paramref name="path"/> at <paramref name="offset"/> (a resumed fetch). Save files are installed whole and
    /// never rewritten in place (written under a temporary name, then renamed or linked), so the open file keeps its
    /// length and bytes even when the name is replaced or unlinked while it is being served.
    /// </summary>
    public static FileStream Open(string path, long offset)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan);
        if (offset < 0 || offset > file.Length)
        {
            file.Dispose();
            throw new ArgumentOutOfRangeException(nameof(offset));
        }
        file.Position = offset;
        return file;
    }

    /// <summary>Copies exactly <paramref name="count"/> bytes; a file that ends early is an error, never a short answer
    /// passed off as whole.</summary>
    public static void Copy(Stream file, Stream output, long count)
    {
        byte[] buffer = new byte[81920];
        while (count > 0)
        {
            int read = file.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (read <= 0) throw new EndOfStreamException("Save file ended " + count + " byte(s) early");
            output.Write(buffer, 0, read);
            count -= read;
        }
    }
}
