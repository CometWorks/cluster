using System.Reflection;

// Entry point. The deployment archive excludes game binaries; they resolve at runtime from the
// game install's DedicatedServer64 named by SPACE_ENGINEERS_BIN64. The handler must be in place
// before any method referencing game types is JIT-compiled, which is why Main lives here and
// only calls into Tool.Run after registering it.
internal static class Bootstrap
{
	private static int Main(string[] args)
	{
		AppDomain.CurrentDomain.AssemblyResolve += (_, resolveArgs) =>
		{
			string bin64 = Environment.GetEnvironmentVariable("SPACE_ENGINEERS_BIN64");
			if (Environment.GetEnvironmentVariable("MAGNETAR_WORLD_TRACE_RESOLVE") == "1") Console.Error.WriteLine($"[resolve] {resolveArgs.Name} from {bin64}");
			if (string.IsNullOrWhiteSpace(bin64)) return null;
			string path = Path.Combine(Path.GetFullPath(bin64), new AssemblyName(resolveArgs.Name).Name + ".dll");
			return File.Exists(path) ? Assembly.LoadFrom(path) : null;
		};
		try { return Invoke(args); }
		catch (Exception e) { Console.Error.WriteLine(e.ToString()); Console.Error.WriteLine("loaded: " + string.Join(", ", AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name))); return 3; }
	}

	[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
	private static int Invoke(string[] args)
	{
		try { return Tool.Run(args); }
		finally { VRage.Utils.MyLog.Default?.Flush(); }
	}
}
