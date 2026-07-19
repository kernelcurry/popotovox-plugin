using System.Collections.Generic;
using PopotoVox.Npc;
using PopotoVox.Tts;

namespace PopotoVox.Presets;

/// <summary>An importable/exportable voice pack (PRD §6.3).</summary>
public sealed class Preset
{
    public int SchemaVersion { get; set; } = 1;
    public PresetMeta Meta { get; set; } = new();
    public List<NpcOverride> NpcOverrides { get; set; } = new();

    /// <summary>Optional pre-baked exact results to share, so importers skip casting.</summary>
    public List<VoiceSpec> VoiceSpecs { get; set; } = new();
}

public sealed class PresetMeta
{
    public string Name { get; set; } = "";
    public string Author { get; set; } = "";
    public string Description { get; set; } = "";
    public int Priority { get; set; } = 50;
}
