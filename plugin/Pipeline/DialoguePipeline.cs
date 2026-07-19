using System;
using PopotoVox.Casting;
using PopotoVox.Dialogue;
using PopotoVox.Npc;

namespace PopotoVox.Pipeline;

/// <summary>
/// Connects captured dialogue to the casting director. Runs on the framework thread
/// (capture callbacks already do), which is exactly where NPC identity must be
/// resolved from live game objects — so we resolve the NpcId here, then hand the
/// line off to the director for the (async) cast/render work.
/// </summary>
public sealed class DialoguePipeline : IDisposable
{
    private readonly DialogueCapture capture;
    private readonly NpcResolver resolver;
    private readonly CastingDirector director;
    private readonly Func<Configuration> config;
    private readonly DialogueProbe? probe; // M10 spike: optional dialogue-vs-static measurement

    public DialoguePipeline(
        DialogueCapture capture, NpcResolver resolver, CastingDirector director, Func<Configuration> config,
        DialogueProbe? probe = null)
    {
        this.capture = capture;
        this.resolver = resolver;
        this.director = director;
        this.config = config;
        this.probe = probe;

        capture.Captured += OnCaptured;
    }

    private void OnCaptured(DialogueEvent evt)
    {
        var enabled = IsSourceEnabled(evt.Source);
        var preview = evt.Text.Length > 40 ? evt.Text[..40] + "…" : evt.Text;
        Plugin.Log.Debug($"[Dialogue] captured {evt.Source} enabled={enabled} speaker=\"{evt.Speaker}\" text=\"{preview}\"");

        if (!enabled) return;
        if (string.IsNullOrWhiteSpace(evt.Text)) return;

        // Bubble capture already knows the exact speaker (evt.NpcId); other sources resolve by name.
        uint? npcId = evt.NpcId;
        if (npcId is null)
        {
            try { npcId = resolver.TryResolveNpcId(evt.Speaker); }
            catch (Exception ex) { Plugin.Log.Warning(ex, "[Dialogue] NpcId resolve failed; continuing without it."); }
        }

        director.Submit(evt.Speaker, evt.Text, npcId, evt.Source, evt.Distance, evt.SpeakerAddress);
        probe?.Observe(npcId, evt.Speaker, evt.Text, evt.Source);
    }

    private bool IsSourceEnabled(DialogueSourceKind source)
    {
        var cfg = config();
        return source switch
        {
            DialogueSourceKind.ChatGui => cfg.CaptureChatGui,
            DialogueSourceKind.AddonTalk => cfg.CaptureAddonTalk,
            DialogueSourceKind.AddonBattleTalk => cfg.CaptureAddonBattleTalk,
            DialogueSourceKind.MiniTalk => cfg.CaptureMiniTalk,
            _ => true,
        };
    }

    public void Dispose() => capture.Captured -= OnCaptured;
}
