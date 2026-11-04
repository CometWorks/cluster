int checks = 0;
void Check(bool value, string why) { if (!value) throw new Exception(why); checks++; }
var replica = new PartitionManager();
byte[] profiles = [1, 2, 3];
var read = (Invocation)OfflineCompensation.AdminRead(4, replica, profiles);
Check(read.Name == "AdminRead" && read.Args.Length == 2, "existing core call contract preserved");
Check(replica.Reads == 0 && ((long[])read.Args[0]).Length == 0, "offline replica needs no live partition");
Check(ReferenceEquals(read.Args[1], profiles), "exact offline bytes forwarded without rewriting");
read = (Invocation)OfflineCompensation.AdminRead(4, null!, []);
Check(((long[])read.Args[0]).Length == 0 && ((byte[])read.Args[1]).Length == 0, "empty offline profiles need no live lookup either");
var online = new PartitionManager { Live = new() { Members = [17, 18] } };
read = (Invocation)OfflineCompensation.AdminRead(4, online);
Check(online.Reads == 1 && online.LastPartition == 4, "live reads retain the owning manager lookup");
Check(((long[])read.Args[0]).SequenceEqual(new long[] { 17, 18 }) && read.Args[1] == null, "live members reach the same core contract");
try { OfflineCompensation.AdminRead(99, replica); throw new Exception("unknown live partition was accepted"); }
catch (KeyNotFoundException) { Check(replica.Reads == 1 && replica.LastPartition == 99, "unknown live partition still refuses"); }
Console.WriteLine($"PASS {checks} exact offline-holder/live admin-read facade assertions (inert collaborators)");

record Invocation(string Name, object[] Args);
sealed class Partition { internal long[] Members = []; }
sealed class PartitionManager
{
    internal int Reads;
    internal ulong LastPartition;
    internal Partition Live;
    internal Partition Peek(ulong partition)
    {
        Reads++; LastPartition = partition;
        return Live ?? throw new KeyNotFoundException("Unknown partition " + partition);
    }
}
