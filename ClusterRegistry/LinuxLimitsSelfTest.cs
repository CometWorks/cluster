namespace ClusterRegistry;

internal static partial class SelfTest
{
    // SE-0012: the startup warnings of LinuxLimits.cs, read from a fake /proc
    private static void TestLinuxLimits()
    {
        static void Assert(bool value, string what) { if (!value) throw new InvalidOperationException("linux-limits: " + what); }

        string proc = Path.Combine(Path.GetTempPath(), "cluster-linux-limits-self-test-" + Guid.NewGuid().ToString("N"));
        string inotify = Path.Combine(proc, "sys", "fs", "inotify");
        Directory.CreateDirectory(Path.Combine(proc, "self"));
        Directory.CreateDirectory(inotify);
        try
        {
            void Fake(string openFiles, string processes, string instances, string watches)
            {
                File.WriteAllText(Path.Combine(proc, "self", "limits"),
                    "Limit                     Soft Limit           Hard Limit           Units     \n"
                    + $"Max processes             {processes,-20} {processes,-20} processes \n"
                    + $"Max open files            {openFiles,-20} 524288               files     \n");
                File.WriteAllText(Path.Combine(inotify, "max_user_instances"), instances + "\n");
                File.WriteAllText(Path.Combine(inotify, "max_user_watches"), watches + "\n");
            }

            Fake("524288", "unlimited", "1024", "524288");
            Assert(LinuxLimits.Check(proc, "factory").Count == 0, "limits at the minimums warned");

            Fake("1024", "4096", "128", "8192");
            List<string> warnings = LinuxLimits.Check(proc, "factory");
            Assert(warnings.Count == 4, $"expected 4 warnings, got {warnings.Count}");
            Assert(warnings[0].StartsWith("Open file limit (ulimit -n, RLIMIT_NOFILE) is 1024, expected at least 65536.")
                && warnings[0].Contains("\"factory - nofile 65536\""), warnings[0]);
            Assert(warnings[1].Contains("is 4096, expected at least 16384") && warnings[1].Contains("LimitNPROC=16384"), warnings[1]);
            Assert(warnings[2].Contains("is 128, expected at least 1024") && warnings[2].Contains("fs.inotify.max_user_instances=1024"), warnings[2]);
            Assert(warnings[3].Contains("is 8192, expected at least 524288") && warnings[3].Contains("fs.inotify.max_user_watches=524288"), warnings[3]);

            Directory.Delete(inotify, true);
            File.Delete(Path.Combine(proc, "self", "limits"));
            Assert(LinuxLimits.Check(proc, "factory").Count == 0, "missing files warned");
        }
        finally
        {
            Directory.Delete(proc, true);
        }
    }
}
