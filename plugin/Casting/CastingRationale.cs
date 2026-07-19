using System.Collections.Generic;
using PopotoVox.Npc;
using PopotoVox.Tts;

namespace PopotoVox.Casting;

/// <summary>
/// Turns the data we already persist (the NPC record + its locked <see cref="VoiceSpec"/>) into
/// plain-English "why this voice" lines for the NPC card. Read-only and presentation-only — it
/// makes no decisions, it just explains the ones already made, so players can see how each fact
/// (gender, age, accent, timbre) shaped the cast and where a value was estimated vs. known.
/// </summary>
public static class CastingRationale
{
    /// <summary>Why a cast NPC sounds the way it does. <paramref name="voice"/> is the chosen voice's info.</summary>
    public static List<string> Explain(NpcRecord r, VoiceSpec spec, VoiceInfo? voice)
    {
        var lines = new List<string>();

        if (spec.Gender != VoiceGender.Neutral)
            lines.Add($"Gender {spec.Gender.Label()} (from game data) locked the voice to the {spec.Gender.Label()} pool.");

        var age = spec.Traits?.Age ?? VoiceAge.Adult;
        lines.Add($"Reads as {age.ToString().ToLowerInvariant()} ({AgeSource(r)}).");

        if (spec.Traits is { } t && t.Accent != VoiceAccent.Unknown)
        {
            var used = voice?.Accent ?? t.Accent;
            var basis = AccentBasis(r, t.Accent);
            if (used != t.Accent)
                lines.Add($"AI chose a {t.Accent.Label()} accent ({basis}), but {spec.Engine} has no " +
                          $"{t.Accent.Label()} {spec.Gender.Label()} voice — using {used.Label()}.");
            else
                lines.Add($"{t.Accent.Label().ToUpperFirst()} accent — {basis}.");
        }

        if (spec.Traits is { Timbre: { Length: > 0 } timbre })
            lines.Add($"Timbre the AI heard: “{timbre}”.");

        return lines;
    }

    /// <summary>What we already know that WILL shape an as-yet-uncast NPC's voice.</summary>
    public static List<string> Predict(NpcRecord r)
    {
        var lines = new List<string>();
        var gender = r.Gender.ToVoiceGender();
        if (gender != VoiceGender.Neutral)
            lines.Add($"Gender {gender.Label()} (from game data) will fix the voice to the {gender.Label()} pool.");
        if (r.ApparentAge != null)
            lines.Add($"Will read as {r.ApparentAge} (from body type).");
        var line = LikelyAccentLine(r);
        if (line != null) lines.Add(line);
        return lines;
    }

    /// <summary>A one-liner for the profile column: the heritage lean (the AI still decides the final accent).</summary>
    public static string? LikelyAccentLine(NpcRecord r)
    {
        var lean = AccentLore.TribeLean(r.Tribe);
        if (lean != VoiceAccent.Unknown)
            return $"Heritage leans {lean.Label()} — the AI sets the final accent at casting";
        return "Accent decided at casting from origin & dialogue";
    }

    /// <summary>How the AI arrived at the accent it picked (it decides; heritage is only a lean).</summary>
    private static string AccentBasis(NpcRecord r, VoiceAccent chosen)
    {
        var lean = AccentLore.TribeLean(r.Tribe);
        if (lean == chosen) return $"matches their {r.Tribe} heritage";
        if (lean != VoiceAccent.Unknown) return $"the AI's read — their {r.Tribe} heritage leans {lean.Label()}";
        return "the AI's read of their origin & words";
    }

    private static string AgeSource(NpcRecord r) =>
        r.ApparentAge != null ? "from body type" : "estimated by AI";

    private static string ToUpperFirst(this string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
