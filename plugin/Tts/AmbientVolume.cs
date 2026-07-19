using System;

namespace PopotoVox.Tts;

/// <summary>
/// Distance → volume for ambient voices, modelling how real speech actually attenuates (M16). Grounding:
/// 1 yalm ≈ 0.91 m; conversational speech ≈ 60 dB @ 1 m and drops ~6 dB per doubling of distance
/// (inverse-square law) — i.e. sound-pressure amplitude ∝ 1/distance. So: full within a near zone, then an
/// inverse-distance falloff, normalized to reach silence exactly at the hearing range. Shared by the initial
/// play and the live "walk-past" tracker so they always agree.
/// </summary>
public static class AmbientVolume
{
    /// <summary>Within this many yalms you hear the speaker clearly regardless (≈ right next to them).</summary>
    public const float NearYalms = 3f;

    /// <summary>0..1 volume for a speaker <paramref name="distYalms"/> away; silent at/beyond <paramref name="hearingYalms"/>.</summary>
    public static float Scale(float distYalms, int hearingYalms)
    {
        if (hearingYalms <= NearYalms) return distYalms <= hearingYalms ? 1f : 0f;
        if (distYalms <= NearYalms) return 1f;
        if (distYalms >= hearingYalms) return 0f;

        // Inverse-distance amplitude (−6 dB per doubling), normalized so it's 1.0 at NearYalms and 0 at the edge.
        var amp = NearYalms / distYalms;          // 1.0 at NearYalms, 0.5 at 2×, 0.25 at 4×, …
        var atEdge = NearYalms / hearingYalms;
        return Math.Clamp((amp - atEdge) / (1f - atEdge), 0f, 1f);
    }
}
