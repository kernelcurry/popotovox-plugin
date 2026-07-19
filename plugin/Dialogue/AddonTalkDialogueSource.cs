using System;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace PopotoVox.Dialogue;

/// <summary>
/// Captures dialogue from the main "Talk" addon — the standard NPC dialogue
/// window (speaker name + body text) that carries the bulk of quest and story
/// speech. This is the most important surface for PopotoVox.
///
/// Capture strategy (ours): we listen on the addon's lifecycle rather than
/// hooking text writes. We watch PostUpdate, which re-fires each frame the window
/// is on screen, and reduce that stream to one event per distinct line with a
/// (speaker, text) dedup guard. Polling-via-lifecycle is deliberately chosen over
/// PostRefresh: it cannot miss a line whose text lands a frame after the refresh,
/// and the per-frame cost (two short string reads, only while talking) is trivial.
/// </summary>
public sealed unsafe class AddonTalkDialogueSource : IDialogueSource
{
    private const string AddonName = "Talk";

    private readonly IAddonLifecycle addonLifecycle;
    private readonly IClientState clientState;
    private bool subscribed;

    // Last line we emitted, for dedup across the repeated lifecycle callbacks.
    private string lastSpeaker = string.Empty;
    private string lastText = string.Empty;
    private DateTime startedAt = DateTime.MinValue;

    // FFXIV keeps the Talk addon alive with the previous conversation's text and keeps firing
    // PostUpdate on it even when it isn't on screen. So we ignore whatever is "up" for a moment
    // right after we start — otherwise reloading the plugin voices a stale, long-finished line.
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(1.5);

    public string Name => "AddonTalk";
    public event Action<DialogueEvent>? Captured;

    public AddonTalkDialogueSource(IAddonLifecycle addonLifecycle, IClientState clientState)
    {
        this.addonLifecycle = addonLifecycle;
        this.clientState = clientState;
    }

    public bool SanityCheck(out string error)
    {
        error = "";
        if (addonLifecycle == null!)
        {
            error = "IAddonLifecycle service unavailable";
            return false;
        }

        // The Talk addon only exists while a dialogue is on screen, so we cannot
        // probe its nodes at startup. Reads are guarded defensively instead.
        return true;
    }

    public void Start()
    {
        if (subscribed) return;
        startedAt = DateTime.UtcNow;
        addonLifecycle.RegisterListener(AddonEvent.PostUpdate, AddonName, OnAddonEvent);
        addonLifecycle.RegisterListener(AddonEvent.PreFinalize, AddonName, OnAddonClosed);
        subscribed = true;
    }

    public void Stop()
    {
        if (!subscribed) return;
        addonLifecycle.UnregisterListener(AddonEvent.PostUpdate, AddonName, OnAddonEvent);
        addonLifecycle.UnregisterListener(AddonEvent.PreFinalize, AddonName, OnAddonClosed);
        subscribed = false;
        ResetDedup();
    }

    // When the dialogue window closes, forget the last line so the same NPC saying
    // the same thing in a later conversation is captured again rather than de-duped.
    private void OnAddonClosed(AddonEvent type, AddonArgs args) => ResetDedup();

    private void ResetDedup()
    {
        lastSpeaker = string.Empty;
        lastText = string.Empty;
    }

    public void Dispose() => Stop();

    private void OnAddonEvent(AddonEvent type, AddonArgs args)
    {
        // Only capture while the window is actually on screen. When it hides (conversation over),
        // forget the last line so re-talking to the same NPC — even the identical opening line —
        // is captured and voiced again. This is what makes a repeat talk play.
        var unit = (AtkUnitBase*)args.Addon.Address;
        if (unit == null || !unit->IsVisible) { ResetDedup(); return; }

        var addon = (AddonTalk*)args.Addon.Address;
        var text = AddonTextReader.Read(addon->AtkTextNode228);
        if (string.IsNullOrWhiteSpace(text)) return;

        var speaker = AddonTextReader.Read(addon->AtkTextNode220);

        if (speaker == lastSpeaker && text == lastText) return;
        lastSpeaker = speaker;
        lastText = text;

        // Don't speak whatever box happens to be up the instant we load — it's a stale, already-
        // finished line. We still recorded it above so it's de-duped, just never voiced.
        if (DateTime.UtcNow - startedAt < StartupGrace) return;

        Captured?.Invoke(new DialogueEvent(
            Speaker: speaker,
            NpcId: null, // identity resolution arrives in M2
            Source: DialogueSourceKind.AddonTalk,
            Language: clientState.ClientLanguage,
            Text: text,
            CapturedAt: DateTime.UtcNow));
    }
}
