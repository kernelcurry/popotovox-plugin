using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using PopotoVox.Infrastructure;

namespace PopotoVox.Npc;

/// <summary>
/// Maps an identity fingerprint (PRD D13) to the NpcId whose locked VoiceSpec it
/// should reuse. This is the mechanism that links different NpcIds belonging to the
/// same logical character so they share one voice. Persisted alongside the specs.
/// </summary>
public sealed class IdentityCrossLink
{
    private readonly string path;
    private readonly ConcurrentDictionary<string, uint> map;

    public IdentityCrossLink(string path)
    {
        this.path = path;
        var loaded = JsonFileStore.Load(path, () => new Dictionary<string, uint>());
        map = new ConcurrentDictionary<string, uint>(loaded);
    }

    public bool TryResolve(string fingerprint, out uint npcId) => map.TryGetValue(fingerprint, out npcId);

    public void Put(string fingerprint, uint npcId)
    {
        map[fingerprint] = npcId;
        Save();
    }

    public void Clear()
    {
        map.Clear();
        Save();
    }

    public int Count => map.Count;

    private void Save() => JsonFileStore.Save(path, map.ToDictionary(kv => kv.Key, kv => kv.Value));
}
