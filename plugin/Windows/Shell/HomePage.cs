using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using PopotoVox.Casting;
using PopotoVox.Npc;
using PopotoVox.Tts;

namespace PopotoVox.Windows;

/// <summary>
/// Home: a one-line status strip plus the NPC browser. The detail pane is a modern "character sheet":
/// a Character card (a hero header with a role monogram + big name, then a Label:Value identity stat
/// block and a full gear paper-doll), a Quotes card ("things they've said"), and a Voice card that
/// explains WHY the NPC was cast and lets you customise it with a friendly named-voice picker.
/// Ported/evolved from the former MainWindow's NPCs tab (PRD §7).
/// </summary>
public sealed class HomePage : IShellPage
{
    // The full worn loadout, in display order. Every NPC shows all of these; empty slots read "--".
    private static readonly string[] GearOrder =
    {
        "Main hand", "Off-hand", "Head", "Body", "Hands", "Legs",
        "Feet", "Ears", "Neck", "Wrists", "Right ring", "Left ring",
    };

    private readonly Plugin plugin;
    private readonly NpcPortrait monogram = new();

    // Browser state
    private string search = "";
    private bool showCastOnly;
    private bool showOverriddenOnly;
    private uint? selectedNpc;

    // Override editor buffers
    private string editPrompt = "";
    private int editSpeaker = -1;
    private float editLength = 1.0f;
    private bool editPinSpeaker;
    private bool editPinLength;
    private bool editShowAllVoices;
    private string previewText = "Well met, adventurer. The road ahead is long.";

    public HomePage(Plugin plugin) => this.plugin = plugin;

    public ShellSection Section => ShellSection.Home;
    public string Label => "NPCs";
    public FontAwesomeIcon Icon => FontAwesomeIcon.Users;

    public void Draw()
    {
        DrawStatusStrip();
        ImGui.Separator();
        DrawBrowser();
    }

    // ---------------------------------------------------------------- status strip

    private void DrawStatusStrip()
    {
        var active = TtsEngineCatalog.Get(plugin.ActiveEngineId);
        var ready = plugin.Engine.IsReady;
        ImGui.TextColored(Ui.Accent, "Active voice:");
        ImGui.SameLine(); ImGui.TextUnformatted(active.DisplayName);
        ImGui.SameLine(); Ui.Pill(ready ? "Ready" : "Not installed", ready ? Ui.Good : Ui.Warn);
        ImGui.SameLine(); ImGui.TextDisabled($"   ·   {plugin.VoiceSpecs.Count} cast   ·   {plugin.NpcRecords.Count} met");
    }

    // ---------------------------------------------------------------- Browser

    private void DrawBrowser()
    {
        if (!plugin.Engine.IsReady)
            ImGui.TextColored(Ui.Warn,
                "TTS engine not installed yet — see the Storage tab. NPCs still list as you meet them.");

        // Width-managed row: a search icon + hint field that shrinks to leave room for both filter
        // checkboxes, so the right one never gets clipped at narrow window widths.
        var style = ImGui.GetStyle();
        float Checkbox(string s) => ImGui.GetFrameHeight() + style.ItemInnerSpacing.X + ImGui.CalcTextSize(s).X;
        var reserved = Checkbox("Cast only") + style.ItemSpacing.X + Checkbox("Overridden only") + style.ItemSpacing.X;

        ImGui.AlignTextToFramePadding();
        Ui.Icon(FontAwesomeIcon.Search, Ui.Muted);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(Math.Max(160f, ImGui.GetContentRegionAvail().X - reserved));
        ImGui.InputTextWithHint("##search", "Search name or id", ref search, 64);
        ImGui.SameLine(); ImGui.Checkbox("Cast only", ref showCastOnly);
        ImGui.SameLine(); ImGui.Checkbox("Overridden only", ref showOverriddenOnly);
        ImGui.Separator();

        var listWidth = 300f;
        var filtering = !string.IsNullOrWhiteSpace(search) || showCastOnly || showOverriddenOnly;
        if (ImGui.BeginChild("##npcList", new Vector2(listWidth, 0), true))
        {
            var matches = FilteredRecords().ToList();
            if (filtering)
            {
                foreach (var record in matches)
                    DrawNpcRow(record);
            }
            else
            {
                var recent = matches
                    .Where(r => r.LastSpokeUtc > DateTime.MinValue)
                    .OrderByDescending(r => r.LastSpokeUtc)
                    .Take(3)
                    .ToList();
                var recentIds = recent.Select(r => r.NpcId!.Value).ToHashSet();

                if (recent.Count > 0)
                {
                    Ui.SubHeading("Recent");
                    foreach (var record in recent)
                        DrawNpcRow(record);
                    ImGui.Spacing();
                }

                Ui.SubHeading(recent.Count > 0 ? "All NPCs" : "NPCs");
                foreach (var record in matches.Where(r => !recentIds.Contains(r.NpcId!.Value)))
                    DrawNpcRow(record);
            }
        }
        ImGui.EndChild();

        ImGui.SameLine();
        if (ImGui.BeginChild("##npcDetail", new Vector2(0, 0), true))
            DrawDetail();
        ImGui.EndChild();
    }

