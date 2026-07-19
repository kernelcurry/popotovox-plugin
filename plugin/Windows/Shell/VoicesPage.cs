using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using PopotoVox.Tts;

namespace PopotoVox.Windows;

/// <summary>
/// Settings: how NPCs sound and behave — a Quality preset chooser (the hero control), then Voice,
/// Smart casting, what PopotoVox reads, on-screen options, and a collapsed Advanced (technical) drawer.
/// Card-styled to match the rest of the shell. Installing voices lives in Storage.
/// </summary>
public sealed class VoicesPage : IShellPage
{
    private static readonly string[] CornerNames = { "Top-left", "Top-right", "Bottom-left", "Bottom-right" };
    private static readonly QualityPreset[] PresetOrder =
        { QualityPreset.Low, QualityPreset.Medium, QualityPreset.High, QualityPreset.Ultra };

    private readonly Plugin plugin;

    // Staged edits: the page edits a DRAFT copy of the config (a live preview), and nothing is applied to
    // the running plugin until the user clicks Apply. Apply commits the draft and — if the engine changed —
    // kicks off the live transition (download → warm → swap). Revert discards the draft.
    private Configuration? draft;
    private bool draftDirty;

    public VoicesPage(Plugin plugin) => this.plugin = plugin;

    public ShellSection Section => ShellSection.Voices;
    public string Label => "Settings";
    public FontAwesomeIcon Icon => FontAwesomeIcon.Cog;

    public void Draw()
    {
        // Re-clone whenever the draft matches what's live (fresh page, or just after Apply/Revert) so an
        // external change (Storage install, Setup) can't be silently overwritten by a stale draft on Apply.
        draft ??= plugin.Configuration.Clone();
        var cfg = draft;

        // The settings scroll INSIDE their own region so the action bar can be pinned to the bottom of the
        // section — you never have to scroll to the end to hit Apply. Reserve one button row for the footer.
        var footerH = ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y * 2f + 6f;
        if (ImGui.BeginChild("##voices_scroll", new Vector2(0, -footerH), false))
        {
            var dirty = false;
            DrawQualityCard(cfg, ref dirty);
            DrawSoundCard(cfg, ref dirty);
            DrawCastingCard(cfg, ref dirty);
            DrawBehaviorCard(cfg, ref dirty);
            DrawAdvanced(cfg, ref dirty);
            if (dirty) draftDirty = true;
        }
        ImGui.EndChild();

        DrawActionBar();
    }

