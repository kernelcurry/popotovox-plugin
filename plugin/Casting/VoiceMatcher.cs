using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using PopotoVox.Llm;
using PopotoVox.Npc;
using PopotoVox.Tts;

namespace PopotoVox.Casting;

/// <summary>
/// The actual casting brain (PRD §5.3–5.6). Turns what we know about an NPC — its
/// game-data gender plus, optionally, the LLM's description of a fitting voice — into a
/// concrete, locked <see cref="VoiceSpec"/>. Gender comes from data and GATES the
/// candidate pool, so a male NPC can only ever get a male voice; within that pool the
/// LLM's traits (age + timbre) pick the best match, with a deterministic tiebreak that
/// keeps same-gender characters sounding distinct. This is the logic the old code never
/// had — it handed the model a blind integer, which is why everyone sounded female.
///
/// Used by every path (LLM cast, deterministic fallback, anonymous lines), so all of
/// them are gender-correct.
/// </summary>
public sealed class VoiceMatcher
{
    private readonly SpeakerCatalog catalog;

    public VoiceMatcher(SpeakerCatalog catalog) => this.catalog = catalog;

    /// <summary>
    /// Build the locked spec. <paramref name="llm"/> null means "no LLM" (disabled, not
    /// installed, or it failed/timed out) → a deterministic gender-matched pick.
    /// </summary>
    public VoiceSpec Build(
        NpcRecord record, string fingerprint, LlmOutput? llm,
        NpcOverride? ovr, VoiceParams globals, string engineId)
    {
        var gender = record.Gender.ToVoiceGender();
        // Age is anchored to authoritative game data (body type); accent is left to the LLM, which
        // weighs the character's dialogue/name/origin (only nudged by a soft heritage hint in the
        // prompt) — tribe is a cultural lean, not a rule, so we never override the model's read.
        var traits = llm != null
            ? new VoiceTraits(ResolveAge(record, llm), llm.Timbre, llm.Accent.ToVoiceAccent())
            : null;

        var speakerId = ovr?.PinnedSpeakerId is { } pin
            ? catalog.Clamp(pin, fingerprint)          // explicit user pin — honour it (range only)
            : SelectId(gender, traits, fingerprint);

        var lengthScale = ovr?.PinnedLengthScale ?? llm?.LengthScale ?? AdjustLength(globals.LengthScale, record);
        var style = !string.IsNullOrWhiteSpace(llm?.Style) ? llm!.Style : DescribeStyle(record);
        var source = ovr != null ? VoiceSource.Override : llm != null ? VoiceSource.Llm : VoiceSource.Rules;

        return new VoiceSpec
        {
            NpcId = record.NpcId,
            SpeakerName = record.Name,
            SpeakerId = speakerId,
            Gender = gender,
            Traits = traits,
            Engine = engineId,
            Params = new VoiceParams
            {
                LengthScale = lengthScale,
                NoiseScale = globals.NoiseScale,
                NoiseW = globals.NoiseW,
            },
            Style = style,
            Description = llm?.Description ?? "",
            Source = source,
            CreatedAt = DateTime.UtcNow,
            InputHash = InputHash(record, ovr),
        };
    }

    /// <summary>
    /// Re-pick a speaker id for a *different* engine's palette from a spec's stored
    /// gender + traits, so a voice stays gender-correct and in-character if the engine
    /// changes. Mirrors <see cref="SelectId"/> exactly (same inputs → same result).
    /// </summary>
    public int ReselectId(VoiceGender gender, VoiceTraits? traits, string key) =>
        SelectId(gender, traits, key);

