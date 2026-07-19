using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using PopotoVox.Casting;
using PopotoVox.Tts;

namespace PopotoVox.Windows;

/// <summary>
/// System: a single dashboard — <b>Live activity</b> (the pipeline flow diagram + a friendly "try it"
/// playground), and <b>System status</b> (one screenshot-ready section that rolls together what's
/// running, the install/version details, and the raw debug views for bug reports).
/// </summary>
public sealed class SystemPage : IShellPage
{
    private static readonly Vector4 Accent = new(0.62f, 0.82f, 1f, 1f);
    private static readonly Vector4 Good = new(0.6f, 1f, 0.6f, 1f);
    private static readonly Vector4 Warn = new(1f, 0.85f, 0.4f, 1f);
    private static readonly Vector4 Bad = new(1f, 0.5f, 0.4f, 1f);
    private static readonly Vector4 Muted = new(0.7f, 0.7f, 0.7f, 1f);
    private static readonly TimeSpan Linger = TimeSpan.FromSeconds(1.8);

    private readonly Plugin plugin;
    private string testText = "Well met, traveler. The road ahead is long.";

    public SystemPage(Plugin plugin) => this.plugin = plugin;

    public ShellSection Section => ShellSection.System;
    public string Label => "System";
    public FontAwesomeIcon Icon => FontAwesomeIcon.Heartbeat;

    public void Draw()
    {
        DrawActivity();
        DrawSystemStatus();
    }

    // ================================================================ Live activity + try-it

    private readonly record struct FlowNode(
        CastingState.PipelineStage Stage, string Label, FontAwesomeIcon Icon, string Sub, Vector4 Color, bool OneShot = false);

    private List<FlowNode> BuildNodes()
    {
        var cfg = plugin.Configuration;
        var engine = TtsEngineCatalog.Get(plugin.ActiveEngineId);
        var smartCasting = cfg.LlmEnabled && plugin.LlmInstalled;
        var emotionOn = engine.SupportsEmotion && cfg.EmotionAnnotation && cfg.LlmEnabled;
        var isUltra = plugin.ActiveEngineId == TtsEngineChoice.VoxCPM2;

        var purple = new Vector4(0.66f, 0.55f, 0.95f, 1f);
        var amber = new Vector4(1f, 0.70f, 0.35f, 1f);
        var green = new Vector4(0.55f, 0.85f, 0.55f, 1f);
        var pink = new Vector4(0.95f, 0.62f, 0.85f, 1f);

        // Nodes appear/disappear with the live settings, so the diagram teaches what each tier does:
        // Low (rules, no emotion) = Capture/Voice/Play; Medium adds Cast; High adds Emotion; Ultra adds Design.
        var nodes = new List<FlowNode>
        {
            new(CastingState.PipelineStage.Capturing, "Capture", FontAwesomeIcon.CommentDots, "dialogue", Accent),
        };
        if (smartCasting) nodes.Add(new(CastingState.PipelineStage.Casting, "Cast", FontAwesomeIcon.Brain, "AI (Qwen)", purple));
        if (emotionOn) nodes.Add(new(CastingState.PipelineStage.Annotating, "Emotion", FontAwesomeIcon.Smile, "AI (Qwen)", amber));
        if (isUltra) nodes.Add(new(CastingState.PipelineStage.Designing, "Design", FontAwesomeIcon.PaintBrush, "once per NPC", pink, OneShot: true));
        nodes.Add(new(CastingState.PipelineStage.Rendering, "Voice", FontAwesomeIcon.VolumeUp, engine.DisplayName, green));
        nodes.Add(new(CastingState.PipelineStage.Playing, "Play", FontAwesomeIcon.Play, "", Good));
        return nodes;
    }

    private bool Lit(CastingState.PipelineStage s)
    {
        var t = plugin.CastingState.StageAt(s);
        return t.HasValue && DateTime.UtcNow - t.Value < Linger;
    }

