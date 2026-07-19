using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using PopotoVox.Infrastructure;
using PopotoVox.Security;
using PopotoVox.Tts;

namespace PopotoVox.Windows;

/// <summary>
/// First-run guided setup: a tight, preset-first flow — Welcome → Choose quality (Low/Medium/High/
/// Ultra) → Download. It detects the user's GPU/CPU and recommends the best preset their machine can
/// actually run well, showing each preset's recommended hardware and a live "will this suit my PC?"
/// verdict. Smart casting is folded into the preset (no separate step). Styled with the shared
/// <see cref="Ui"/> helpers to match the rest of the shell.
/// </summary>
public sealed class SetupWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    private int step;                       // 0 welcome · 1 quality · 2 download
    private QualityPreset chosen;

    private volatile HardwareInfo? hw;      // null until the async probe completes
    private bool probeStarted;
    private bool userPicked;                // once true, stop auto-syncing to the recommendation

    public SetupWindow(Plugin plugin)
        : base("PopotoVox — Setup###PopotoVoxSetup", ImGuiWindowFlags.NoCollapse)
    {
        this.plugin = plugin;
        chosen = plugin.Configuration.DetectQualityPreset();
        if (chosen == QualityPreset.Custom) chosen = QualityPreset.Medium;
        Size = new Vector2(600, 640);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() { }

    /// <summary>Reset to the first page (used when re-opening the wizard from Settings).</summary>
    public void Restart()
    {
        step = 0;
        chosen = plugin.Configuration.DetectQualityPreset();
        if (chosen == QualityPreset.Custom) chosen = QualityPreset.Medium;
        IsOpen = true;
    }

    public override void Draw()
    {
        StartProbe();
        DrawStepHeader();
        ImGui.Separator();

        switch (step)
        {
            case 0: DrawWelcome(); break;
            case 1: DrawChooseQuality(); break;
            default: DrawDownload(); break;
        }
    }

    private void StartProbe()
    {
        if (probeStarted) return;
        probeStarted = true;
        _ = HardwareProbe.DetectAsync(plugin.Paths.Assets).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully) hw = t.Result;
        });
    }

    private void DrawStepHeader()
    {
        string[] titles = { "Welcome", "Choose quality", "Download" };
        for (var i = 0; i < titles.Length; i++)
        {
            if (i > 0) { ImGui.SameLine(); ImGui.TextDisabled("›"); ImGui.SameLine(); }
            var col = i == step ? Ui.Accent : i < step ? Ui.Good : Ui.Muted;
            ImGui.TextColored(col, $"{i + 1}. {titles[i]}");
        }
    }

    // ---------------------------------------------------------------- step 0

    private void DrawWelcome()
    {
        Ui.Heading("PopotoVox 🎙️");
        Ui.Paragraph(
            "Every NPC you talk to gets their own distinct, spoken voice — generated live on your own " +
            "PC as dialogue appears.");
        ImGui.Spacing();
        Bullet("100% local & free — no account, no cloud, no subscriptions.");
        Bullet("Each NPC keeps the same voice every time you meet them.");
        Bullet("Voices run in a separate helper, so a hiccup can never crash the game.");
        ImGui.Spacing();
        Ui.Paragraph("This quick setup picks a quality level and downloads it once. Takes about a " +
            "minute — and you can change everything later in Settings.");

        FooterButtons(showBack: false, nextLabel: "Get started →", onNext: GoToQuality, allowSkip: true);
    }

    private void GoToQuality()
    {
        userPicked = false;          // page auto-selects the recommended preset
        chosen = Recommended();
        step = 1;
    }

    private void GoToDownload()
    {
        plugin.Configuration.ApplyQualityPreset(chosen);
        plugin.Configuration.Save();
        step = 2;
    }

    private static void Bullet(string text)
    {
        ImGui.TextColored(Ui.Good, "  ✓");
        ImGui.SameLine();
        Ui.Paragraph(text);
    }

    // ---------------------------------------------------------------- step 1

    private static readonly QualityPreset[] PresetOrder =
        { QualityPreset.Low, QualityPreset.Medium, QualityPreset.High, QualityPreset.Ultra };

    private void DrawChooseQuality()
    {
        // Until the user clicks a tile, keep the selection on the recommendation (which updates
        // once the async hardware probe completes).
        if (!userPicked) chosen = Recommended();

        ImGui.Spacing();
        ImGui.TextColored(Ui.Accent, HardwareLine());
        ImGui.TextDisabled($"Recommended for your PC: {PresetName(Recommended())}.");
        ImGui.Spacing();

        DrawSelector();
        DrawPresetDetail(chosen);

        FooterButtons(showBack: true, onBack: () => step = 0, nextLabel: "Continue →", onNext: GoToDownload);
    }

    // A segmented Low/Medium/High/Ultra row; the recommended one is starred.
    private void DrawSelector()
    {
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var w = (ImGui.GetContentRegionAvail().X - spacing * (PresetOrder.Length - 1)) / PresetOrder.Length;
        for (var i = 0; i < PresetOrder.Length; i++)
        {
            if (i > 0) ImGui.SameLine();
            var p = PresetOrder[i];
            var selected = chosen == p;
            var label = PresetName(p) + (Recommended() == p ? "  ★" : "");

            if (selected)
            {
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(Ui.Accent.X, Ui.Accent.Y, Ui.Accent.Z, 0.35f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(Ui.Accent.X, Ui.Accent.Y, Ui.Accent.Z, 0.45f));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(Ui.Accent.X, Ui.Accent.Y, Ui.Accent.Z, 0.55f));
            }
            if (ImGui.Button($"{label}##seg{(int)p}", new Vector2(w, 0)))
            {
                chosen = p;
                userPicked = true;
            }
            if (selected) ImGui.PopStyleColor(3);
        }
        ImGui.Spacing();
    }

    // Details for just the selected preset.
    private void DrawPresetDetail(QualityPreset p)
    {
        var engine = TtsEngineCatalog.Get(PresetEngine(p));
        var bundle = BundleFor(p);
        var mb = bundle.Sum(a => a.Size) / (1024 * 1024);
        var installed = PresetInstalled(p, bundle);

        Ui.BeginCard(QualityIcon(p), PresetName(p));

        var (fitText, fitColor) = Fit(p);
        Ui.Pill(fitText, fitColor);
        if (Recommended() == p) { ImGui.SameLine(0, 8f); ImGui.TextColored(Ui.Good, "★ Recommended"); }
        if (installed) { ImGui.SameLine(0, 8f); ImGui.TextColored(Ui.Good, "✓ Installed"); }

        ImGui.TextDisabled($"{engine.DisplayName} · {(engine.RequiresGpu ? "NVIDIA GPU" : "CPU")} · ~{mb} MB download");
        Ui.Paragraph(PresetBlurb(p));
        ImGui.TextDisabled("Recommended hardware: " + PresetHardware(p));

        Ui.EndCard();
    }

    // ---------------------------------------------------------------- step 2

    private void DrawDownload()
    {
        if (!plugin.Assets.Available)
        {
            ImGui.Spacing();
            ImGui.TextColored(Ui.Bad, "Downloads are unavailable — the asset manifest failed verification.");
            Ui.Paragraph(plugin.Assets.UnavailableReason ?? "");
            FooterButtons(showBack: true, onBack: () => step = 1, nextLabel: null);
            return;
        }

        var engine = TtsEngineCatalog.Get(PresetEngine(chosen));
        var bundle = BundleFor(chosen);
        var downloaded = bundle.All(a => plugin.Downloads.IsInstalled(a.Id) == true);
        var installed = downloaded && plugin.EngineRuntimePresent(PresetEngine(chosen));
        var totalMb = bundle.Sum(a => a.Size) / (1024 * 1024);

        Ui.Heading($"Set up {PresetName(chosen)}");
        Ui.Paragraph("Everything below is verified by SHA-256 against a signed manifest before it's used.");
        ImGui.Spacing();

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
                ImGui.ProgressBar((float)prog.Fraction, new Vector2(180, 0), prog.Phase.ToString());
            }
        }

        ImGui.Spacing(); ImGui.Separator(); ImGui.Spacing();

        if (installed)
        {
            Ui.Banner(FontAwesomeIcon.CheckCircle,
                "All set! Click Finish and your NPC voices go live right away — no reload needed.", Ui.Good);
            if (engine.RequiresGpu)
                ImGui.TextDisabled($"{engine.DisplayName} loads its model onto your GPU first — the progress window shows it warming up.");
        }
        else if (downloaded)
        {
            // Everything downloadable is here but the engine still can't run (Ultra until its packaged
            // runtime ships) — say so honestly instead of pretending it's ready.
            Ui.Banner(FontAwesomeIcon.ExclamationTriangle,
                $"{engine.DisplayName} isn't available as a download yet — it currently needs a manual " +
                "install. Pick another quality to get talking now; Ultra ships as a one-click download soon.", Ui.Warn);
        }
        else if (plugin.Downloads.Busy)
        {
            ImGui.TextColored(Ui.Warn, "Downloading… you can leave this window open.");
            ImGui.SameLine();
            if (ImGui.Button("Cancel")) plugin.Downloads.Cancel();
        }
        else
        {
            if (ImGui.Button($"Download everything  (~{totalMb} MB)", new Vector2(-1, 0)))
                plugin.Downloads.StartDownload(bundle);
        }

        if (plugin.Downloads.LastError != null)
            ImGui.TextColored(Ui.Bad, "Error: " + plugin.Downloads.LastError);

        FooterButtons(
            showBack: !plugin.Downloads.Busy, onBack: () => step = 1,
            nextLabel: installed ? "Finish" : null, onNext: FinishAndGoLive);
    }

    // ---------------------------------------------------------------- hardware

    private string HardwareLine()
    {
        if (hw is not { } h) return "Detecting your hardware…";
        var gpu = h.HasNvidiaGpu ? $"{h.GpuName} · {h.GpuVramMb / 1024} GB VRAM" : "No NVIDIA GPU";
        return $"Your PC: {gpu} · {h.CpuCores} cores";
    }

    private QualityPreset Recommended()
    {
        // Ultra (VoxCPM2) isn't auto-recommended until it ships as a verified download (it runs from a
        // dev config for now). High (Kokoro) is CPU-only — recommend it on any reasonably modern
        // machine; Medium (Piper) stays the pick for low-core CPUs where Kokoro would lag behind.
        if (hw is { } h && h.CpuCores >= 8) return QualityPreset.High;
        return QualityPreset.Medium;
    }

    // The "will this run well on my PC?" verdict, compared against the detected hardware.
    private (string Text, Vector4 Color) Fit(QualityPreset p)
    {
        if (hw is not { } h) return ("checking…", Ui.Muted);
        switch (p)
        {
            case QualityPreset.Low:
                return ("Great on your PC", Ui.Good);
            case QualityPreset.Medium:
                return h.CpuCores < 4 ? ("Works — AI casting may be slower", Ui.Warn) : ("Great on your PC", Ui.Good);
            case QualityPreset.High:
                // Kokoro runs entirely on the CPU (the old GPU requirement here described a deleted engine).
                return h.CpuCores < 6 ? ("Works — voices take a bit longer on fewer cores", Ui.Warn)
                                      : ("Great on your PC", Ui.Good);
            case QualityPreset.Ultra:
                if (!h.HasNvidiaGpu) return ("Needs an NVIDIA GPU", Ui.Bad);
                if (DriverOld(h)) return ("Update your NVIDIA driver", Ui.Warn);
                if (h.GpuVramMb < 8000) return ("Needs ~8 GB VRAM", Ui.Bad);
                if (h.FreeDiskMb is > 0 and < 13000)
                    return ($"Needs ~13 GB free — you have {h.FreeDiskMb / 1024} GB", Ui.Warn);
                return ("Great on your PC", Ui.Good);
            default:
                return ("", Ui.Muted);
        }
    }

    // The bundled CUDA build needs a reasonably recent driver; warn well below the CUDA 12.x floor.
    private static bool DriverOld(HardwareInfo h)
    {
        if (string.IsNullOrEmpty(h.GpuDriver)) return false;
        var major = h.GpuDriver.Split('.')[0];
        return int.TryParse(major, out var m) && m > 0 && m < 525;
    }

    // ---------------------------------------------------------------- preset metadata

    private List<AssetEntry> BundleFor(QualityPreset p) =>
        plugin.Assets.BundleForEngine(PresetEngine(p),
            withLlm: p != QualityPreset.Low,                 // Medium/High/Ultra use smart casting
            withCudaLlm: hw?.HasNvidiaGpu == true);          // GPU machines get the CUDA caster-LLM build

    /// <summary>Bundle installed AND the engine's non-downloadable runtime present (VoxCPM2 dev-config
    /// until its packaged download ships) — the one honest "it will actually run" answer.</summary>
    private bool PresetInstalled(QualityPreset p, List<AssetEntry> bundle) =>
        bundle.All(a => plugin.Downloads.IsInstalled(a.Id) == true)
        && plugin.EngineRuntimePresent(PresetEngine(p));

    private static TtsEngineChoice PresetEngine(QualityPreset p) => p switch
    {
        QualityPreset.Low => TtsEngineChoice.Piper,
        QualityPreset.Medium => TtsEngineChoice.Piper,
        QualityPreset.Ultra => TtsEngineChoice.VoxCPM2,
        _ => TtsEngineChoice.Kokoro, // High
    };

    private static string PresetName(QualityPreset p) => p switch
    {
        QualityPreset.Low => "Low",
        QualityPreset.Medium => "Medium",
        QualityPreset.High => "High",
        QualityPreset.Ultra => "Ultra",
        _ => "Custom",
    };

    private static string PresetHardware(QualityPreset p) => p switch
    {
        QualityPreset.Low => "Any CPU",
        QualityPreset.Medium => "Any modern CPU (4+ cores ideal)",
        QualityPreset.High => "Any modern CPU (6+ cores ideal) — no GPU needed",
        QualityPreset.Ultra => "NVIDIA GPU · 8 GB+ VRAM",
        _ => "",
    };

    private static string PresetBlurb(QualityPreset p) => p switch
    {
        QualityPreset.Low => "Fastest and lightest. Clear but a bit robotic. No AI casting.",
        QualityPreset.Medium => "Natural voices across nine real-world accents, with a local AI picking " +
            "a fitting voice for each NPC. The best starting point for most people.",
        QualityPreset.High => "Natural, lifelike speech with AI casting — no graphics card needed.",
        QualityPreset.Ultra => "The most lifelike — NPCs can sigh, laugh and emote line by line. The " +
            "heaviest option; per-line emotion adds a little delay.",
        _ => "",
    };

    private static FontAwesomeIcon QualityIcon(QualityPreset p) => p switch
    {
        QualityPreset.Low => FontAwesomeIcon.FeatherAlt,
        QualityPreset.Medium => FontAwesomeIcon.Microchip,
        QualityPreset.High => FontAwesomeIcon.Bolt,
        QualityPreset.Ultra => FontAwesomeIcon.Star,
        _ => FontAwesomeIcon.Gauge,
    };

    // ---------------------------------------------------------------- shared footer

    private void FooterButtons(
        bool showBack, Action? onBack = null, string? nextLabel = null,
        Action? onNext = null, bool allowSkip = false)
    {
        ImGui.Spacing(); ImGui.Separator(); ImGui.Spacing();

        if (showBack)
        {
            if (ImGui.Button("← Back")) onBack?.Invoke();
            ImGui.SameLine();
        }

        if (allowSkip)
        {
            if (ImGui.Button("Skip setup")) Finish();
            ImGui.SameLine();
        }

        if (nextLabel != null)
        {
            var w = ImGui.CalcTextSize(nextLabel).X + 28;
            ImGui.SameLine(ImGui.GetWindowWidth() - w - 12);
            if (ImGui.Button(nextLabel)) onNext?.Invoke();
        }
    }

    private void Finish()
    {
        plugin.Configuration.SetupCompleted = true;
        plugin.Configuration.Save();
        IsOpen = false;
        plugin.Shell.IsOpen = true;
    }

    /// <summary>Download-page Finish: close the wizard AND bring the chosen engine live right away (the
    /// same hot-swap machinery as Settings → Apply — warm-up + progress window, no reload). "Skip setup"
    /// uses plain <see cref="Finish"/> so skipping never kicks off downloads or a swap.</summary>
    private void FinishAndGoLive()
    {
        Finish();
        plugin.RequestEngineTransition(plugin.Configuration.TtsEngine);
    }
}
