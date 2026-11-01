using System;
using System.Reflection;
using Sandbox.Game.Entities;
using VRage.Game.Entity;

namespace ClusterPlugin;

/// <summary>
/// The msbuild publicizer exposes each event's compiler-generated backing field under the event's own name,
/// which makes <c>entity.OnClosing += handler</c> ambiguous (CS0229) between the event and the field. The
/// project files resolve that for other events with <c>DoNotPublicize</c> items; the A-players batch may not
/// edit project files, so the three events it needs are bound once to their real public add/remove accessors
/// (open-instance delegates, no per-call reflection). They operate on the same multicast list as the C#
/// operators, so vanilla's own <c>-=</c> (e.g. MyPlayerCollection.EntityClosing) still removes what we add.
/// Adding <c>VRage.Game:VRage.Game.Entity.MyEntity.OnClosing</c>,
/// <c>Sandbox.Game:Sandbox.Game.Entities.MyDataReceiver.OnBroadcasterFound</c> and
/// <c>...OnBroadcasterLost</c> to the DoNotPublicize lists would let the call sites go back to <c>+=</c>.
/// </summary>
internal static class PublicizedEvents
{
	public static readonly Action<MyEntity, Action<MyEntity>> AddOnClosing = Accessor<MyEntity, Action<MyEntity>>("add_OnClosing");
	public static readonly Action<MyEntity, Action<MyEntity>> RemoveOnClosing = Accessor<MyEntity, Action<MyEntity>>("remove_OnClosing");
	public static readonly Action<MyDataReceiver, MyDataReceiver.BroadcasterChangeInfo> AddOnBroadcasterFound
		= Accessor<MyDataReceiver, MyDataReceiver.BroadcasterChangeInfo>("add_OnBroadcasterFound");
	public static readonly Action<MyDataReceiver, MyDataReceiver.BroadcasterChangeInfo> AddOnBroadcasterLost
		= Accessor<MyDataReceiver, MyDataReceiver.BroadcasterChangeInfo>("add_OnBroadcasterLost");

	private static Action<T, THandler> Accessor<T, THandler>(string name)
	{
		MethodInfo accessor = typeof(T).GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
			null, new[] { typeof(THandler) }, null)
			?? throw new MissingMethodException(typeof(T).FullName, name);
		return (Action<T, THandler>)Delegate.CreateDelegate(typeof(Action<T, THandler>), accessor);
	}
}
