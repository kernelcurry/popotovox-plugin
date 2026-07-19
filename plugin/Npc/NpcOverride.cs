namespace PopotoVox.Npc;

/// <summary>
/// A user/community per-NPC override (PRD §5.3, §7.3). The freeform
/// <see cref="Prompt"/> guides the LLM; the optional pins force exact values
/// regardless of what the LLM or rules would pick.
/// </summary>
public sealed class NpcOverride
{
    public uint NpcId { get; set; }
    public string Prompt { get; set; } = "";
    public int? PinnedSpeakerId { get; set; }
    public float? PinnedLengthScale { get; set; }
}
