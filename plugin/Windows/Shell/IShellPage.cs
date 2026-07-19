using Dalamud.Interface;

namespace PopotoVox.Windows;

/// <summary>
/// The top-level sections of the unified management shell, surfaced as the left-nav rail.
/// </summary>
public enum ShellSection
{
    Home,
    Voices,
    Storage,
    Library,
    System,
    About,
}

/// <summary>
/// One navigable section of <see cref="ShellWindow"/>. A page owns its own UI state and renders
/// into the shell's content region; the shell owns window chrome, the rail, and the setup banner.
/// </summary>
public interface IShellPage
{
    /// <summary>Which rail entry selects this page (also used for command deep-links).</summary>
    ShellSection Section { get; }

    /// <summary>The label shown in the left-nav rail.</summary>
    string Label { get; }

    /// <summary>The FontAwesome icon shown beside the label in the left-nav rail.</summary>
    FontAwesomeIcon Icon { get; }

    /// <summary>Render the page body into the current content child.</summary>
    void Draw();
}
