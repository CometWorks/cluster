using ClusterPlugin;
using HarmonyLib;
using Sandbox.Game.Gui;

namespace Shared.Patches;

/// <summary>
/// After vanilla's server-side <c>UploadSettingsToServer</c> wrote an admin's Trash Removal settings into this
/// server's session, the seam takes them to the cluster. The target is a private static event handler, hence the
/// string name. Inert while the seam is null.
/// </summary>
[HarmonyPatch(typeof(MyGuiScreenAdminMenu), "UploadSettingsToServer")]
public static class MyGuiScreenAdminMenuPatch
{
    [HarmonyPostfix]
    private static void Uploaded() =>
        MyGuiScreenAdminMenuSeams.ClusterTrashSettingsUploaded?.Invoke();
}
