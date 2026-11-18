using System;

namespace ClusterPlugin;

/// <summary>
/// Cluster seams of <c>Sandbox.Game.Gui.MyGuiScreenAdminMenu</c>, installed by
/// <c>Shared.Patches.MyGuiScreenAdminMenuPatch</c>.
/// </summary>
public static class MyGuiScreenAdminMenuSeams
{
    // ClusterPlugin seam: an admin's Trash Removal upload was written into this server's session settings
    // (TrashSettingsService forwards it to the World Authority).
    public static Action ClusterTrashSettingsUploaded;
}
