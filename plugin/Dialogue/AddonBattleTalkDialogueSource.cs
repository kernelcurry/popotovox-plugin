using System;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace PopotoVox.Dialogue;

/// <summary>
/// Captures combat / mid-fight dialogue from the "_BattleTalk" addon — the
/// banner-style line that pops mid-encounter (boss taunts, ally callouts).
///
/// Same capture strategy as <see cref="AddonTalkDialogueSource"/>: watch the
/// addon's PostUpdate and dedup down to one event per distinct line. Reads go
/// through our own <see cref="BattleTalkAddon"/> view since ClientStructs has no
/// definition for this addon.
/// </summary>
public sealed unsafe class AddonBattleTalkDialogueSource : IDialogueSource
{
    private const string AddonName = "_BattleTalk";

    private readonly IAddonLifecycle addonLifecycle;
    private readonly IClientState clientState;
    private bool subscribed;

    private string lastSpeaker = string.Empty;
    private string lastText = string.Empty;
    private DateTime startedAt = DateTime.MinValue;

    // Ignore whatever is "up" right after we start so reloading doesn't voice a stale banner line.
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(1.5);

    public string Name => "AddonBattleTalk";
    public event Action<DialogueEvent>? Captured;

    public AddonBattleTalkDialogueSource(IAddonLifecycle addonLifecycle, IClientState clientState)
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

    // Forget the last line when the banner closes so a repeated identical line later
    // is still captured.
    private void OnAddonClosed(AddonEvent type, AddonArgs args) => ResetDedup();

    private void ResetDedup()
    {
        lastSpeaker = string.Empty;
        lastText = string.Empty;
    }

    public void Dispose() => Stop();

    private void OnAddonEvent(AddonEvent type, AddonArgs args)
    {
        // Only capture while the banner is on screen; reset when it hides so a repeated line plays.
        var unit = (AtkUnitBase*)args.Addon.Address;
        if (unit == null || !unit->IsVisible) { ResetDedup(); return; }

        var addon = (BattleTalkAddon*)args.Addon.Address;
        var text = AddonTextReader.Read(addon->Text);
        if (string.IsNullOrWhiteSpace(text)) return;

        var speaker = AddonTextReader.Read(addon->Speaker);

        if (speaker == lastSpeaker && text == lastText) return;
        lastSpeaker = speaker;
        lastText = text;

        if (DateTime.UtcNow - startedAt < StartupGrace) return; // don't voice a stale banner at load

        Captured?.Invoke(new DialogueEvent(
            Speaker: speaker,
            NpcId: null, // identity resolution arrives in M2
            Source: DialogueSourceKind.AddonBattleTalk,
            Language: clientState.ClientLanguage,
            Text: text,
            CapturedAt: DateTime.UtcNow));
    }
}
