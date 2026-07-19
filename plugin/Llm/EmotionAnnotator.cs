using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace PopotoVox.Llm;

/// <summary>The directed form of a line: the (unchanged) spoken text plus an optional emotion instruct —
/// a short free-text delivery direction the engine performs.</summary>
public readonly record struct LineDirection(string Text, string? EmotionPreset);

/// <summary>
/// Uses the local casting LLM to "direct" a dialogue line for the emotion-capable engine (VoxCPM2, Ultra):
/// given the NPC's character and BASE MANNER (their per-NPC casting <c>Style</c>) plus the line, it writes a
/// short free-text delivery direction the engine performs as a <c>(style)</c> prefix on the cloned voice. The
/// spoken WORDS are never changed. Cached per (character, line).
/// </summary>
public sealed class EmotionAnnotator
{
    private readonly ILlmClient llm;
    private readonly IPluginLog log;
    private readonly ConcurrentDictionary<string, LineDirection> cache = new();

    public EmotionAnnotator(ILlmClient llm, IPluginLog log)
    {
        this.llm = llm;
        this.log = log;
    }

    /// <summary>
    /// Direct a line: <paramref name="character"/> is a compact NPC description and <paramref name="baseMood"/>
    /// their baseline manner (the casting <c>Style</c>). Returns the line unchanged plus a short delivery
    /// direction (falling back to the base manner so a line is never flat). Cached per (character, line).
    /// </summary>
    public async Task<LineDirection> DirectAsync(string line, string? character, string? baseMood, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(line)) return new LineDirection(line, null);
        var key = (character ?? "") + "" + line;
        if (cache.TryGetValue(key, out var cached)) return cached;

        var direction = string.IsNullOrWhiteSpace(baseMood) ? null : baseMood;
        try
        {
            var raw = await llm.CompleteTextAsync(BuildPrompt(line, character, baseMood), maxTokens: 24, temperature: 0.3f, ct)
                .ConfigureAwait(false);
            var phrase = CleanStylePhrase(raw);
            if (!string.IsNullOrWhiteSpace(phrase)) direction = phrase;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { log.Warning(ex, "[Emotion] direction failed; using the NPC's base manner."); }

        // Text unchanged (the engine takes no inline tags); the direction rides as a (style) prefix on the clone.
        var result = new LineDirection(line, direction);
        cache[key] = result;
        return result;
    }

    private static string BuildPrompt(string line, string? character, string? baseMood)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are the voice director for a Final Fantasy XIV NPC.");
        if (!string.IsNullOrWhiteSpace(character)) sb.AppendLine($"Character: {character}");
        if (!string.IsNullOrWhiteSpace(baseMood)) sb.AppendLine($"Their usual manner: {baseMood}");
        sb.AppendLine($"Line: \"{line}\"");
        sb.AppendLine("Describe HOW they deliver this line in ONE short phrase of 3 to 6 words (emotion + pace).");
        sb.Append("No narration, no quotes, no name. Delivery:");
        return sb.ToString();
    }

    /// <summary>The 1.5B model rambles/narrates — keep only the first clause, capped to ~8 words, so the
    /// (style) prefix stays a clean directive (e.g. "weary and grave, slow").</summary>
    private static string CleanStylePhrase(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var p = raw.Replace("\r", "").Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
        var cut = p.IndexOfAny(new[] { '.', ';', ':', '"' });
        if (cut > 0) p = p[..cut];
        p = p.Trim().Trim('"').Trim(',').Trim();
        var words = p.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 8) p = string.Join(' ', words[..8]);
        return p.Trim();
    }
}
