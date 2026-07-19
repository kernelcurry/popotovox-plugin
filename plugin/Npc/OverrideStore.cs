using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using PopotoVox.Infrastructure;

namespace PopotoVox.Npc;

/// <summary>Persisted per-NPC overrides, keyed by NpcId.</summary>
public sealed class OverrideStore
{
    private readonly string path;
    private readonly ConcurrentDictionary<uint, NpcOverride> overrides;

    public OverrideStore(string path)
    {
        this.path = path;
        var loaded = JsonFileStore.Load(path, () => new Dictionary<uint, NpcOverride>());
        overrides = new ConcurrentDictionary<uint, NpcOverride>(loaded);
    }

    public bool TryGet(uint npcId, out NpcOverride ovr) => overrides.TryGetValue(npcId, out ovr!);

    public void Put(NpcOverride ovr)
    {
        overrides[ovr.NpcId] = ovr;
        Save();
    }

    public bool Remove(uint npcId)
    {
        var removed = overrides.TryRemove(npcId, out _);
        if (removed) Save();
        return removed;
    }

    /// <summary>Erase every override. These are user-authored, so callers should confirm first.</summary>
    public void Clear()
    {
        overrides.Clear();
        Save();
    }

    public int Count => overrides.Count;
    public IReadOnlyDictionary<uint, NpcOverride> All => overrides;

    private void Save() => JsonFileStore.Save(path, overrides.ToDictionary(kv => kv.Key, kv => kv.Value));
}
