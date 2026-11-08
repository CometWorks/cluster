using System;
using System.Threading.Tasks;

namespace ClusterPlugin;

/// <summary>
/// SE-1228: World Authority calls that wait on a node run here, off the WA's game thread, one at a time per lane. A node
/// runs its control routes on its game thread, and a node's game thread waits on the WA's game thread for its banking,
/// faction and identity commands. On octahedron-test (cluster 0.3.2) the WA's drop sweep POSTed a drop to a node whose
/// game thread was waiting on a store payment leg: neither moved until the node's 5 s command timeout, once per leg, and
/// both stalled 5-10 s. What the answer changes in game state goes back to the game thread through MySandboxGame.Invoke.
/// Game thread only.
/// </summary>
internal sealed class NodeDispatchLane
{
	private Task m_tail = Task.CompletedTask;

	/// <summary>A call queued earlier has not finished: the node it waits on may be stuck.</summary>
	public bool Busy => !m_tail.IsCompleted;

	/// <summary>Runs <paramref name="call"/> after the calls queued before it. It must catch its own exceptions.</summary>
	public void Run(Action call) => m_tail = m_tail.ContinueWith(_ => call(), TaskScheduler.Default);
}
