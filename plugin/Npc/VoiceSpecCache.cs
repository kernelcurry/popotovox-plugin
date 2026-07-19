using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using PopotoVox.Infrastructure;
using PopotoVox.Tts;

namespace PopotoVox.Npc;

/// <summary>
/// The locked per-NPC voice decisions, keyed by NpcId and persisted to disk
/// (PRD §5.6). Once a spec is written it is the single source of truth for that
/// identity; the render path reads it directly and never re-derives. Invalidation
/// only ever happens through explicit user action (clear / re-cast).
/// </summary>
public sealed class VoiceSpecCache
{
    private readonly string path;
    private readonly ConcurrentDictionary<uint, VoiceSpec> specs;

    public VoiceSpecCache(string path)
    {
        this.path = path;
        var loaded = JsonFileStore.Load(path, () => new Dictionary<uint, VoiceSpec>());

        // One-time migration: specs cast before gender-aware casting (schemaVersion < 2)
        // picked a voice blind to gender, so a male NPC could have a female voice. Drop
        // them — they recast correctly on next encounter. User overrides live in a
        // separate store and are untouched, so pins survive.
        var stale = loaded.Where(kv => kv.Value.SchemaVersion < VoiceSpec.CurrentSchemaVersion)
            .Select(kv => kv.Key).ToList();
        foreach (var key in stale) loaded.Remove(key);

        specs = new ConcurrentDictionary<uint, VoiceSpec>(loaded);
        if (stale.Count > 0) Save();
    }

    public bool TryGet(uint npcId, out VoiceSpec spec) => specs.TryGetValue(npcId, out spec!);

    public void Put(uint npcId, VoiceSpec spec)
    {
        specs[npcId] = spec;
        Save();
    }

    public bool Remove(uint npcId)
    {
        var removed = specs.TryRemove(npcId, out _);
        if (removed) Save();
        return removed;
    }

    public void Clear()
    {
        specs.Clear();
        Save();
    }

    public int Count => specs.Count;
    public IReadOnlyDictionary<uint, VoiceSpec> All => specs;

    private void Save() => JsonFileStore.Save(path, specs.ToDictionary(kv => kv.Key, kv => kv.Value));
}
