using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace PopotoVox.Dialogue;

/// <summary>
/// Captures NPC OVERHEAD SPEECH BUBBLES — the floating remarks NPCs make spontaneously as you pass, with no
/// interaction. Each <c>Character</c> carries an inline <c>Balloon</c> (text + timer) at 0x21E0, and the
/// active bubble text IS in <c>Balloon.Text</c> (confirmed via /pvox bubbles). We poll the object table a few
/// times a second and voice each bubble ONCE after its text settles (force-emit after ~1.6 s), re-arming on
/// text-clear (debounced) or a <c>PlayTimer</c> jump-up (looping bubble). Tracking is keyed by the object's
/// ADDRESS — many ambient/nameless NPCs share <c>EntityId == 0</c>, which collided in the dictionary and
/// thrashed the settle state (the original silence bug). Default OFF; distance-culled + per-poll capped.
/// </summary>
public sealed unsafe class MiniTalkDialogueSource : IDialogueSource
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(400);
    private const int MaxPerPoll = 3;          // voice only the nearest few new bubbles per tick (crowd guard)
    private const int AbsentPollsToForget = 4; // a bubble must be gone this many polls before we re-arm it
    private const int ForceEmitPolls = 4;      // voice even if the text never fully settles, after ~1.6 s

    private readonly IFramework framework;
    private readonly IObjectTable objects;
    private readonly IClientState clientState;
    private readonly Func<Configuration> config;
    private readonly IPluginLog log;

    private bool subscribed;
    private DateTime lastPoll = DateTime.MinValue;
    private readonly Dictionary<nint, BubbleState> bubbles = new(); // object Address -> per-NPC bubble tracking

    public string Name => "Ambient bubbles"; // must NOT contain Talk/Battle/Chat (SystemPage.MapSource)
    public event Action<DialogueEvent>? Captured;

    public MiniTalkDialogueSource(IFramework framework, IObjectTable objects, IClientState clientState,
        Func<Configuration> config, IPluginLog log)
    {
        this.framework = framework;
        this.objects = objects;
        this.clientState = clientState;
        this.config = config;
        this.log = log;
    }

    public bool SanityCheck(out string error) { error = ""; return true; } // polling — always available

    public void Start()
    {
        if (subscribed) return;
        framework.Update += OnUpdate;
        subscribed = true;
    }

    public void Stop()
    {
        if (!subscribed) return;
        framework.Update -= OnUpdate;
        subscribed = false;
        bubbles.Clear();
    }

    public void Dispose() => Stop();

    private void OnUpdate(IFramework fw)
    {
        if (!config().CaptureMiniTalk) return; // cheap early-out when ambient capture is off
        var now = DateTime.UtcNow;
        if (now - lastPoll < PollInterval) return;
        lastPoll = now;

        try
        {
            var cull = config().AmbientHearingYalms;
            var origin = objects[0]?.Position; // slot 0 = local player
            List<(nint Key, string Speaker, uint NpcId, string Text, float? Distance, float Sort)>? ready = null;

            foreach (var obj in objects)
            {
                if (obj.ObjectKind is not (ObjectKind.EventNpc or ObjectKind.BattleNpc)) continue;
                var chr = (Character*)obj.Address;
                if (chr == null) continue;
                var key = obj.Address; // stable + unique per live object (EntityId can be 0 for nameless NPCs)

                // The active bubble text lives in Balloon.Text (empty when no bubble is up).
                var text = chr->Balloon.Text.ToString();
                var timer = chr->Balloon.PlayTimer;
                var showing = !string.IsNullOrWhiteSpace(text);

                if (!bubbles.TryGetValue(key, out var st)) { st = new BubbleState(); bubbles[key] = st; }

                if (!showing)
                {
                    if (++st.Absent >= AbsentPollsToForget) bubbles.Remove(key); // gone (debounced) → re-arm
                    continue;
                }
                st.Absent = 0;

                // A fresh bubble re-arms a previously-voiced NPC: text changed (new line) or the timer jumped up.
                if (st.Emitted && (text != st.LastText || timer > st.LastTimer + 0.05f))
                {
                    st.Emitted = false;
                    st.Polls = 0;
                }
                st.LastTimer = timer;

                if (st.Emitted) continue;

                var settled = text == st.LastText; // reveal finished (text unchanged since last poll)?
                st.LastText = text;
                st.Polls++;
                if (st.Polls == 1)
                    log.Information($"[Ambient] (detect) {obj.Name.TextValue} ~{(origin is { } o ? Vector3.Distance(o, obj.Position) : 0):0}y: \"{(text.Length > 50 ? text[..50] + "…" : text)}\"");
                if (!settled && st.Polls < ForceEmitPolls) continue; // give the reveal a moment, but never forever

                var dist = origin is { } p ? Vector3.Distance(p, obj.Position) : 0f;
                if (cull > 0 && dist > cull) continue; // out of hearing range (retry if the player moves closer)
                (ready ??= new()).Add((key, obj.Name.TextValue, obj.BaseId, text, origin is null ? null : dist, dist));
            }

            if (ready != null)
                foreach (var c in ready.OrderBy(c => c.Sort).Take(MaxPerPoll))
                {
                    if (bubbles.TryGetValue(c.Key, out var st)) st.Emitted = true; // mark only the ones we voice
                    log.Information($"[Ambient] VOICE {c.Speaker} (~{c.Sort:0}y): \"{(c.Text.Length > 60 ? c.Text[..60] + "…" : c.Text)}\"");
                    Captured?.Invoke(new DialogueEvent(
                        Speaker: c.Speaker, NpcId: c.NpcId, Source: DialogueSourceKind.MiniTalk,
                        Language: clientState.ClientLanguage, Text: c.Text, CapturedAt: now, Distance: c.Distance,
                        SpeakerAddress: c.Key));
                }

            if (bubbles.Count > 1024) bubbles.Clear(); // bound memory across a long session
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[Ambient] bubble poll failed.");
        }
    }

    /// <summary>Dev diagnostic (/pvox bubbles): dump nearby NPCs' raw balloon fields + the global queue so we can
    /// see exactly where a bubble's text lives.</summary>
    public void DumpBubbles()
    {
        static string Clip(string s) => string.IsNullOrEmpty(s) ? "" : (s.Length > 45 ? s[..45] + "…" : s);

        var origin = objects[0]?.Position;
        var npcs = new List<(IGameObject Obj, float Dist)>();
        foreach (var obj in objects)
            if (obj.ObjectKind is ObjectKind.EventNpc or ObjectKind.BattleNpc)
                npcs.Add((obj, origin is { } p ? Vector3.Distance(p, obj.Position) : 0f));

        log.Information($"[BubbleDump] === {npcs.Count} nearby NPCs; nearest 12 per-Character balloon state ===");
        foreach (var (obj, dist) in npcs.OrderBy(n => n.Dist).Take(12))
        {
            var chr = (Character*)obj.Address;
            if (chr == null) continue;
            ref var b = ref chr->Balloon;
            ref var y = ref chr->YellBalloon;
            log.Information($"[BubbleDump] {obj.Name.TextValue} ~{dist:0}y | Balloon now={b.NowPlayingBalloonId} def={b.DefaultBalloonId} " +
                           $"timer={b.PlayTimer:0.0} state={b.State} txt=\"{Clip(b.Text.ToString())}\" || Yell state={y.State} txt=\"{Clip(y.Text.ToString())}\"");
        }

        try
        {
            var agent = AgentScreenLog.Instance();
            if (agent != null)
            {
                ref var q = ref agent->BalloonQueue;
                var arr = q.ToArray();
                log.Information($"[BubbleDump] AgentScreenLog.BalloonQueue count={arr.Length} counter={agent->BalloonCounter} hasUpdate={agent->BalloonsHaveUpdate}");
                foreach (var bi in arr)
                    log.Information($"[BubbleDump]   queue: dist={bi.CameraDistance:0} hasChar={bi.Character != null} " +
                                   $"fmt=\"{Clip(bi.FormattedText.ToString())}\" orig=\"{Clip(bi.OriginalText.ToString())}\"");
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[BubbleDump] balloon-queue read failed.");
        }
    }

    private sealed class BubbleState
    {
        public string LastText = "";
        public bool Emitted;    // voiced the current text already?
        public int Absent;      // consecutive polls with no bubble (debounce before re-arming)
        public int Polls;       // polls since this bubble appeared (force-emit fallback)
        public float LastTimer; // last seen PlayTimer (a jump up = a fresh bubble on a loop)
    }
}
