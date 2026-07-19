using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace PopotoVox.Dialogue;

/// <summary>
/// Decodes and lightly normalizes the text held by a game UI text node.
///
/// The node's text is a packed SeString that can carry payloads (auto-translate
/// tokens, name/colour envelopes, line-break markers). A raw UTF-8 decode of those
/// bytes leaves control-code garbage in the string, so we parse it through Dalamud's
/// SeString and take its plain TextValue. Centralizing this keeps the one piece of
/// code that touches raw game memory in a single, auditable spot (PRD §5.1).
/// </summary>
internal static unsafe class AddonTextReader
{
    public static string Read(AtkTextNode* node)
    {
        if (node == null)
            return string.Empty;

        var span = node->NodeText.AsSpan();
        if (span.IsEmpty)
            return string.Empty;

        string raw;
        try
        {
            raw = SeString.Parse(span).TextValue;
        }
        catch
        {
            // Defensive fallback: a raw decode is still better than dropping the line.
            raw = node->NodeText.ToString();
        }

        if (string.IsNullOrEmpty(raw))
            return string.Empty;

        // Flatten the hard line breaks the game inserts for on-screen wrapping.
        return raw.Replace("\r", string.Empty).Replace("\n", " ").Trim();
    }
}
