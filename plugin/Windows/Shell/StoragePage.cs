using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using PopotoVox.Security;
using PopotoVox.Tts;

namespace PopotoVox.Windows;

/// <summary>
/// Storage: a SaaS-style "what's on my PC" page. A usage bar breaks down PopotoVox's whole footprint;
/// a "Voices &amp; AI" card lets you pick a package and read what it gives you before installing/removing
/// it; and a "Saved data" grid manages the small things PopotoVox learns. All icon-driven, colour-coded.
/// </summary>
public sealed class StoragePage : IShellPage
{
    private static readonly Vector4 Accent = new(0.62f, 0.82f, 1f, 1f);
    private static readonly Vector4 Good = new(0.6f, 1f, 0.6f, 1f);
    private static readonly Vector4 Warn = new(1f, 0.85f, 0.4f, 1f);
    private static readonly Vector4 Bad = new(1f, 0.5f, 0.4f, 1f);
    private static readonly Vector4 Muted = new(0.7f, 0.7f, 0.7f, 1f);

    private readonly Plugin plugin;
    private readonly Action<ShellSection> navigate;

    private int selectedPackage;
    private string? pendingClear; // which saved-data row is awaiting a clear confirmation

    // Cached on-disk sizes + derived counts, recomputed lazily (not every frame).
    private bool sizesStale = true;
    private long voiceSpecBytes, npcRecordBytes, crossLinkBytes, overrideBytes, lineCacheBytes, voicesCacheBytes;
    private int sampleLineCount;

    public StoragePage(Plugin plugin, Action<ShellSection> navigate)
    {
        this.plugin = plugin;
        this.navigate = navigate;
    }

    public ShellSection Section => ShellSection.Storage;
    public string Label => "Storage";
    public FontAwesomeIcon Icon => FontAwesomeIcon.Hdd;

    public void Draw()
    {
        if (sizesStale) RefreshSizes();

        var packages = BuildPackages();
        DrawUsageOverview(packages);
        DrawVoicesCard(packages);
        DrawSavedDataCard();
        DrawLineCacheCard();
        DrawVoicesCacheCard();
    }

    // ---- Usage overview: segmented "disk usage" bar of everything PopotoVox stores ----

