using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using PopotoVox.Casting;

namespace PopotoVox.Windows;

/// <summary>
/// Small, corner-anchored overlay that shows what the user is waiting on (PRD D12):
/// an NPC being cast (one-time voice assignment) and/or a line being synthesized
/// ("Generating voice…", which on the GPU engine takes a few seconds and covers the one-time
/// model warm-up). Non-interactive and toggleable.
/// </summary>
public sealed class CastingIndicatorWindow : Window
{
    // Don't flash the overlay for sub-second renders (Kokoro/Piper); only show once a render
    // has clearly taken a moment. GPU renders (and the first-line warm-up) easily exceed this.
    private static readonly TimeSpan RenderVisibleAfter = TimeSpan.FromSeconds(0.4);
    private static readonly Vector2 EstimatedSize = new(280, 90);
    private const float Margin = 24f;

    private readonly Plugin plugin;

    public CastingIndicatorWindow(Plugin plugin)
        : base("PopotoVox Casting###PopotoVoxIndicator",
               ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
               ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoNav |
               ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoFocusOnAppearing)
    {
        this.plugin = plugin;
        RespectCloseHotkey = false;
        ForceMainWindow = true;
    }

    public override bool DrawConditions() =>
        plugin.Configuration.ShowCastingIndicator &&
        (plugin.CastingState.Any || plugin.CastingState.ActiveRenders(RenderVisibleAfter).Count > 0);

    public override void PreDraw()
    {
        var vp = ImGui.GetMainViewport();
        var area = vp.WorkSize;
        var origin = vp.WorkPos;

        var pos = plugin.Configuration.IndicatorPosition switch
        {
            IndicatorCorner.TopLeft => origin + new Vector2(Margin, Margin),
            IndicatorCorner.TopRight => origin + new Vector2(area.X - EstimatedSize.X - Margin, Margin),
            IndicatorCorner.BottomLeft => origin + new Vector2(Margin, area.Y - EstimatedSize.Y - Margin),
            _ => origin + new Vector2(area.X - EstimatedSize.X - Margin, area.Y - EstimatedSize.Y - Margin),
        };

        Position = pos;
        PositionCondition = ImGuiCond.Always;
    }

    public override void Draw()
    {
        ImGui.TextColored(new Vector4(0.7f, 0.85f, 1f, 1f), "PopotoVox");
        ImGui.Separator();

        // Simple animated ellipsis so the user can see it's alive, not stuck.
        var dots = new string('.', 1 + (int)(ImGui.GetTime() * 2) % 3);

        foreach (var speaker in plugin.CastingState.ActiveSpeakers)
            ImGui.Text($"Casting voice for {speaker}{dots}");

        // While the Ultra engine is designing an NPC's one-time voice, say so — it's a brief
        // per-NPC step, not a stall. Otherwise it's the normal per-line synthesis.
        var designedAt = plugin.CastingState.StageAt(CastingState.PipelineStage.Designing);
        var who = plugin.CastingState.ActivitySpeaker;
        foreach (var (speaker, seconds) in plugin.CastingState.ActiveRenders(RenderVisibleAfter))
        {
            var designing = designedAt is { } d && DateTime.UtcNow - d < TimeSpan.FromSeconds(3) && who == speaker;
            var verb = designing ? "Designing voice for" : "Generating voice for";
            ImGui.Text($"{verb} {speaker}{dots} ({seconds:F0}s)");
        }
    }
}
