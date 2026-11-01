using System.Reflection;
using HarmonyLib;
using Sandbox.Engine.Utils;
using Sandbox.Game.Gui;
using Sandbox.Graphics.GUI;
using VRage.Game;
using VRageMath;

namespace ClusterClientDiagnostics;

[HarmonyPatch]
internal static class DiagnosticsMenuPatch
{
    private static MethodBase TargetMethod() => AccessTools.Method("Sandbox.Game.Gui.MyGuiScreenDebugOfficial:RecreateControls");

    private static void Postfix(MyGuiScreenDebugBase __instance)
    {
        foreach (var control in __instance.Controls)
            if (control is MyGuiControlMultilineText || control is MyGuiControlPanel)
                control.Size -= new Vector2(0, 0.06f);
        var button = new MyGuiControlButton(position: new Vector2(-0.02f, 0.45f), text: new System.Text.StringBuilder("Cluster diagnostics"),
            onButtonClick: _ => Open(), visualStyle: MyGuiControlButtonStyleEnum.Rectangular);
        button.Size = new Vector2(0.31f, 0.04f);
        __instance.Controls.Add(button);
    }

    internal static void Open()
    {
        if (MyScreenManager.GetScreenWithFocus() is DiagnosticsScreen) return;
        MyGuiSandbox.AddScreen(new DiagnosticsScreen());
    }
}

[HarmonyPatch]
internal static class MultiplayerF11Patch
{
    private static MethodBase TargetMethod() => AccessTools.Method("Sandbox.Graphics.GUI.MyDX9Gui:SwitchModDebugScreen");

    private static bool Prefix()
    {
        // Vanilla blocks the entire mod menu in multiplayer. Open only our read-only screen.
        if (!MyFakes.ENABLE_F12_MENU && Plugin.Instance?.Diagnostics.Connected == true)
        {
            DiagnosticsMenuPatch.Open();
            return false;
        }
        return true;
    }
}