    private void DrawUsageOverview(List<Package> packages)
    {
        var savedBytes = voiceSpecBytes + npcRecordBytes + crossLinkBytes + overrideBytes;
        var segs = new List<(string Name, long Bytes, Vector4 Color)>();
        foreach (var p in packages)
            if (p.InstalledBytes > 0) segs.Add((p.Name, p.InstalledBytes, p.Color));
        if (savedBytes > 0) segs.Add(("Saved data", savedBytes, ColorFor("saved")));
        if (lineCacheBytes > 0) segs.Add(("Cached lines", lineCacheBytes, ColorFor("lines")));
        var total = segs.Sum(s => s.Bytes);

        Ui.Heading($"PopotoVox is using {Ui.FormatBytes(total)}");

        if (total == 0)
        {
            ImGui.TextDisabled("Nothing downloaded yet — pick a voice below.");
            ImGui.Spacing(); ImGui.Separator();
            return;
        }

        const float barH = 18f;
        var p0 = ImGui.GetCursorScreenPos();
        var barW = ImGui.GetContentRegionAvail().X;
        var round = barH * 0.5f;
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(p0, new Vector2(p0.X + barW, p0.Y + barH),
            ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.06f)), round);

        const float minW = 6f;
        var widths = new float[segs.Count];
        float sum = 0;
        for (var i = 0; i < segs.Count; i++)
        {
            widths[i] = Math.Max(minW, (float)(segs[i].Bytes / (double)total) * barW);
            sum += widths[i];
        }
        if (sum > barW) for (var i = 0; i < widths.Length; i++) widths[i] *= barW / sum;

        var x = p0.X;
        for (var i = 0; i < segs.Count; i++)
        {
            var flags = segs.Count == 1 ? ImDrawFlags.RoundCornersAll
                      : i == 0 ? ImDrawFlags.RoundCornersLeft
                      : i == segs.Count - 1 ? ImDrawFlags.RoundCornersRight
                      : ImDrawFlags.RoundCornersNone;
            draw.AddRectFilled(new Vector2(x, p0.Y), new Vector2(x + widths[i], p0.Y + barH),
                ImGui.ColorConvertFloat4ToU32(segs[i].Color), round, flags);
            x += widths[i];
        }
        ImGui.Dummy(new Vector2(barW, barH));

        ImGui.Spacing();
        for (var i = 0; i < segs.Count; i++)
        {
            if (i > 0)
            {
                ImGui.SameLine(0, 16);
                if (ImGui.GetContentRegionAvail().X < 160) ImGui.NewLine();
            }
            LegendSwatch(segs[i].Color);
            ImGui.SameLine(0, 5);
            ImGui.TextUnformatted(segs[i].Name);
            ImGui.SameLine(0, 5);
            ImGui.TextDisabled(Ui.FormatBytes(segs[i].Bytes));
        }

        ImGui.Spacing(); ImGui.Separator(); ImGui.Spacing();
    }

    private static void LegendSwatch(Vector4 color)
    {
        var sz = ImGui.GetFontSize() * 0.8f;
        var line = ImGui.GetTextLineHeight();
        var p = ImGui.GetCursorScreenPos();
        var a = new Vector2(p.X, p.Y + (line - sz) * 0.5f);
        ImGui.GetWindowDrawList().AddRectFilled(a, new Vector2(a.X + sz, a.Y + sz),
            ImGui.ColorConvertFloat4ToU32(color), 2f);
        ImGui.Dummy(new Vector2(sz, line));
    }

    // ---- Card 1: Voices & AI (package picker + marketing detail) ----

    private void DrawVoicesCard(List<Package> packages)
    {
        if (selectedPackage >= packages.Count) selectedPackage = 0;

        var installedTotal = plugin.Assets.Available
            ? plugin.Assets.Manifest.Assets.Where(a => plugin.Downloads.IsInstalled(a.Id) == true).Sum(a => a.Size)
            : 0;
        var right = installedTotal > 0 ? $"{Ui.FormatBytes(installedTotal)} installed" : "nothing installed yet";

        Ui.BeginCard(FontAwesomeIcon.Microphone, "Voices & AI", right);

        if (!plugin.Assets.Available)
        {
            ImGui.TextColored(Bad, "Downloads are unavailable — the asset manifest failed verification.");
            ImGui.TextWrapped(plugin.Assets.UnavailableReason ?? "");
            Ui.EndCard();
            return;
        }

        Ui.Paragraph("Pick a voice or feature to see what it does — then install it (or remove it to free " +
            "up space). It all runs on your PC and stays offline. Whatever's in use right now is locked.");
        ImGui.Spacing();

        const float tileW = 184f, tileH = 88f, gap = 8f;
        var avail = ImGui.GetContentRegionAvail().X;
        var perRow = Math.Max(1, (int)((avail + gap) / (tileW + gap)));
        for (var i = 0; i < packages.Count; i++)
        {
            if (i % perRow != 0) ImGui.SameLine(0, gap);
            DrawPackageTile(packages[i], i, new Vector2(tileW, tileH));
        }

        ImGui.Spacing(); ImGui.Separator(); ImGui.Spacing();

        DrawPackageDetail(packages[selectedPackage]);

        if (plugin.Downloads.LastError != null)
            ImGui.TextColored(Bad, "Last error: " + plugin.Downloads.LastError);

        Ui.EndCard();
    }

    private void DrawPackageTile(Package pkg, int index, Vector2 size)
    {
        var installed = pkg.InstalledCount == pkg.Assets.Count && pkg.Assets.Count > 0;
        var selected = selectedPackage == index;

        var p0 = ImGui.GetCursorScreenPos();
        var hovered = ImGui.IsMouseHoveringRect(p0, p0 + size);
        var draw = ImGui.GetWindowDrawList();
        var bg = selected ? new Vector4(pkg.Color.X, pkg.Color.Y, pkg.Color.Z, 0.18f)
               : hovered ? new Vector4(1f, 1f, 1f, 0.06f) : new Vector4(1f, 1f, 1f, 0.02f);
        draw.AddRectFilled(p0, p0 + size, ImGui.ColorConvertFloat4ToU32(bg), 6f);
        draw.AddRect(p0, p0 + size, ImGui.ColorConvertFloat4ToU32(selected ? pkg.Color : new Vector4(1, 1, 1, 0.13f)), 6f);

        ImGui.SetCursorScreenPos(p0 + new Vector2(9, 7));
        ImGui.BeginGroup();
        Ui.Icon(pkg.Icon, pkg.Color);
        ImGui.SameLine();
        ImGui.TextUnformatted(pkg.Name);
        if (pkg.Recommended) { ImGui.SameLine(); ImGui.TextColored(Warn, "★"); }

        if (installed) Ui.Pill("Installed", Good);
        else if (plugin.Downloads.Busy && pkg.Assets.Any(a => plugin.Downloads.ProgressFor(a.Id) != null))
            Ui.Pill("Downloading", Accent);
        else if (pkg.InstalledCount > 0) Ui.Pill("Partly", Warn);
        else Ui.Pill("Not installed", Muted);

        ImGui.TextDisabled($"{pkg.Hardware} · {Ui.FormatBytes(pkg.Assets.Sum(a => a.Size))}");
        ImGui.EndGroup();

        ImGui.SetCursorScreenPos(p0);
        if (ImGui.InvisibleButton($"##pkg{index}", size)) selectedPackage = index;
    }

    private void DrawPackageDetail(Package pkg)
    {
        Ui.SubHeading(pkg.Name);
        ImGui.SameLine();
        ImGui.TextColored(Muted, "— " + pkg.Tagline);
        ImGui.Spacing();

        ImGui.TextColored(Accent, "What it does");
        Ui.Paragraph(pkg.WhatItDoes);
        ImGui.Spacing();

        ImGui.TextColored(Accent, "What you get");
        foreach (var b in pkg.Bullets) { ImGui.Bullet(); ImGui.TextWrapped(b); }
        ImGui.Spacing();

        ImGui.TextColored(Accent, "What's included");
        foreach (var a in pkg.Assets)
            ImGui.TextDisabled($"   • {a.Id} — {Ui.FormatBytes(a.Size)} · {a.License}");
        ImGui.Spacing();

        var installed = pkg.InstalledCount == pkg.Assets.Count && pkg.Assets.Count > 0;
        if (installed)
        {
            if (pkg.InUse)
            {
                Ui.IconAction($"lock_{pkg.Key}", FontAwesomeIcon.Lock, pkg.InUseReason, enabled: false);
                ImGui.SameLine();
                ImGui.TextColored(Muted, "In use — " + pkg.InUseReason);
            }
            else if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Trash, "Remove", Vector2.Zero) && !plugin.Downloads.Busy)
            {
                plugin.Downloads.RemoveGroup(pkg.Assets, BuildProtectedIds());
                sizesStale = true;
            }

        }
        else
        {
            ImGui.BeginDisabled(plugin.Downloads.Busy);
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Download,
                    pkg.InstalledCount == 0 ? "Install" : "Finish installing", Vector2.Zero))
            {
                plugin.Downloads.StartDownload(pkg.Assets);
                sizesStale = true;
            }
            ImGui.EndDisabled();

            if (plugin.Downloads.Busy)
            {
                foreach (var a in pkg.Assets)
                {
                    var p = plugin.Downloads.ProgressFor(a.Id);
                    if (p != null && p.Phase is not DownloadPhase.Done && plugin.Downloads.IsInstalled(a.Id) != true)
                    {
                        ImGui.ProgressBar((float)p.Fraction, new Vector2(-1, 0), $"{a.Id} · {p.Phase}");
                        break;
                    }
                }
            }
        }
    }

    // ---- Saved data: a reflowing grid of roomy cards ----

    private void DrawSavedDataCard()
    {
        Ui.Heading("Saved data");
        Ui.Paragraph("Little things PopotoVox remembers as you play, kept on your PC. The learned ones " +
            "are safe to clear anytime — it'll just relearn them as you go.");
        ImGui.Spacing();

        const float gap = 8f, minCardW = 270f, cardH = 116f;
        var avail = ImGui.GetContentRegionAvail().X;
        var cols = Math.Max(1, (int)((avail + gap) / (minCardW + gap)));
        var cardW = (avail - gap * (cols - 1)) / cols;

        var i = 0;
        void Card(FontAwesomeIcon icon, string key, string title, string desc, string[] chips,
                  Action onClear, bool accent = false, bool canEdit = false)
        {
            if (i % cols != 0) ImGui.SameLine(0, gap);
            DrawSavedCard(cardW, cardH, icon, key, title, desc, chips, onClear, accent, canEdit);
            i++;
        }

        Card(FontAwesomeIcon.Microphone, "voices", "Remembered voices",
            "Keeps every NPC sounding the same each time you meet.",
            new[] { $"{plugin.VoiceSpecs.Count:N0} voices", Ui.FormatBytes(voiceSpecBytes) },
            () => plugin.VoiceSpecs.Clear());

        Card(FontAwesomeIcon.Users, "records", "Characters met",
            "Notes used to cast a fitting voice for each NPC.",
            new[] { $"{plugin.NpcRecords.Count:N0} characters", $"{sampleLineCount:N0} lines", Ui.FormatBytes(npcRecordBytes) },
            () => plugin.NpcRecords.Clear());

        Card(FontAwesomeIcon.Link, "links", "Identity links",
            "Keeps a voice steady when the game changes an NPC's internal ID.",
            new[] { $"{plugin.CrossLink.Count:N0} links", Ui.FormatBytes(crossLinkBytes) },
            () => plugin.CrossLink.Clear());

        Card(FontAwesomeIcon.Star, "overrides", "Your custom voices",
            "Voices you pinned or guided yourself — your own choices, not cache.",
            new[] { $"{plugin.Overrides.Count:N0} custom", Ui.FormatBytes(overrideBytes) },
            () => plugin.Overrides.Clear(), accent: true, canEdit: true);

        ImGui.Spacing();
        ImGui.TextDisabled("Temporary audio clips tidy themselves up every time the game restarts.");
    }

    private void DrawSavedCard(float cardW, float cardH, FontAwesomeIcon icon, string key, string title,
        string description, string[] chips, Action onClear, bool accent, bool canEdit)
    {
        if (accent) ImGui.PushStyleColor(ImGuiCol.Border, Warn);
        ImGui.BeginChild($"sd_{key}", new Vector2(cardW, cardH), true);

        // Header: icon + title, then right-aligned action icon(s) within the card.
        Ui.Icon(icon, accent ? Warn : Accent);
        ImGui.SameLine();
        ImGui.TextUnformatted(title);

        ImGui.SameLine();
        var reserve = canEdit ? 62f : 32f;
        var headAvail = ImGui.GetContentRegionAvail().X;
        if (headAvail > reserve) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (headAvail - reserve));
        if (canEdit)
        {
            if (Ui.IconAction($"edit_{key}", FontAwesomeIcon.Pen, "Manage in Home"))
                navigate(ShellSection.Home);
            ImGui.SameLine();
        }
        if (Ui.IconAction($"clr_{key}", FontAwesomeIcon.Trash, "Clear")) pendingClear = key;

        // Description.
        ImGui.PushStyleColor(ImGuiCol.Text, Muted);
        ImGui.TextWrapped(description);
        ImGui.PopStyleColor();

        // Stats as chips, or an inline clear confirmation.
        if (pendingClear == key)
        {
            ImGui.TextColored(Warn, "Clear?");
            ImGui.SameLine();
            if (ImGui.SmallButton($"Yes##{key}")) { onClear(); sizesStale = true; pendingClear = null; }
            ImGui.SameLine();
            if (ImGui.SmallButton($"No##{key}")) pendingClear = null;
        }
        else
        {
            for (var c = 0; c < chips.Length; c++)
            {
                if (c > 0) ImGui.SameLine(0, 4);
                Ui.Pill(chips[c], c == 0 ? Accent : Muted);
            }
        }

        ImGui.EndChild();
        if (accent) ImGui.PopStyleColor();
    }

    // ---- Cached voice lines (performance) ----

    private void DrawLineCacheCard()
    {
        var cfg = plugin.Configuration;
        Ui.BeginCard(FontAwesomeIcon.Clock, "Cached voice lines", Ui.FormatBytes(lineCacheBytes));
        Ui.Paragraph("Saves the finished audio of spoken lines so a repeated line plays instantly and skips " +
            "the GPU entirely — a nice break for busier machines. Lines older than the keep-time are cleared " +
            "automatically (including leftovers from a previous session, on the next launch).");

        var on = cfg.LineCacheEnabled;
        if (ImGui.Checkbox("Cache rendered lines", ref on))
        {
            cfg.LineCacheEnabled = on;
            if (!on) plugin.LineCache.Clear(); // turning it off frees the disk now
            cfg.Save();
            sizesStale = true;
        }

        if (cfg.LineCacheEnabled)
        {
            var hours = cfg.LineCacheRetentionHours;
            ImGui.SetNextItemWidth(220);
            if (ImGui.SliderInt("Keep for", ref hours, 1, 24, "%d hours"))
            {
                cfg.LineCacheRetentionHours = hours;
                plugin.LineCache.Prune(); // re-apply the new age limit immediately
                cfg.Save();
                sizesStale = true;
            }
            ImGui.SameLine();
            if (ImGui.Button("Clear now")) { plugin.LineCache.Clear(); sizesStale = true; }
            ImGui.TextDisabled($"Using {Ui.FormatBytes(lineCacheBytes)} (auto-capped at 300 MB, oldest cleared first).");
        }

        Ui.EndCard();
    }

    // ---- Designed voices (Ultra engine) ----

    private void DrawVoicesCacheCard()
    {
        var cfg = plugin.Configuration;
        // Only relevant to the Ultra (VoxCPM2) engine, which designs a unique reference voice per NPC. Hide the
        // card entirely unless Ultra is selected or some design WAVs already exist on disk.
        if (cfg.TtsEngine != TtsEngineChoice.VoxCPM2 && voicesCacheBytes <= 0) return;

        Ui.BeginCard(FontAwesomeIcon.Fingerprint, "Designed voices (Ultra)", Ui.FormatBytes(voicesCacheBytes));
        Ui.Paragraph("The Ultra (VoxCPM2) engine designs a unique reference voice per NPC and reuses it for every " +
            "line they speak. These are kept across sessions so a returning NPC sounds the same instantly. Old, " +
            "unused voices are cleared automatically by age and a size cap.");

        var dir = plugin.Paths.VoicesCache;

        var hours = cfg.VoicesCacheRetentionHours;
        ImGui.SetNextItemWidth(220);
        if (ImGui.SliderInt("Keep for##voices", ref hours, 24, 720, "%d hours"))
        {
            cfg.VoicesCacheRetentionHours = hours;
            VoxCpmEngine.PruneVoicesCache(dir, cfg.VoicesCacheRetentionHours, cfg.VoicesCacheMaxMB); // apply now
            cfg.Save();
            sizesStale = true;
        }

        var maxMb = cfg.VoicesCacheMaxMB;
        ImGui.SetNextItemWidth(220);
        if (ImGui.SliderInt("Size limit##voices", ref maxMb, 64, 4096, "%d MB"))
        {
            cfg.VoicesCacheMaxMB = maxMb;
            VoxCpmEngine.PruneVoicesCache(dir, cfg.VoicesCacheRetentionHours, cfg.VoicesCacheMaxMB); // evict to the new cap
            cfg.Save();
            sizesStale = true;
        }
        ImGui.SameLine();
        if (ImGui.Button("Clear now##voices")) { VoxCpmEngine.ClearVoicesCache(dir); sizesStale = true; }
        ImGui.TextDisabled($"Using {Ui.FormatBytes(voicesCacheBytes)} (oldest cleared first; in-use voices are kept).");

        Ui.EndCard();
    }

    // ---- package model + copy ----

    private sealed class Package
    {
        public string Key = "";
        public string Name = "";
        public FontAwesomeIcon Icon;
        public bool Recommended;
        public string Hardware = "";
        public string Tagline = "";
        public string WhatItDoes = "";
        public string[] Bullets = Array.Empty<string>();
        public List<AssetEntry> Assets = new();
        public bool InUse;
        public string InUseReason = "";
        public int InstalledCount;
        public long InstalledBytes;
        public Vector4 Color;
    }

    /// <summary>Stable per-package colour used by both the usage bar/legend and the package tile.</summary>
    private static Vector4 ColorFor(string key) => key switch
    {
        "llm" => new(0.66f, 0.55f, 0.95f, 1f),     // purple
        "piper" => new(0.55f, 0.85f, 0.55f, 1f),   // green
        "kokoro" => new(0.45f, 0.72f, 1.0f, 1f),   // blue
        "voxcpm2" => new(1.0f, 0.70f, 0.35f, 1f),  // orange
        "lines" => new(0.40f, 0.80f, 0.75f, 1f),   // teal — cached line audio
        _ => new(0.62f, 0.62f, 0.68f, 1f),         // saved data / fallback
    };

    private List<Package> BuildPackages()
    {
        var list = new List<Package>();
        if (!plugin.Assets.Available) return list;

        // Smart casting AI leads (it's the brain), then the voice engines low → ultra. GPU machines
        // include the CUDA runtime in the package so its size and install state are truthful.
        var llm = plugin.Assets.LlmAssets(includeCuda: plugin.Hardware?.HasNvidiaGpu == true).ToList();
        if (llm.Count > 0)
        {
            list.Add(new Package
            {
                Key = "llm",
                Name = "Smart casting AI",
                Icon = FontAwesomeIcon.Brain,
                Hardware = "CPU or GPU",
                Tagline = "casts the perfect voice for every NPC",
                WhatItDoes = "A small local AI reads each NPC's race, gender, build and even their gear, then " +
                             "picks the voice that suits them — and on Ultra, directs the emotion of each line.",
                Bullets = new[]
                {
                    "Tailored casting instead of fixed rules",
                    "Adds emotion cues on expressive engines",
                    "Runs on your PC — faster with a GPU",
                    "Optional: without it, voices use built-in rules",
                },
                Assets = llm,
                InUse = plugin.Configuration.LlmEnabled,
                InUseReason = "Smart casting is on. Turn it off in Settings first.",
                InstalledCount = llm.Count(a => plugin.Downloads.IsInstalled(a.Id) == true),
                InstalledBytes = llm.Where(a => plugin.Downloads.IsInstalled(a.Id) == true).Sum(a => a.Size),
                Color = ColorFor("llm"),
            });
        }

        foreach (var e in TtsEngineCatalog.All.OrderBy(x => x.Tier))
        {
            var assets = plugin.Assets.AssetsForEngine(e.Id).ToList();
            if (assets.Count == 0) continue;
            var copy = EngineCopy(e.Id);
            list.Add(new Package
            {
                Key = e.Id.ToString().ToLowerInvariant(),
                Name = $"{e.DisplayName} voice",
                Icon = copy.icon,
                Recommended = e.Recommended,
                Hardware = e.RequiresGpu ? "GPU" : "CPU",
                Tagline = e.Tagline,
                WhatItDoes = copy.what,
                Bullets = copy.bullets,
                Assets = assets,
                InUse = e.Id == plugin.ActiveEngineId,
                InUseReason = "you're using this voice. Switch engines in Settings first (no reload needed).",
                InstalledCount = assets.Count(a => plugin.Downloads.IsInstalled(a.Id) == true),
                InstalledBytes = assets.Where(a => plugin.Downloads.IsInstalled(a.Id) == true).Sum(a => a.Size),
                Color = ColorFor(e.Id.ToString().ToLowerInvariant()),
            });
        }

        return list;
    }

    private static (FontAwesomeIcon icon, string what, string[] bullets) EngineCopy(TtsEngineChoice id) => id switch
    {
        TtsEngineChoice.Kokoro => (FontAwesomeIcon.Microphone,
            "Turns NPC dialogue into clear, natural-sounding speech entirely on your processor — no graphics card required.",
            new[] { "53 voices across 9 real-world accents", "Light on memory and quick to start", "The recommended pick for most players" }),
        TtsEngineChoice.Piper => (FontAwesomeIcon.Bolt,
            "The smallest, snappiest engine — great for older machines, or when you want lines to start the instant they appear.",
            new[] { "904 voices", "Tiny footprint, near-instant startup", "A little more robotic than Kokoro" }),
        _ => (FontAwesomeIcon.Star,
            "Performances, not just narration — VoxCPM2 designs a one-of-a-kind voice for each NPC and performs every line with emotion.",
            new[] { "A unique designed voice per NPC", "Per-line emotion, acted in character", "Real-world accents in the speaker's own tongue", "Needs an NVIDIA GPU and a larger download" }),
    };

    // ---- shared helpers ----

    private ISet<string> BuildProtectedIds()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in plugin.Assets.AssetsForEngine(plugin.ActiveEngineId)) set.Add(a.Id);
        if (plugin.Configuration.LlmEnabled)
            foreach (var a in plugin.Assets.LlmAssets(includeCuda: true)) set.Add(a.Id);
        return set;
    }

    private void RefreshSizes()
    {
        voiceSpecBytes = FileLen(plugin.Paths.VoiceSpecCachePath);
        npcRecordBytes = FileLen(plugin.Paths.NpcRecordCachePath);
        crossLinkBytes = FileLen(plugin.Paths.CrossLinkPath);
        overrideBytes = FileLen(plugin.Paths.OverridesPath);
        lineCacheBytes = plugin.LineCache.Bytes();
        voicesCacheBytes = VoxCpmEngine.VoicesCacheBytes(plugin.Paths.VoicesCache);
        sampleLineCount = plugin.NpcRecords.All.Values.Sum(r => r.SampleLines.Count);
        sizesStale = false;
    }

    private static long FileLen(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch { return 0; }
    }
}
