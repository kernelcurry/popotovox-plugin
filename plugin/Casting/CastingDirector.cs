using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using PopotoVox.Dialogue;
using PopotoVox.Llm;
using PopotoVox.Npc;
using PopotoVox.Pipeline;
using PopotoVox.Tts;

namespace PopotoVox.Casting;

/// <summary>
/// The brain of the pipeline (PRD §5.4). For each captured line it resolves the
/// speaker's identity, and either renders instantly from the locked cache, reuses
/// a cross-linked voice, or runs a one-shot cast (LLM → rules fallback) while
/// holding the line behind the on-screen indicator. No transient voice is ever
/// played (D12); once cast, a voice is locked forever (§5.6).
/// </summary>
public sealed class CastingDirector : IDisposable
{
    private readonly VoiceSpecCache voiceCache;
    private readonly IdentityCrossLink crossLink;
    private readonly NpcRecordCache records;
    private readonly OverrideStore overrides;
    private readonly NpcResolver resolver;
    private readonly ActiveEngine active;         // the hot-swappable engine identity (engine+id+catalog+matcher+annotator)
    private readonly ILlmClient llm;
    private readonly AudioPlayer audio;
    private readonly CastingState state;
    private readonly IChatGui chat;
    private readonly IPluginLog log;
    private readonly Func<Configuration> config;
    private readonly LineAudioCache? lineCache;   // optional: caches final rendered lines for instant repeats

    private readonly ConcurrentDictionary<uint, PendingLine> pendingLines = new();
    private readonly ConcurrentDictionary<uint, byte> precasting = new(); // guards background pre-casts
    private int foregroundRenders; // M13: in-flight foreground (dialogue/battle) renders
    private int warnedLlmMissing;  // latch: log the "caster model not installed" warning only once per session
    private CancellationTokenSource backgroundCts = new(); // M14: cancelled when a foreground line needs the engine

    // M13: source priority — foreground (dialogue box / combat banner) outranks background (chat / ambient).
    // With the mixer, background no longer DROPS under foreground; it DUCKS (AudioPlayer lowers its gain) so
    // the hub stays alive mid-conversation. Foreground still preempts in-flight background RENDERS to keep
    // dialogue responsive on a single GPU host.
    private const int ForegroundThreshold = 2;
    private static int Priority(DialogueSourceKind s) => s switch
    {
        DialogueSourceKind.AddonTalk => 3,
        DialogueSourceKind.AddonBattleTalk => 2,
        DialogueSourceKind.ChatGui => 1,
        DialogueSourceKind.MiniTalk => 0,
        _ => 1,
    };
    private bool ForegroundBusy() => Volatile.Read(ref foregroundRenders) > 0 || audio.IsPlayingAtLeast(ForegroundThreshold);

    /// <summary>True while a foreground line (dialog box / combat banner) is being rendered or is playing —
    /// the signal background NPC precompute hard-halts on (foreground is WAY more important).</summary>
    public bool IsForegroundActive => ForegroundBusy();

    /// <summary>The current background cancellation token. A foreground <see cref="Submit"/> swaps and cancels
    /// this CTS, so background work (e.g. an in-flight voice design) that snapshots this token aborts the
    /// moment a dialog box opens. Read fresh per call — the token is replaced on each foreground line.</summary>
    public CancellationToken BackgroundToken => Volatile.Read(ref backgroundCts).Token;

    // Mixer supersession is PER SLOT, not global: foreground (dialogue/combat) is one slot whose newer line
    // supersedes the older (the box advanced); each ambient speaker (by object address) is its own slot, so
    // overlapping bubbles from different NPCs coexist instead of cancelling each other. A line plays only while
    // its slot's generation is still current.
    private readonly ConcurrentDictionary<string, long> slotGeneration = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> slotCts = new();

    // Render concurrency cap: the GPU engine (VoxCPM2) uses ONE host process, so parallel renders thrash —
    // default 1. The win is that PLAYBACK overlaps even though renders serialize. Foreground bypasses the cap
    // (it preempts background instead) so a conversation never waits behind an ambient render.
    private readonly SemaphoreSlim renderSlots;

    private static string SlotKey(int priority, nint speakerAddress, string text) =>
        priority >= ForegroundThreshold ? "fg"
        : speakerAddress != 0 ? "amb:" + speakerAddress.ToString("x")
        : "txt:" + (uint)text.GetHashCode();

    private long BumpSlot(string slot) => slotGeneration.AddOrUpdate(slot, 1L, (_, v) => v + 1L);
    private bool IsCurrent(string slot, long generation) =>
        slotGeneration.TryGetValue(slot, out var v) && v == generation;

