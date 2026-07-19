using System.Collections.Generic;
using System.Linq;

namespace PopotoVox.Tts;

/// <summary>
/// The set of voices the casting layer may use for the active engine, with the
/// gender/age/timbre metadata needed to pick one that fits the character (PRD D4 +
/// §5.7). Backed by a <see cref="VoicePalette"/> so selection can be gated to the
/// NPC's gender — the missing piece that let male NPCs get female voices.
///
/// CURATION STATUS: the allowlist still defaults to the full palette (a per-voice
/// quality audit is PRD M2 / §14). Gender gating is independent of that and active now.
/// </summary>
public sealed class SpeakerCatalog
{
    private readonly IReadOnlyList<VoiceInfo> palette;
    private readonly Dictionary<int, VoiceInfo> byId;
    private readonly List<int> allowed;

    /// <summary>How many speaker identities the active TTS model exposes (engine-dependent).</summary>
    public int ModelSpeakerCount => palette.Count;

    public SpeakerCatalog(IReadOnlyList<VoiceInfo> palette, IEnumerable<int>? allowlist = null)
    {
        this.palette = palette;
        byId = palette.ToDictionary(v => v.Id);
        allowed = (allowlist ?? palette.Select(v => v.Id))
            .Where(id => byId.ContainsKey(id))
            .Distinct()
            .OrderBy(id => id)
            .ToList();
        if (allowed.Count == 0)
            allowed = palette.Select(v => v.Id).ToList();
    }

    public int Count => allowed.Count;
    public IReadOnlyList<int> AllowedIds => allowed;
    public bool IsAllowed(int speakerId) => allowed.Contains(speakerId);

    /// <summary>The labeled voice for an id, or null if the id isn't in the palette.</summary>
    public VoiceInfo? Describe(int speakerId) => byId.TryGetValue(speakerId, out var v) ? v : null;

    /// <summary>
    /// Allowed voice ids matching <paramref name="gender"/>. A known gender yields that
    /// gender's voices; an unknown/ambiguous NPC (<see cref="VoiceGender.Neutral"/>)
    /// prefers genuine neutral/unisex voices when the model ships them. Either way, if the
    /// palette labels no voice that way we fall back to the full allowlist, so the pool is
    /// never empty and we never force a wrong-gender voice (PRD §5.7).
    /// </summary>
    public IReadOnlyList<int> PoolFor(VoiceGender gender)
    {
        var pool = allowed.Where(id => byId[id].Gender == gender).ToList();
        return pool.Count > 0 ? pool : allowed;
    }

    /// <summary>
    /// Deterministically maps a stable identity key to an allowed speaker id within the
    /// gender pool. Same key + gender → same voice, on every machine; distinct keys
    /// spread across the pool so characters of the same gender still sound distinct.
    /// </summary>
    public int PickDeterministic(string identityKey, VoiceGender gender = VoiceGender.Neutral)
    {
        var pool = PoolFor(gender);
        var index = (int)(StableHash(identityKey) % (uint)pool.Count);
        return pool[index];
    }

    /// <summary>
    /// Clamps an arbitrary id onto the gender pool: in-pool ids are kept, anything else
    /// is remapped deterministically into the correct-gender pool. With the default
    /// Neutral gender this is the old "clamp onto the allowlist" behaviour.
    /// </summary>
    public int Clamp(int speakerId, string fallbackKey, VoiceGender gender = VoiceGender.Neutral)
    {
        var pool = PoolFor(gender);
        return pool.Contains(speakerId) ? speakerId : PickDeterministic(fallbackKey, gender);
    }

    // FNV-1a — stable across processes/machines, unlike string.GetHashCode().
    private static uint StableHash(string s)
    {
        const uint offset = 2166136261, prime = 16777619;
        var hash = offset;
        foreach (var c in s)
        {
            hash ^= c;
            hash *= prime;
        }
        return hash;
    }
}
