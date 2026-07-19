using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;

namespace PopotoVox.Windows;

/// <summary>
/// Shared, SaaS-flavoured UI helpers for the shell. Emphasis uses only what ImGui/Dalamud natively
/// provide — font scaling, the FontAwesome icon font, draw-list rounded rects, and ImGuiComponents
/// icon buttons — so we get cards, status pills, and icon actions without any custom theming.
/// </summary>
internal static class Ui
{
    public static readonly Vector4 Accent = new(0.62f, 0.82f, 1f, 1f);
    public static readonly Vector4 Muted = new(0.66f, 0.66f, 0.70f, 1f);
    public static readonly Vector4 Good = new(0.55f, 0.95f, 0.6f, 1f);
    public static readonly Vector4 Warn = new(1f, 0.85f, 0.4f, 1f);
    public static readonly Vector4 Bad = new(1f, 0.5f, 0.4f, 1f);

    private static readonly Vector4 CardBg = new(1f, 1f, 1f, 0.035f);
    private static readonly Vector4 CardBorder = new(1f, 1f, 1f, 0.13f);
    private const float CardPad = 10f;
    private const float CardRound = 6f;

    // Card state (cards never nest, so a single slot is fine).
    private static Vector2 cardTopLeft;
    private static float cardWidth;

    // --------------------------------------------------------------- text

    /// <summary>Large, accent-coloured section heading with breathing room above and below.</summary>
    public static void Heading(string text)
    {
        ImGui.Spacing();
        Scaled(1.35f, () => ImGui.TextColored(Accent, text));
        ImGui.Spacing();
    }

    /// <summary>A medium, accent-coloured sub-heading for groups within a section.</summary>
    public static void SubHeading(string text) => Scaled(1.12f, () => ImGui.TextColored(Accent, text));

    private const float StatLabelWidth = 92f;
    private static readonly Vector4 StatValue = new(0.92f, 0.92f, 0.95f, 1f);

    /// <summary>
    /// One "character sheet" stat line: a muted label in a fixed-width column, then its value.
    /// Pass <paramref name="valueColor"/> to emphasise a value (e.g. class), and set
    /// <paramref name="estimated"/> to append a subtle "~" with an explanatory tooltip.
    /// </summary>
    public static void StatRow(string label, string value, Vector4? valueColor = null,
        bool estimated = false, string? estimatedTooltip = null)
    {
        ImGui.TextColored(Muted, label);
        ImGui.SameLine(StatLabelWidth);
        ImGui.TextColored(valueColor ?? StatValue, value);
        if (estimated)
        {
            ImGui.SameLine(0, 4f);
            ImGui.TextColored(Muted, "~");
            if (!string.IsNullOrEmpty(estimatedTooltip) && ImGui.IsItemHovered())
                ImGui.SetTooltip(estimatedTooltip);
        }
    }

