using System;
using System.Collections.Generic;

namespace PopotoVox.Npc;

/// <summary>
/// Everything we could assemble about an NPC from static game data plus runtime
/// observation (PRD §6.2). Feeds both the casting layer and the bio card. Fields
/// are best-effort: not every NPC has clean race/gender, hence
/// <see cref="DataConfidence"/> which flags what is reliable vs. missing.
/// </summary>
public sealed class NpcRecord
{
    public uint? NpcId { get; set; }
    public string Name { get; set; } = "";
    public string? Title { get; set; }

    public string? Race { get; set; }
    public string? Tribe { get; set; }
    public string? Gender { get; set; }
    public string? BodyType { get; set; }

    /// <summary>Apparent age read from the model body type: "child" | "adult" | "elderly".</summary>
    public string? ApparentAge { get; set; }

    /// <summary>Build read from the model scale, only when notable: "towering" | "large" | "small" | "child-sized".</summary>
    public string? Stature { get; set; }

    /// <summary>The weapon category the NPC wields, e.g. "Conjurer's Arm" (a class/personality cue).</summary>
    public string? Weapon { get; set; }

    /// <summary>Coarse role classified from the weapon, e.g. "mage", "knight/soldier", "archer".</summary>
    public string? Role { get; set; }

    /// <summary>The NPC's class read from their weapon (e.g. "Conjurer"), when the item maps to one.</summary>
    public string? Job { get; set; }

    /// <summary>Named worn gear ("Body: Hempen Tunic (Soot Black)") — the readable list the casting
    /// LLM reads. <see cref="Gear"/> is the per-slot view (incl. empty/unidentified) for the card.</summary>
    public List<string> Equipment { get; set; } = new();

    /// <summary>Every non-empty worn slot with its state. <see cref="GearSlot.Item"/> is null when the
    /// slot is filled but maps to no nameable item; truly-empty slots are simply absent from the list.
    /// Drives the card's full paper-doll (empty "--" vs. unidentified "?").</summary>
    public List<GearSlot> Gear { get; set; } = new();

    public List<string> Zones { get; set; } = new();
    public string? Affiliation { get; set; }

    /// <summary>When we last heard this NPC speak (UTC). <see cref="DateTime.MinValue"/> means never
    /// heard. Runtime-observed (not resolver output) — drives the "Recent" list and the card's
    /// "Last heard" line. Old records deserialize to MinValue and sort last in Recent until heard.</summary>
    public DateTime LastSpokeUtc { get; set; }

    /// <summary>Lines we've actually heard this NPC say — context for the LLM and the bio card.</summary>
    public List<string> SampleLines { get; set; } = new();

    /// <summary>Per-field reliability: "high" | "low" | "missing".</summary>
    public Dictionary<string, string> DataConfidence { get; set; } = new();

    /// <summary>Stable input for the identity fingerprint's modelHash (PRD D13).</summary>
    public string ModelHashSeed { get; set; } = "";

    /// <summary>
    /// Bumped when the resolver's static-data logic changes so stale cached records re-resolve
    /// once (e.g. records assembled before <see cref="ApparentAge"/> existed). On upgrade the
    /// runtime-accumulated <see cref="SampleLines"/> and <see cref="Zones"/> are preserved.
    /// Records loaded from before this field existed deserialize to 0 → below current → refreshed.
    /// v2 = full worn gear (accessories + off-hand) and weapon-derived <see cref="Job"/>;
    /// v3 = per-slot <see cref="Gear"/> (records every position, incl. unidentified).
    /// </summary>
    public const int CurrentSchemaVersion = 3;

    public int SchemaVersion { get; set; }
}

/// <summary>One worn-equipment position for the card's paper-doll. <see cref="Item"/> is the readable
/// item name (with dye) when identifiable, or null when the slot is filled but maps to no nameable
/// player item (present-but-unidentified). Truly-empty slots are omitted from <see cref="NpcRecord.Gear"/>.</summary>
public sealed class GearSlot
{
    public string Slot { get; set; } = "";
    public string? Item { get; set; }
}
