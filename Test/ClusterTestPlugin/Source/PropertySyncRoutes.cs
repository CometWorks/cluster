using System;
using System.Collections.Generic;
using System.Globalization;
using Sandbox.Game.Entities;
using Sandbox.Game.Replication;
using SpaceEngineers.Game.EntityComponents.Blocks;
using VRage.Game.Entity;

namespace ClusterTestPlugin;

/// <summary>
/// #1376: puts an emotion controller component into the state a frozen import leaves it in - its
/// m_selectedLCDsIds (Sync&lt;List&lt;long&gt;&gt;) and m_selectedEmotionSubtype (Sync&lt;string&gt;) null, every
/// property dirty for every client - so the next SendUpdate serializes the nulls, as it did after a
/// client-view adopt before the commit's thaw on octahedron-1191c local-node-3. The values are written
/// past the Sync setters: those raise the component's handlers, which put a fresh list back at once.
/// </summary>
internal static class PropertySyncRoutes
{
    private static readonly Dictionary<long, (List<long> Lcds, string Emotion)> Saved = new();

    public static void Register()
    {
        // POST /property-sync-test-null?block=&restore=false|true - null the Syncs (or put the saved values back)
        // and mark the component's property group dirty for every client.
        TestRoutes.Add("POST", "/property-sync-test-null", context =>
        {
            long block = long.Parse(TestRoutes.Query(context, "block"), CultureInfo.InvariantCulture);
            bool restore = TestRoutes.Query(context, "restore", "false") == "true";
            TestRoutes.Json(context, 200, TestRoutes.OnGameThread(() =>
            {
                if (!MyEntities.TryGetEntityById(block, out MyEntity entity))
                    throw new ArgumentException("Unknown entity " + block);
                if (!entity.Components.TryGet(out MyEmotionControllerComponent component))
                    throw new ArgumentException("No emotion controller component on " + block);
                if (MyExternalReplicable.FindByObject(component) is not MyEventProxyEntityComponentReplicable replicable
                    || replicable.m_propertySync == null)
                    throw new InvalidOperationException("No property sync group for the component of " + block);
                if (restore)
                {
                    if (!Saved.Remove(block, out (List<long> Lcds, string Emotion) saved))
                        throw new InvalidOperationException("Nothing saved for " + block);
                    component.m_selectedLCDsIds.m_value = saved.Lcds;
                    component.m_selectedEmotionSubtype.m_value = saved.Emotion;
                }
                else
                {
                    Saved[block] = (component.m_selectedLCDsIds.m_value, component.m_selectedEmotionSubtype.m_value);
                    component.m_selectedLCDsIds.m_value = null;
                    component.m_selectedEmotionSubtype.m_value = null;
                }
                replicable.m_propertySync.MarkDirty();
                return (object)new
                {
                    block,
                    restore,
                    lcdsNull = component.m_selectedLCDsIds.m_value == null,
                    emotionNull = component.m_selectedEmotionSubtype.m_value == null,
                    properties = replicable.m_propertySync.PropertyCount,
                };
            }));
        });
        // GET /property-sync-test-counters - the cluster's #1376 withheld/dropped counters, null on a build without them
        // (read by name, so this plugin still builds against such a package).
        TestRoutes.Add("GET", "/property-sync-test-counters", context =>
        {
            Type patch = Type.GetType("Shared.Patches.MyPropertySyncStateGroupPatch, " + typeof(Shared.Patches.PatchHelpers).Assembly.FullName);
            TestRoutes.Json(context, 200, new
            {
                withheld = patch?.GetProperty("WithheldCount")?.GetValue(null),
                dropped = patch?.GetProperty("DroppedCount")?.GetValue(null),
            });
        });
    }
}
