using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using PopotoVox.Presets;

namespace PopotoVox.Windows;

/// <summary>
/// Packs: share and apply voice packs. A pack is one shareable file bundling your per-NPC overrides
/// (and optionally the baked voice specs). The page leads with the on-disk folder — the missing piece
/// for the "I downloaded a pack, now what?" journey — then previews each pack before you import it,
/// and lets you create one. Models are referenced by id, never embedded (PRD §6.3).
/// </summary>
public sealed class LibraryPage : IShellPage
{
    private readonly Plugin plugin;

    // Export buffers
    private string presetName = "My Pack";
    private string presetAuthor = "";
    private string presetDesc = "";
    private bool presetIncludeSpecs = true;

    // Cached folder scan (refreshed on demand, never per-frame).
    private List<PackEntry>? packs;
    private string? pendingDelete;

    // Transient feedback line.
    private string? lastMessage;
    private bool lastWasError;

    public LibraryPage(Plugin plugin) => this.plugin = plugin;

    public ShellSection Section => ShellSection.Library;
    public string Label => "Packs";
    public FontAwesomeIcon Icon => FontAwesomeIcon.BoxOpen;

    public void Draw()
    {
        if (packs == null) RefreshPacks();

        Ui.Paragraph("Voice packs bundle your NPC voice choices into one shareable file. Models aren't " +
            "included — packs reference voices by id, so everyone uses their own installed voices.");

        DrawFolderCard();
        DrawPacksList();
        DrawExportCard();
        DrawDeletePopup();

        if (lastMessage != null)
        {
            ImGui.Spacing();
            ImGui.TextColored(lastWasError ? Ui.Bad : Ui.Good, lastMessage);
        }
    }

    // ---------------------------------------------------------------- folder

    private void DrawFolderCard()
    {
        Ui.BeginCard(FontAwesomeIcon.FolderOpen, "Packs folder");
        Ui.Paragraph("Drop .json packs you download into this folder, then Import them below.");

        var dir = plugin.Paths.PresetsDir;
        ImGui.TextWrapped(dir);

        ImGui.Spacing();
        if (ImGui.Button("Open folder")) OpenFolder();
        ImGui.SameLine();
        if (ImGui.Button("Copy path")) { ImGui.SetClipboardText(dir); SetMsg("Path copied to clipboard.", false); }
        ImGui.SameLine();
        if (ImGui.Button("Refresh")) RefreshPacks();

        Ui.EndCard();
    }

    private void OpenFolder()
    {
        try
        {
            var dir = plugin.Paths.PresetsDir;
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[Packs] Open folder failed.");
            SetMsg("Couldn't open the folder: " + ex.Message, true);
        }
    }

    // ---------------------------------------------------------------- pack list

    private void DrawPacksList()
    {
        if (packs!.Count == 0)
        {
            Ui.BeginCard(FontAwesomeIcon.BoxOpen, "Your packs");
            ImGui.TextDisabled("No packs yet — open the folder above and drop a .json in, or create one below.");
            Ui.EndCard();
            return;
        }

        ImGui.Spacing();
        ImGui.TextColored(Ui.Warn, "Importing can replace voices you've already customized.");
        foreach (var e in packs!)
            DrawPackCard(e);
    }

    private void DrawPackCard(PackEntry e)
    {
        var title = !string.IsNullOrWhiteSpace(e.Meta?.Name) ? e.Meta!.Name : e.FileName;
        var author = e.Meta?.Author;
        Ui.BeginCard(FontAwesomeIcon.BoxOpen, title, string.IsNullOrWhiteSpace(author) ? null : "by " + author);

        if (e.Error != null)
        {
            ImGui.TextColored(Ui.Bad, e.Error);
            ImGui.TextDisabled(e.FileName);
            Ui.EndCard();
            return;
        }

        if (!string.IsNullOrWhiteSpace(e.Meta?.Description))
            Ui.Paragraph(e.Meta!.Description);

        ImGui.TextDisabled($"{e.VoiceCount} voices · {e.OverrideCount} overrides");
        if (e.IncludesSpecs) { ImGui.SameLine(0, 8f); Ui.Pill("exact voices", Ui.Good); }

        ImGui.Spacing();
        if (ImGui.Button($"Import##{e.Path}")) ImportPack(e.Path);
        ImGui.SameLine();
        if (Ui.IconAction($"##del{e.Path}", FontAwesomeIcon.Trash, "Delete this pack"))
        {
            pendingDelete = e.Path;
            ImGui.OpenPopup("##confirmDeletePack");
        }
        ImGui.SameLine(); ImGui.TextDisabled(e.FileName);

        Ui.EndCard();
    }

