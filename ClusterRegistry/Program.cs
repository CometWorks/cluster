using System.Reflection;
using ClusterRegistry;

// The deployment archive excludes game binaries (EOSSDK.dll); resolve them at runtime from the
// user-supplied game install's Bin64 named by SPACE_ENGINEERS_BIN64. Repo builds carry the
// assembly beside the gateway and never reach this handler.
AppDomain.CurrentDomain.AssemblyResolve += (_, resolveArgs) =>
{
    string? bin64 = Environment.GetEnvironmentVariable("SPACE_ENGINEERS_BIN64");
    if (string.IsNullOrWhiteSpace(bin64)) return null;
    string path = Path.Combine(Path.GetFullPath(bin64), new AssemblyName(resolveArgs.Name).Name + ".dll");
    if (!File.Exists(path)) return null;
    var assembly = Assembly.LoadFrom(path);
    // Native imports of a Bin64-loaded assembly probe beside Bin64, not beside the gateway;
    // point them at the bundled Linux natives (e.g. EOSSDK-Shipping.dll) in the gateway directory.
    try
    {
        System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver(assembly, (libraryName, _, _) =>
        {
            string bundled = Path.Combine(AppContext.BaseDirectory, libraryName);
            return File.Exists(bundled) ? System.Runtime.InteropServices.NativeLibrary.Load(bundled) : IntPtr.Zero;
        });
    }
    catch (InvalidOperationException)
    {
        // A resolver is already registered for this assembly.
    }
    return assembly;
};

if (args.Length == 2 && args[0] == "--check-plugin-sdk")
{
    try { PluginSdkRequirement.CheckFile(args[1]); Console.WriteLine("PluginSdk requirement satisfied."); }
    catch (Exception error) { Console.Error.WriteLine(error.Message); Environment.ExitCode = 2; }
    return;
}

// Build/release.sh records the contract of the SDK the release was built against; prints nothing when it declares none.
if (args.Length == 2 && args[0] == "--plugin-sdk-contract")
{
    Console.WriteLine(PluginSdkRequirement.ReadContract(args[1]));
    return;
}

// #1304: replay the balance planner on saved /cluster/partitions reports (see DirectGateway.BalanceReplay).
if (args.Length > 1 && args[0] == "--balance-replay")
{
    DirectGateway.BalanceReplay(args[1..]);
    return;
}

AppDomain.CurrentDomain.UnhandledException += (_, fatal) =>
    DiagnosticReports.Write("unhandled_exception", "Unhandled registry exception", fatal.ExceptionObject as Exception);
SecretRedactor.ConfigureFromEnvironment();
Console.SetOut(new TimestampedWriter(SecretRedactor.Wrap(Console.Out)));
Console.SetError(new TimestampedWriter(SecretRedactor.Wrap(Console.Error)));

// `--bench-collapse [records]` measures the lineage-collapse closure at a scale the self-test cannot
// reach. The self-test proves the 2026-08-22 rewrite is FAITHFUL (it recomputes the closure the old
// way and compares every flag) over the ~40 records a test run produces, where an O(n^3) algorithm is
// invisible. This measures the same two algorithms on the same synthetic lineage so the performance
// claim rests on a number rather than on the exponent alone.
int benchCollapse = Array.IndexOf(args, "--bench-collapse");
if (benchCollapse >= 0)
{
    int records = benchCollapse + 1 < args.Length && int.TryParse(args[benchCollapse + 1], out int parsed) ? parsed : 400;
    SelfTest.BenchmarkCollapse(records);
    return;
}

// Isolated crash-durability fixture; Environment.Exit bypasses Registry disposal/flush.
int abandonedAllocation = Array.IndexOf(args, "--self-test-abandon-allocation");
if (abandonedAllocation >= 0)
{
    if (abandonedAllocation + 1 >= args.Length) throw new ArgumentException("Allocation fixture directory required");
    SelfTest.AllocateAndAbandon(args[abandonedAllocation + 1]);
    return;
}

// Isolated SIGTERM fixture: waits for the signal in a process of its own.
int sigtermFixture = Array.IndexOf(args, "--self-test-sigterm");
if (sigtermFixture >= 0)
{
    if (sigtermFixture + 2 >= args.Length) throw new ArgumentException("SIGTERM fixture directory and lock mode required");
    SelfTest.AwaitSigterm(args[sigtermFixture + 1], args[sigtermFixture + 2] == "locked");
    return;
}

int selfTest = Array.IndexOf(args, "--self-test");
if (selfTest >= 0)
{
    // `--self-test [section]` so a test scenario can run and cite exactly the section it owns.
    string? section = selfTest + 1 < args.Length && !args[selfTest + 1].StartsWith('-') ? args[selfTest + 1] : null;
    try
    {
        SelfTest.Run(section);
    }
    catch (ArgumentException e)
    {
        Console.Error.WriteLine(e.Message);
        Environment.ExitCode = 2;
    }
    return;
}

// SE-0012: warn only, a small cluster may be fine below the minimums
if (OperatingSystem.IsLinux())
    foreach (string warning in LinuxLimits.Check())
        Console.Error.WriteLine("Warning: " + warning);

try
{
    // T-0321: a gateway-only restart keeps the running start's tokens (StartCredentials).
    GatewayOptions options = StartCredentials.Apply(GatewayOptions.Parse(args));
    using var gateway = new DirectGateway(options);
    gateway.Run();
}
catch (ArgumentException e)
{
    Console.Error.WriteLine(e.Message);
    Environment.ExitCode = 2;
}
catch (InvalidOperationException e)
{
    // A refused start (e.g. the Orchestration §5.1 run-root provenance mismatch) is an operator
    // error with a printed resolution, not a crash - no dump, deterministic exit code.
    Console.Error.WriteLine(e.Message);
    Environment.ExitCode = 3;
}
