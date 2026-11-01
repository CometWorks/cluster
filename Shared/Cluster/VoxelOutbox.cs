using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace ClusterPlugin;

/// <summary>
/// Disk-backed ordered queue of voxel journal envelopes on a regular node.
/// <para>
/// The capture seam is a range-changed notification, so every queued entry describes an edit this node has
/// already applied to its own storage. An entry that dies with the process is therefore not a refused edit but a
/// local change the cluster will never see, and the owning node diverges permanently and silently. Entries are
/// written here before they count as enqueued and deleted only once the World Authority has acknowledged the
/// append, so a crash at any point either loses nothing or re-sends an entry the WA deduplicates by operation id.
/// </para>
/// <para>
/// Files are written to a sibling <c>.tmp</c> and moved into place, so a reader never sees a partial envelope;
/// the codec checksum guards the rest. Writes are not fsynced: the failure being defended against is process
/// death, which buffered-but-moved files survive, and an fsync per published range on the game thread is a
/// stutter not worth paying for host power loss.
/// </para>
/// </summary>
internal sealed class VoxelOutbox
{
	private readonly string m_directory;
	private long m_lastId;

	public VoxelOutbox(string directory)
	{
		m_directory = directory;
		Directory.CreateDirectory(directory);
		// A tmp file is an entry whose Add never completed, and its edit was never reported as journalled: the
		// enqueue threw, loudly. Replaying it could resurrect a half-written envelope, so clear rather than adopt.
		foreach (string stale in Directory.EnumerateFiles(directory, "*.tmp")) File.Delete(stale);
		m_lastId = Directory.EnumerateFiles(directory, "*.vjr").Select(Id).DefaultIfEmpty(0L).Max();
	}

	private static long Id(string path) => long.Parse(Path.GetFileNameWithoutExtension(path), CultureInfo.InvariantCulture);

	public int Count => Directory.EnumerateFiles(m_directory, "*.vjr").Count();

	/// <summary>Persists an envelope at the tail of the queue and returns its id. Throws on any storage failure.</summary>
	public long Add(byte[] envelope)
	{
		long id = Interlocked.Increment(ref m_lastId);
		string path = Path.Combine(m_directory, id.ToString("D20", CultureInfo.InvariantCulture) + ".vjr");
		string temporary = path + ".tmp";
		File.WriteAllBytes(temporary, envelope);
		File.Move(temporary, path, overwrite: true);
		return id;
	}

	/// <summary>Pending envelopes in id order, oldest first. Ids from a previous session sort before this one's.</summary>
	public IEnumerable<(long Id, byte[] Envelope)> Pending()
	{
		foreach (string path in Directory.EnumerateFiles(m_directory, "*.vjr").OrderBy(value => value, StringComparer.Ordinal))
			yield return (Id(path), File.ReadAllBytes(path));
	}

	/// <summary>Removes an acknowledged entry. Missing files are fine: acknowledging twice loses nothing.</summary>
	public void Acknowledge(long id)
		=> File.Delete(Path.Combine(m_directory, id.ToString("D20", CultureInfo.InvariantCulture) + ".vjr"));
}
