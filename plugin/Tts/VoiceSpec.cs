using System;
using System.Text.Json.Serialization;

namespace PopotoVox.Tts;

/// <summary>Where a locked VoiceSpec came from (most-specific wins, PRD §5.3).</summary>
public enum VoiceSource
{
    Rules,
    Llm,
    Override,
}

/// <summary>
/// Per-utterance synthesis parameters. In v1 the per-NPC distinctness lever is
/// <see cref="VoiceSpec.SpeakerId"/> (PRD D4 — distinct speaker identities, not
/// pitch-shifting). LengthScale/NoiseScale/NoiseW are recorded here for forward
/// compatibility and to let rules/LLM express delivery intent; the Piper v1 path
/// applies them as process-global defaults (Piper cannot vary them per line).
/// </summary>
public sealed class VoiceParams
{
    [JsonPropertyName("lengthScale")] public float LengthScale { get; init; } = 1.0f;
    [JsonPropertyName("noiseScale")] public float NoiseScale { get; init; } = 0.667f;
    [JsonPropertyName("noiseW")] public float NoiseW { get; init; } = 0.8f;
}

/// <summary>
/// The cached, LOCKED per-NPC voice decision (PRD §6.1). Once written it is the
/// single source of truth for that identity and never re-derived (§5.6).
/// </summary>
public sealed record VoiceSpec
{
    /// <summary>
    /// Bumped when cached specs must be re-cast: v2 = gender-aware casting; v3 = accent-aware
    /// casting + the Kokoro model swap (old sids/genders are invalid); v4 = age anchored to game
    /// data (fixes the LLM casting nearly every NPC as a child); v5 = no-game-age NPCs (beast
    /// tribes etc.) default to adult instead of the LLM's reflexive child, plus a soft tribe
    /// "heritage cue" added to the casting prompt to improve accents (the LLM still decides); v6 = the
    /// casting AI also writes a free-text voice <see cref="Description"/> (shown on the card); v7 =
    /// fixed the description being dropped in LLM validation (v6 specs have an empty one). The cache
    /// drops specs below this on load so they re-cast correctly (one-time). Overrides survive.
    /// </summary>
    public const int CurrentSchemaVersion = 7;

    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    [JsonPropertyName("npcId")] public uint? NpcId { get; init; }
    [JsonPropertyName("speakerName")] public string SpeakerName { get; init; } = "";
    [JsonPropertyName("engine")] public string Engine { get; init; } = "piper";
    [JsonPropertyName("model")] public string Model { get; init; } = "en_US-libritts-high";
    [JsonPropertyName("speakerId")] public int SpeakerId { get; init; }

    /// <summary>The voice's gender bucket (from game data), so an engine switch re-maps to the right gender.</summary>
    [JsonPropertyName("gender")] public VoiceGender Gender { get; init; } = VoiceGender.Neutral;

    /// <summary>The character-voice intent the cast resolved to; lets a different engine re-pick a fitting voice.</summary>
    [JsonPropertyName("traits")] public VoiceTraits? Traits { get; init; }

    [JsonPropertyName("params")] public VoiceParams Params { get; init; } = new();
    [JsonPropertyName("style")] public string Style { get; init; } = "";

    /// <summary>The casting AI's own-words description of the voice (transparency; shown on the NPC card).</summary>
    [JsonPropertyName("description")] public string Description { get; init; } = "";
    [JsonPropertyName("source")] public VoiceSource Source { get; init; }
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>Hash of record+prompt at cast time; records a deliberate re-cast, never auto-invalidates (§5.6).</summary>
    [JsonPropertyName("inputHash")] public string InputHash { get; init; } = "";
}
