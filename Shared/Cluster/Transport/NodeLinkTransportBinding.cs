using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using LiteNetLib;
using Shared.Logging;
using VRage.GameServices;

namespace ClusterPlugin;

/// <summary>
/// Binds <see cref="DirectTransportNodeLinkPatch"/> (the Gateway⇄node multiplexed link: relayed virtual
/// clients and global-state frames) to the Direct Transport plugin's <c>Shared.Transport.UdpPeer2Peer</c>.
/// <para>
/// The transport is a separate plugin compiled from its dev folder into an assembly with a
/// random name, so nothing here can reference it at build time: the type is resolved by full name from
/// the loaded assemblies, its private members are reached through delegates built once (an accepted
/// use of reflection), and its seven call sites into the patch class are manual Harmony patches.
/// </para>
/// <para>
/// LiteNetLib types (NetPeer, NetManager, ConnectionRequest, NetPacketReader, DeliveryMethod) are
/// referenced from the LiteNetLib 1.3.1 package both plugins declare on purpose, so the runtime binds both
/// to one assembly and the Harmony patch signatures match the plugin's method signatures. Install verifies
/// that assumption and refuses to patch when the two LiteNetLib assemblies differ.
/// </para>
/// </summary>
public static class NodeLinkTransportBinding
{
	private const string PeerTypeName = "Shared.Transport.UdpPeer2Peer";
	private const string TransportTypeName = "Shared.Transport.DirectTransport";

	private static readonly object Sync = new object();
	private static bool m_installed;

	// Cached accessors into the transport plugin, built once by Install. The names follow the
	// UdpPeer2Peer.cs members they reach.
	private static string m_protocolKey;
	private static Func<Action<string>> m_transportLog;
	private static Func<Action<string>> m_transportLogError;
	private static Func<object, bool> m_isServer;
	private static Func<object, NetManager> m_manager;
	private static Func<object, ConcurrentDictionary<ulong, NetPeer>> m_peersById;
	private static Func<object, ConcurrentQueue<Action>> m_engineEvents;
	private static Func<object, Action<ulong>> m_sessionRequest;
	private static Func<object, Action<ulong, string>> m_connectionFailed;
	private static Action<object, int, ulong, byte[]> m_enqueueIncoming;

	/// <summary>
	/// The callbacks the call sites need as lambdas over `this`, bound once per transport
	/// instance so the per-packet ReceivePrefix allocates nothing. All the fields they close over are
	/// readonly in the plugin, so caching them is safe.
	/// </summary>
	private sealed class Callbacks
	{
		public readonly bool IsServer;
		public readonly NetManager Manager;
		public readonly Func<ulong, bool> PhysicalPeerExists;
		public readonly Action<ulong> SessionRequest;
		public readonly Action<ulong, string> ConnectionFailed;
		public readonly Action<int, ulong, byte[]> Enqueue;

		public Callbacks(object instance)
		{
			IsServer = m_isServer(instance);
			Manager = m_manager(instance);
			ConcurrentDictionary<ulong, NetPeer> peersById = m_peersById(instance);
			PhysicalPeerExists = client => peersById.ContainsKey(client);
			SessionRequest = client => RaiseSessionRequest(instance, client);
			ConnectionFailed = (client, reason) => RaiseConnectionFailed(instance, client, reason);
			Enqueue = (seChannel, client, payload) => m_enqueueIncoming(instance, seChannel, client, payload);
		}
	}

	private static readonly ConditionalWeakTable<object, Callbacks> CallbacksByInstance = new ConditionalWeakTable<object, Callbacks>();
	private static Callbacks For(object instance) => CallbacksByInstance.GetValue(instance, i => new Callbacks(i));