    private IEnumerable<NpcRecord> FilteredRecords()
    {
        var all = plugin.NpcRecords.All.Values
            .Where(r => r.NpcId != null)
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var r in all)
        {
            var id = r.NpcId!.Value;
            if (showCastOnly && !plugin.VoiceSpecs.TryGet(id, out _)) continue;
            if (showOverriddenOnly && !plugin.Overrides.TryGet(id, out _)) continue;
            if (!string.IsNullOrWhiteSpace(search))
            {
                var hit = r.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                          || id.ToString().Contains(search);
                if (!hit) continue;
            }
            yield return r;
        }
    }

    // A two-line list row: name + muted class/role subtext, with a trailing status glyph
    // (♪ cast · … casting · ★ overridden). Drawn over a full-width invisible selectable.
    private void DrawNpcRow(NpcRecord r)
    {
        var id = r.NpcId!.Value;
        var cast = plugin.VoiceSpecs.TryGet(id, out _);
        var casting = !cast && plugin.CastingState.IsCasting(id);
        var overridden = plugin.Overrides.TryGet(id, out _);

        var lineH = ImGui.GetTextLineHeight();
        var rowH = lineH * 2 + 6f;
        var start = ImGui.GetCursorScreenPos();
        if (ImGui.Selectable($"##row{id}", selectedNpc == id, ImGuiSelectableFlags.None, new Vector2(0, rowH)))
            SelectNpc(id);
        var width = ImGui.GetItemRectSize().X;

        var draw = ImGui.GetWindowDrawList();
        draw.AddText(start + new Vector2(2, 3),
            ImGui.ColorConvertFloat4ToU32(new Vector4(0.92f, 0.92f, 0.95f, 1f)), r.Name);
        var sub = r.Job ?? r.Role ?? r.Race ?? "—";
        draw.AddText(start + new Vector2(2, 3 + lineH + 1),
            ImGui.ColorConvertFloat4ToU32(Ui.Muted), sub);

        if (overridden) StatusGlyph(draw, start, width, lineH + 3 + lineH + 1, "★", Ui.Accent);
        var (glyph, gcol) = cast ? ("♪", Ui.Good) : casting ? ("…", Ui.Warn) : ("", Ui.Muted);
        if (glyph.Length > 0) StatusGlyph(draw, start, width, 3, glyph, gcol);
    }

    private static void StatusGlyph(ImDrawListPtr draw, Vector2 rowStart, float rowWidth, float dy, string glyph, Vector4 color)
    {
        var w = ImGui.CalcTextSize(glyph).X;
        draw.AddText(rowStart + new Vector2(rowWidth - w - 6f, dy), ImGui.ColorConvertFloat4ToU32(color), glyph);
    }

    private void SelectNpc(uint id)
    {
        selectedNpc = id;
        editShowAllVoices = false;
        if (plugin.Overrides.TryGet(id, out var ovr))
        {
            editPrompt = ovr.Prompt;
            editPinSpeaker = ovr.PinnedSpeakerId.HasValue;
            editSpeaker = ovr.PinnedSpeakerId ?? -1;
            editPinLength = ovr.PinnedLengthScale.HasValue;
            editLength = ovr.PinnedLengthScale ?? 1.0f;
        }
        else
        {
            editPrompt = "";
            editPinSpeaker = false; editSpeaker = -1;
            editPinLength = false; editLength = 1.0f;
        }
    }

    private void DrawDetail()
    {
        if (selectedNpc is not { } id || !plugin.NpcRecords.TryGet(id, out var r))
        {
            ImGui.TextDisabled("Select an NPC to see its bio and the voice we gave it.");
            return;
        }

        DrawCharacterCard(id, r);
        DrawQuotesCard(r);
        DrawVoiceCard(id, r);
    }

    // ---------------------------------------------------------------- character card

    private void DrawCharacterCard(uint id, NpcRecord r)
    {
        Ui.BeginCard();

        DrawHeroHeader(r);
        ImGui.Spacing();
        DrawIdentityStats(id, r);

        ImGui.Spacing();
        Ui.SubHeading("Gear");
        DrawGearGrid(r);

        Ui.EndCard();
    }

    // Hero header: a role monogram + a large name (and title). Voice status lives in the Voice Casting
    // card and the left-list row glyphs, so the header stays clean.
    private void DrawHeroHeader(NpcRecord r)
    {
        const float badge = 46f;
        monogram.DrawBadge(r, new Vector2(badge, badge));
        ImGui.SameLine(0, 12f);

        ImGui.BeginGroup();
        Ui.Scaled(1.7f, () => ImGui.TextUnformatted(r.Name));
        if (!string.IsNullOrWhiteSpace(r.Title))
            ImGui.TextColored(Ui.Muted, r.Title!);
        ImGui.EndGroup();
    }

    // The "character sheet" stat block: Label : Value rows, class emphasised, estimated fields marked.
    private void DrawIdentityStats(uint id, NpcRecord r)
    {
        var conf = r.DataConfidence;
        if (r.Gender != null)
            Ui.StatRow("Gender", r.Gender);
        if (r.ApparentAge != null)
            Ui.StatRow("Age", r.ApparentAge, null, IsLow(conf, "bodyType"), "Estimated from the model's body type");
        var classOrRole = r.Job ?? r.Role;
        if (classOrRole != null)
            Ui.StatRow("Class", classOrRole, Ui.Accent, IsLow(conf, "weapon"), "Estimated from the equipped weapon");
        var lineage = string.Join(" · ", new[] { r.Race, r.Tribe }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (lineage.Length > 0)
            Ui.StatRow("Race", lineage);
        if (r.Stature != null)
            Ui.StatRow("Build", r.Stature);
        if (r.Zones.Count > 0)
            Ui.StatRow("Met", string.Join(", ", r.Zones));
        var heard = FormatLastHeard(r.LastSpokeUtc);
        if (heard != null)
            Ui.StatRow("Last heard", heard);
        Ui.StatRow("ID", id.ToString(), Ui.Muted);
    }

    private static void DrawGearGrid(NpcRecord r)
    {
        var map = new Dictionary<string, GearSlot>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in r.Gear) map[g.Slot] = g;

        ImGui.Columns(2, "##gear", false);
        foreach (var slot in GearOrder)
        {
            map.TryGetValue(slot, out var g);
            DrawGearCell(slot, g);
            ImGui.NextColumn();
        }
        ImGui.Columns(1);
    }

    private static void DrawGearCell(string slot, GearSlot? g)
    {
        ImGui.TextDisabled(slot);
        ImGui.SameLine(96f);
        if (g == null)
        {
            ImGui.TextColored(Ui.Muted, "--");
        }
        else if (g.Item == null)
        {
            ImGui.TextColored(Ui.Warn, "?");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Worn, but not a nameable item");
        }
        else
        {
            var name = g.Item.Length > 26 ? g.Item[..25] + "…" : g.Item;
            ImGui.TextUnformatted(name);
            if (name != g.Item && ImGui.IsItemHovered()) ImGui.SetTooltip(g.Item);
        }
    }

    // ---------------------------------------------------------------- quotes card

    private void DrawQuotesCard(NpcRecord r)
    {
        if (r.SampleLines.Count == 0) return;

        Ui.BeginCard(FontAwesomeIcon.QuoteLeft, "Things they've said");

        // Bound the height so a few long lines can't blow up the card; scroll past the cap. The
        // estimate (≈ 2.2 wrapped rows/line) keeps short content compact instead of reserving 180px.
        var lineH = ImGui.GetTextLineHeightWithSpacing();
        var estimate = r.SampleLines.Count * lineH * 2.2f + 8f;
        var height = Math.Clamp(estimate, lineH * 2f, 180f);
        if (ImGui.BeginChild("##quotes", new Vector2(0, height), false))
        {
            foreach (var line in r.SampleLines)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.88f, 0.88f, 0.92f, 1f));
                ImGui.TextWrapped($"“{line}”");
                ImGui.PopStyleColor();
                ImGui.Spacing();
            }
        }
        ImGui.EndChild();

        Ui.EndCard();
    }

    // ---------------------------------------------------------------- voice card (why + edit)

    private void DrawVoiceCard(uint id, NpcRecord r)
    {
        Ui.BeginCard(FontAwesomeIcon.VolumeUp, "Voice Casting");

        if (plugin.VoiceSpecs.TryGet(id, out var spec))
        {
            // Show the cast the way it was ACTUALLY made (spec.Engine) — NOT the currently-active engine.
            // Otherwise a voice designed by Ultra (which stores SpeakerId 0) gets relabelled as catalog
            // "voice 0 (Female, American)" the moment you switch to Piper/Kokoro. (Bug: wrong voice bubble.)
            if (IsDesignedEngine(spec.Engine))
                DrawUltraVoice(spec);
            else
                DrawCatalogVoice(r, spec);
        }
        else if (plugin.CastingState.IsCasting(id))
        {
            ImGui.TextDisabled("Cast as"); ImGui.SameLine(); Ui.Pill("casting…", Ui.Warn);
        }
        else
        {
            ImGui.TextDisabled("Not cast yet — speaks on first encounter.");
            ImGui.Spacing();
            foreach (var line in CastingRationale.Predict(r))
                Ui.Paragraph("•  " + line);
        }

        Ui.CardSeparator();
        Ui.Heading("Customize");
        DrawOverrideControls(id, r);

        Ui.EndCard();
    }

    // The current voice for a pooled engine (Kokoro/Piper): a named catalog speaker + why.
    private void DrawCatalogVoice(NpcRecord r, VoiceSpec spec)
    {
        // Describe the speaker from the catalog it was CAST with (spec.Engine), and take gender/accent
        // from the locked spec itself — both authoritative no matter which engine happens to be active now.
        var vi = VoiceForSpec(spec);
        var gender = spec.Gender != VoiceGender.Neutral ? spec.Gender : (vi?.Gender ?? VoiceGender.Neutral);
        var accent = spec.Traits?.Accent ?? vi?.Accent ?? VoiceAccent.Unknown;
        var name = vi?.Name ?? $"speaker {spec.SpeakerId}";
        var voiceLabel = accent != VoiceAccent.Unknown
            ? $"{name} ({gender.Label()}, {accent.Label()})"
            : $"{name} ({gender.Label()})";
        ImGui.TextDisabled("Cast as"); ImGui.SameLine();
        Ui.Pill(voiceLabel, Ui.Good); ImGui.SameLine();
        Ui.Pill(SourceLabel(spec.Source), Ui.Accent);

        if (!string.IsNullOrWhiteSpace(spec.Description))
        {
            ImGui.Spacing();
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.92f, 0.92f, 0.95f, 1f));
            ImGui.TextWrapped($"“{spec.Description}”");
            ImGui.PopStyleColor();
            ImGui.TextDisabled("— the casting director");
        }

        ImGui.Spacing();
        foreach (var line in CastingRationale.Explain(r, spec, vi))
            Ui.Paragraph("•  " + line);
    }

    // The current voice for the Ultra engine: a one-of-a-kind designed voice — show its traits, the
    // base mood the lines are performed from, and the casting director's description (no catalog speaker).
    private void DrawUltraVoice(VoiceSpec spec)
    {
        ImGui.TextDisabled("Voice"); ImGui.SameLine();
        Ui.Pill("Designed for this NPC", Ui.Good); ImGui.SameLine();
        Ui.Pill(SourceLabel(spec.Source), Ui.Accent);

        var bits = new List<string>();
        if (spec.Gender != VoiceGender.Neutral) bits.Add(spec.Gender.Label());
        if (spec.Traits is { } t)
        {
            bits.Add(AgeLabel(t.Age));
            if (t.Accent != VoiceAccent.Unknown) bits.Add(t.Accent.Label() + " accent");
            if (!string.IsNullOrWhiteSpace(t.Timbre)) bits.Add(t.Timbre);
        }

        ImGui.Spacing();
        if (bits.Count > 0) Ui.StatRow("Voice", string.Join(", ", bits));
        if (!string.IsNullOrWhiteSpace(spec.Style)) Ui.StatRow("Base mood", spec.Style);

        if (!string.IsNullOrWhiteSpace(spec.Description))
        {
            ImGui.Spacing();
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.92f, 0.92f, 0.95f, 1f));
            ImGui.TextWrapped($"“{spec.Description}”");
            ImGui.PopStyleColor();
            ImGui.TextDisabled("— the casting director");
        }

        ImGui.Spacing();
        Ui.Paragraph("•  A one-of-a-kind voice is designed for this NPC, then every line is performed from " +
                     "the base mood above — shaded to the words by the per-line director.");
    }

    private void DrawOverrideControls(uint id, NpcRecord r)
    {
        var isUltra = plugin.ActiveEngineId == TtsEngineChoice.VoxCPM2;

        if (isUltra)
        {
            // Ultra (VoxCPM2) has no fixed voice pool — the voice is designed from the casting. The lever is
            // the casting note + re-cast (which re-designs). Speaker pinning / speaking-rate don't apply.
            Ui.Paragraph("Ultra designs this NPC's voice from the casting. If it's not right, add a note " +
                         "below and re-cast — it will design a new voice (and base mood) to match.");
        }
        else
        {
            // ---- Voice ----------------------------------------------------------------
            Ui.SubHeading("Voice");
            DrawVoicePicker(r);

            // ---- Speaking rate --------------------------------------------------------
            ImGui.Spacing();
            Ui.SubHeading("Speaking rate");
            ImGui.Checkbox("Override speaking rate", ref editPinLength);
            if (editPinLength)
            {
                ImGui.SetNextItemWidth(-1);
                ImGui.SliderFloat("##len", ref editLength, 0.5f, 2.0f);
                ImGui.TextDisabled("Lower = faster, higher = slower. 1.0 is natural.");
            }
            else
            {
                ImGui.TextDisabled("Uses the global pace from Settings.");
            }
        }

        // ---- Casting note (both) --------------------------------------------------
        ImGui.Spacing();
        Ui.SubHeading("Casting note");
        ImGui.InputTextMultiline("##prompt", ref editPrompt, 512, new Vector2(-1, 60));
        ImGui.TextDisabled("Free-text hint for the AI when (re-)casting this NPC — e.g. \"gruff old sailor, weary and slow\".");

        // ---- Preview --------------------------------------------------------------
        ImGui.Spacing();
        Ui.SubHeading("Preview");
        ImGui.SetNextItemWidth(-44);
        ImGui.InputText("##previewLine", ref previewText, 256);
        ImGui.SameLine();
        if (Ui.IconAction("##preview", FontAwesomeIcon.Play, "Hear this line in the current voice", plugin.Engine.IsReady))
            PreviewSelected(id, r);

        // ---- Actions --------------------------------------------------------------
        ImGui.Spacing();
        if (!isUltra)
        {
            if (ImGui.Button("Save override")) SaveOverride(id);
            ImGui.SameLine();
        }
        if (ImGui.Button("Save & re-cast")) { SaveOverride(id); plugin.Director.RecastNpc(id); }
        if (plugin.Overrides.TryGet(id, out _))
        {
            ImGui.SameLine();
            if (ImGui.Button("Remove override")) plugin.Overrides.Remove(id);
        }
    }

    // A friendly named-voice dropdown: "Automatic" plus the engine's voices, gender-matched to the
    // NPC by default (a toggle reveals the full roster). Selecting a named voice pins it.
    private void DrawVoicePicker(NpcRecord r)
    {
        var gender = r.Gender.ToVoiceGender();
        var ids = (editShowAllVoices ? plugin.SpeakerCatalog.AllowedIds : plugin.SpeakerCatalog.PoolFor(gender)).ToList();
        // Keep a pinned-but-out-of-pool voice visible so the current choice never silently vanishes.
        if (editPinSpeaker && !ids.Contains(editSpeaker)) ids.Insert(0, editSpeaker);

        var names = new List<string> { "Automatic (best match)" };
        names.AddRange(ids.Select(VoiceLabel));

        var cur = editPinSpeaker ? ids.IndexOf(editSpeaker) + 1 : 0;
        if (cur < 0) cur = 0;

        ImGui.SetNextItemWidth(-1);
        if (ImGui.Combo("##voice", ref cur, names.ToArray(), names.Count))
        {
            if (cur == 0) editPinSpeaker = false;
            else { editPinSpeaker = true; editSpeaker = ids[cur - 1]; }
        }

        var showAll = editShowAllVoices;
        if (ImGui.Checkbox("Show all voices (ignore gender match)", ref showAll)) editShowAllVoices = showAll;
    }

    private string VoiceLabel(int speakerId)
    {
        var vi = plugin.SpeakerCatalog.Describe(speakerId);
        return vi != null ? $"{vi.Name} — {vi.Gender.Label()}, {vi.Accent.Label()}" : $"speaker {speakerId}";
    }

    private void SaveOverride(uint id)
    {
        plugin.Overrides.Put(new NpcOverride
        {
            NpcId = id,
            Prompt = editPrompt,
            PinnedSpeakerId = editPinSpeaker ? editSpeaker : null,
            PinnedLengthScale = editPinLength ? editLength : null,
        });
    }

    private void PreviewSelected(uint id, NpcRecord r)
    {
        // Ultra renders from the NPC's cached designed voice (traits), not a catalog speaker — preview that.
        if (plugin.ActiveEngineId == TtsEngineChoice.VoxCPM2)
        {
            if (plugin.VoiceSpecs.TryGet(id, out var castSpec))
                plugin.Preview(castSpec, previewText);
            return;
        }

        // Synthesize with the pending editor values without touching the locked cache.
        var key = IdentityFingerprint.Compute(r.Name, r.ModelHashSeed);
        var gender = r.Gender.ToVoiceGender();
        var speaker = editPinSpeaker ? plugin.SpeakerCatalog.Clamp(editSpeaker, key)
                                     : plugin.SpeakerCatalog.PickDeterministic(key, gender);
        var globals = plugin.Configuration.GlobalVoiceParams();
        var spec = new VoiceSpec
        {
            NpcId = id,
            SpeakerName = r.Name,
            SpeakerId = speaker,
            Params = new VoiceParams
            {
                LengthScale = editPinLength ? editLength : globals.LengthScale,
                NoiseScale = globals.NoiseScale,
                NoiseW = globals.NoiseW,
            },
            Source = VoiceSource.Override,
        };
        plugin.Preview(spec, previewText);
    }

    // ---------------------------------------------------------------- helpers

    private static string SourceLabel(VoiceSource source) => source switch
    {
        VoiceSource.Llm => "AI",
        VoiceSource.Override => "your override",
        _ => "rules",
    };

    /// <summary>Engines that design a bespoke voice per NPC (no catalog speaker — SpeakerId is unused).</summary>
    private static bool IsDesignedEngine(string engine) =>
        string.Equals(engine, "voxcpm2", StringComparison.OrdinalIgnoreCase);

    /// <summary>The catalog voice matching how this spec was CAST (spec.Engine), independent of the
    /// engine that's active now — so an already-cast NPC's voice bubble never gets mislabelled.</summary>
    private static VoiceInfo? VoiceForSpec(VoiceSpec spec) =>
        VoicePalette.For(EngineOf(spec.Engine)).FirstOrDefault(v => v.Id == spec.SpeakerId);

    private static TtsEngineChoice EngineOf(string engine) => engine?.ToLowerInvariant() switch
    {
        "piper" => TtsEngineChoice.Piper,
        "kokoro" => TtsEngineChoice.Kokoro,
        "voxcpm2" => TtsEngineChoice.VoxCPM2,
        _ => TtsEngineChoice.Kokoro,
    };

    private static string AgeLabel(VoiceAge age) => age switch
    {
        VoiceAge.Child => "child",
        VoiceAge.Young => "young adult",
        VoiceAge.MiddleAged => "middle-aged",
        VoiceAge.Elderly => "elderly",
        _ => "adult",
    };

    private static bool IsLow(Dictionary<string, string> conf, string key) =>
        conf.TryGetValue(key, out var c) && c == "low";

    // Friendly "when did we last hear them" — relative for recent, an absolute date past a week.
    private static string? FormatLastHeard(DateTime utc)
    {
        if (utc <= DateTime.MinValue) return null;
        var delta = DateTime.UtcNow - utc;
        if (delta < TimeSpan.FromMinutes(1)) return "just now";
        if (delta < TimeSpan.FromHours(1)) return $"{(int)delta.TotalMinutes} min ago";
        if (delta < TimeSpan.FromDays(1)) return $"{(int)delta.TotalHours} hr ago";
        if (delta < TimeSpan.FromDays(7)) return $"{(int)delta.TotalDays} d ago";
        return utc.ToLocalTime().ToString("MMM d, yyyy");
    }
}
