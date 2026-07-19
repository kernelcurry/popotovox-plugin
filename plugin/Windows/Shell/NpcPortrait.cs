using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using PopotoVox.Npc;

namespace PopotoVox.Windows;

/// <summary>
/// The NPC card's identity "monogram": a small tinted rounded badge bearing a role/race
/// FontAwesome glyph. It gives each character a splash of visual identity in the hero header
/// without pretending to be a real portrait (a live 3D render isn't this plugin's purpose).
/// Kept as its own component so the glyph/tint mapping lives in one place. Never throws.
/// </summary>
internal sealed class NpcPortrait
{
    private static readonly Vector4 Purple = new(0.66f, 0.55f, 0.95f, 1f);
    private static readonly Vector4 Steel = new(0.55f, 0.70f, 0.95f, 1f);
    private static readonly Vector4 Green = new(0.55f, 0.90f, 0.60f, 1f);
    private static readonly Vector4 Teal = new(0.45f, 0.85f, 0.80f, 1f);
    private static readonly Vector4 Crimson = new(0.95f, 0.55f, 0.55f, 1f);
    private static readonly Vector4 Amber = new(0.95f, 0.78f, 0.45f, 1f);

    /// <summary>Draw the monogram badge at the cursor and reserve <paramref name="size"/> so the
    /// surrounding layout advances past it (behaves like an inline widget for SameLine).</summary>
    public void DrawBadge(NpcRecord r, Vector2 size)
    {
        var (glyph, tint) = Pick(r);
        var p0 = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();

        var bg = tint; bg.W = 0.12f;
        var border = tint; border.W = 0.50f;
        var round = size.Y * 0.22f;
        draw.AddRectFilled(p0, p0 + size, ImGui.ColorConvertFloat4ToU32(bg), round);
        draw.AddRect(p0, p0 + size, ImGui.ColorConvertFloat4ToU32(border), round);

        // Centred glyph, scaled to fill ~55% of the badge height.
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
        {
            var scale = size.Y * 0.55f / ImGui.GetFontSize();
            ImGui.SetWindowFontScale(scale);
            var icon = glyph.ToIconString();
            var ts = ImGui.CalcTextSize(icon);
            ImGui.SetCursorScreenPos(p0 + (size - ts) * 0.5f);
            ImGui.TextColored(tint, icon);
            ImGui.SetWindowFontScale(1.0f);
        }

        // Reserve the box so the surrounding layout advances past it.
        ImGui.SetCursorScreenPos(p0);
        ImGui.Dummy(size);
    }

    private static (FontAwesomeIcon Glyph, Vector4 Tint) Pick(NpcRecord r)
    {
        var role = r.Role?.ToLowerInvariant() ?? "";
        if (role.Contains("mage") || role.Contains("caster")) return (FontAwesomeIcon.HatWizard, Purple);
        if (role.Contains("knight") || role.Contains("warrior") || role.Contains("soldier")) return (FontAwesomeIcon.ShieldAlt, Steel);
        if (role.Contains("archer") || role.Contains("ranged")) return (FontAwesomeIcon.Bullseye, Green);
        if (role.Contains("heal")) return (FontAwesomeIcon.Plus, Teal);

        return r.Race switch
        {
            "Au Ra" => (FontAwesomeIcon.Dragon, Crimson),
            "Miqo'te" => (FontAwesomeIcon.Cat, Amber),
            _ => (FontAwesomeIcon.User, Steel),
        };
    }
}
