using System.Collections.Generic;
using System.Text.Json.Serialization;
using PopotoVox.Npc;

namespace PopotoVox.Llm;

/// <summary>Everything the casting director hands the LLM for one NPC (PRD §5.5 input).</summary>
public sealed record CastingRequest(
    NpcRecord Record,
    string? OverridePrompt,
    float Temperature,
    long Seed);

/// <summary>
/// The constrained shape the LLM must return (validated before use). The LLM describes
/// the *kind of voice* that fits the character — age, timbre, pace, mood — and the
/// <see cref="PopotoVox.Casting.VoiceMatcher"/> maps that onto a concrete voice of the
/// NPC's (game-data) gender from the active engine's palette. The LLM no longer picks a
/// raw speaker id, which is what let it choose an out-of-gender voice.
/// </summary>
public sealed class LlmOutput
{
    /// <summary>"child" | "young" | "adult" | "middleAged" | "elderly".</summary>
    [JsonPropertyName("age")] public string Age { get; set; } = "adult";

    /// <summary>A few timbre keywords, e.g. "deep, gravelly, weary".</summary>
    [JsonPropertyName("timbre")] public string Timbre { get; set; } = "";

    /// <summary>
    /// The real-world accent that fits the character's homeland/culture — one of the menu in
    /// <see cref="VoiceSpecSchema"/>. The matcher prefers a voice of this accent (gender stays hard).
    /// </summary>
    [JsonPropertyName("accent")] public string Accent { get; set; } = "";

    [JsonPropertyName("lengthScale")] public float LengthScale { get; set; } = 1.0f;
    [JsonPropertyName("style")] public string Style { get; set; } = "";

    /// <summary>1-2 sentences in the model's own words: what this voice should sound like and why (shown to the player).</summary>
    [JsonPropertyName("description")] public string Description { get; set; } = "";
}

/// <summary>Renders an NPC casting decision, or null if the model is unavailable/failed.</summary>
public interface ILlmClient : System.IDisposable
{
    bool Enabled { get; }

    /// <summary>True only when the runtime AND the model are both present on disk. When false, casting
    /// silently falls back to the rules engine — callers should surface that so the user knows to download it.</summary>
    bool Installed { get; }

    System.Threading.Tasks.Task<LlmOutput?> CastAsync(CastingRequest request, System.Threading.CancellationToken ct = default);

    /// <summary>Free-form completion (used by the emotion annotator). Null if unavailable.</summary>
    System.Threading.Tasks.Task<string?> CompleteTextAsync(
        string prompt, int maxTokens, float temperature, System.Threading.CancellationToken ct = default);
}