    /// <summary>
    /// Pick a voice from the gender pool. Gender is a hard gate. A requested accent is a
    /// strong preference — narrow to voices of that accent when the engine has any, but
    /// never break gender to honor it (so a male "French" NPC, when the model has no French
    /// male, still gets a male voice of another accent). Within the surviving set, score by
    /// timbre-keyword overlap then age closeness; ties (and the no-traits case) break
    /// deterministically so the same character is stable and different characters spread.
    /// </summary>
    private int SelectId(VoiceGender gender, VoiceTraits? traits, string key)
    {
        if (traits is null) return catalog.PickDeterministic(key, gender);

        IReadOnlyList<int> pool = catalog.PoolFor(gender);
        if (traits.Accent != VoiceAccent.Unknown)
        {
            var byAccent = pool.Where(id => catalog.Describe(id)?.Accent == traits.Accent).ToList();
            if (byAccent.Count > 0) pool = byAccent;   // accent available for this gender → use it
        }

        var scored = pool
            .Select(id => catalog.Describe(id))
            .Where(v => v != null)
            .Select(v => (v!.Id,
                          kw: KeywordOverlap(v.Descriptor, traits.Timbre),
                          ageGap: Math.Abs((int)v.AgeRank - (int)traits.Age)))
            .ToList();
        if (scored.Count == 0) return catalog.PickDeterministic(key, gender);

        var bestKw = scored.Max(s => s.kw);
        var bestGap = scored.Where(s => s.kw == bestKw).Min(s => s.ageGap);
        var best = scored.Where(s => s.kw == bestKw && s.ageGap == bestGap)
            .Select(s => s.Id).OrderBy(id => id).ToList();

        return best[(int)(StableHash(key) % (uint)best.Count)];
    }

    private static int KeywordOverlap(string descriptor, string timbre)
    {
        if (string.IsNullOrWhiteSpace(descriptor) || string.IsNullOrWhiteSpace(timbre)) return 0;
        var a = Words(descriptor);
        return Words(timbre).Count(a.Contains);
    }

    private static HashSet<string> Words(string s) =>
        s.ToLowerInvariant()
            .Split(new[] { ' ', ',', ';', '.', '-', '/', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet();

    /// <summary>
    /// Decide the voice's age band. Like gender, age is anchored to reliable game data
    /// (ENpcBase.BodyType → <see cref="NpcRecord.ApparentAge"/>) rather than trusted to the small
    /// casting LLM, which reflexively answers "child" for nearly every NPC (age is the first
    /// schema field and the model grabs the first enum value). A child/elderly body is taken as-is;
    /// an adult body lets the LLM refine within young/adult/middle-aged but can never drop to child.
    /// </summary>
    private static VoiceAge ResolveAge(NpcRecord record, LlmOutput? llm)
    {
        var llmAge = llm?.Age.ToVoiceAge();
        return record.ApparentAge switch
        {
            "child" => VoiceAge.Child,
            "elderly" => VoiceAge.Elderly,
            "adult" => llmAge is VoiceAge.Young or VoiceAge.Adult or VoiceAge.MiddleAged
                ? llmAge.Value
                : VoiceAge.Adult,
            // No game age data (e.g. non-playable races — beast tribes, moogles). The LLM's age
            // is unreliable and defaults to "child", so reject its reflexive child and assume an
            // adult; only honour a deliberate non-child age it offers.
            _ => llmAge is null or VoiceAge.Child ? VoiceAge.Adult : llmAge.Value,
        };
    }

    /// <summary>A tiny, deterministic delivery nudge from coarse attributes (no LLM).</summary>
    private static float AdjustLength(float baseScale, NpcRecord record)
    {
        // Elderly NPCs read a touch slower; otherwise leave the global default.
        if (record.ApparentAge == "elderly")
            return MathF.Round(baseScale * 1.08f, 3);
        return baseScale;
    }

    private static string DescribeStyle(NpcRecord record)
    {
        var parts = new List<string>();
        if (record.Tribe != null) parts.Add(record.Tribe);
        else if (record.Race != null) parts.Add(record.Race);
        if (record.Gender != null) parts.Add(record.Gender.ToLowerInvariant());
        if (record.Title != null) parts.Add($"\"{record.Title}\"");
        return parts.Count > 0 ? string.Join(", ", parts) : "neutral";
    }

    /// <summary>Records the inputs a spec was cast from (PRD §6.1 inputHash) — never an auto-invalidation trigger.</summary>
    public static string InputHash(NpcRecord record, NpcOverride? ovr)
    {
        var material = new StringBuilder()
            .Append(record.Name).Append('|')
            .Append(record.ModelHashSeed).Append('|')
            .Append(ovr?.Prompt ?? "").Append('|')
            .Append(ovr?.PinnedSpeakerId?.ToString() ?? "");
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(material.ToString()));
        return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
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