    // Globally-unique render id for the on-screen indicator (CastingState keys renders by token, and per-slot
    // generations are NOT unique across slots — two slots could both be at generation 1 and collide).
    private long renderSeq;

    private static void SafeCancel(CancellationTokenSource? c) { try { c?.Cancel(); } catch { /* already disposed */ } }

    // Register this render's CTS as the slot's current one and cancel the slot's previous in-flight render
    // (a newer line supersedes the older WITHIN its slot). Background renders also observe backgroundCts so a
    // foreground line can preempt them. Each render disposes its own CTS in its finally.
    private CancellationTokenSource StartSlotRender(string slot, bool foreground)
    {
        var cts = foreground
            ? new CancellationTokenSource()
            : CancellationTokenSource.CreateLinkedTokenSource(backgroundCts.Token);
        var prev = slotCts.TryGetValue(slot, out var p) ? p : null;
        slotCts[slot] = cts;
        SafeCancel(prev);
        return cts;
    }
    private void CancelSlot(string slot) { if (slotCts.TryGetValue(slot, out var c)) SafeCancel(c); }

    // The play-time "is the source still up?" gate. A line plays only if it's still the current line for its
    // slot AND (for an ambient bubble) its speaker is still present. Otherwise the render still completes and is
    // cached — it just isn't played.
    private bool LiveAtPlayTime(string slot, long generation, nint speakerAddress)
    {
        if (!IsCurrent(slot, generation)) return false;
        if (speakerAddress != 0 && SpatialTracker is { } t && !t.IsPresent(speakerAddress)) return false;
        return true;
    }

    // Small ring buffer of actual playback starts, for the Diagnostics window. Lets us see
    // whether a line plays once or is being re-triggered rapidly (the "fast skipping" report).
    private readonly object playLogGate = new();
    private readonly Queue<(DateTime At, string Text)> playLog = new();

    public (DateTime At, string Text)[] RecentPlays()
    {
        lock (playLogGate) return playLog.ToArray();
    }

    private void LogPlay(string text)
    {
        lock (playLogGate)
        {
            playLog.Enqueue((DateTime.UtcNow, text.Length > 48 ? text[..48] + "…" : text));
            while (playLog.Count > 30) playLog.Dequeue();
        }
    }

    private readonly record struct PendingLine(long Generation, string SlotKey, string Text, float? Distance, int Priority, nint SpeakerAddress);

    // Cross-source de-duplication: the same line often arrives twice — once from the
    // dialogue box (AddonTalk) and again when the game echoes it to the chat log
    // (ChatGui), frequently only once the conversation ends. We remember recently-voiced
    // lines by COUNT (not a clock window) so dedup spans a whole conversation no matter
    // how long it took, and voice each line at most once.
    private const int RecentLineCapacity = 128;
    private readonly object dedupGate = new();
    private readonly HashSet<string> recentSet = new();
    private readonly Queue<string> recentOrder = new();

    public CastingDirector(
        VoiceSpecCache voiceCache, IdentityCrossLink crossLink, NpcRecordCache records,
        OverrideStore overrides, NpcResolver resolver, ActiveEngine active,
        ILlmClient llm, AudioPlayer audio, CastingState state,
        IChatGui chat, IPluginLog log, Func<Configuration> config,
        LineAudioCache? lineCache = null)
    {
        this.lineCache = lineCache;
        this.voiceCache = voiceCache;
        this.crossLink = crossLink;
        this.records = records;
        this.overrides = overrides;
        this.resolver = resolver;
        this.active = active;
        this.llm = llm;
        this.audio = audio;
        this.state = state;
        this.chat = chat;
        this.log = log;
        this.config = config;
        renderSlots = new SemaphoreSlim(Math.Max(1, config().MaxConcurrentRenders));
    }

    /// <summary>Cancel any in-flight BACKGROUND renders (precompute designs / ambient pre-renders) by swapping
    /// and cancelling the shared token — used by an engine swap so a long background render can't hold up the
    /// drain, mirroring the foreground-preempt in <see cref="Submit"/>.</summary>
    public void CancelBackgroundRenders() =>
        Interlocked.Exchange(ref backgroundCts, new CancellationTokenSource()).Cancel();