	/// <summary>
	/// Resolves the transport plugin, builds the accessors and installs the seven patches.
	/// Returns false (after logging) when the Direct Transport plugin is not loaded and the cluster is not
	/// configured (CLUSTER_GATEWAY_REGISTRY unset); throws when it is: a clustered node without the link
	/// is useless, and a loud failure at startup beats a node that registers and then relays nothing.
	/// </summary>
	public static bool Install(Harmony harmony, IPluginLogger log)
	{
		if (harmony == null) throw new ArgumentNullException(nameof(harmony));
		if (log == null) throw new ArgumentNullException(nameof(log));
		lock (Sync)
		{
			if (m_installed) return true;

			Type peerType = FindType(PeerTypeName);
			if (peerType == null)
			{
				bool clustered = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CLUSTER_GATEWAY_REGISTRY"));
				string message = "Direct Transport plugin not loaded (" + PeerTypeName + " not found in "
					+ AppDomain.CurrentDomain.GetAssemblies().Length + " loaded assemblies); the Gateway node link is unavailable";
				if (clustered)
					throw new InvalidOperationException(message + " but CLUSTER_GATEWAY_REGISTRY is set; enable the direct-transport plugin");
				log.Warning(message);
				return false;
			}
			Type transportType = FindType(TransportTypeName)
				?? throw new InvalidOperationException(TransportTypeName + " not found next to " + peerType.AssemblyQualifiedName);

			BuildAccessors(peerType, transportType, log);
			InstallPatches(harmony, peerType);
			m_installed = true;
			log.Info("Installed the Gateway node link on " + peerType.FullName + " from " + peerType.Assembly.GetName().Name
				+ " (" + Describe(peerType.Assembly) + ")");
			return true;
		}
	}

	private static Type FindType(string fullName)
	{
		// The transport plugin's assembly name is FriendlyName + '_' + random under Magnetar (and
		// DirectTransport under msbuild), so the search goes by the full type name only.
		foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
		{
			if (assembly.IsDynamic) continue;
			Type type;
			try { type = assembly.GetType(fullName, false); }
			catch (Exception) { continue; }
			if (type != null) return type;
		}
		return null;
	}

	private static string Describe(Assembly assembly)
	{
		string location;
		try { location = assembly.Location; }
		catch (Exception) { location = null; }
		return string.IsNullOrEmpty(location) ? "in-memory, no location" : location;
	}

	private static void BuildAccessors(Type peerType, Type transportType, IPluginLogger log)
	{
		// LiteNetLib binding check: the patch signatures below name NetPeer/NetManager/... from OUR
		// LiteNetLib reference; they only match the plugin's methods when both resolved to one assembly.
		FieldInfo managerField = RequireField(peerType, "m_manager");
		Assembly theirs = managerField.FieldType.Assembly;
		Assembly ours = typeof(NetManager).Assembly;
		log.Info("LiteNetLib bound by the cluster plugin: " + ours.GetName().FullName + " at " + Describe(ours));
		log.Info("LiteNetLib bound by the Direct Transport plugin: " + theirs.GetName().FullName + " at " + Describe(theirs));
		if (!ReferenceEquals(ours, theirs))
			throw new InvalidOperationException("The cluster plugin and the Direct Transport plugin loaded different LiteNetLib assemblies ("
				+ Describe(ours) + " vs " + Describe(theirs) + "); the node link patches cannot bind. Both plugins must declare the same LiteNetLib package version.");
		if (managerField.FieldType != typeof(NetManager))
			throw new InvalidOperationException(peerType.FullName + ".m_manager is " + managerField.FieldType + ", expected " + typeof(NetManager));

		// DirectTransportService.ProtocolKey -> Shared.Transport.DirectTransport.ProtocolKey (const string).
		FieldInfo protocolKeyField = RequireField(transportType, "ProtocolKey");
		m_protocolKey = protocolKeyField.IsLiteral
			? (string)protocolKeyField.GetRawConstantValue()
			: (string)protocolKeyField.GetValue(null);
		if (string.IsNullOrEmpty(m_protocolKey))
			throw new InvalidOperationException(transportType.FullName + ".ProtocolKey is empty");

		// DirectTransportService.Log / LogError -> DirectTransport.Log / LogError, public static Action<string>
		// fields the transport plugin assigns in DirectServer.Init. Read per call: the field is a hook the
		// plugin may (re)assign after we were installed, and the transport initialises to no-op delegates.
		m_transportLog = StaticGetter<Action<string>>(RequireField(transportType, "Log"));
		m_transportLogError = StaticGetter<Action<string>>(RequireField(transportType, "LogError"));

		m_isServer = InstanceGetter<bool>(peerType, RequireField(peerType, "m_isServer"));
		m_manager = InstanceGetter<NetManager>(peerType, managerField);
		m_peersById = InstanceGetter<ConcurrentDictionary<ulong, NetPeer>>(peerType, RequireField(peerType, "m_peersById"));
		// Engine events: the plugin never raises SessionRequest / ConnectionFailed from the poll thread; it
		// queues them through m_engineEvents and DispatchEngineEvents drains that queue from
		// IMyGameService.OnUpdate. The node link raises them the same way (see Raise below).
		m_engineEvents = InstanceGetter<ConcurrentQueue<Action>>(peerType, RequireField(peerType, "m_engineEvents"));
		// Event backing fields (compiler generated, same name as the event; never publicized).
		m_sessionRequest = InstanceGetter<Action<ulong>>(peerType, RequireField(peerType, "SessionRequest"));
		m_connectionFailed = InstanceGetter<Action<ulong, string>>(peerType, RequireField(peerType, "ConnectionFailed"));
		m_enqueueIncoming = BuildEnqueue(peerType);
	}