    private void DrawActivity()
    {
        Ui.BeginCard(FontAwesomeIcon.WaveSquare, "Live activity");

        DrawFlow();

        var nodes = BuildNodes();
        var anyLit = nodes.Any(nd => Lit(nd.Stage));
        var speaker = plugin.CastingState.ActivitySpeaker;
        if (anyLit && !string.IsNullOrEmpty(speaker))
            ImGui.TextColored(Accent, $"Processing: {speaker}");
        else
            ImGui.TextDisabled("Idle — waiting for dialogue.");

        ImGui.Spacing(); ImGui.Separator(); ImGui.Spacing();

        DrawTryIt();

        Ui.EndCard();
    }

    private void DrawFlow()
    {
        var nodes = BuildNodes();
        var n = nodes.Count;
        const float radius = 22f;
        var labelH = ImGui.GetTextLineHeight();
        var origin = ImGui.GetCursorScreenPos();
        var avail = ImGui.GetContentRegionAvail().X;
        var slot = avail / n;
        var cy = origin.Y + radius;
        var draw = ImGui.GetWindowDrawList();
        var lineCol = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.18f));
        var pulse = 0.7f + 0.3f * (float)Math.Sin(ImGui.GetTime() * 4.0);

        for (var i = 0; i < n - 1; i++)
        {
            var x1 = origin.X + slot * (i + 0.5f) + radius;
            var x2 = origin.X + slot * (i + 1.5f) - radius;
            draw.AddLine(new Vector2(x1, cy), new Vector2(x2, cy), lineCol, 2f);
        }

        for (var i = 0; i < n; i++)
        {
            var node = nodes[i];
            var cx = origin.X + slot * (i + 0.5f);
            var center = new Vector2(cx, cy);
            var lit = Lit(node.Stage);

            Vector4 fill, border, iconCol;
            if (lit)
            {
                fill = new Vector4(node.Color.X, node.Color.Y, node.Color.Z, 0.30f + 0.5f * pulse);
                border = node.Color;
                iconCol = node.Color;
            }
            else if (node.OneShot)
            {
                // A one-time step (Ultra's per-NPC design): when idle it reads as "done / cached" — a
                // calm settled tint in its own colour, NOT the empty idle look, so it never looks broken.
                fill = new Vector4(node.Color.X, node.Color.Y, node.Color.Z, 0.10f);
                border = new Vector4(node.Color.X, node.Color.Y, node.Color.Z, 0.45f);
                iconCol = new Vector4(node.Color.X, node.Color.Y, node.Color.Z, 0.8f);
            }
            else
            {
                fill = new Vector4(1f, 1f, 1f, 0.05f);
                border = new Vector4(1f, 1f, 1f, 0.2f);
                iconCol = new Vector4(0.8f, 0.8f, 0.85f, 1f);
            }
            draw.AddCircleFilled(center, radius, ImGui.ColorConvertFloat4ToU32(fill));
            draw.AddCircle(center, radius, ImGui.ColorConvertFloat4ToU32(border), 0, lit ? 2.5f : 1.5f);

            using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
            {
                var glyph = node.Icon.ToIconString();
                var gs = ImGui.CalcTextSize(glyph);
                ImGui.SetCursorScreenPos(new Vector2(cx - gs.X / 2f, cy - gs.Y / 2f));
                ImGui.TextColored(iconCol, glyph);
            }

            CenteredText(node.Label, cx, cy + radius + 4f, new Vector4(0.9f, 0.9f, 0.93f, 1f));
            if (!string.IsNullOrEmpty(node.Sub))
                CenteredText(node.Sub, cx, cy + radius + 4f + labelH, Muted);
        }

        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new Vector2(avail, radius * 2f + 8f + labelH * 2f));
    }

    private static void CenteredText(string text, float centerX, float y, Vector4 color)
    {
        var w = ImGui.CalcTextSize(text).X;
        ImGui.SetCursorScreenPos(new Vector2(centerX - w / 2f, y));
        ImGui.TextColored(color, text);
    }

    // ---- the "try it" playground ----

    private void DrawTryIt()
    {
        var info = TtsEngineCatalog.Get(plugin.ActiveEngineId);
        var ready = plugin.Engine.IsReady;

        Ui.SubHeading("Try it out");
        Ui.Paragraph("Type a line and press Speak — PopotoVox walks it through each step above (lighting " +
            "up the flow), then plays it in your active voice. A quick way to preview a voice or check your setup.");
        ImGui.Spacing();

        ImGui.SetNextItemWidth(Math.Max(180f, ImGui.GetContentRegionAvail().X - 96f));
        ImGui.InputText("##testline", ref testText, 256);
        ImGui.SameLine();
        ImGui.BeginDisabled(!ready || string.IsNullOrWhiteSpace(testText));
        if (ImGui.Button("🔊 Speak"))
            plugin.Preview(new VoiceSpec
            {
                SpeakerId = 0,
                SpeakerName = "Test",
                Params = plugin.Configuration.GlobalVoiceParams(),
                Source = VoiceSource.Override,
            }, testText, showFlow: true);
        ImGui.EndDisabled();

        if (!ready)
        {
            ImGui.TextColored(Warn, "Install a voice first — see the Storage tab.");
            return;
        }

        // Quick-start examples.
        ImGui.Spacing();
        ImGui.TextDisabled("Examples:");
        ImGui.SameLine();
        Example("Greeting", "Well met, traveler. The road ahead is long.");
        ImGui.SameLine();
        Example("Dramatic", "Stay back! I shall not warn you again.");

        // Emotion coaching.
        ImGui.Spacing();
        if (info.SupportsEmotion)
        {
            // Ultra (VoxCPM2): emotion is performed automatically per line — nothing to place by hand.
            Ui.SubHeading("Performed emotion");
            Ui.Paragraph($"{info.DisplayName} designs a unique voice for each NPC, then performs every line with emotion " +
                "automatically — no need to place cues by hand.");
            if (!plugin.Configuration.EmotionAnnotation)
                ImGui.TextDisabled("Tip: turn on Auto-emotion (Settings) so each line is performed with feeling.");
        }
        else
        {
            Ui.Paragraph("This voice reads text plainly. Switch to Ultra for performed emotion — the AI directs " +
                "each line's delivery, and the designed voice acts it.");
        }
    }

    private void Example(string label, string line)
    {
        if (ImGui.SmallButton(label)) testText = line;
    }

    // ================================================================ System status (merged)

    private void DrawSystemStatus()
    {
        var info = TtsEngineCatalog.Get(plugin.ActiveEngineId);
        var cfg = plugin.Configuration;

        Ui.BeginCard(FontAwesomeIcon.Stethoscope, "System status");
        Ui.Paragraph("A full snapshot of your setup — handy to screenshot if you ever file a bug report.");
        ImGui.Spacing();

        // ---- Running now (colour-coded) ----
        Ui.SubHeading("Running now");
        if (plugin.Engine.IsReady)
            StatusRow("Voice engine", "Ready", Good, info.DisplayName);
        else
            StatusRow("Voice engine", "Not installed", Warn, $"{info.DisplayName} — install in Storage");

        if (!cfg.LlmEnabled)
            StatusRow("Smart casting AI", "Off", Muted, "voices picked by built-in rules");
        else if (plugin.LlmInstalled)
            StatusRow("Smart casting AI", "On", Good, "Qwen2.5-1.5B picks voices");
        else
            StatusRow("Smart casting AI", "Not installed", Warn,
                "every NPC uses a built-in rules voice — download Qwen2.5-1.5B in Storage to enable AI casting"
                + (plugin.ActiveEngineId == TtsEngineChoice.VoxCPM2 ? " (Ultra also needs it for voice design + emotion)" : ""));

        if (info.SupportsEmotion)
            StatusRow("Auto-emotion", cfg.EmotionAnnotation ? "On" : "Off",
                cfg.EmotionAnnotation ? Good : Muted,
                "performs each line's emotion");

        if (plugin.ActiveEngineId == TtsEngineChoice.VoxCPM2)
            StatusRow("Voice design", "Per NPC", Good, "a one-of-a-kind voice is designed for each NPC");

        var report = plugin.DialogueCapture.SanityReport;
        foreach (var src in plugin.DialogueCapture.Sources)
        {
            var (label, enabled) = MapSource(src.Name, cfg);
            var err = report.TryGetValue(src.Name, out var e) ? e : null;
            if (!enabled) StatusRow(label, "Off", Muted, "");
            else if (err != null) StatusRow(label, "Problem", Bad, err);
            else StatusRow(label, "Listening", Good, "");
        }

        ImGui.Spacing();

        // ---- Install details ----
        Ui.SubHeading("Install details");
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        InfoRow("Plugin", $"PopotoVox v{(v != null ? $"{v.Major}.{v.Minor}.{v.Build}" : "?")}");
        InfoRow("Voice engine", $"{info.DisplayName} · {info.Hardware} · " +
                                 (info.SpeakerCount > 0 ? $"{info.SpeakerCount} voices" : "designed per NPC") +
                                 (info.SupportsEmotion ? " · real emotion" : ""));
        InfoRow("Casting model", "Qwen2.5-1.5B-Instruct");

        ImGui.Spacing();

        // ---- Debug info ----
        Ui.SubHeading("Debug info");
        Ui.Paragraph("Recent pipeline activity — useful detail for bug reports.");
        ImGui.Spacing();
        DrawPlaybackLog();
        ImGui.Spacing();
        DrawRecentCaptures();

        Ui.EndCard();
    }

    private static (string Label, bool Enabled) MapSource(string srcName, Configuration cfg)
    {
        if (srcName.Contains("Ambient", StringComparison.OrdinalIgnoreCase) || srcName.Contains("Bubble", StringComparison.OrdinalIgnoreCase)) return ("Ambient chatter", cfg.CaptureMiniTalk);
        if (srcName.Contains("Battle", StringComparison.OrdinalIgnoreCase)) return ("Combat dialogue", cfg.CaptureAddonBattleTalk);
        if (srcName.Contains("Chat", StringComparison.OrdinalIgnoreCase)) return ("Chat log", cfg.CaptureChatGui);
        if (srcName.Contains("Talk", StringComparison.OrdinalIgnoreCase)) return ("Main dialogue", cfg.CaptureAddonTalk);
        return (srcName, true);
    }

    private static void StatusRow(string label, string state, Vector4 color, string detail)
    {
        ImGui.TextUnformatted(label);
        ImGui.SameLine(170f);
        Ui.Pill(state, color);
        if (!string.IsNullOrEmpty(detail)) { ImGui.SameLine(0, 8); ImGui.TextDisabled(detail); }
    }

    private static void InfoRow(string label, string value)
    {
        ImGui.TextDisabled($"{label}:");
        ImGui.SameLine(170f);
        ImGui.TextUnformatted(value);
    }

    private void DrawPlaybackLog()
    {
        var plays = plugin.Director.RecentPlays();
        InfoRow("Recent playbacks", plays.Length == 0 ? "none yet" : plays.Length.ToString());
        ImGui.PushStyleColor(ImGuiCol.Text, Muted);
        for (var i = plays.Length - 1; i >= 0 && i >= plays.Length - 8; i--)
            ImGui.TextWrapped($"    {plays[i].At.ToLocalTime():HH:mm:ss}  “{plays[i].Text}”");
        ImGui.PopStyleColor();
    }

    private void DrawRecentCaptures()
    {
        var events = plugin.DialogueCapture.Recent;
        InfoRow("Recent captures", events.Count == 0 ? "none yet" : events.Count.ToString());
        ImGui.PushStyleColor(ImGuiCol.Text, Muted);
        foreach (var evt in events.OrderByDescending(e => e.CapturedAt).Take(8))
            ImGui.TextWrapped($"    {evt.CapturedAt.ToLocalTime():HH:mm:ss}  {evt.Speaker} ({evt.Source})  “{evt.Text}”");
        ImGui.PopStyleColor();
    }
}
