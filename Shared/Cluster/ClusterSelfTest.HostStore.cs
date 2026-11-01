using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using ClusterProtocol;
using VRageMath;

namespace ClusterPlugin;

internal static partial class ClusterSelfTest
{
	/// T-0340: several nodes of one host write one save directory. With real save files, through the writers the node
	/// uses (PartitionSaveFile.WriteFile with a store owner, PartitionPersistence.WriteVerifiedGlobalSave): two writers
	/// of the same save both succeed and leave one intact file; a second save with other content under the same id is
	/// refused and the first stays; two downloaders of one save both succeed; a torn file is replaced; nothing but the
	/// save files is left in the directory. The registry self-test (host-store-install) runs the same install rule
	/// under contention with plain bytes; this section ties it to the save container's own checksum.
	private static void HostSaveStoreWriters()
	{
		void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("host-save-store: " + message); }
		// The registry alone switches the store on: its registration answer, never this node's environment.
		bool Told(string answer, string dataRoot)
		{
			using JsonDocument document = JsonDocument.Parse(answer);
			return ClusterRuntime.HostStoreFrom(document.RootElement, dataRoot);
		}
		string was = Environment.GetEnvironmentVariable("CLUSTER_HOST_STORE");
		try
		{
			Environment.SetEnvironmentVariable("CLUSTER_HOST_STORE", "1");
			Check(!Told("{\"Epoch\":1}", "/run/node-7-data"), "a registry that does not name the store (before T-0340, or its switch off) leaves the node legacy, whatever the node's environment says");
			Check(!Told("{\"Epoch\":1,\"HostStore\":false}", "/run/node-7-data"), "a registry that refuses the store leaves the node legacy");
			Environment.SetEnvironmentVariable("CLUSTER_HOST_STORE", null);
			Check(Told("{\"Epoch\":1,\"HostStore\":true}", "/run/node-7-data"), "the registry's yes switches the store on without any switch in the node's environment");
			Check(!Told("{\"Epoch\":1,\"HostStore\":true}", null), "a node without a data root has no run root: legacy even when told");
		}
		finally { Environment.SetEnvironmentVariable("CLUSTER_HOST_STORE", was); }
		string top = Path.Combine(Path.GetTempPath(), "host-save-store-" + Guid.NewGuid().ToString("N"));
		try
		{
			string saves = HostSaveStore.Root(top, "polaris");
			BoundingSphereD sphere = new BoundingSphereD(new Vector3D(1, 2, 3), 100);
			byte[] Save(int seed) => PartitionSaveFile.Write(42, 3, 7, 0, sphere, false, 1,
				Enumerable.Range(0, 20_000).Select(value => (byte)(value * 31 + seed)).ToArray(), 1000 + seed);
			string path = HostSaveStore.PartitionSave(saves, 42, 3, 7);
			Check(path == Path.Combine(top, "saves", "polaris", "P-000000000000002A", "g00000003-s00000007.mps"), "the store path: " + path);

			// Eight nodes write the same save at once (unfsynced, as a Periodic save is).
			byte[] file = Save(1);
			int failed = 0;
			using (ManualResetEventSlim start = new ManualResetEventSlim())
			{
				Thread[] writers = Enumerable.Range(0, 8).Select(writer => new Thread(() =>
				{
					start.Wait();
					try { PartitionSaveFile.WriteFile(path, file, durable: false, storeOwner: "node-" + writer); }
					catch (Exception) { Interlocked.Increment(ref failed); }
				})).ToArray();
				foreach (Thread writer in writers) writer.Start();
				start.Set();
				foreach (Thread writer in writers) writer.Join();
			}
			PartitionSaveFile.Parsed parsed = PartitionSaveFile.ReadFile(path);
			Check(failed == 0 && File.ReadAllBytes(path).SequenceEqual(file), "eight writers of one save: all succeed, one intact file (" + failed + " failed)");
			Check(PartitionSaveFile.StoredIs(path, parsed.BodyChecksum) == true && PartitionSaveFile.StoredIs(path, parsed.BodyChecksum + 1) == false,
				"a stored save is told from another by its body checksum");

			// Another capture under the same save id: refused, the catalogued file stays.
			bool refused = false;
			try { PartitionSaveFile.WriteFile(path, Save(2), durable: false, storeOwner: "node-9"); }
			catch (IOException) { refused = true; }
			Check(refused && File.ReadAllBytes(path).SequenceEqual(file), "other content under the same save id is refused and the first file stays");

			// A torn file (an unfsynced write cut by a power loss) is replaced by a writer and by a fetched copy.
			File.WriteAllBytes(path, file.Take(file.Length / 2).ToArray());
			Check(PartitionSaveFile.StoredIs(path, parsed.BodyChecksum) == null, "a torn file reads as damaged");
			PartitionSaveFile.WriteFile(path, file, durable: false, storeOwner: "node-1");
			Check(File.ReadAllBytes(path).SequenceEqual(file), "a writer replaces a torn file");

			// Two downloaders of one global save; then a fetched copy over a wrong file.
			string global = HostSaveStore.GlobalSave(saves, 2, 11);
			string checksum = parsed.BodyChecksum.ToString("X16");
			PartitionPersistence.WriteVerifiedGlobalSave(file, file.Length, checksum, global, "node-1");
			PartitionPersistence.WriteVerifiedGlobalSave(file, file.Length, checksum, global, "node-2");
			Check(File.ReadAllBytes(global).SequenceEqual(file), "two downloaders of one save leave one intact file");
			File.WriteAllBytes(global, Save(3));
			PartitionPersistence.WriteVerifiedGlobalSave(file, file.Length, checksum, global, "node-2");
			Check(File.ReadAllBytes(global).SequenceEqual(file), "a fetched copy, verified against the catalogue, replaces a wrong file");

			// T-0397: the WA restore uses a catalogued copy only when it verifies. A torn local copy (cut by a host crash)
			// is skipped, the next verified copy wins, and with none the WA fetches from a holder instead of failing.
			string torn = HostSaveStore.GlobalSave(saves, 2, 12), good = HostSaveStore.GlobalSave(HostSaveStore.Root(top, "ai"), 2, 12);
			Directory.CreateDirectory(Path.GetDirectoryName(torn));
			Directory.CreateDirectory(Path.GetDirectoryName(good));
			byte[] zeroed = (byte[])file.Clone();
			Array.Clear(zeroed, zeroed.Length / 2, Math.Min(4096, zeroed.Length / 8));   // full length, a block of the body zero
			Check(!zeroed.SequenceEqual(file), "the zeroed block changed the file (" + file.Length + " bytes)");
			File.WriteAllBytes(torn, zeroed);
			File.WriteAllBytes(good, file);
			Check(WorldAuthorityCheckpointPatch.VerifiedGlobalCopy(new[] { torn }, file.Length, checksum) == null,
				"a full-length copy with a zeroed page does not verify");
			Check(WorldAuthorityCheckpointPatch.VerifiedGlobalCopy(new[] { torn, good }, file.Length, checksum) == good,
				"a torn local copy is skipped and the next verified copy is used");
			File.WriteAllBytes(torn, file.Take(file.Length / 3).ToArray());
			Check(WorldAuthorityCheckpointPatch.VerifiedGlobalCopy(new[] { torn, Path.Combine(top, "missing.mps") }, file.Length, checksum) == null,
				"a truncated copy and a missing one: none verifies, the WA goes to the holders");
			Check(WorldAuthorityCheckpointPatch.VerifiedGlobalCopy(new[] { good }, file.Length, (parsed.BodyChecksum + 1).ToString("X16")) == null,
				"an intact copy of another save is not the catalogued one");
			File.Delete(torn);
			Directory.Delete(HostSaveStore.Root(top, "ai"), true);

			string[] left = Directory.EnumerateFiles(saves, "*", SearchOption.AllDirectories).Where(name => !name.EndsWith(".mps", StringComparison.Ordinal)).ToArray();
			Check(left.Length == 0, "no temporary file is left: " + string.Join(",", left.Select(Path.GetFileName)));
		}
		finally
		{
			if (Directory.Exists(top)) Directory.Delete(top, true);
		}
	}
}