	private static FieldInfo RequireField(Type type, string name)
		=> AccessTools.Field(type, name) ?? throw new InvalidOperationException(type.FullName + " has no field " + name + "; the Direct Transport plugin changed");

	private static Func<T> StaticGetter<T>(FieldInfo field)
	{
		if (!field.IsStatic || field.FieldType != typeof(T))
			throw new InvalidOperationException(field.DeclaringType?.FullName + "." + field.Name + " is not a static " + typeof(T));
		return Expression.Lambda<Func<T>>(Expression.Field(null, field)).Compile();
	}

	private static Func<object, T> InstanceGetter<T>(Type declaringType, FieldInfo field)
	{
		if (field.IsStatic || field.FieldType != typeof(T))
			throw new InvalidOperationException(field.DeclaringType?.FullName + "." + field.Name + " is not an instance " + typeof(T));
		ParameterExpression instance = Expression.Parameter(typeof(object), "instance");
		return Expression.Lambda<Func<object, T>>(Expression.Field(Expression.Convert(instance, declaringType), field), instance).Compile();
	}

	/// <summary>
	/// (instance, seChannel, client, payload) => m_receiveQueues.GetOrAdd(seChannel, _ => new ConcurrentQueue&lt;Incoming&gt;()).Enqueue(new Incoming(client, payload))
	/// - exactly what UdpPeer2Peer.OnNetworkReceive does for a physical packet. Incoming is a private nested
	/// readonly struct, so the queue's element type cannot be named here; the expression is compiled once
	/// (LambdaCompiler emits a visibility-skipping DynamicMethod, so the private type is reachable).
	/// </summary>
	private static Action<object, int, ulong, byte[]> BuildEnqueue(Type peerType)
	{
		Type incomingType = peerType.GetNestedType("Incoming", BindingFlags.Public | BindingFlags.NonPublic)
			?? throw new InvalidOperationException(peerType.FullName + " has no nested Incoming type; the Direct Transport plugin changed");
		ConstructorInfo incomingCtor = incomingType.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
				null, new[] { typeof(ulong), typeof(byte[]) }, null)
			?? throw new InvalidOperationException(incomingType.FullName + " has no (ulong, byte[]) constructor");
		FieldInfo queuesField = RequireField(peerType, "m_receiveQueues");
		Type queueType = typeof(ConcurrentQueue<>).MakeGenericType(incomingType);
		Type dictionaryType = typeof(ConcurrentDictionary<,>).MakeGenericType(typeof(int), queueType);
		if (queuesField.FieldType != dictionaryType)
			throw new InvalidOperationException(peerType.FullName + ".m_receiveQueues is " + queuesField.FieldType + ", expected " + dictionaryType);
		Type factoryType = typeof(Func<,>).MakeGenericType(typeof(int), queueType);
		MethodInfo getOrAdd = dictionaryType.GetMethod("GetOrAdd", new[] { typeof(int), factoryType })
			?? throw new InvalidOperationException(dictionaryType + " has no GetOrAdd(int, Func) overload");
		MethodInfo enqueue = queueType.GetMethod("Enqueue", new[] { incomingType })
			?? throw new InvalidOperationException(queueType + " has no Enqueue");

