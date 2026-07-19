using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using PopotoVox.Security;
using PopotoVox.Tts;

namespace PopotoVox.Windows;

/// <summary>
/// Shared "getting your voice engine ready" progress window. One surface for BOTH the background boot
/// warm-up and an Apply-driven live engine swap — it just polls <see cref="Plugin.Transition"/> each frame
/// (immediate-mode) and shows the current step: downloading → warming up → ready. Reuses the same per-asset
/// progress-bar pattern as the setup wizard's download page.
/// </summary>
public sealed class EngineProgressWindow : Window
{
    private static readonly TimeSpan AutoCloseAfterReady = TimeSpan.FromSeconds(2.5);

    private readonly Plugin plugin;

    public EngineProgressWindow(Plugin plugin)
        : base("PopotoVox — Voice engine###PopotoVoxEngineProgress",
               ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize)
    {
        this.plugin = plugin;
        Size = new Vector2(440, 0);
        SizeCondition = ImGuiCond.Appearing;
    }

    public override void Draw()
    {
        var t = plugin.Transition;

        // Nothing happening (or a completed run that's had its moment) — get out of the way.
        if (t.Phase == TransitionPhase.Idle ||
            (t.Phase == TransitionPhase.Ready && DateTime.UtcNow - t.CompletedAt > AutoCloseAfterReady))
        {
            IsOpen = false;
            return;
        }

        var name = TtsEngineCatalog.Get(t.Target).DisplayName;
        DrawSteps(t.Phase);
        ImGui.Separator();
        ImGui.Spacing();

        switch (t.Phase)
        {
            case TransitionPhase.Downloading:
                ImGui.TextColored(Ui.Warn, $"Downloading {name}…");
                Ui.Paragraph("You can leave this open — it verifies each file against a signed manifest.");
                ImGui.Spacing();
                DrawBundleProgress(t.Target);
                break;

            case TransitionPhase.Warming:
                ImGui.TextColored(Ui.Accent, $"Warming up {name}…");
                Ui.Paragraph("Starting the voice engine and loading its model. The first time can take " +
                             "a little while on a GPU engine.");
                break;

            case TransitionPhase.Swapping:
                ImGui.TextColored(Ui.Accent, $"Switching to {name}…");
                Ui.Paragraph("Finishing the current line, then handing over — no reload needed.");
                break;

            case TransitionPhase.Ready:
                ImGui.TextColored(Ui.Good, $"✓ {name} is ready.");
                if (ImGui.Button("Close")) IsOpen = false;
                break;

            case TransitionPhase.Failed:
                Ui.Banner(FontAwesomeIcon.ExclamationTriangle,
                    $"Couldn't switch to {name}. {t.Error} Your previous voice is still running.", Ui.Bad);
                if (ImGui.Button("Dismiss")) IsOpen = false;
                break;
        }
    }

    // A tiny breadcrumb so the user can see where they are in the flow.
    private static void DrawSteps(TransitionPhase phase)
    {
        var rank = phase switch
        {
            TransitionPhase.Downloading => 0,
            TransitionPhase.Warming => 1,
            TransitionPhase.Swapping => 2,
            TransitionPhase.Ready => 3,
            _ => 1,
        };
        string[] titles = { "Download", "Warm up", "Switch", "Ready" };
        for (var i = 0; i < titles.Length; i++)
        {
            if (i > 0) { ImGui.SameLine(); ImGui.TextDisabled("›"); ImGui.SameLine(); }
            var col = i == rank ? Ui.Accent : i < rank ? Ui.Good : Ui.Muted;
            ImGui.TextColored(col, titles[i]);
        }
    }

    private void DrawBundleProgress(TtsEngineChoice target)
    {
        // Mirror the swap's bundle (incl. the CUDA caster-LLM build on GPU machines) so every
        // downloading asset shows its progress here.
        var bundle = plugin.Assets.BundleForEngine(target, plugin.Configuration.LlmEnabled,
            withCudaLlm: plugin.Hardware?.HasNvidiaGpu == true);
        foreach (var a in bundle)
        {
            var st = plugin.Downloads.IsInstalled(a.Id);
            var mark = st switch { true => "✓", false => "•", _ => "…" };
            ImGui.TextColored(st == true ? Ui.Good : Ui.Muted, $"  {mark} {a.Id}");
            ImGui.SameLine(); ImGui.TextDisabled($"({a.Size / (1024 * 1024)} MB)");

            var prog = plugin.Downloads.ProgressFor(a.Id);
            if (prog != null && prog.Phase is not DownloadPhase.Done && st != true)
            {
                ImGui.SameLine();
                ImGui.ProgressBar((float)prog.Fraction, new Vector2(160, 0), prog.Phase.ToString());
            }
        }
        if (plugin.Downloads.LastError != null)
            ImGui.TextColored(Ui.Bad, "Error: " + plugin.Downloads.LastError);
    }
}
