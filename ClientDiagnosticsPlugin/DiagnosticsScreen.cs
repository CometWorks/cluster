using System;
using System.Text;
using Sandbox.Game.Gui;
using Sandbox.Graphics.GUI;
using CometWorks.ClusterGateway.AdminContract.V1;
using static ClusterClientDiagnostics.DiagnosticsText;
using VRage.Utils;
using VRage.Input;
using VRageMath;

namespace ClusterClientDiagnostics;

internal sealed class DiagnosticsScreen : MyGuiScreenBase
{
    private readonly MyGuiControlMultilineText text;
    private string previousText;

    public DiagnosticsScreen() : base(new Vector2(0.5f), MyGuiConstants.SCREEN_BACKGROUND_COLOR, new Vector2(0.8f, 0.85f))
    {
        EnabledBackgroundFade = true;
        m_closeOnEsc = true;
        CloseButtonEnabled = true;
        AddCaption("Cluster diagnostics");
        text = new MyGuiControlMultilineText(position: new Vector2(0, 0.025f), size: new Vector2(0.73f, 0.7f),
            textScale: 0.75f, textAlign: MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_TOP, drawScrollbarH: false);
        Controls.Add(text);
    }

    public override string GetFriendlyName() => "ClusterDiagnostics";

    public override void HandleInput(bool receivedFocusInThisUpdate)
    {
        base.HandleInput(receivedFocusInThisUpdate);
        if (!receivedFocusInThisUpdate && MyInput.Static.IsNewKeyPressed(MyKeys.F11)) CloseScreen();
    }

    public override bool Update(bool hasFocus)
    {
        if (!base.Update(hasFocus)) return false;
        string value = Format(Plugin.Instance?.Diagnostics);
        if (value != previousText)
        {
            text.Text = new StringBuilder(value);
            previousText = value;
        }
        return true;
    }

    private static string Format(DiagnosticsClient client)
    {
        if (client?.Connected != true) return "Connect to a cluster server to receive diagnostics.";
        if (client.Snapshot == null) return "Waiting for server diagnostics.\nThe server must run the Cluster Diagnostics companion plugin.";
        if (client.DataAge > 12) return "Diagnostics unavailable: no recent server response.";
        DiagnosticsSnapshot data = client.Snapshot;
        NodeDiagnostics node = data.Current;
        var builder = new StringBuilder();
        builder.AppendLine(client.CanShowAdvanced ? "Admin mode" : "Player mode");
        builder.AppendLine("Game node: " + node.Name);
        builder.AppendLine("Endpoint: " + node.Endpoint);
        builder.AppendLine("Node age: " + Duration(node.AgeSeconds));
        builder.AppendLine("Process uptime: " + Duration(node.ProcessUptimeSeconds < 0 ? -1 : node.ProcessUptimeSeconds + (long)client.DataAge));
        builder.AppendLine("Serving age: " + Duration(node.ServingSeconds));
        builder.AppendLine(Rotation(node, data.ServerAt, client.DataAge));
        if (node.DrainAt != 0) builder.AppendLine("Drain deadline (UTC): " + Timestamp(node.DrainAt));
        builder.AppendLine("Cluster Registry status: " + node.State);
        builder.AppendLine("Received " + (int)client.DataAge + " s ago; refresh every 3 s");
        if (!client.CanShowAdvanced) return builder.ToString();

        builder.AppendLine();
        builder.AppendLine("Cluster Registry: " + data.Registry + (data.RegistryAvailable ? " (reachable)" : " (unavailable)"));
        if (!data.RegistryAvailable) return builder.ToString();
        builder.AppendLine("Cluster Registry observation (UTC): " + Timestamp(data.CapturedAt));
        builder.AppendLine("Nodes: " + data.TotalNodes);
        foreach (NodeDiagnostics other in data.Nodes)
        {
            builder.AppendLine();
            builder.AppendLine(other.Name + (other.Authority ? " [World Authority]" : " [Game node]") + " — " + other.Endpoint);
            builder.AppendLine("  " + other.State + "; serving: " + other.Serving + "; hot spare: " + other.Spare + "; epoch: " + other.Epoch);
            builder.AppendLine("  Node age: " + Duration(other.AgeSeconds) + "; serving age: " + Duration(other.ServingSeconds));
            builder.AppendLine("  Lease expires (UTC): " + Timestamp(other.LeaseAt));
            builder.AppendLine("  " + Rotation(other, data.ServerAt, client.DataAge));
            if (other.Authority) builder.AppendLine("  Authority generation: " + other.AuthorityGeneration);
        }
        if (data.TotalNodes > data.Nodes.Length) builder.AppendLine("Showing first " + data.Nodes.Length + " nodes.");
        return builder.ToString();
    }

}
