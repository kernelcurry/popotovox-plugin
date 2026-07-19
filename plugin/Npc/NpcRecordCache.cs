using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using PopotoVox.Infrastructure;

namespace PopotoVox.Npc;

/// <summary>
/// Caches assembled <see cref="NpcRecord"/>s by NpcId and accumulates sample lines
/// as they're heard. Sample lines are capped so the cache (and the prompt context
/// built from it) stays small.
/// </summary>
public sealed class NpcRecordCache
{
    private const int MaxSampleLines = 5;

    private readonly string path;
    private readonly ConcurrentDictionary<uint, NpcRecord> records;
    private readonly object mutateGate = new();

    public NpcRecordCache(string path)
    {
        this.path = path;
        var loaded = JsonFileStore.Load(path, () => new Dictionary<uint, NpcRecord>());
        records = new ConcurrentDictionary<uint, NpcRecord>(loaded);
    }

    public bool TryGet(uint npcId, out NpcRecord record) => records.TryGetValue(npcId, out record!);

    public void Put(NpcRecord record)
    {
        if (record.NpcId is not { } id) return;
        records[id] = record;
        Save();
    }

    /// <summary>Records a freshly-heard line on an NPC's record, de-duped and capped.</summary>
    public void AddSampleLine(uint npcId, string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        lock (mutateGate)
        {
            if (!records.TryGetValue(npcId, out var record)) return;
            if (record.SampleLines.Contains(line)) return;

            // Copy-on-write: swap in a fresh list rather than mutating in place, so a
            // concurrent reader (prompt builder) or serializer never sees a torn list.
            var updated = new List<string>(record.SampleLines) { line };
            while (updated.Count > MaxSampleLines)
                updated.RemoveAt(0);
            record.SampleLines = updated;
            Save();
        }
    }

    /// <summary>Stamps the moment we last heard this NPC speak — drives the "Recent" list and the
    /// card's "Last heard" line. Called on every spoken line, so it always reflects "talked to".</summary>
    public void MarkSeen(uint npcId)
    {
        lock (mutateGate)
        {
            if (!records.TryGetValue(npcId, out var record)) return;
            record.LastSpokeUtc = DateTime.UtcNow;
            Save();
        }
    }

    public void Clear()
    {
        records.Clear();
        Save();
    }

    public int Count => records.Count;
    public IReadOnlyDictionary<uint, NpcRecord> All => records;

    private void Save() => JsonFileStore.Save(path, records.ToDictionary(kv => kv.Key, kv => kv.Value));
}