    /// <summary>
    /// Entry point for a captured line. <paramref name="npcId"/> must have been
    /// resolved on the framework thread already (it reads live game objects). The
    /// rest — Lumina lookups, cache writes, casting, rendering — runs off-thread so
    /// nothing here hitches the game's frame.
    /// </summary>
    public void Submit(string speaker, string text, uint? npcId, DialogueSourceKind source, float? distance = null, nint speakerAddress = 0)
    {
        if (!active.Current.Engine.IsReady) return;  // assets not installed yet — nothing to voice

        var preview = text.Length > 40 ? text[..40] + "…" : text;
        if (IsChatEcho(text, source))
        {
            log.Debug($"[Dialogue] {source} echo dropped: \"{preview}\"");
            return; // chat-log echo of a line we already voiced
        }
        var npcLabel = npcId?.ToString() ?? "?";
        log.Debug($"[Dialogue] {source} voicing: \"{preview}\" (npc={npcLabel})");

        var priority = Priority(source);
        // M14: a foreground line preempts in-flight background renders so the engine/LLM is freed at once and
        // the conversation stays responsive on a single GPU host. Background lines are NOT dropped any more —
        // they render (subject to the render cap) and play DUCKED under the conversation (mixer overlap).
        if (priority >= ForegroundThreshold)
            Interlocked.Exchange(ref backgroundCts, new CancellationTokenSource()).Cancel();

        // A new line becomes the current target for ITS slot (foreground box, or this ambient speaker). We
        // deliberately do NOT cut a currently-playing line here: an expressive engine can take a few seconds
        // to render, and cutting to dead air is worse than letting the prior line finish. The audio joins the
        // mix the instant it's ready (see RenderAndPlay), so fast engines still feel immediate.
        var slotKey = SlotKey(priority, speakerAddress, text);
        var generation = BumpSlot(slotKey);
        _ = Task.Run(() => SubmitCore(speaker, text, npcId, generation, slotKey, distance, priority, speakerAddress));
    }

    /// <summary>
    /// True only for a chat-log line that mirrors something we already spoke. The dialogue box
    /// (and battle banner) echo their lines into the chat log, so when both sources are on we'd
    /// otherwise voice each line twice. We suppress ONLY the chat-log copy — a dialogue-box line is
    /// always voiced, even identical text re-triggered by re-talking to the same NPC (every box
    /// appearance should be read once). Box lines are recorded so a later chat echo can be matched.
    /// </summary>
    private bool IsChatEcho(string text, DialogueSourceKind source)
    {
        lock (dedupGate)
        {
            var seen = recentSet.Contains(text);
            if (source == DialogueSourceKind.ChatGui && seen)
                return true; // chat-log echo of a line we already spoke — drop it

            if (!seen)
            {
                recentSet.Add(text);
                recentOrder.Enqueue(text);
                while (recentOrder.Count > RecentLineCapacity)
                    recentSet.Remove(recentOrder.Dequeue());
            }
            return false;
        }
    }

    private void SubmitCore(string speaker, string text, uint? npcId, long generation, string slotKey, float? distance, int priority, nint speakerAddress)
    {
        state.Mark(CastingState.PipelineStage.Capturing, speaker);
        if (npcId is not { } id)
        {
            // No stable identity (e.g. some chat-bubble lines). Use a name-only
            // deterministic voice so the same name still sounds consistent; not cached.
            // Gender is unknown here → the matcher draws from the whole palette.
            var anon = new NpcRecord { Name = speaker };
            var anonKey = IdentityFingerprint.Compute(anon.Name, anon.ModelHashSeed);
            var b = active.Current;
            var anonSpec = b.Matcher.Build(anon, anonKey, null, null, config().GlobalVoiceParams(), b.EngineId);
            _ = RenderAndPlay(text, anonSpec, generation, slotKey, speaker, distance, priority, speakerAddress);
            return;
        }

        var record = GetOrBuildRecord(id, speaker);
        records.MarkSeen(id);
        records.AddSampleLine(id, text);

        if (voiceCache.TryGet(id, out var cached))
        {
            _ = RenderAndPlay(text, cached, generation, slotKey, record.Name, distance, priority, speakerAddress);
            return;
        }

        var fingerprint = IdentityFingerprint.Compute(record.Name, record.ModelHashSeed);
        if (crossLink.TryResolve(fingerprint, out var linkedId) && voiceCache.TryGet(linkedId, out var linkedSpec))
        {
            // Re-stamp the linked spec to THIS identity before caching — the spec still carries the
            // other NpcId's NpcId/SpeakerName, which would otherwise show the wrong character on the
            // card. The voice (SpeakerId/Gender/Traits) is unchanged — that's the point of the link.
            var adopted = linkedSpec with { NpcId = id, SpeakerName = record.Name };
            voiceCache.Put(id, adopted); // write-through so future lines hit instantly
            _ = RenderAndPlay(text, adopted, generation, slotKey, record.Name, distance, priority, speakerAddress);
            return;
        }

        // Genuinely new identity → hold the line and cast. Speak it after casting only
        // if it's still the current line for its slot (IsCurrent check in RenderAndPlay).
        pendingLines[id] = new PendingLine(generation, slotKey, text, distance, priority, speakerAddress);
        if (state.IsCasting(id)) return; // a cast is already in flight; pending line updated
        state.Begin(id, record.Name);
        Status($"Casting voice for {record.Name}…");
        _ = CastThenSpeak(id, record, fingerprint);
    }