    private void DrawDeletePopup()
    {
        var open = true;
        if (!ImGui.BeginPopupModal("##confirmDeletePack", ref open, ImGuiWindowFlags.AlwaysAutoResize))
            return;

        var name = pendingDelete != null ? Path.GetFileName(pendingDelete) : "";
        ImGui.TextUnformatted($"Delete “{name}”? This removes the file from your packs folder.");
        ImGui.Spacing();
        if (ImGui.Button("Delete") && pendingDelete != null)
        {
            DeletePack(pendingDelete);
            pendingDelete = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel")) { pendingDelete = null; ImGui.CloseCurrentPopup(); }
        ImGui.EndPopup();
    }

    // ---------------------------------------------------------------- export

    private void DrawExportCard()
    {
        Ui.BeginCard(FontAwesomeIcon.FileExport, "Create a pack");
        ImGui.InputText("Pack name", ref presetName, 64);
        ImGui.InputText("Author", ref presetAuthor, 64);
        ImGui.InputText("Description", ref presetDesc, 128);
        ImGui.Checkbox("Include the exact voices", ref presetIncludeSpecs);
        Ui.Paragraph(presetIncludeSpecs
            ? "On: the pack locks in the specific voice each NPC was given, so anyone who imports it hears " +
              "exactly what you hear — no AI casting needed."
            : "Off: only your manual changes (pinned voices, prompts) are shared; the importer's own AI " +
              "casts every other NPC.");
        ImGui.Spacing();
        if (ImGui.Button("Export")) ExportPack();
        Ui.EndCard();
    }

    // ---------------------------------------------------------------- actions

    private void ExportPack()
    {
        var safe = string.Concat((presetName.Length == 0 ? "pack" : presetName).Split(Path.GetInvalidFileNameChars()));
        var path = Path.Combine(plugin.Paths.PresetsDir, safe + ".json");
        try
        {
            plugin.Presets.Export(path, new PresetMeta
            {
                Name = presetName,
                Author = presetAuthor,
                Description = presetDesc,
            }, presetIncludeSpecs);
            SetMsg($"Saved “{Path.GetFileName(path)}” to the packs folder.", false);
            RefreshPacks();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[Packs] Export failed.");
            SetMsg("Export failed: " + ex.Message, true);
        }
    }

    private void ImportPack(string file)
    {
        try
        {
            var preset = plugin.Presets.Import(file);
            var name = !string.IsNullOrWhiteSpace(preset.Meta?.Name) ? preset.Meta!.Name : Path.GetFileName(file);
            SetMsg($"Imported “{name}” — {preset.VoiceSpecs.Count} voices, {preset.NpcOverrides.Count} overrides applied.", false);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[Packs] Import failed.");
            SetMsg("Import failed: " + ex.Message, true);
        }
    }

    private void DeletePack(string file)
    {
        try
        {
            File.Delete(file);
            SetMsg($"Deleted “{Path.GetFileName(file)}”.", false);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[Packs] Delete failed.");
            SetMsg("Delete failed: " + ex.Message, true);
        }
        RefreshPacks();
    }

    // ---------------------------------------------------------------- helpers

    private void RefreshPacks()
    {
        var list = new List<PackEntry>();
        var dir = plugin.Paths.PresetsDir;
        if (Directory.Exists(dir))
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var entry = new PackEntry { Path = file, FileName = Path.GetFileName(file) };
                try
                {
                    var preset = plugin.Presets.Read(file);
                    if (preset == null)
                    {
                        entry.Error = "Couldn't read this pack.";
                    }
                    else
                    {
                        entry.Meta = preset.Meta;
                        entry.VoiceCount = preset.VoiceSpecs.Count;
                        entry.OverrideCount = preset.NpcOverrides.Count;
                        entry.IncludesSpecs = preset.VoiceSpecs.Count > 0;
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.Warning(ex, $"[Packs] Couldn't read {file}.");
                    entry.Error = "Couldn't read this pack.";
                }
                list.Add(entry);
            }
        }
        packs = list;
    }

    private void SetMsg(string msg, bool error) { lastMessage = msg; lastWasError = error; }

    private sealed class PackEntry
    {
        public string Path = "";
        public string FileName = "";
        public PresetMeta? Meta;
        public int VoiceCount;
        public int OverrideCount;
        public bool IncludesSpecs;
        public string? Error;
    }
}