		// One factory delegate shared by every call (`_ => new ConcurrentQueue<Incoming>()`).
		ParameterExpression key = Expression.Parameter(typeof(int), "_");
		Delegate factory = Expression.Lambda(factoryType, Expression.New(queueType), key).Compile();

		ParameterExpression instance = Expression.Parameter(typeof(object), "instance");
		ParameterExpression channel = Expression.Parameter(typeof(int), "seChannel");
		ParameterExpression sender = Expression.Parameter(typeof(ulong), "client");
		ParameterExpression payload = Expression.Parameter(typeof(byte[]), "payload");
		Expression queues = Expression.Field(Expression.Convert(instance, peerType), queuesField);
		Expression queue = Expression.Call(queues, getOrAdd, channel, Expression.Constant(factory, factoryType));
		Expression body = Expression.Call(queue, enqueue, Expression.New(incomingCtor, sender, payload));
		return Expression.Lambda<Action<object, int, ulong, byte[]>>(body, instance, channel, sender, payload).Compile();
	}

	private static void InstallPatches(Harmony harmony, Type peerType)
	{
		Prefix(harmony, peerType, "SendPacket", nameof(SendPrefix));
		Prefix(harmony, peerType, "AcceptSession", nameof(AcceptSessionPrefix));
		Prefix(harmony, peerType, "CloseSession", nameof(CloseSessionPrefix));
		Prefix(harmony, peerType, "GetSessionState", nameof(GetSessionStatePrefix));
		Prefix(harmony, peerType, "OnConnectionRequest", nameof(ConnectionRequestPrefix));
		harmony.Patch(RequireMethod(peerType, "OnPeerDisconnected"),
			postfix: new HarmonyMethod(typeof(NodeLinkTransportBinding), nameof(DisconnectedPostfix)));
		Prefix(harmony, peerType, "OnNetworkReceive", nameof(ReceivePrefix));
	}

	private static void Prefix(Harmony harmony, Type peerType, string method, string prefix)
		=> harmony.Patch(RequireMethod(peerType, method), prefix: new HarmonyMethod(typeof(NodeLinkTransportBinding), prefix));

	private static MethodInfo RequireMethod(Type type, string name)
	{
		MethodInfo[] candidates = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
			.Where(m => m.Name == name).ToArray();
		if (candidates.Length != 1)
			throw new InvalidOperationException(type.FullName + "." + name + ": expected exactly one declaration, found " + candidates.Length);
		return candidates[0];
	}

	// --- Callbacks handed to the durable helper --------------------------------------------------------

	private static void Log(string message) => m_transportLog()?.Invoke(message);
	private static void LogError(string message) => m_transportLogError()?.Invoke(message);

	/// <summary>
	/// The plugin's own OnConnectionRequest / OnPeerDisconnected queue engine events instead of raising them
	/// on the poll thread; DispatchEngineEvents (hooked to IMyGameService.OnUpdate) drains the queue on the
	/// engine's update thread. The event field is read at dispatch time, like `SessionRequest?.Invoke`.
	/// </summary>
	private static void RaiseSessionRequest(object instance, ulong client)
		=> m_engineEvents(instance).Enqueue(() => m_sessionRequest(instance)?.Invoke(client));

	private static void RaiseConnectionFailed(object instance, ulong client, string reason)
		=> m_engineEvents(instance).Enqueue(() => m_connectionFailed(instance)?.Invoke(client, reason));

	// --- Harmony patch bodies: the UdpPeer2Peer.cs call sites into the node link ----------------------------

	// ClusterPlugin patch point: DirectTransportNodeLinkPatch.SendPrefix
	// Harmony migration: prefix on UdpPeer2Peer.SendPacket
	private static bool SendPrefix(object __instance, ulong remoteUser, byte[] data, int byteCount, MyP2PMessageEnum msgType, int channel, ref bool __result)
	{
		bool result = false;
		if (!DirectTransportNodeLinkPatch.SendPrefix(__instance, remoteUser, data, byteCount, msgType, channel, ref result))
		{
			__result = result;
			return false;
		}
		return true;
	}

	// ClusterPlugin patch point: DirectTransportNodeLinkPatch.AcceptSessionPrefix
	// Harmony migration: prefix on UdpPeer2Peer.AcceptSession
	private static bool AcceptSessionPrefix(object __instance, ulong remotePeerId, ref bool __result)
	{
		bool result = false;
		if (!DirectTransportNodeLinkPatch.AcceptSessionPrefix(__instance, remotePeerId, ref result))
		{
			__result = result;
			return false;
		}
		return true;
	}

	// ClusterPlugin patch point: DirectTransportNodeLinkPatch.CloseSessionPrefix
	// Harmony migration: prefix on UdpPeer2Peer.CloseSession
	private static bool CloseSessionPrefix(object __instance, ulong remotePeerId, ref bool __result)
	{
		bool result = false;
		if (!DirectTransportNodeLinkPatch.CloseSessionPrefix(__instance, remotePeerId, ref result))
		{
			__result = result;
			return false;
		}
		return true;
	}

	// ClusterPlugin patch point: DirectTransportNodeLinkPatch.GetSessionStatePrefix
	// Harmony migration: prefix on UdpPeer2Peer.GetSessionState
	private static bool GetSessionStatePrefix(object __instance, ulong remoteUser, ref MyP2PSessionState state, ref bool __result)
	{
		bool result = false;
		if (!DirectTransportNodeLinkPatch.GetSessionStatePrefix(__instance, remoteUser, ref state, ref result))
		{
			__result = result;
			return false;
		}
		return true;
	}

	// ClusterPlugin patch point: DirectTransportNodeLinkPatch.ConnectionRequestPrefix
	// Harmony migration: prefix on UdpPeer2Peer.OnConnectionRequest
	// The node-link check belongs after the `!m_isServer -> Reject` guard and inside the method's try block
	// whose catch logs and rejects; a prefix runs before both, so the guard and the catch are reproduced here.
	// The helper restores request.Data.Position when it declines so the original parser sees a fresh reader,
	// sets m_manager.ChannelsCount = 16 only for a validated node link, and suppresses the original after
	// accepting or rejecting a handled request.
	private static bool ConnectionRequestPrefix(object __instance, ConnectionRequest request)
	{
		Callbacks callbacks = For(__instance);
		if (!callbacks.IsServer)
			return true;
		try
		{
			return DirectTransportNodeLinkPatch.ConnectionRequestPrefix(__instance, request, callbacks.Manager,
				m_protocolKey, callbacks.PhysicalPeerExists, Log);
		}
		catch (Exception e)
		{
			LogError("OnConnectionRequest failed: " + e);
			request.Reject();
			return false;
		}
	}

	// ClusterPlugin patch point: DirectTransportNodeLinkPatch.DisconnectedPostfix
	// Harmony migration: postfix on UdpPeer2Peer.OnPeerDisconnected
	private static void DisconnectedPostfix(object __instance, NetPeer peer, DisconnectInfo disconnectInfo)
	{
		DirectTransportNodeLinkPatch.DisconnectedPostfix(__instance, peer, disconnectInfo.Reason.ToString(),
			For(__instance).ConnectionFailed);
	}

	// ClusterPlugin patch point: DirectTransportNodeLinkPatch.ReceivePrefix
	// Harmony migration: prefix on UdpPeer2Peer.OnNetworkReceive
	private static bool ReceivePrefix(object __instance, NetPeer peer, NetPacketReader reader)
	{
		Callbacks callbacks = For(__instance);
		return DirectTransportNodeLinkPatch.ReceivePrefix(__instance, peer, reader, Log,
			callbacks.PhysicalPeerExists, callbacks.SessionRequest, callbacks.ConnectionFailed, callbacks.Enqueue);
	}
}