    private NpcRecord GetOrBuildRecord(uint id, string speaker)
    {
        if (records.TryGet(id, out var existing))
        {
            if (existing.SchemaVersion >= NpcRecord.CurrentSchemaVersion) return existing;

            // Stale record from older resolver logic (e.g. assembled before ApparentAge existed).
            // Re-resolve from static game data once, preserving the runtime-accumulated sample
            // lines and zones, so the refreshed record (and the re-cast that follows) is correct.
            var refreshed = resolver.ResolveRecord(id, existing.Name);
            foreach (var line in existing.SampleLines)
                if (!refreshed.SampleLines.Contains(line)) refreshed.SampleLines.Add(line);
            foreach (var zone in existing.Zones)
                if (!refreshed.Zones.Contains(zone)) refreshed.Zones.Add(zone);
            records.Put(refreshed);
            return refreshed;
        }

        var record = resolver.ResolveRecord(id, speaker);
        records.Put(record);
        return record;
    }

    private async Task CastThenSpeak(uint id, NpcRecord record, string fingerprint)
    {
        try
        {
            state.Mark(CastingState.PipelineStage.Casting, record.Name);
            var spec = await Cast(id, record, fingerprint).ConfigureAwait(false);
            voiceCache.Put(id, spec);
            crossLink.Put(fingerprint, id);
            Status($"{record.Name} cast as speaker {spec.SpeakerId} ({spec.Source.ToString().ToLowerInvariant()}).");
        }
        catch (Exception ex)
        {
            log.Error(ex, $"[Casting] Cast failed for NpcId {id}.");
        }
        finally
        {
            state.End(id);
        }

        if (pendingLines.TryRemove(id, out var pending) && voiceCache.TryGet(id, out var finalSpec))
            await RenderAndPlay(pending.Text, finalSpec, pending.Generation, pending.SlotKey, record.Name, pending.Distance, pending.Priority, pending.SpeakerAddress).ConfigureAwait(false);
    }

