using System;
using System.Text;

namespace ClusterPlugin;

internal static partial class ClusterSelfTest
{
	/// SE-883: what the exit watchdog logs from a crash report: the game thread first, native runs folded, native-only threads counted.
	private static void GameThreadStacksRender()
	{
		const string report = """
			{"payload":{"threads":[
			{"native_thread_id":"10","stack_frames":[{"native_module":"libc.so.6"}]},
			{"native_thread_id":"11"},
			{"native_thread_id":"12","stack_frames":[{"method_name":"Other.Work"}]},
			{"native_thread_id":"1f","stack_frames":[{"native_module":"libc.so.6"},{"native_module":"libc.so.6"},{"native_module":"libcoreclr.so"},
				{"method_name":"Hang.Block"},{"method_name":""},{"method_name":"Game.Run"}]}
			]}}
			""";
		string text = GameThreadStacks.Render(Encoding.UTF8.GetBytes(report), 0x1f);
		const string expected = "thread 31 (game thread)\n  [libc.so.6 x2]\n  [libcoreclr.so]\n  Hang.Block\n  [native]\n  Game.Run\n"
			+ "thread 18\n  Other.Work\n2 native threads not shown";
		if (text != expected) throw new InvalidOperationException("game-thread-stacks: " + text);
	}
}
