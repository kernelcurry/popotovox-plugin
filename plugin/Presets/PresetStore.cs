using System;
using System.Linq;
using PopotoVox.Infrastructure;
using PopotoVox.Npc;
using PopotoVox.Tts;

namespace PopotoVox.Presets;

/// <summary>
/// Imports/exports preset packs. A pack carries per-NPC overrides and optionally
/// pre-baked VoiceSpecs so a community pack can be shared and applied without each
/// user re-running the cast. Models/voices are referenced by id only — never
/// embedded (PRD §6.3).
/// </summary>
public sealed class PresetStore
{
    private readonly OverrideStore overrides;
    private readonly VoiceSpecCache voiceCache;

    public PresetStore(OverrideStore overrides, VoiceSpecCache voiceCache)
    {
        this.overrides = overrides;
        this.voiceCache = voiceCache;
    }

    /// <summary>Snapshot the current overrides (and optionally locked specs) into a pack file.</summary>
    public void Export(string path, PresetMeta meta, bool includeVoiceSpecs)
    {
        var preset = new Preset
        {
            Meta = meta,
            NpcOverrides = overrides.All.Values.Select(Clone).ToList(),
            VoiceSpecs = includeVoiceSpecs ? voiceCache.All.Values.ToList() : new(),
        };
        JsonFileStore.Save(path, preset);
    }

    /// <summary>Load a pack's contents without applying anything — for previewing it in the UI.
    /// Returns null if the file can't be read/parsed.</summary>
    public Preset? Read(string path) => JsonFileStore.Load<Preset?>(path, () => null);

    /// <summary>Apply a pack: its overrides win, and any baked specs seed the cache.</summary>
    public Preset Import(string path)
    {
        var preset = JsonFileStore.Load<Preset?>(path, () => null)
            ?? throw new InvalidOperationException("Could not read preset file.");

        foreach (var ovr in preset.NpcOverrides)
            overrides.Put(ovr);

        foreach (var spec in preset.VoiceSpecs)
            if (spec.NpcId is { } id)
                voiceCache.Put(id, spec);

        return preset;
    }

    private static NpcOverride Clone(NpcOverride o) => new()
    {
        NpcId = o.NpcId,
        Prompt = o.Prompt,
        PinnedSpeakerId = o.PinnedSpeakerId,
        PinnedLengthScale = o.PinnedLengthScale,
    };
}