    /// <summary>
    /// Background pre-cast: resolve + cast (and cache) an NPC's voice with NO dialogue, so the first line
    /// the player hears skips the cast. Returns the cast/cached spec (null if it couldn't). Cheap to call
    /// repeatedly — returns immediately if already cached, a live cast owns the id, or a pre-cast is already
    /// running for it. Never renders; never touches the on-screen indicator. Used by NPC precompute (M9b).
    /// </summary>
    public async Task<VoiceSpec?> PrecastAsync(uint npcId, string name)
    {
        if (npcId == 0) return null;
        if (voiceCache.TryGet(npcId, out var already)) return already;
        if (state.IsCasting(npcId)) return null;        // a live cast owns this id — let it win
        if (!precasting.TryAdd(npcId, 0)) return null;  // a pre-cast for this id is already in flight
        try
        {
            var record = GetOrBuildRecord(npcId, name);
            var fingerprint = IdentityFingerprint.Compute(record.Name, record.ModelHashSeed);

            // Adopt a cross-linked voice if this character was already voiced under another NpcId (no LLM).
            if (crossLink.TryResolve(fingerprint, out var linkedId) && voiceCache.TryGet(linkedId, out var linkedSpec))
            {
                var adopted = linkedSpec with { NpcId = npcId, SpeakerName = record.Name };
                voiceCache.Put(npcId, adopted);
                return adopted;
            }

            var spec = await Cast(npcId, record, fingerprint).ConfigureAwait(false);
            if (voiceCache.TryGet(npcId, out var live)) return live; // a live line cast meanwhile — don't clobber
            voiceCache.Put(npcId, spec);
            crossLink.Put(fingerprint, npcId);
            return spec;
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"[Precast] failed for NpcId {npcId}.");
            return null;
        }
        finally
        {
            precasting.TryRemove(npcId, out _);
        }
    }

    /// <summary>
    /// Render one line for a voice straight into the line cache (no playback), applying the SAME emotion
    /// direction a live line gets (base mood + per-line director) — so a pre-warmed ambient line sounds
    /// identical to when it's spoken live. Returns true if it actually rendered (false = already cached / off).
    /// Used by ambient pre-render (M12b).
    /// </summary>
    public async Task<bool> PrerenderLineAsync(VoiceSpec spec, string text, CancellationToken ct = default)
    {
        if (lineCache is not { Enabled: true } || string.IsNullOrWhiteSpace(text)) return false;
        if (!active.TryEnterRender(out var lease)) return false; // an engine swap is draining — skip
        try
        {
            var b = lease.Binding;
            if (!b.Engine.IsReady) return false;
            // M14: link to backgroundCts so a foreground line aborts this background render + frees the engine.
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, backgroundCts.Token);
            var bg = linked.Token;
            spec = AdaptToEngine(spec, b);
            var key = LineAudioCache.Key(spec, text); // key off the RAW line — matches the live render's key
            if (lineCache.Has(key)) return false;

            // Same direction a live line gets: only when the engine performs emotion AND it's enabled.
            string? preset = null;
            if (b.Annotator != null && config().LlmEnabled && config().EmotionAnnotation)
            {
                var (character, baseMood) = BuildCharacterContext(spec, spec.SpeakerName ?? "");
                var direction = await b.Annotator.DirectAsync(text, character, baseMood, bg).ConfigureAwait(false);
                text = direction.Text;
                preset = direction.EmotionPreset;
            }
            var rendered = await b.Engine.RenderAsync(text, spec, new RenderContext(preset), bg).ConfigureAwait(false);
            lineCache.Put(key, rendered);
            return true;
        }
        finally { lease.Exit(); }
    }

    /// <summary>
    /// Runs the LLM cast (if smart casting is on) with a wait budget, then hands the
    /// result — or null on disabled/failed/timeout — to the matcher, which gates on the
    /// NPC's gender and resolves a concrete voice. Null output → deterministic gender
    /// match, which IS the locked spec (never a transient voice).
    /// </summary>
    private async Task<VoiceSpec> Cast(uint id, NpcRecord record, string fingerprint)
    {
        var cfg = config();
        var ovr = overrides.TryGet(id, out var o) ? o : null;
        var globals = cfg.GlobalVoiceParams();
        LlmOutput? output = null;

        if (cfg.LlmEnabled && llm.Enabled && !llm.Installed)
        {
            // Smart casting is ON but the caster model isn't on disk → every NPC silently gets a rules voice
            // (and on Ultra, the design/emotion that depend on it are degraded). Warn ONCE so it isn't invisible.
            if (Interlocked.Exchange(ref warnedLlmMissing, 1) == 0)
                log.Warning("[Casting] Smart casting is enabled but the caster model (Qwen2.5-1.5B) is NOT installed — " +
                            "every NPC is using a built-in rules voice. Download it in /pvox → Storage to enable AI casting.");
        }
        else if (cfg.LlmEnabled && llm.Enabled)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(5, cfg.CastWaitTimeoutSeconds)));
            try
            {
                var request = new CastingRequest(record, ovr?.Prompt, cfg.LlmTemperature, StableSeed(fingerprint));
                output = await llm.CastAsync(request, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                log.Information($"[Casting] LLM wait budget elapsed for {record.Name}; using rules spec.");
            }
        }

        var b = active.Current;
        return b.Matcher.Build(record, fingerprint, output, ovr, globals, b.EngineId);
    }

    /// <summary>
    /// Render one line and speak it — but only if it's still the line on screen when
    /// the render finishes. If the dialogue box advanced while we were rendering/casting
    /// (a newer line was captured), the result is dropped (PRD D14 + the "read it only if
    /// done before the next box" behaviour).
    /// </summary>
    /// <summary>Compact NPC description + baseline manner for the per-line voice director (Ultra).
    /// Pulls the full record (race/age/job/zone/affiliation) when available, plus the casting AI's
    /// own-words <see cref="VoiceSpec.Description"/>; the base manner is the casting <see cref="VoiceSpec.Style"/>.</summary>
    private (string? Character, string? BaseMood) BuildCharacterContext(VoiceSpec spec, string speaker)
    {
        NpcRecord? rec = spec.NpcId is { } id && records.TryGet(id, out var r) ? r : null;
        var name = !string.IsNullOrWhiteSpace(spec.SpeakerName) ? spec.SpeakerName : speaker;

        var who = new List<string>();
        var age = rec?.ApparentAge ?? spec.Traits?.Age.ToString().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(age)) who.Add(age!);
        var kind = rec?.Tribe ?? rec?.Race;
        if (!string.IsNullOrWhiteSpace(kind)) who.Add(kind!);
        if (spec.Gender != VoiceGender.Neutral) who.Add(spec.Gender.Label());
        var role = rec?.Job ?? rec?.Role;
        if (!string.IsNullOrWhiteSpace(role)) who.Add(role!);

        var character = name;
        if (who.Count > 0) character += " — " + string.Join(" ", who);
        if (!string.IsNullOrWhiteSpace(rec?.Affiliation)) character += ", " + rec!.Affiliation;
        if (rec is { Zones.Count: > 0 }) character += ", in " + rec.Zones[^1];
        character += ".";
        if (!string.IsNullOrWhiteSpace(spec.Description)) character += " " + spec.Description;

        var baseMood = string.IsNullOrWhiteSpace(spec.Style) ? null : spec.Style;
        return (character, baseMood);
    }

    /// <summary>M16 spatial ambient audio tracker — wired by Plugin after construction (optional).</summary>
    public SpatialAudioTracker? SpatialTracker { get; set; }

    /// <summary>Mixer: when an ambient line (speakerAddress set) starts playing, follow that voice's speaker so
    /// its volume tracks distance live; interactive dialogue (or tracking off) is left at its fixed volume.</summary>
    private void TrackSpatial(VoiceHandle handle, nint speakerAddress)
    {
        var t = SpatialTracker;
        if (t == null || !handle.IsValid) return;
        var cfg = config();
        if (speakerAddress != 0 && cfg.AmbientDistanceVolume && cfg.AmbientSpatialTracking)
            t.Track(handle, speakerAddress);
    }

    private async Task RenderAndPlay(string text, VoiceSpec spec, long generation, string slotKey, string speaker, float? distance = null, int priority = int.MaxValue, nint speakerAddress = 0)
    {
        if (!active.TryEnterRender(out var lease)) return; // an engine swap is draining — drop this line
        var b = lease.Binding;
        try
        {
        spec = AdaptToEngine(spec, b);

        // Ambient bubbles fade with distance (M12a/M16): full within a few yalms, then the real inverse-distance
        // falloff (−6 dB per doubling of distance), silent at the hearing range.
        var cfg = config();
        var volumeScale = (cfg.AmbientDistanceVolume && distance is { } dist && cfg.AmbientHearingYalms > 0)
            ? AmbientVolume.Scale(dist, cfg.AmbientHearingYalms) : 1f;

        // Instant replay: if we've already rendered this exact line for this voice, play it from the line
        // cache — skipping the per-line LLM director AND the engine entirely (zero GPU). Great for repeats.
        var lineKey = (lineCache?.Enabled ?? false) ? LineAudioCache.Key(spec, text) : null;
        if (lineKey != null && lineCache!.TryGet(lineKey, out var cachedAudio))
        {
            CancelSlot(slotKey); // supersede any in-flight render for an older line in THIS slot
            if (LiveAtPlayTime(slotKey, generation, speakerAddress)) // only play if the box/bubble is still up
            {
                LogPlay(text);
                var handle = audio.Play(cachedAudio, volumeScale, priority); // joins the mix (cap may reject)
                TrackSpatial(handle, speakerAddress);
                state.Mark(CastingState.PipelineStage.Playing, speaker);
            }
            return;
        }

        var foreground = priority >= ForegroundThreshold;
        if (foreground) Interlocked.Increment(ref foregroundRenders);
        var cts = StartSlotRender(slotKey, foreground); // supersede this slot's older render; bg observes backgroundCts

        // Globally-unique id so concurrent renders don't collide in the indicator's render map.
        var renderId = Interlocked.Increment(ref renderSeq);
        // Surface "Generating voice…" on the on-screen indicator while we synthesize — this is
        // where the GPU engine spends a few seconds (and the one-time model warm-up on the first line).
        state.BeginRender(renderId, speaker);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long emotionMs = 0;
        var slotAcquired = false;
        try
        {
            // For an emotion-capable engine, let the LLM weave in <sigh>/<laugh>/… first — but only
            // when smart casting AND auto-emotion are both on (auto-emotion is a per-line LLM call,
            // so it's separately toggleable for users who want faster lines).
            string? preset = null;
            if (b.Annotator != null && config().LlmEnabled && config().EmotionAnnotation)
            {
                state.Mark(CastingState.PipelineStage.Annotating, speaker);
                var (character, baseMood) = BuildCharacterContext(spec, speaker);
                var direction = await b.Annotator.DirectAsync(text, character, baseMood, cts.Token).ConfigureAwait(false);
                text = direction.Text;
                preset = direction.EmotionPreset;
                emotionMs = sw.ElapsedMilliseconds;
            }

            // Surface the per-line direction the LLM chose so it's easy to see WHY a line sounded the
            // way it did — invaluable while tuning emotion by ear.
            if (preset != null) log.Information($"[Ultra] {speaker} · direction: {preset}");

            // Always pass a context: carries the directed emotion preset (if any) and a callback the
            // engine fires for a one-time voice "design" (Ultra), which lights the Design stage.
            var ctx = new RenderContext(preset, () => state.Mark(CastingState.PipelineStage.Designing, speaker));

            // Render concurrency cap: background renders wait for a slot (GPU single-host → serialized); a
            // foreground line bypasses the cap (it already preempted background via backgroundCts) so the
            // conversation never waits behind an ambient render.
            if (!foreground) { await renderSlots.WaitAsync(cts.Token).ConfigureAwait(false); slotAcquired = true; }

            state.Mark(CastingState.PipelineStage.Rendering, speaker);
            if (TtsEngineCatalog.Get(b.Id).SupportsStreaming && config().StreamAudio)
            {
                // Stream audio out as it's generated — first sound starts seconds before the line finishes.
                // The sink begins playback only if this is still the current line for its slot AND the source
                // is still up; otherwise it finishes buffering (for the cache) but doesn't play.
                var sink = new GuardedSink(this, generation, slotKey, renderId, text, speaker, cts.Token, capture: lineKey != null, volumeScale: volumeScale, priority: priority, speakerAddress: speakerAddress);
                var underrunsBefore = audio.StreamUnderrunCount;
                await b.Engine.RenderStreamingAsync(text, spec, sink, ctx, cts.Token).ConfigureAwait(false);
                if (sink.Started) audio.EndStream(sink.Handle);
                if (lineKey != null && sink.Captured is { } cap) lineCache!.Put(lineKey, cap);
                var underruns = audio.StreamUnderrunCount - underrunsBefore;
                log.Debug($"[Latency] {speaker}: emotion={emotionMs}ms streamed firstSound={sink.FirstSoundMs}ms total={sw.ElapsedMilliseconds}ms underruns={underruns}");
                if (sink.Started && underruns >= ChoppyUnderrunThreshold) WarnChoppy(underruns);
            }
            else
            {
                var renderStart = sw.ElapsedMilliseconds;
                var rendered = await b.Engine.RenderAsync(text, spec, ctx, cts.Token).ConfigureAwait(false);
                if (lineKey != null) lineCache!.Put(lineKey, rendered); // cache before the play gate (cache even if not played)
                log.Debug($"[Latency] {speaker}: emotion={emotionMs}ms render={sw.ElapsedMilliseconds - renderStart}ms");
                if (!cts.IsCancellationRequested && LiveAtPlayTime(slotKey, generation, speakerAddress))
                {
                    LogPlay(text);
                    var handle = audio.Play(rendered, volumeScale, priority); // joins the mix (cap may reject)
                    TrackSpatial(handle, speakerAddress);
                    state.Mark(CastingState.PipelineStage.Playing, speaker);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer line (or preempted by a foreground line) — expected.
        }
        catch (Exception ex)
        {
            log.Error(ex, "[Casting] Render/playback failed.");
        }
        finally
        {
            if (slotAcquired) renderSlots.Release();
            if (foreground) Interlocked.Decrement(ref foregroundRenders);
            state.EndRender(renderId);
            if (slotCts.TryGetValue(slotKey, out var cur) && ReferenceEquals(cur, cts))
                slotCts.TryRemove(slotKey, out _);
            cts.Dispose();
        }
        }
        finally { lease.Exit(); }
    }

    /// <summary>
    /// If a cached spec was cast on a different engine, re-pick a fitting voice from the
    /// active engine's palette (same gender + traits) so switching engines keeps voices
    /// gender-correct instead of reinterpreting the old id modulo the new palette. The
    /// pinned-override id is kept. Non-persisting — the locked on-disk spec is unchanged.
    /// </summary>
    private static VoiceSpec AdaptToEngine(VoiceSpec spec, EngineBinding b)
    {
        if (spec.Engine == b.EngineId || spec.Source == VoiceSource.Override) return spec;
        var key = spec.NpcId?.ToString() ?? spec.SpeakerName;
        var id = b.Matcher.ReselectId(spec.Gender, spec.Traits, key);
        return spec with { SpeakerId = id, Engine = b.EngineId };
    }

    public void RecastNpc(uint npcId)
    {
        voiceCache.Remove(npcId);
        // Next line from this NPC re-casts; the inputHash change makes it deliberate.
    }

    private void Status(string message)
    {
        if (!config().StatusMessages) return;
        try { chat.Print($"[PopotoVox] {message}"); } catch { /* ignore */ }
    }

    // Choppy-audio self-diagnosis: a streamed line that underruns this many times means generation
    // can't hold real-time pace (usually the GPU is busy) — tell the user (rate-limited) instead of
    // silently stuttering. Independent of the StatusMessages toggle; it's a diagnostic, not status spam.
    private const int ChoppyUnderrunThreshold = 3;
    private static readonly TimeSpan ChoppyWarnCooldown = TimeSpan.FromMinutes(3);
    private DateTime lastChoppyWarn = DateTime.MinValue;

    private void WarnChoppy(int underruns)
    {
        var now = DateTime.UtcNow;
        if (now - lastChoppyWarn < ChoppyWarnCooldown) return;
        lastChoppyWarn = now;
        log.Information($"[Audio] Streaming underran {underruns}x on a line — GPU likely can't keep real-time pace.");
        try
        {
            chat.Print("[PopotoVox] Voice playback is stuttering — your GPU can't generate this voice fast enough " +
                       "to stream it. Turn off \"Stream audio\" in Settings (it'll wait for the whole line instead), " +
                       "or pick a lighter voice (High/Medium).");
        }
        catch { /* ignore */ }
    }

    private static long StableSeed(string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return BitConverter.ToInt64(hash, 0) & long.MaxValue;
    }

    /// <summary>
    /// Feeds streamed PCM into the mixer as its own voice. Always accumulates the full line (for the cache),
    /// but starts PLAYBACK on the first chunk only if this is still the current line for its slot AND the
    /// source (dialogue box / overhead bubble) is still up. If it isn't, the line finishes buffering — so the
    /// cache still gets it — but is never played (latches via <see cref="suppressed"/>).
    /// </summary>
    private sealed class GuardedSink : IAudioSink
    {
        private readonly CastingDirector d;
        private readonly long generation;
        private readonly string slotKey;
        private readonly long renderId;                  // indicator render token (unique per render)
        private readonly string text;
        private readonly string speaker;
        private readonly CancellationToken ct;
        private readonly System.IO.MemoryStream? buffer; // accumulates the full line for the line cache (when capturing)
        private readonly float volumeScale;              // distance-based ambient attenuation (M12a)
        private readonly int priority;                   // M13: playback priority (foreground > background)
        private readonly nint speakerAddress;            // live object to follow for spatial volume
        private readonly System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        private int sampleRate, channels;
        private bool suppressed; // once true, keep buffering but never play (source gone / superseded)

        public bool Started { get; private set; }
        public VoiceHandle Handle { get; private set; } = VoiceHandle.None;
        public long FirstSoundMs { get; private set; }

        public GuardedSink(CastingDirector d, long generation, string slotKey, long renderId, string text, string speaker, CancellationToken ct, bool capture = false, float volumeScale = 1f, int priority = int.MaxValue, nint speakerAddress = 0)
        { this.d = d; this.generation = generation; this.slotKey = slotKey; this.renderId = renderId; this.text = text; this.speaker = speaker; this.ct = ct; buffer = capture ? new System.IO.MemoryStream() : null; this.volumeScale = volumeScale; this.priority = priority; this.speakerAddress = speakerAddress; }

        /// <summary>The full streamed line as one RenderedAudio (for the line cache), or null if not capturing / empty.</summary>
        public RenderedAudio? Captured => buffer is { Length: > 0 } ? new RenderedAudio(buffer.ToArray(), sampleRate, channels) : null;

        public void Begin(int sampleRate, int channels) { this.sampleRate = sampleRate; this.channels = channels; }

        public void Feed(byte[] pcm16)
        {
            buffer?.Write(pcm16, 0, pcm16.Length); // always capture the full line for the cache
            if (suppressed || ct.IsCancellationRequested) return;
            if (!Started)
            {
                // First audio is ready — start playing ONLY if the source is still up; else cache, don't play.
                if (!d.LiveAtPlayTime(slotKey, generation, speakerAddress)) { suppressed = true; return; }
                d.LogPlay(text);
                d.state.EndRender(renderId);    // sound is starting — drop the "Generating…" badge
                d.state.Mark(CastingState.PipelineStage.Playing, speaker);
                Handle = d.audio.Begin(sampleRate, channels, volumeScale, priority); // this line's own mixer voice
                if (!Handle.IsValid) { suppressed = true; return; }                  // cap rejected it
                d.TrackSpatial(Handle, speakerAddress); // follow the speaker live as this streamed line starts
                Started = true;
                FirstSoundMs = sw.ElapsedMilliseconds;
            }
            d.audio.Feed(Handle, pcm16);
        }
    }

    public void Dispose()
    {
        // Only cancel — each in-flight render disposes its own CTS in its finally.
        foreach (var c in slotCts.Values) SafeCancel(c);
    }
}