    /// <summary>Muted, wrapped body copy — the friendly explanation under a heading.</summary>
    public static void Paragraph(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Muted);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    /// <summary>Human-friendly byte size (KB / MB / GB).</summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 KB";
        if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):0.0} GB";
        if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):0.0} MB";
        return $"{Math.Max(1, bytes / 1024)} KB";
    }

    /// <summary>Plain-language hint for an in-game distance in yalms (≈ metres) so users can size distance
    /// settings — melee ≈ 3 y, most spells ≈ 25 y. e.g. "15 yalms (~15 m — across a courtyard)".</summary>
    public static string DistanceHint(int yalms)
    {
        var what = yalms <= 5 ? "right beside you"
            : yalms <= 12 ? "across a room"
            : yalms <= 25 ? "across a courtyard, ~spell range"
            : yalms <= 45 ? "across a plaza"
            : "a city block / whole loaded area";
        return $"{yalms} yalms (~{yalms} m — {what})";
    }

    /// <summary>Run <paramref name="draw"/> at a scaled window font size, always restoring 1.0.</summary>
    public static void Scaled(float scale, Action draw)
    {
        ImGui.SetWindowFontScale(scale);
        try { draw(); }
        finally { ImGui.SetWindowFontScale(1.0f); }
    }

    // --------------------------------------------------------------- icons

    /// <summary>Draw a FontAwesome glyph inline as text (optionally coloured).</summary>
    public static void Icon(FontAwesomeIcon icon, Vector4? color = null)
    {
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
        {
            if (color.HasValue) ImGui.TextColored(color.Value, icon.ToIconString());
            else ImGui.TextUnformatted(icon.ToIconString());
        }
    }

    /// <summary>An icon button with a hover tooltip; greyed and inert when <paramref name="enabled"/> is false.</summary>
    public static bool IconAction(string id, FontAwesomeIcon icon, string tooltip, bool enabled = true)
    {
        if (!enabled) ImGui.BeginDisabled(true);
        var clicked = ImGuiComponents.IconButton(id, icon);
        if (!enabled) ImGui.EndDisabled();
        if (!string.IsNullOrEmpty(tooltip) && ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
        return clicked && enabled;
    }

    // --------------------------------------------------------------- pill

    /// <summary>A small rounded status chip (text on a tinted background).</summary>
    public static void Pill(string text, Vector4 color)
    {
        var draw = ImGui.GetWindowDrawList();
        var ts = ImGui.CalcTextSize(text);
        var pad = new Vector2(7f, 2f);
        var size = ts + pad * 2;
        var p0 = ImGui.GetCursorScreenPos();

        var bg = color; bg.W = 0.22f;
        draw.AddRectFilled(p0, p0 + size, ImGui.ColorConvertFloat4ToU32(bg), size.Y * 0.5f);
        draw.AddText(p0 + pad, ImGui.ColorConvertFloat4ToU32(color), text);
        ImGui.Dummy(size);
    }

    // --------------------------------------------------------------- banner

    /// <summary>A full-width tinted, rounded callout bar with a leading icon + wrapped message.
    /// Eye-catching and auto-heights to the text — use for "reload to apply" and similar notices.</summary>
    public static void Banner(FontAwesomeIcon icon, string text, Vector4 color)
    {
        ImGui.Spacing();
        const float pad = 8f, gap = 8f;
        var draw = ImGui.GetWindowDrawList();
        var avail = ImGui.GetContentRegionAvail().X;

        var iconStr = icon.ToIconString();
        float iconW;
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
            iconW = ImGui.CalcTextSize(iconStr).X;

        // Measure with a slightly conservative wrap width so the box can't end up too short.
        var wrapW = MathF.Max(40f, avail - pad * 2 - iconW - gap);
        var textH = ImGui.CalcTextSize(text, false, wrapW).Y;
        var h = MathF.Max(ImGui.GetTextLineHeight(), textH) + pad * 2;

        var p0 = ImGui.GetCursorScreenPos();
        var bg = color; bg.W = 0.20f;
        draw.AddRectFilled(p0, p0 + new Vector2(avail, h), ImGui.ColorConvertFloat4ToU32(bg), 6f);
        draw.AddRect(p0, p0 + new Vector2(avail, h), ImGui.ColorConvertFloat4ToU32(color), 6f);

        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
            draw.AddText(p0 + new Vector2(pad, pad), ImGui.ColorConvertFloat4ToU32(color), iconStr);

        ImGui.SetCursorScreenPos(p0 + new Vector2(pad + iconW + gap, pad));
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.PushTextWrapPos(0f); // wrap at the window content edge (≈ banner right)
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.PopStyleColor();

        ImGui.SetCursorScreenPos(p0);
        ImGui.Dummy(new Vector2(avail, h));
        ImGui.Spacing();
    }

    // --------------------------------------------------------------- card

    /// <summary>
    /// Begin a headerless full-width card — for cards that draw their own custom header (e.g. the NPC
    /// character sheet's hero header). Pair with <see cref="EndCard"/>.
    /// </summary>
    public static void BeginCard()
    {
        ImGui.Spacing();
        var draw = ImGui.GetWindowDrawList();
        draw.ChannelsSplit(2);
        draw.ChannelsSetCurrent(1); // content draws on the foreground channel

        cardTopLeft = ImGui.GetCursorScreenPos();
        cardWidth = ImGui.GetContentRegionAvail().X;

        ImGui.BeginGroup();
        ImGui.Indent(CardPad);
        ImGui.Dummy(new Vector2(0, CardPad - ImGui.GetStyle().ItemSpacing.Y));
    }

    /// <summary>
    /// Begin a full-width bordered "card" with an icon + title header (and optional right-aligned status).
    /// Pair with <see cref="EndCard"/>. The background/border is drawn behind the content via a draw-list
    /// channel split, so the card sizes itself to whatever it contains.
    /// </summary>
    public static void BeginCard(FontAwesomeIcon icon, string title, string? right = null)
    {
        BeginCard();

        Icon(icon, Accent);
        ImGui.SameLine();
        Scaled(1.15f, () => ImGui.TextColored(Accent, title));
        if (!string.IsNullOrEmpty(right))
        {
            ImGui.SameLine();
            var avail = ImGui.GetContentRegionAvail().X;
            var tw = ImGui.CalcTextSize(right).X;
            if (avail > tw + CardPad) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (avail - tw - CardPad * 2));
            ImGui.TextColored(Muted, right);
        }
        CardSeparator();
    }

    /// <summary>
    /// A divider that stays inside the card. <see cref="ImGui.Separator"/> spans the whole window width
    /// (ignoring the card's group indent on the right), so it pokes past the card border — this draws a
    /// line clamped to the card's content bounds instead. Only valid between BeginCard/EndCard.
    /// </summary>
    public static void CardSeparator()
    {
        ImGui.Spacing();
        var y = ImGui.GetCursorScreenPos().Y;
        var draw = ImGui.GetWindowDrawList();
        draw.AddLine(new Vector2(cardTopLeft.X + CardPad, y),
                     new Vector2(cardTopLeft.X + cardWidth - CardPad, y),
                     ImGui.ColorConvertFloat4ToU32(CardBorder));
        ImGui.Dummy(new Vector2(0, 1f));
        ImGui.Spacing();
    }

    public static void EndCard()
    {
        ImGui.Dummy(new Vector2(0, CardPad));
        ImGui.Unindent(CardPad);
        ImGui.EndGroup();

        var max = new Vector2(cardTopLeft.X + cardWidth, ImGui.GetItemRectMax().Y);
        var draw = ImGui.GetWindowDrawList();
        draw.ChannelsSetCurrent(0); // background channel
        draw.AddRectFilled(cardTopLeft, max, ImGui.ColorConvertFloat4ToU32(CardBg), CardRound);
        draw.AddRect(cardTopLeft, max, ImGui.ColorConvertFloat4ToU32(CardBorder), CardRound);
        draw.ChannelsMerge();
        ImGui.Spacing();
    }
}
