using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using PopotoVox.Tts;

namespace PopotoVox.Windows;

/// <summary>
/// The unified management shell (PRD §7): one window hosting a left-nav rail and a content region
/// that swaps between sections — Home (NPC browser), Voices (settings + downloads), Library (packs),
/// and System (diagnostics, data, about). Replaces the former separate Main/Settings/Diag/
/// Acknowledgements windows. The first-run Setup wizard and the casting HUD overlay stay separate.
/// </summary>
public sealed class ShellWindow : Window, IDisposable
{
    private const float RailWidth = 150f;

    private readonly Plugin plugin;
    private readonly IShellPage[] pages;
    private readonly SystemPage systemPage;
    private IShellPage current;

    public ShellWindow(Plugin plugin)
        : base("PopotoVox###PopotoVoxMain")
    {
        this.plugin = plugin;

        systemPage = new SystemPage(plugin);
        pages = new IShellPage[]
        {
            new HomePage(plugin),
            systemPage,
            new VoicesPage(plugin),
            new StoragePage(plugin, Navigate),
            new LibraryPage(plugin),
            new AboutPage(plugin),
        };
        current = pages[0];

        SizeConstraints = new WindowSizeConstraints
        {
            // Width is fixed at 1200 (min.X == max.X) so the layout is stable; height is the user's.
            MinimumSize = new Vector2(1200, 540),
            MaximumSize = new Vector2(1200, float.MaxValue),
        };
    }

    public void Dispose() { }

    /// <summary>Switch the shell to the given section (command / config-gear deep-links).</summary>
    public void Navigate(ShellSection section)
    {
        var page = pages.FirstOrDefault(p => p.Section == section);
        if (page != null) current = page;
    }

    /// <summary>Deep-link to the System dashboard (the <c>/popotovox diag</c> entry point).</summary>
    public void NavigateToDiagnostics() => Navigate(ShellSection.System);

    public override void Draw()
    {
        if (ImGui.BeginChild("##nav", new Vector2(RailWidth, 0), true))
        {
            foreach (var page in pages)
                DrawNavItem(page);
        }
        ImGui.EndChild();

        ImGui.SameLine();

        if (ImGui.BeginChild("##content", new Vector2(0, 0), true))
        {
            DrawSetupBannerIfNeeded();
            current.Draw();
        }
        ImGui.EndChild();
    }

    /// <summary>A polished rail row: a full-width selectable with an icon + label overlaid on top.</summary>
    private void DrawNavItem(IShellPage page)
    {
        var selected = current == page;
        var rowH = ImGui.GetFrameHeight() + 6f;
        var width = ImGui.GetContentRegionAvail().X;
        var p0 = ImGui.GetCursorScreenPos();

        if (ImGui.Selectable($"##nav_{page.Section}", selected, ImGuiSelectableFlags.None, new Vector2(width, rowH)))
            current = page;

        var color = selected ? Ui.Accent : new Vector4(0.86f, 0.86f, 0.9f, 1f);
        var textY = p0.Y + (rowH - ImGui.GetTextLineHeight()) * 0.5f;
        ImGui.SetCursorScreenPos(new Vector2(p0.X + 8f, textY));
        Ui.Icon(page.Icon, color);
        ImGui.SameLine(0, 8f);
        ImGui.TextColored(color, page.Label);

        // Restore the layout cursor to where the selectable left it, for the next row.
        ImGui.SetCursorScreenPos(new Vector2(p0.X, p0.Y + rowH + ImGui.GetStyle().ItemSpacing.Y));
    }

    // ----------------------------------------------------- shared first-run setup banner

    /// <summary>
    /// A gentle one-time nudge shown above every section while the active engine's voice assets
    /// aren't installed yet. Self-hides once everything is downloaded.
    /// </summary>
    private void DrawSetupBannerIfNeeded()
    {
        var info = TtsEngineCatalog.Get(plugin.Configuration.TtsEngine);
        var group = plugin.Assets.AssetsForEngine(info.Id).ToList();
        if (group.Count == 0) return;
        if (group.All(a => plugin.Downloads.IsInstalled(a.Id) == true)) return;

        var sizeText = Ui.FormatBytes(group.Sum(a => a.Size));
        ImGui.TextColored(Ui.Accent, "👋 Welcome to PopotoVox — quick one-time setup");
        ImGui.TextWrapped($"To give NPCs their voices, download the {info.DisplayName} voice (~{sizeText}, one time). " +
                          "It's verified and runs entirely on your PC — no account, no cloud.");

        if (plugin.Downloads.Busy)
        {
            var pending = group.FirstOrDefault(a => plugin.Downloads.IsInstalled(a.Id) != true);
            var p = pending != null ? plugin.Downloads.ProgressFor(pending.Id) : null;
            ImGui.ProgressBar(p != null ? (float)p.Fraction : 0f, new Vector2(-1, 0),
                p?.Phase.ToString() ?? "Starting…");
        }
        else
        {
            if (ImGui.Button($"Download & set up {info.DisplayName} voice  (~{sizeText})"))
                plugin.Downloads.StartDownload(group);
            ImGui.SameLine();
            ImGui.TextDisabled("it goes live by itself when the download finishes — no reload needed");
        }
        if (plugin.Downloads.LastError != null)
            ImGui.TextColored(Ui.Bad, "Setup error: " + plugin.Downloads.LastError);

        ImGui.Spacing(); ImGui.Separator(); ImGui.Spacing();
    }
}
