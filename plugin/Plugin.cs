using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using PopotoVox.Casting;
using PopotoVox.Dialogue;
using PopotoVox.Infrastructure;
using PopotoVox.Llm;
using PopotoVox.Npc;
using PopotoVox.Pipeline;
using PopotoVox.Presets;
using PopotoVox.Security;
using PopotoVox.Tts;
using PopotoVox.Windows;

namespace PopotoVox;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInterop { get; private set; } = null!;

    private const string MainCommand = "/popotovox";
    private const string ShortCommand = "/pvox";

    public readonly WindowSystem WindowSystem = new("PopotoVox");

    public Configuration Configuration { get; }
    public PluginPaths Paths { get; }

    // Subsystems
    public AssetService Assets { get; }
    public DownloadCoordinator Downloads { get; }

    /// <summary>Best-effort hardware probe result (GPU/VRAM/cores), filled by a background task shortly
    /// after load. Drives the hardware-aware download bundles (GPU machines also get the CUDA caster-LLM
    /// build) — null just means "not probed yet", which reads as CPU-only until it resolves.</summary>
    public HardwareInfo? Hardware { get; private set; }

    // The active engine lives behind a hot-swappable holder (no plugin reload on engine change). Engine,
    // ActiveEngineId and SpeakerCatalog delegate to the current binding, so every existing reader stays
    // swap-safe: each read is a single volatile snapshot of a consistent engine identity.
    private readonly ActiveEngine active;
    public ITtsEngine Engine => active.Current.Engine;
    public TtsEngineChoice ActiveEngineId => active.Current.Id;
    public SpeakerCatalog SpeakerCatalog => active.Current.Catalog;

    /// <summary>Observable state of a boot warm-up / Apply-driven engine swap (drives the progress window).</summary>
    public EngineTransition Transition { get; } = new();
    public VoiceSpecCache VoiceSpecs { get; }
    public IdentityCrossLink CrossLink { get; }
    public NpcRecordCache NpcRecords { get; }
    public OverrideStore Overrides { get; }
    public PresetStore Presets { get; }
    public LineAudioCache LineCache { get; }
    public CastingState CastingState { get; }
    public CastingDirector Director { get; }

    /// <summary>Whether the smart-casting LLM (runtime + model) is present on disk.</summary>
    public bool LlmInstalled => llm.Installed;

    // Windows
    public ShellWindow Shell { get; }
    public SetupWindow SetupWindow { get; }
    public CastingIndicatorWindow IndicatorWindow { get; }
    public EngineProgressWindow EngineProgress { get; }
    public DialogueCapture DialogueCapture { get; }

    private readonly string pluginDir;   // where the bundled host exes live (for rebuilding an engine on swap)
    private int swapping;                 // latch: one engine transition at a time

    private readonly AudioPlayer audio;
    private readonly LlamaServerClient llm;
    private readonly DialoguePipeline pipeline;
    private readonly NpcPrecomputeService precompute;
    private readonly DialogueProbe dialogueProbe; // M10 spike (dev: /pvox dialogueprobe|dialoguedump|dialoguestats)
    private readonly MiniTalkDialogueSource bubbleSource; // M15 (dev: /pvox bubbles)
    private readonly SpatialAudioTracker spatialTracker;  // M16: live volume as you move past ambient speakers

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        var configVersionBeforeMigration = Configuration.Version;
        MigrateConfiguration(Configuration);
        Paths = new PluginPaths(PluginInterface.GetPluginConfigDirectory());

        // --- Asset / security subsystem ---
        Assets = new AssetService(Paths, Log);
        Downloads = new DownloadCoordinator(Assets, Log);

        // Probe the hardware once in the background; download bundles become GPU-aware as soon as it
        // lands (the setup wizard runs its own probe for its verdicts — this one serves everything else).
        _ = HardwareProbe.DetectAsync(Paths.Assets).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully) Hardware = t.Result;
        });

        // --- TTS: pick the configured engine from the registry, auto-falling-back to
        // whichever engine is installed so audio keeps working until the chosen one downloads. ---
        pluginDir = PluginInterface.AssemblyLocation.DirectoryName ?? AppContext.BaseDirectory;
        var (initialId, initialEngine) = SelectEngine(Configuration.TtsEngine, Paths, pluginDir, Configuration);
        audio = new AudioPlayer(() => Configuration) { Volume = Configuration.Volume };

        // --- Identity + caches ---
        VoiceSpecs = new VoiceSpecCache(Paths.VoiceSpecCachePath);
        CrossLink = new IdentityCrossLink(Paths.CrossLinkPath);
        // v<5 cross-link entries were keyed by the old fingerprint formula (which mixed in npcId +
        // equipment models), so they can never match the new identity-stable fingerprint. Drop them
        // once so the store doesn't carry dead keys; it repopulates correctly as NPCs are re-met.
        if (configVersionBeforeMigration < 5 && CrossLink.Count > 0)
        {
            Log.Information($"[Npc] Clearing {CrossLink.Count} stale identity cross-links (fingerprint format changed).");
            CrossLink.Clear();
        }
        NpcRecords = new NpcRecordCache(Paths.NpcRecordCachePath);
        Overrides = new OverrideStore(Paths.OverridesPath);
        Presets = new PresetStore(Overrides, VoiceSpecs);
        LineCache = new LineAudioCache(Paths.LinesCache, () => Configuration, Log);

        var equipmentNamer = new EquipmentNamer(DataManager);
        var resolver = new NpcResolver(DataManager, ObjectTable, TargetManager, ClientState, equipmentNamer, Log);

        // --- LLM ---
        // Prefer the CUDA llama-server (the `llama-cuda` runtime) for the casting/emotion LLM so it runs on
        // the GPU (a 1.5B doing ~160 tokens of emotion tagging per line on CPU was the dominant per-line
        // delay). CPU-only users fall back to the CPU build with no offload. If the CUDA runtime installs
        // (or is removed) later, the BatchCompleted hook below re-resolves and restarts the server live.
        var llmOptions = BuildLlmOptions();
        Log.Information($"[LLM] casting/emotion LLM: {(llmOptions.NGpuLayers > 0 ? "GPU (CUDA build)" : "CPU build")}, gpuLayers={llmOptions.NGpuLayers}.");
        llm = new LlamaServerClient(llmOptions, enabled: true, Log);
        Downloads.BatchCompleted += OnDownloadsBatchCompleted;

        // --- Casting + pipeline ---
        CastingState = new CastingState();
        // Build the swappable engine identity now that the LLM exists (the emotion annotator needs it).
        // Everything that mirrors the active engine — voice catalog, matcher, annotator — travels in one
        // binding so a live swap rewires them all atomically.
        active = new ActiveEngine(BuildBinding(initialEngine, initialId));
        Director = new CastingDirector(
            VoiceSpecs, CrossLink, NpcRecords, Overrides, resolver, active,
            llm, audio, CastingState, ChatGui, Log, () => Configuration, LineCache);

        bubbleSource = new MiniTalkDialogueSource(Framework, ObjectTable, ClientState, () => Configuration, Log);
        DialogueCapture = new DialogueCapture(
            new IDialogueSource[]
            {
                new ChatGuiDialogueSource(ChatGui, ClientState),
                new AddonTalkDialogueSource(AddonLifecycle, ClientState),
                new AddonBattleTalkDialogueSource(AddonLifecycle, ClientState),
                bubbleSource,
            },
            Log);
        DialogueCapture.Start();
        var npcDialogueProbe = new NpcDialogueProbe(DataManager, Log);
        dialogueProbe = new DialogueProbe(npcDialogueProbe, TargetManager, ChatGui, Log);
        pipeline = new DialoguePipeline(DialogueCapture, resolver, Director, () => Configuration, dialogueProbe);
        precompute = new NpcPrecomputeService(Framework, resolver, Director, active, CastingState, () => Configuration,
            npcDialogueProbe, Log);
        spatialTracker = new SpatialAudioTracker(Framework, ObjectTable, audio, () => Configuration);
        Director.SpatialTracker = spatialTracker; // M16: follow the speaking NPC's live distance during ambient lines

        // --- Windows ---
        // One unified management shell (Home/Voices/Library/System) replaces the former separate
        // Main/Settings/Diag/Acknowledgements windows. The first-run Setup wizard and the casting
        // HUD overlay stay independent.
        Shell = new ShellWindow(this);
        SetupWindow = new SetupWindow(this);
        IndicatorWindow = new CastingIndicatorWindow(this) { IsOpen = true };
        EngineProgress = new EngineProgressWindow(this);
        WindowSystem.AddWindow(Shell);
        WindowSystem.AddWindow(SetupWindow);
        WindowSystem.AddWindow(IndicatorWindow);
        WindowSystem.AddWindow(EngineProgress);

        CommandManager.AddHandler(MainCommand, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open PopotoVox. Subcommands: setup, config, diag.",
        });
        CommandManager.AddHandler(ShortCommand, new CommandInfo(OnCommand) { HelpMessage = "Alias for /popotovox." });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += OpenMain;
        PluginInterface.UiBuilder.OpenConfigUi += OpenSettings;

        // First-run: walk new users through the guided setup; afterwards just open the hub
        // if their chosen voice still isn't installed.
        if (!Configuration.SetupCompleted)
            SetupWindow.IsOpen = true;
        else if (!Engine.IsReady)
            Shell.IsOpen = true;

        // Pre-load the engine (the GPU engine spins up a host + loads its model) in the background so the
        // first NPC line isn't lost to a cold start. MUST go through Task.Run: the GPU host's warm-up blocks
        // SYNCHRONOUSLY on the model-load handshake (VoxCpmHostProcess.EnsureStarted's Wait) with no async
        // yield before it, so awaiting it directly on this thread would freeze the ctor — and Dalamud's whole
        // enable/plugin-list — until the model finished loading. Task.Run keeps the ctor instant. Surface the
        // progress in the shared window only for the slow GPU engine; CPU engines warm instantly (no-op).
        if (Engine.IsReady)
            _ = Task.Run(() => WarmActiveEngineAsync(showWindow: TtsEngineCatalog.Get(ActiveEngineId).RequiresGpu));

        // Warm the casting/emotion LLM too (it had no warm-up) so the first NPC cast — and, on
        // expressive engines, the first per-line emotion pass — isn't a cold model load.
        if (Configuration.LlmEnabled && llm.Installed)
            _ = Task.Run(() => llm.CompleteTextAsync("Ready.", maxTokens: 1, temperature: 0.1f));
        else if (Configuration.LlmEnabled && !llm.Installed)
            // Smart casting is on but the model isn't downloaded — say so at load (otherwise every NPC
            // silently uses a rules voice, with no hint why). The System page also shows this state.
            Log.Warning("[LLM] Smart casting is enabled but the caster model (Qwen2.5-1.5B) isn't installed — " +
                        "NPCs will use rules-based voices until you download it in /pvox → Storage.");

        var built = System.IO.File.GetLastWriteTime(PluginInterface.AssemblyLocation.FullName);
        Log.Information($"PopotoVox loaded — build {built:yyyy-MM-dd HH:mm}.");
    }

    /// <summary>Synthesize a one-off preview line without touching the locked cache (override editor).</summary>
    public void Preview(VoiceSpec spec, string text, bool showFlow = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        _ = Task.Run(async () =>
        {
            if (!active.TryEnterRender(out var lease)) return; // an engine swap is draining — skip the preview
            try
            {
                var engine = lease.Binding.Engine;
                if (!engine.IsReady) return;
                var who = spec.SpeakerName;
                if (showFlow)
                {
                    // Walk the test line through the visible upstream stages so the flow diagram
                    // demonstrates the whole pipeline; the render + playback below are real.
                    CastingState.Mark(Casting.CastingState.PipelineStage.Capturing, who);
                    await Task.Delay(380).ConfigureAwait(false);
                    CastingState.Mark(Casting.CastingState.PipelineStage.Casting, who);
                    await Task.Delay(520).ConfigureAwait(false);
                    if (TtsEngineCatalog.Get(lease.Binding.Id).SupportsEmotion && Configuration.EmotionAnnotation && Configuration.LlmEnabled)
                    {
                        CastingState.Mark(Casting.CastingState.PipelineStage.Annotating, who);
                        await Task.Delay(380).ConfigureAwait(false);
                    }
                }

                CastingState.Mark(Casting.CastingState.PipelineStage.Rendering, who);

                // Stream when the engine supports it and streaming is on — exactly like the real pipeline, so
                // the first sound starts seconds sooner instead of waiting for the whole clip to render.
                if (TtsEngineCatalog.Get(lease.Binding.Id).SupportsStreaming && Configuration.StreamAudio)
                {
                    var sink = new PreviewSink(audio,
                        () => CastingState.Mark(Casting.CastingState.PipelineStage.Playing, who));
                    await engine.RenderStreamingAsync(text, spec, sink).ConfigureAwait(false);
                    if (sink.Started) audio.EndStream(sink.Handle);
                }
                else
                {
                    var rendered = await engine.RenderAsync(text, spec).ConfigureAwait(false);
                    audio.Play(rendered);
                    CastingState.Mark(Casting.CastingState.PipelineStage.Playing, who);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[Preview] Failed to render preview line.");
            }
            finally { lease.Exit(); }
        });
    }

    /// <summary>Streams preview PCM to the player as its own mixer voice, starting playback (and lighting
    /// "Play") on the first chunk.</summary>
    private sealed class PreviewSink : IAudioSink
    {
        private readonly AudioPlayer audio;
        private readonly Action onFirstSound;
        private int sampleRate, channels;

        public bool Started { get; private set; }
        public VoiceHandle Handle { get; private set; } = VoiceHandle.None;

        public PreviewSink(AudioPlayer audio, Action onFirstSound)
        {
            this.audio = audio;
            this.onFirstSound = onFirstSound;
        }

        public void Begin(int sampleRate, int channels)
        {
            this.sampleRate = sampleRate;
            this.channels = channels;
        }

        public void Feed(byte[] pcm16)
        {
            if (!Started)
            {
                Handle = audio.Begin(sampleRate, channels);
                Started = true;
                onFirstSound();
            }
            audio.Feed(Handle, pcm16);
        }
    }

    /// <summary>Push live-applicable config changes (volume, global voice params) to running subsystems.</summary>
    public void OnConfigChanged()
    {
        audio.Volume = Configuration.Volume;
        // Piper's delivery params are process-global; Kokoro takes pace per call.
        if (Engine is PiperEngine piper)
            piper.UpdateGlobalParams(Configuration.GlobalVoiceParams());
    }

    /// <summary>Resolve the caster-LLM launch options from what's on disk right now: the CUDA
    /// llama-server when the `llama-cuda` runtime is installed (default 0 GPU layers reads as "auto" →
    /// full offload; the 1.5B fits in ~1 GB), else the CPU build with no offload.</summary>
    private LlmOptions BuildLlmOptions()
    {
        var cudaExe = Path.Combine(Paths.LlamaCudaDir, "llama-server.exe");
        var hasCuda = File.Exists(cudaExe);
        var exe = hasCuda ? cudaExe : Path.Combine(Paths.LlamaDir, "llama-server.exe");
        var gpuLayers = hasCuda ? (Configuration.LlmGpuLayers > 0 ? Configuration.LlmGpuLayers : 99) : 0;
        return new LlmOptions(
            exe,
            Path.Combine(Paths.Models, "Qwen2.5-1.5B-Instruct-Q4_K_M.gguf"),
            Configuration.LlmPort, Configuration.LlmContextSize,
            gpuLayers, Configuration.LlmThreads);
    }

    /// <summary>After any download/removal batch: if the resolved caster-LLM runtime changed (the CUDA
    /// build appeared or went away), restart the server on the new binary so GPU casting kicks in
    /// without a plugin reload.</summary>
    private void OnDownloadsBatchCompleted()
    {
        var fresh = BuildLlmOptions();
        if (fresh.ExePath == llm.Options.ExePath && fresh.NGpuLayers == llm.Options.NGpuLayers) return;
        Log.Information($"[LLM] runtime changed on disk — restarting the casting/emotion LLM on the " +
                        $"{(fresh.NGpuLayers > 0 ? "GPU (CUDA build)" : "CPU build")}.");
        llm.Reconfigure(fresh);
    }

    /// <summary>Whether an engine's non-downloadable runtime is present. Engines whose files all come
    /// from the signed manifest are always "present" (their bundle answers the rest); VoxCPM2 needs its
    /// hand-installed dev-config runtime until the packaged download ships.</summary>
    public bool EngineRuntimePresent(TtsEngineChoice engine) =>
        engine != TtsEngineChoice.VoxCPM2 || VoxCpmEngine.RuntimePresent(Paths);

    /// <summary>Commit a settings draft (the Voices-page "Apply"). Live-only knobs take effect immediately;
    /// an engine/preset change kicks off a live transition (download-if-needed → warm → swap), no reload.</summary>
    public void ApplySettings(Configuration draft)
    {
        var engineChanged = draft.TtsEngine != Configuration.TtsEngine;
        Configuration.CopyFrom(draft);   // commit into the live, Dalamud-registered instance
        Configuration.Save();
        OnConfigChanged();               // pushes the cached live params (volume + Piper pool)
        if (engineChanged) RequestEngineTransition(Configuration.TtsEngine);
    }

    /// <summary>Kick off a live engine swap to <paramref name="target"/> on a background task (never the
    /// framework thread — the GPU host's warm-up blocks synchronously). Latched so only one runs at a time.</summary>
    public void RequestEngineTransition(TtsEngineChoice target)
    {
        if (target == ActiveEngineId && Engine.IsReady) return; // already live and ready
        if (Interlocked.Exchange(ref swapping, 1) == 1) return; // a transition is already in flight
        // Mark the transition active BEFORE opening the window, so its first frame doesn't see the stale
        // "Ready" phase from a previous transition (boot warm-up) and auto-close before the swap task starts.
        // The swap task re-sets the precise phase (Downloading/Warming) within a moment.
        Transition.Begin(target, TransitionPhase.Warming);
        EngineProgress.IsOpen = true;
        _ = Task.Run(async () =>
        {
            try { await SwapEngineAsync(target).ConfigureAwait(false); }
            catch (Exception ex) { Log.Error(ex, "[Engine] transition failed."); Transition.Fail(ex.Message); }
            finally { Volatile.Write(ref swapping, 0); }
        });
    }

    /// <summary>
    /// The live engine swap. Default path (no two-GPU collision — impossible with one GPU engine today) warms
    /// the NEW engine while the OLD keeps serving lines, publishes atomically, drains the old, then disposes
    /// it — near-zero dead air, and precompute stays gated until the new engine is warm (the warm-up fix).
    /// </summary>
    private async Task SwapEngineAsync(TtsEngineChoice target)
    {
        var name = TtsEngineCatalog.Get(target).DisplayName;
        active.Swapping = true;
        try
        {
            // 1. Ensure the target (and its caster LLM) is installed — download first if needed. On GPU
            //    machines the bundle includes the CUDA caster-LLM build (probe if it hasn't resolved yet,
            //    so a fast first Apply still gets the right bundle). An empty/complete bundle skips
            //    straight to the readiness check below.
            if (Hardware == null)
                try { Hardware = await HardwareProbe.DetectAsync(Paths.Assets).ConfigureAwait(false); }
                catch { /* best-effort — reads as CPU-only */ }
            var bundle = Assets.BundleForEngine(target, Configuration.LlmEnabled,
                withCudaLlm: Hardware?.HasNvidiaGpu == true);
            var installed = bundle.All(a => Downloads.IsInstalled(a.Id) == true);
            if (!installed)
            {
                if (!Assets.Available) { Transition.Fail(Assets.UnavailableReason ?? $"{name} can't be downloaded."); return; }
                if (Downloads.Busy) { Transition.Fail("A download is already in progress — try again in a moment."); return; }
                Transition.Begin(target, TransitionPhase.Downloading);
                Downloads.StartDownload(bundle);
                while (Downloads.Busy) await Task.Delay(200).ConfigureAwait(false);
                await Downloads.RefreshInstalledAsync().ConfigureAwait(false);
                if (!bundle.All(a => Downloads.IsInstalled(a.Id) == true))
                { Transition.Fail(Downloads.LastError ?? $"{name} download didn't complete."); return; }
            }

            // 2. Build the new engine (cheap ctor). If it still isn't ready (e.g. VoxCPM2 without its
            //    dev-config — nothing to download), keep the working engine and point the user at Storage.
            var next = TtsEngineCatalog.Create(target, Paths, pluginDir, Configuration);
            if (!next.IsReady)
            {
                next.Dispose();
                Transition.Fail($"{name} isn't ready to run — check its files in Storage.");
                return;
            }

            Director.CancelBackgroundRenders(); // free the drain from any long background render
            var newBinding = BuildBinding(next, target);
            var gpuCollision = TtsEngineCatalog.Get(ActiveEngineId).RequiresGpu && TtsEngineCatalog.Get(target).RequiresGpu;

            if (gpuCollision)
            {
                // Fallback (unreachable with one GPU engine): free VRAM before loading the new model, so two
                // GPU engines never co-reside. Closes the gate → incoming lines drop during the dead window.
                Transition.Begin(target, TransitionPhase.Swapping);
                active.CloseGate();
                var old = active.CurrentSlot;
                await active.QuiesceAsync(old, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                old.Binding.Engine.Dispose();
                Transition.Begin(target, TransitionPhase.Warming);
                await next.WarmUpAsync().ConfigureAwait(false);
                active.Publish(newBinding);
                active.OpenGate();
            }
            else
            {
                // Default: warm the new engine while the old keeps serving, then swap and retire the old.
                Transition.Begin(target, TransitionPhase.Warming);
                await next.WarmUpAsync().ConfigureAwait(false);
                Transition.Begin(target, TransitionPhase.Swapping);
                var old = active.Publish(newBinding);
                await active.QuiesceAsync(old, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                old.Binding.Engine.Dispose();
            }

            OnConfigChanged();      // re-push Piper params etc. to the freshly-swapped engine
            Transition.Complete();
            Log.Information($"[Engine] switched to {name} live (no reload).");
        }
        finally
        {
            active.Swapping = false;
        }
    }

    /// <summary>Warm the current engine (background), surfacing progress via <see cref="Transition"/>.</summary>
    private async Task WarmActiveEngineAsync(bool showWindow)
    {
        Transition.Begin(ActiveEngineId, TransitionPhase.Warming);
        if (showWindow) EngineProgress.IsOpen = true;
        try { await Engine.WarmUpAsync().ConfigureAwait(false); Transition.Complete(); }
        catch (Exception ex) { Transition.Fail(ex.Message); Log.Warning(ex, "[Engine] warm-up failed."); }
    }

    /// <summary>Assemble the full engine identity (engine + id + catalog + matcher + emotion annotator).</summary>
    private EngineBinding BuildBinding(ITtsEngine engine, TtsEngineChoice id)
    {
        var catalog = new SpeakerCatalog(VoicePalette.For(id));
        var matcher = new VoiceMatcher(catalog);
        var annotator = TtsEngineCatalog.Get(id).SupportsEmotion ? new EmotionAnnotator(llm, Log) : null;
        return new EngineBinding(engine, id, id.ToString().ToLowerInvariant(), catalog, matcher, annotator);
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "config": Shell.Navigate(ShellSection.Voices); Shell.IsOpen = true; break;
            case "setup": SetupWindow.Restart(); break;
            case "diag": Shell.NavigateToDiagnostics(); Shell.IsOpen = true; break;
            case "dialogueprobe": dialogueProbe.Toggle(); break;     // M10 spike
            case "dialoguedump": dialogueProbe.DumpTarget(); break;  // M10 spike
            case "dialoguestats": dialogueProbe.DumpStats(); break;  // M10 spike
            case "bubbles": bubbleSource.DumpBubbles(); break;       // M15 diagnostic
            default: Shell.IsOpen = true; break;
        }
    }

    private void OpenMain() => Shell.IsOpen = true;
    private void OpenSettings() { Shell.Navigate(ShellSection.Voices); Shell.IsOpen = true; }

    /// <summary>
    /// One-time settings migrations for configs saved by older versions.
    /// v3: stop reading the chat log by default. The game echoes many dialogue-box lines to the
    /// chat log, so capturing both voiced each line twice (box, then chat echo). New default is
    /// "dialogue boxes only"; users can re-enable the chat-log source in Settings.
    /// v4: raise the first-line wait default 40 → 90s. Casting is one-shot and locks in, so we'd
    /// rather wait than permanently fall back to a rules voice. Only bumps configs still on the old
    /// default; a hand-set value is respected.
    /// </summary>
    private static void MigrateConfiguration(Configuration cfg)
    {
        if (cfg.Version < 3)
        {
            cfg.CaptureChatGui = false;
            cfg.Version = 3;
            cfg.Save();
        }

        if (cfg.Version < 4)
        {
            if (cfg.CastWaitTimeoutSeconds == 40) cfg.CastWaitTimeoutSeconds = 90;
            cfg.Version = 4;
            cfg.Save();
        }

        // v5: the identity-fingerprint formula changed (no longer mixes in npcId/equipment), so the
        // persisted cross-link store is cleared once on first load after upgrade — see the ctor,
        // which keys off the pre-migration version. Nothing to change on the config itself.
        if (cfg.Version < 5)
        {
            cfg.Version = 5;
            cfg.Save();
        }

        // v6: "High" used to mean Orpheus WITHOUT per-line emotion; it now includes emotion. Flip emotion
        // on for old-High users. (Referenced by old numeric value 2 = Orpheus, since that enum member is
        // removed in v7 — see below.)
        if (cfg.Version < 6)
        {
            if ((int)cfg.TtsEngine == 2 && cfg.LlmEnabled && !cfg.EmotionAnnotation)
                cfg.EmotionAnnotation = true;
            cfg.Version = 6;
            cfg.Save();
        }

        // v7: engine-ladder overhaul — the Orpheus (High) and Studio (Ultra) engines are deleted. Remap by
        // their old numeric enum values (2 = Orpheus, 3 = Studio) so no one strands on "Custom":
        // Orpheus → Kokoro (new High), Studio → VoxCPM2 (new Ultra). New Medium is Piper, but an existing
        // Kokoro user is a valid new High, so they're left as-is (they detect as High).
        if (cfg.Version < 7)
        {
            var engineInt = (int)cfg.TtsEngine;
            if (engineInt == 2) cfg.ApplyQualityPreset(QualityPreset.High);       // old Orpheus → High (Kokoro)
            else if (engineInt == 3) cfg.ApplyQualityPreset(QualityPreset.Ultra); // old Studio  → Ultra (VoxCPM2)
            cfg.Version = 7;
            cfg.Save();
        }
    }

    private static (TtsEngineChoice, ITtsEngine) SelectEngine(
        TtsEngineChoice chosen, PluginPaths paths, string pluginDir, Configuration config)
    {
        var chosenEngine = TtsEngineCatalog.Create(chosen, paths, pluginDir, config);
        if (chosenEngine.IsReady) return (chosen, chosenEngine);

        // Chosen engine isn't installed — fall back to the first engine that IS, so audio
        // keeps working until the user downloads their pick.
        foreach (var info in TtsEngineCatalog.All.Where(i => i.Id != chosen))
        {
            var candidate = TtsEngineCatalog.Create(info.Id, paths, pluginDir, config);
            if (candidate.IsReady) { chosenEngine.Dispose(); return (info.Id, candidate); }
            candidate.Dispose();
        }
        return (chosen, chosenEngine); // nothing installed yet — keep the choice; setup banner shows
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= OpenMain;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenSettings;

        WindowSystem.RemoveAllWindows();
        CommandManager.RemoveHandler(MainCommand);
        CommandManager.RemoveHandler(ShortCommand);

        Downloads.BatchCompleted -= OnDownloadsBatchCompleted;
        precompute.Dispose();
        spatialTracker.Dispose();
        pipeline.Dispose();
        DialogueCapture.Dispose();
        Director.Dispose();
        llm.Dispose();
        active.Current.Engine.Dispose();
        audio.Dispose();
        Downloads.Dispose();
        Assets.Dispose();

        Shell.Dispose();
        SetupWindow.Dispose();
    }
}
