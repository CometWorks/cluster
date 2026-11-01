using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ClusterProtocol;
using VRageMath;

namespace ClusterPlugin;

internal static partial class ClusterSelfTest
{
	/// T-0399: the global-save scrub (PartitionPersistence.Scrub.cs) on real save files in a temp dir. A copy written the
	/// way a replica is (WriteVerifiedGlobalSave) checks intact, through one reused buffer; a copy of another save, a
	/// full-length copy with a zeroed block, a truncated one and a missing one do not. A failed copy is renamed
	/// <c>.corrupt</c> (then <c>.corrupt.1</c>), never deleted, and the withdraw request names generation, sequence, node,
	/// epoch and location - through the seam, without a network; a failed withdraw is logged and the next pass (file
	/// missing) withdraws again. Also: which catalogue rows a node scrubs, and which node of a host scrubs its store.
	private static void GlobalScrub()
	{
		void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("global-scrub: " + message); }
		string top = Path.Combine(Path.GetTempPath(), "global-scrub-" + Guid.NewGuid().ToString("N"));
		try
		{
			string saves = HostSaveStore.Root(top, "polaris");
			// ~290 KB of incompressible body, the size of a live global save (and over the large-object-heap threshold).
			byte[] body = new byte[290_000];
			new Random(1).NextBytes(body);
			byte[] file = PartitionSaveFile.Write(0, 2, 11, 0, new BoundingSphereD(Vector3D.Zero, 1), false, 1, body, 1001);
			string checksum = PartitionSaveFile.ReadHeader(file).BodyChecksum.ToString("X16");
			string path = HostSaveStore.GlobalSave(saves, 2, 11);
			PartitionPersistence.WriteVerifiedGlobalSave(file, file.Length, checksum, path, null, durable: false);
			var target = new PartitionPersistence.ScrubTarget(2, 11, file.Length, checksum, path);

			byte[] buffer = null;
			Check(PartitionPersistence.CheckGlobalCopy(path, file.Length, checksum, ref buffer, out string reason) == true && reason == null,
				"a copy as installed checks intact: " + reason);
			byte[] first = buffer;
			Check(PartitionPersistence.CheckGlobalCopy(path, file.Length, checksum.ToLowerInvariant(), ref buffer, out _) == true && ReferenceEquals(first, buffer),
				"the second check reuses the buffer (and the checksum compares case-insensitively)");
			string other = (PartitionSaveFile.ReadHeader(file).BodyChecksum + 1).ToString("X16");
			Check(PartitionPersistence.CheckGlobalCopy(path, file.Length, other, ref buffer, out reason) == false && reason.StartsWith("checksum"),
				"an intact file that is not the catalogued save fails: " + reason);
			Check(PartitionPersistence.CheckGlobalCopy(path, file.Length + 1, checksum, ref buffer, out reason) == false && reason.StartsWith("size"),
				"a size other than the catalogued one fails: " + reason);

			List<string> requests = new List<string>();
			bool Withdraw(string request) { requests.Add(request); return true; }
			Check(PartitionPersistence.ScrubOne(target, "node-3", 17, ref buffer, Withdraw, out string line) == true && line == null && requests.Count == 0,
				"an intact copy: no line, no request");

			// Rot: full length, a block of the body zeroed. Set aside and withdrawn.
			byte[] zeroed = (byte[])file.Clone();
			Array.Clear(zeroed, zeroed.Length / 2, 4096);
			File.WriteAllBytes(path, zeroed);
			Check(PartitionPersistence.ScrubOne(target, "node-3", 17, ref buffer, Withdraw, out line) == false, "a rotted copy fails");
			Check(!File.Exists(path) && File.Exists(path + ".corrupt") && File.ReadAllBytes(path + ".corrupt").SequenceEqual(zeroed),
				"the rotted copy is renamed .corrupt, bytes kept");
			Check(requests.Single() == "/registry/global-saves/holders?withdraw=1&generation=2&sequence=11&node=node-3&epoch=17&location="
				+ Uri.EscapeDataString(path), "the withdraw request: " + requests.Single());
			Check(line.StartsWith("[ClusterPlugin] global_scrub corrupt generation=2 sequence=11 path=" + path + " reason=Partition save checksum mismatch", StringComparison.Ordinal)
				&& line.EndsWith(" kept=" + path + ".corrupt withdrawn=true", StringComparison.Ordinal), "the corrupt line: " + line);

			// A fresh copy, then torn (truncated): set aside under the next free name.
			PartitionPersistence.WriteVerifiedGlobalSave(file, file.Length, checksum, path, null, durable: false);
			File.WriteAllBytes(path, file.Take(file.Length / 2).ToArray());
			requests.Clear();
			Check(PartitionPersistence.ScrubOne(target, "node-3", 17, ref buffer, Withdraw, out line) == false && line.Contains(" reason=size ")
				&& File.Exists(path + ".corrupt.1") && File.ReadAllBytes(path + ".corrupt").SequenceEqual(zeroed) && requests.Count == 1,
				"a torn copy is set aside as .corrupt.1, the first .corrupt kept: " + line);

			// The withdraw fails (registry unreachable): logged as not withdrawn; the next pass finds the file missing and
			// withdraws again, renaming nothing.
			PartitionPersistence.WriteVerifiedGlobalSave(file, file.Length, checksum, path, null, durable: false);
			File.WriteAllBytes(path, file.Take(10).ToArray());
			Check(PartitionPersistence.ScrubOne(target, "node-3", 17, ref buffer, _ => throw new InvalidOperationException("no registry"), out line) == false
				&& line.Contains("withdraw failed: no registry") && line.EndsWith(" withdrawn=false", StringComparison.Ordinal) && File.Exists(path + ".corrupt.2"),
				"a failed withdraw is logged: " + line);
			requests.Clear();
			Check(PartitionPersistence.ScrubOne(target, "node-3", 17, ref buffer, Withdraw, out line) == false && line.Contains(" reason=missing ")
				&& line.Contains(" kept=none ") && requests.Count == 1 && !File.Exists(path + ".corrupt.3"), "a missing copy is withdrawn again: " + line);

			// Which rows a node scrubs: its own holder at exactly its own path, not pending deletion.
			string Path_(long generation, long sequence) => HostSaveStore.GlobalSave(saves, generation, sequence);
			string catalogue = JsonSerializer.Serialize(new object[]
			{
				Row(2, 12, "node-3", Path_(2, 12)),
				Row(2, 11, "node-3", Path_(2, 11)),
				Row(2, 13, "node-3", "/elsewhere/g.mps"),
				Row(2, 14, "node-4", Path_(2, 14)),
				Row(2, 15, "node-3", Path_(2, 15), pending: true),
				Row(2, 16, "host:polaris", Path_(2, 16)),
			});
			using (JsonDocument document = JsonDocument.Parse(catalogue))
			{
				PartitionPersistence.ScrubTarget[] mine = PartitionPersistence.ScrubTargetsOf(document.RootElement, "node-3", Path_);
				Check(mine.Select(x => x.Sequence).SequenceEqual(new uint[] { 11, 12 }) && mine[0].Path == Path_(2, 11) && mine[0].Size == 42 && mine[0].Checksum == "C11",
					"node-3 scrubs its own copies at its own path, oldest first");
				Check(PartitionPersistence.ScrubTargetsOf(document.RootElement, "host:polaris", Path_).Single().Sequence == 16,
					"in store mode the host's copies");
			}

			// Which node scrubs a host's store: the lowest id among the host's Active regular store-mode nodes.
			string nodes = JsonSerializer.Serialize(new object[]
			{
				new { Node = "host:polaris", State = 3, Role = 0, Host = "polaris", HostStore = false },
				new { Node = "node-1", State = 1, Role = 0, Host = "polaris", HostStore = true },   // Closed (draining)
				new { Node = "node-2", State = 0, Role = 0, Host = "polaris", HostStore = true },
				new { Node = "node-4", State = 0, Role = 0, Host = "polaris", HostStore = true },
				new { Node = "node-0", State = 0, Role = 0, Host = "polaris", HostStore = false },  // legacy: its own dir
				new { Node = "node-3", State = 0, Role = 0, Host = "ai", HostStore = true },
				new { Node = "wa-1", State = 0, Role = 1, Host = "polaris", HostStore = true },
			});
			using (JsonDocument document = JsonDocument.Parse(nodes))
			{
				Check(PartitionPersistence.IsHostScrubber(document.RootElement, "node-2", "polaris")
					&& !PartitionPersistence.IsHostScrubber(document.RootElement, "node-4", "polaris")
					&& !PartitionPersistence.IsHostScrubber(document.RootElement, "node-1", "polaris")
					&& PartitionPersistence.IsHostScrubber(document.RootElement, "node-3", "ai"), "one scrubber per host");
			}
		}
		finally
		{
			if (Directory.Exists(top)) Directory.Delete(top, true);
		}

		static object Row(long generation, long sequence, string holder, string location, bool pending = false) => new
		{
			SaveId = new { Generation = generation, Sequence = sequence },
			SizeBytes = 42,
			Checksum = "C" + sequence,
			PendingDelete = pending,
			LocationHolders = new Dictionary<string, string> { [location] = holder },
		};
	}
}
