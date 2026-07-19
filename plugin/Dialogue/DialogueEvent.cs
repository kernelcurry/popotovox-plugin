using System;
using Dalamud.Game;

namespace PopotoVox.Dialogue;

public enum DialogueSourceKind
{
    ChatGui,
    AddonTalk,
    AddonBattleTalk,
    MiniTalk, // overhead NPC speech bubbles (ambient chatter)
}

public sealed record DialogueEvent(
    string Speaker,
    uint? NpcId,
    DialogueSourceKind Source,
    ClientLanguage Language,
    string Text,
    DateTime CapturedAt,
    float? Distance = null, // yalms from the player (set by the ambient/bubble source for distance-volume)
    nint SpeakerAddress = 0) // live object address of the speaker (ambient bubbles set it for M16 spatial tracking)
{
    public override string ToString() => $"[{Source}] {Speaker}: {Text}";
}