    // The pinned action bar: Apply commits the draft into the live config (swapping the engine if it changed);
    // Revert discards unapplied edits back to the current settings; Reset to defaults stages the factory
    // defaults (you still confirm with Apply). Anchored to the bottom of the section so it's always in reach.
    private void DrawActionBar()
    {
        ImGui.Separator();

        // Apply — the primary action, tinted so it stands out; enabled only when there's something to apply.
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(Ui.Accent.X, Ui.Accent.Y, Ui.Accent.Z, draftDirty ? 0.55f : 0.20f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(Ui.Accent.X, Ui.Accent.Y, Ui.Accent.Z, 0.75f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(Ui.Accent.X, Ui.Accent.Y, Ui.Accent.Z, 0.9f));
        ImGui.BeginDisabled(!draftDirty);
        if (ImGui.Button("Apply changes"))
        {
            plugin.ApplySettings(draft!);
            draft = plugin.Configuration.Clone();
            draftDirty = false;
        }
        ImGui.EndDisabled();
        ImGui.PopStyleColor(3);

        ImGui.SameLine();
        ImGui.BeginDisabled(!draftDirty);
        if (ImGui.Button("Revert"))
        {
            draft = plugin.Configuration.Clone(); // discard unapplied edits → back to the current settings
            draftDirty = false;
        }
        ImGui.EndDisabled();
        if (draftDirty && ImGui.IsItemHovered())
            ImGui.SetTooltip("Discard unapplied changes and go back to your current settings.");

        ImGui.SameLine();
        if (draftDirty) ImGui.TextColored(Ui.Warn, "You have unapplied changes.");
        else ImGui.TextDisabled("All changes applied.");

        // Reset to defaults — secondary, right-aligned. Stages the factory defaults into the draft (preserving
        // the setup-done + version flags so it doesn't re-trigger the wizard); the user still confirms via Apply.
        const string resetLabel = "Reset to defaults";
        var resetW = ImGui.CalcTextSize(resetLabel).X + ImGui.GetStyle().FramePadding.X * 2f;
        ImGui.SameLine(Math.Max(ImGui.GetCursorPosX(), ImGui.GetContentRegionMax().X - resetW));
        if (ImGui.Button(resetLabel))
        {
            draft = new Configuration { SetupCompleted = plugin.Configuration.SetupCompleted, Version = plugin.Configuration.Version };
            draftDirty = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Restore every setting to its original default, then click Apply to use it.");
    }

    /// <summary>True if everything the draft's engine needs to actually run is present: its download
    /// bundle (engine assets + caster LLM when casting is on, incl. the CUDA build on GPU machines) AND
    /// any non-downloadable runtime (VoxCPM2's dev-config until its packaged download ships) — so Ultra
    /// can never read "Ready" on a machine where it would throw at render time.</summary>
    private bool EngineBundleInstalled(TtsEngineChoice engine)
    {
        var bundle = plugin.Assets.BundleForEngine(engine,
            withLlm: draft?.LlmEnabled ?? plugin.Configuration.LlmEnabled,
            withCudaLlm: plugin.Hardware?.HasNvidiaGpu == true);
        return bundle.All(a => plugin.Downloads.IsInstalled(a.Id) == true)
               && plugin.EngineRuntimePresent(engine);
    }

    // ================================================================ Quality (hero)

    private void DrawQualityCard(Configuration cfg, ref bool dirty)
    {
        Ui.BeginCard(FontAwesomeIcon.Gauge, "Quality");
        Ui.Paragraph("Pick a preset to set the voice engine, smart casting and emotion in one go — " +
            "from fastest-and-lightest to most lifelike. Fine-tune anything below to make your own mix.");
        ImGui.Spacing();

        var current = cfg.DetectQualityPreset();
        const float tileW = 150f, tileH = 96f, gap = 8f;
        var perRow = Math.Max(1, (int)((ImGui.GetContentRegionAvail().X + gap) / (tileW + gap)));
        for (var i = 0; i < PresetOrder.Length; i++)
        {
            if (i % perRow != 0) ImGui.SameLine(0, gap);
            DrawPresetTile(cfg, PresetOrder[i], current, new Vector2(tileW, tileH), ref dirty);
        }

        ImGui.Spacing();
        DrawSetupSummary(cfg);

        if (current == QualityPreset.Custom)
        {
            ImGui.Spacing();
            ImGui.TextColored(Ui.Accent, "Custom — you've hand-tuned the settings below.");
        }

        // Pending-engine feedback (the old "reload to apply" banner is gone — Apply does it live). The draft
        // may differ from the running engine; explain what Apply will do.
        if (plugin.ActiveEngineId != cfg.TtsEngine)
        {
            var target = TtsEngineCatalog.Get(cfg.TtsEngine).DisplayName;
            var tr = plugin.Transition;
            if (tr.Active && tr.Target == cfg.TtsEngine)
                Ui.Banner(FontAwesomeIcon.SyncAlt, $"Switching to {target}…", Ui.Accent);
            else if (!EngineBundleInstalled(cfg.TtsEngine))
                Ui.Banner(FontAwesomeIcon.Download, $"{target} isn't installed yet — Apply will download it first.", Ui.Warn);
            else
                Ui.Banner(FontAwesomeIcon.SyncAlt, $"{target} selected — click Apply to switch to it (no reload).", Ui.Muted);
        }

        ImGui.Spacing();
        if (ImGui.SmallButton("New here? Run the setup wizard")) plugin.SetupWindow.Restart();

        Ui.EndCard();
    }

    /// <summary>A one-line "Your setup" chip row summarising the chosen engine, casting and reads.</summary>
    private void DrawSetupSummary(Configuration cfg)
    {
        var e = TtsEngineCatalog.Get(cfg.TtsEngine);
        ImGui.TextDisabled("Your setup");
        ImGui.SameLine(0, 10f); Ui.Pill(e.DisplayName, Ui.Accent);
        ImGui.SameLine(0, 6f); Ui.Pill(e.RequiresGpu ? "GPU" : "CPU", Ui.Muted);
        ImGui.SameLine(0, 6f);
        Ui.Pill(cfg.LlmEnabled ? "Smart casting" : "Rules only", cfg.LlmEnabled ? Ui.Good : Ui.Muted);
        if (e.SupportsEmotion)
        {
            ImGui.SameLine(0, 6f);
            Ui.Pill(cfg.EmotionAnnotation ? "Emotion" : "No emotion", cfg.EmotionAnnotation ? Ui.Good : Ui.Muted);
        }
        ImGui.SameLine(0, 6f); Ui.Pill("Reads: " + ReadsSummary(cfg), Ui.Muted);
    }

    private static string ReadsSummary(Configuration cfg)
    {
        var parts = new[]
        {
            cfg.CaptureAddonTalk ? "dialogue" : null,
            cfg.CaptureAddonBattleTalk ? "battle" : null,
            cfg.CaptureChatGui ? "chat" : null,
            cfg.CaptureMiniTalk ? "ambient" : null,
        }.Where(s => s != null);
        var joined = string.Join(", ", parts);
        return joined.Length > 0 ? joined : "nothing";
    }

    private void DrawPresetTile(Configuration cfg, QualityPreset preset, QualityPreset current, Vector2 size,
        ref bool dirty)
    {
        var (name, engine, hardware, emotion, _) = PresetMeta(preset);
        // Check the whole bundle (engine + caster LLM, incl. the CUDA build on GPU machines), matching
        // the setup wizard — plus the engine's non-downloadable runtime (VoxCPM2 dev-config), so Ultra
        // never reads installed on a machine where it can't actually render.
        var bundle = plugin.Assets.BundleForEngine(engine, withLlm: preset != QualityPreset.Low,
            withCudaLlm: plugin.Hardware?.HasNvidiaGpu == true);
        var installed = bundle.All(a => plugin.Downloads.IsInstalled(a.Id) == true)
                        && plugin.EngineRuntimePresent(engine);
        var selected = current == preset;

        var p0 = ImGui.GetCursorScreenPos();
        var hovered = ImGui.IsMouseHoveringRect(p0, p0 + size);
        var draw = ImGui.GetWindowDrawList();
        var bg = selected ? new Vector4(Ui.Accent.X, Ui.Accent.Y, Ui.Accent.Z, 0.18f)
               : hovered ? new Vector4(1f, 1f, 1f, 0.06f) : new Vector4(1f, 1f, 1f, 0.02f);
        draw.AddRectFilled(p0, p0 + size, ImGui.ColorConvertFloat4ToU32(bg), 6f);
        draw.AddRect(p0, p0 + size, ImGui.ColorConvertFloat4ToU32(selected ? Ui.Accent : new Vector4(1, 1, 1, 0.13f)), 6f);

        ImGui.SetCursorScreenPos(p0 + new Vector2(9, 7));
        ImGui.BeginGroup();
        Ui.Scaled(1.1f, () => ImGui.TextUnformatted(name));
        Ui.Pill(installed ? "Ready" : "Needs download", installed ? Ui.Good : Ui.Warn);
        ImGui.TextDisabled($"{TtsEngineCatalog.Get(engine).DisplayName} · {hardware}");
        ImGui.TextDisabled(emotion ? "AI casting + emotion" : preset == QualityPreset.Low ? "rules casting" : "AI casting");
        ImGui.EndGroup();

        ImGui.SetCursorScreenPos(p0);
        if (ImGui.InvisibleButton($"##preset{(int)preset}", size))
        {
            cfg.ApplyQualityPreset(preset); // mutates the draft only — committed on Apply
            dirty = true;
        }
    }

    private static (string Name, TtsEngineChoice Engine, string Hardware, bool Emotion, bool Recommended)
        PresetMeta(QualityPreset p) => p switch
    {
        QualityPreset.Low => ("Low", TtsEngineChoice.Piper, "CPU", false, false),
        QualityPreset.Medium => ("Medium", TtsEngineChoice.Piper, "CPU", false, false),
        QualityPreset.High => ("High", TtsEngineChoice.Kokoro, "CPU", false, true),
        QualityPreset.Ultra => ("Ultra", TtsEngineChoice.VoxCPM2, "GPU", true, false),
        _ => ("Custom", TtsEngineChoice.Kokoro, "", false, false),
    };

    // ================================================================ Sound

    private void DrawSoundCard(Configuration cfg, ref bool dirty)
    {
        Ui.BeginCard(FontAwesomeIcon.VolumeUp, "Sound");

        var volPct = (int)MathF.Round(cfg.Volume * 100f);
        if (ImGui.SliderInt("Volume", ref volPct, 0, 100, "%d%%"))
        { cfg.Volume = volPct / 100f; dirty = true; }

        var len = cfg.GlobalLengthScale;
        if (ImGui.SliderFloat("Speaking pace", ref len, 0.5f, 2.0f, "%.2fx"))
        { cfg.GlobalLengthScale = len; dirty = true; }
        ImGui.TextDisabled("Lower = faster, higher = slower. 1.0 is natural.");

        ImGui.Spacing();
        // Only offered on engines that can stream (Ultra) — gated like casting/emotion so future streaming
        // engines light it up automatically.
        var supportsStreaming = TtsEngineCatalog.Get(cfg.TtsEngine).SupportsStreaming;
        var stream = cfg.StreamAudio;
        ImGui.BeginDisabled(!supportsStreaming);
        if (ImGui.Checkbox("Stream audio (start speaking sooner)", ref stream)) { cfg.StreamAudio = stream; dirty = true; }
        ImGui.EndDisabled();
        if (supportsStreaming)
            ImGui.TextDisabled("Plays each line as it's generated instead of waiting for the whole clip — the voice " +
                               "starts seconds sooner. Great on a fast GPU; if it stutters, your card can't keep up — turn it off.");
        else
            ImGui.TextDisabled("Only available on an engine that can stream (Ultra).");

        // Several voice lines can play at once (overlapping ambient bubbles, etc.), each at its own distance
        // volume. This caps how many — applied live on the next line, no reload needed.
        var maxVoices = cfg.MaxConcurrentVoices;
        ImGui.SetNextItemWidth(220);
        if (ImGui.SliderInt("Overlapping voices", ref maxVoices, 0, 16, maxVoices == 0 ? "Auto" : "%d"))
        { cfg.MaxConcurrentVoices = maxVoices; dirty = true; }
        ImGui.TextDisabled("How many voice lines may play simultaneously. Auto sizes it to your CPU. " +
                           "Extra lines beyond the cap drop the quietest/least-important one.");

        ImGui.Spacing();
        if (ImGui.CollapsingHeader("Choose engine directly"))
            DrawEngineControls(cfg, ref dirty);

        Ui.EndCard();
    }

    // The manual engine picker + per-engine options. Most users stay on a preset; this is the
    // power-user escape hatch. Changes stage into the draft and take effect on Apply.
    private void DrawEngineControls(Configuration cfg, ref bool dirty)
    {
        Ui.Paragraph("Most people should just pick a Quality preset above. Choose an engine directly " +
            "only if you want a specific mix.");

        var engines = TtsEngineCatalog.All.OrderByDescending(e => e.Tier).ToList();
        var names = engines.Select(e =>
        {
            var hw = e.RequiresGpu ? "GPU" : "CPU";
            var emo = e.SupportsEmotion ? ", emotion" : "";
            return $"{e.DisplayName} ({hw}{emo})";
        }).ToArray();
        var idx = engines.FindIndex(e => e.Id == cfg.TtsEngine);
        if (idx < 0) idx = 0;
        if (ImGui.Combo("##engine", ref idx, names, names.Length)) { cfg.TtsEngine = engines[idx].Id; dirty = true; }

        var picked = engines[idx];
        ImGui.TextDisabled(picked.Summary);
        if (!EngineBundleInstalled(picked.Id))
            ImGui.TextColored(Ui.Warn, "Not installed — get it in Storage (or the setup wizard).");

        ImGui.Spacing();
        if (picked.Id == TtsEngineChoice.Piper)
        {
            var ns = cfg.GlobalNoiseScale;
            if (ImGui.SliderFloat("Expressiveness", ref ns, 0.0f, 1.5f)) { cfg.GlobalNoiseScale = ns; dirty = true; }
            ImGui.TextDisabled("Higher = more varied delivery; lower = flatter and steadier.");
            var nw = cfg.GlobalNoiseW;
            if (ImGui.SliderFloat("Cadence variation", ref nw, 0.0f, 1.5f)) { cfg.GlobalNoiseW = nw; dirty = true; }
            ImGui.TextDisabled("Variation in timing and rhythm between words.");
        }
        else
        {
            ImGui.TextDisabled("This engine has no extra options — it just works.");
        }
    }

    // ================================================================ Smart casting

    private void DrawCastingCard(Configuration cfg, ref bool dirty)
    {
        Ui.BeginCard(FontAwesomeIcon.Brain, "Smart casting (AI)");

        var castingRequired = cfg.CastingRequired;
        if (castingRequired && !cfg.LlmEnabled) { cfg.LlmEnabled = true; dirty = true; } // Kokoro/VoxCPM2 can't run without it
        var llm = cfg.LlmEnabled;
        ImGui.BeginDisabled(castingRequired);
        if (ImGui.Checkbox("Let a local AI pick each NPC's voice", ref llm)) { cfg.LlmEnabled = llm; dirty = true; }
        ImGui.EndDisabled();
        Ui.Paragraph("On: an AI reads each NPC's looks and gear to choose a fitting voice (and emotion on " +
            "expressive engines). Off: voices are assigned by built-in rules — still consistent, just less " +
            "tailored. Either way the voice always matches the character's gender.");
        if (castingRequired)
            ImGui.TextDisabled("Required for this engine — it uses the AI to pick or design each NPC's voice.");

        var temp = cfg.LlmTemperature;
        if (ImGui.SliderFloat("Creativity", ref temp, 0f, 1f, "%.2f")) { cfg.LlmTemperature = temp; dirty = true; }
        ImGui.TextDisabled("Low = safe, consistent choices; high = more adventurous casting.");

        var supportsEmotion = TtsEngineCatalog.Get(cfg.TtsEngine).SupportsEmotion;
        var emotion = cfg.EmotionAnnotation;
        ImGui.BeginDisabled(!supportsEmotion); // only enablable when the engine can perform emotion (Ultra)
        if (ImGui.Checkbox("Auto-emotion", ref emotion)) { cfg.EmotionAnnotation = emotion; dirty = true; }
        ImGui.EndDisabled();
        if (supportsEmotion)
            ImGui.TextDisabled("Performs each line's emotion for richer delivery. Costs an extra AI pass per line. On by default.");
        else
            ImGui.TextDisabled("Only available on an emotion-capable engine (Ultra).");

        var wait = cfg.CastWaitTimeoutSeconds;
        if (ImGui.SliderInt("First-line wait", ref wait, 5, 120, "%d s")) { cfg.CastWaitTimeoutSeconds = wait; dirty = true; }
        Ui.Paragraph("The first time you meet an NPC, their opening line is held silently while the AI " +
            "picks a voice (you'll see the casting indicator). This is the longest it will wait.");
        Ui.Paragraph("If the AI doesn't answer in time, a built-in rules voice (matched by gender/age) is " +
            "used so the line still plays — and casting runs only once, so that voice is then locked in for " +
            "this NPC. Because the AI's pick is almost always better, keep this generous enough that a slow " +
            "or cold model finishes. (You can always re-cast an NPC from the NPCs page.)");

        ImGui.Spacing(); ImGui.Separator(); ImGui.Spacing();

        var pre = (int)cfg.NpcPrecompute;
        string[] preNames = { "Off", "Nearby (a few)", "Mid (more)", "Whole zone" };
        ImGui.SetNextItemWidth(220);
        if (ImGui.Combo("Pre-build nearby voices", ref pre, preNames, preNames.Length))
        {
            cfg.NpcPrecompute = (PrecomputeRange)pre;
            dirty = true;
        }
        Ui.Paragraph("Builds nearby NPCs' voices in the background BEFORE you talk to them, so their first " +
            "line starts without the casting/design wait. It uses the GPU/AI in the background and always " +
            "yields to a line you're actually hearing. Off by default — turn it up if your PC has headroom " +
            "(wider reaches more NPCs, at the cost of more background work and disk).");
        ImGui.TextDisabled("Range: Nearby ≈ 15 yalms (a shop) · Mid ≈ 35 yalms (a plaza) · Whole zone = everything loaded.");

        Ui.EndCard();
    }

    // ================================================================ Behavior (reads + on-screen)

    private void DrawBehaviorCard(Configuration cfg, ref bool dirty)
    {
        Ui.BeginCard(FontAwesomeIcon.SlidersH, "Behavior");

        Ui.SubHeading("What's read");
        Ui.Paragraph("Choose which in-game text gets a voice. By default it reads the in-world dialogue boxes.");

        var a = cfg.CaptureAddonTalk;
        if (ImGui.Checkbox("Main dialogue (story / quest talk)", ref a)) { cfg.CaptureAddonTalk = a; dirty = true; }
        ImGui.TextDisabled("The main NPC conversation window. The recommended default.");

        var b = cfg.CaptureAddonBattleTalk;
        if (ImGui.Checkbox("Combat dialogue (mid-battle lines)", ref b)) { cfg.CaptureAddonBattleTalk = b; dirty = true; }
        ImGui.TextDisabled("The banner lines NPCs speak during battles and duties.");

        var c = cfg.CaptureChatGui;
        if (ImGui.Checkbox("Chat log (NPC lines in the chat box)", ref c)) { cfg.CaptureChatGui = c; dirty = true; }
        Ui.Paragraph("Off by default: the game echoes many dialogue-box lines to chat, so turning this on " +
            "can read the same line twice — once from the box, then again from the chat echo.");

        var amb = cfg.CaptureMiniTalk;
        if (ImGui.Checkbox("Ambient chatter (overhead NPC bubbles)", ref amb)) { cfg.CaptureMiniTalk = amb; dirty = true; }
        Ui.Paragraph("Off by default: voices the floating remarks NPCs make as you pass by, without clicking " +
            "them. Lively — but chatty and GPU-heavy in crowded cities (it self-limits with distance + dedup).");

        if (cfg.CaptureMiniTalk)
        {
            var fade = cfg.AmbientDistanceVolume;
            if (ImGui.Checkbox("Fade ambient volume with distance", ref fade)) { cfg.AmbientDistanceVolume = fade; dirty = true; }

            var hear = cfg.AmbientHearingYalms;
            ImGui.SetNextItemWidth(220);
            if (ImGui.SliderInt("Hearing distance", ref hear, 5, 50, "%d yalms")) { cfg.AmbientHearingYalms = hear; dirty = true; }
            ImGui.TextDisabled($"Full within ~3 yalms, then fades like a real voice (−6 dB per doubling) → silent at {Ui.DistanceHint(cfg.AmbientHearingYalms)}.");

            if (cfg.AmbientDistanceVolume)
            {
                var spatial = cfg.AmbientSpatialTracking;
                if (ImGui.Checkbox("Track movement (volume rises and falls as you walk past)", ref spatial))
                { cfg.AmbientSpatialTracking = spatial; dirty = true; }
            }

            var pra = cfg.PrerenderAmbientLines;
            if (ImGui.Checkbox("Pre-render ambient lines (instant; needs 'Pre-build nearby voices' on)", ref pra))
            { cfg.PrerenderAmbientLines = pra; dirty = true; }
            if (cfg.PrerenderAmbientLines)
            {
                var prd = cfg.PrerenderAmbientYalms;
                ImGui.SetNextItemWidth(220);
                if (ImGui.SliderInt("Pre-render within", ref prd, 5, 40, "%d yalms")) { cfg.PrerenderAmbientYalms = prd; dirty = true; }
                ImGui.TextDisabled($"Renders known bubble lines ahead for NPCs within {Ui.DistanceHint(cfg.PrerenderAmbientYalms)} — so they play instantly.");
            }
        }

        ImGui.Spacing();
        Ui.SubHeading("On screen");

        var ind = cfg.ShowCastingIndicator;
        if (ImGui.Checkbox("Show casting indicator", ref ind)) { cfg.ShowCastingIndicator = ind; dirty = true; }
        ImGui.TextDisabled("A small badge while a new NPC's voice is being chosen or generated.");

        var corner = (int)cfg.IndicatorPosition;
        if (ImGui.Combo("Indicator corner", ref corner, CornerNames, CornerNames.Length))
        { cfg.IndicatorPosition = (IndicatorCorner)corner; dirty = true; }

        var status = cfg.StatusMessages;
        if (ImGui.Checkbox("Status messages in chat", ref status)) { cfg.StatusMessages = status; dirty = true; }
        ImGui.TextDisabled("Prints brief casting notes (e.g. \"cast as speaker 12\") to your chat log.");

        Ui.EndCard();
    }

    // ================================================================ Advanced (technical)

    private void DrawAdvanced(Configuration cfg, ref bool dirty)
    {
        ImGui.Spacing();
        if (!ImGui.CollapsingHeader("Advanced (technical) — for troubleshooting, reload to apply"))
            return;

        ImGui.Spacing();
        Ui.SubHeading("Audio rendering");
        var maxRenders = cfg.MaxConcurrentRenders;
        if (ImGui.SliderInt("Simultaneous renders##mix", ref maxRenders, 1, 8)) { cfg.MaxConcurrentRenders = Math.Clamp(maxRenders, 1, 8); dirty = true; }
        ImGui.TextDisabled("How many lines synthesize at once. Keep at 1 for the GPU engine (VoxCPM2) — it " +
                           "shares one GPU process, so more just thrashes (playback still overlaps). CPU engines " +
                           "(Kokoro/Piper) can go higher. (Reload to apply.)");

        ImGui.Spacing();
        Ui.SubHeading("Smart-casting AI runtime");
        var lgl = cfg.LlmGpuLayers;
        if (ImGui.SliderInt("GPU layers (0 = auto)##llm", ref lgl, 0, 99)) { cfg.LlmGpuLayers = lgl; dirty = true; }
        ImGui.TextDisabled("Casting/emotion AI offload. 0 = auto (full GPU when a GPU build is installed, else CPU).");
        var threads = cfg.LlmThreads;
        if (ImGui.SliderInt("Threads (0 = auto)##llm", ref threads, 0, 32)) { cfg.LlmThreads = threads; dirty = true; }
        var ctx = cfg.LlmContextSize;
        if (ImGui.InputInt("Context size##llm", ref ctx)) { cfg.LlmContextSize = Math.Clamp(ctx, 512, 32768); dirty = true; }
        var lport = cfg.LlmPort;
        if (ImGui.InputInt("Loopback port##llm", ref lport)) { cfg.LlmPort = Math.Clamp(lport, 1024, 65535); dirty = true; }
    }
}
