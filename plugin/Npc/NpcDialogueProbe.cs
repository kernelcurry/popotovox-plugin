using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;

namespace PopotoVox.Npc;

/// <summary>One resolved static dialogue line + which sheet it came from + whether it carries macros
/// (e.g. <c>&lt;name&gt;</c>), which is why a static line may not byte-match the spoken one.</summary>
public sealed record DialogueLine(string Type, string Text, bool HasMacro);

/// <summary>An NPC's resolvable static dialogue pool + the references we couldn't resolve to plain text.</summary>
public sealed class NpcDialoguePool
{
    public uint NpcId;
    public readonly List<DialogueLine> Lines = new();
    public readonly List<string> UnresolvedRefs = new(); // e.g. "CustomTalk#123 (script text)"
}

/// <summary>
/// SPIKE (M10): resolves an NPC's STATIC dialogue from game data with no dialogue spoken, by following
/// <see cref="ENpcBase.Balloon"/> + <see cref="ENpcBase.ENpcData"/>. Resolves the plain-text sheets
/// (DefaultTalk / NpcYell / Balloon); CustomTalk + Quest text is script-driven, so those are only NOTED as
/// unresolved. Purpose: measure whether this static text matches what NPCs actually say (→ whether
/// pre-rendering lines into the cache is worth building). Results cached per NpcId.
/// </summary>
public sealed class NpcDialogueProbe
{
    private readonly IDataManager data;
    private readonly IPluginLog log;
    private readonly Dictionary<uint, NpcDialoguePool> cache = new();

    public NpcDialogueProbe(IDataManager data, IPluginLog log)
    {
        this.data = data;
        this.log = log;
    }

    public NpcDialoguePool Resolve(uint npcId)
    {
        if (cache.TryGetValue(npcId, out var hit)) return hit;

        var pool = new NpcDialoguePool { NpcId = npcId };
        try
        {
            if (data.GetExcelSheet<ENpcBase>().GetRowOrDefault(npcId) is { } b)
            {
                AddBalloon(pool, b.Balloon.RowId); // overhead bubble (direct)

                foreach (var r in b.ENpcData)
                {
                    if (r.RowId == 0) continue;
                    if (r.Is<DefaultTalk>() && r.GetValueOrDefault<DefaultTalk>() is { } dt)
                        foreach (var t in dt.Text) AddLine(pool, "DefaultTalk", t);
                    else if (r.Is<NpcYell>() && r.GetValueOrDefault<NpcYell>() is { } ny)
                        AddLine(pool, "NpcYell", ny.Text);
                    else if (r.Is<Balloon>())
                        AddBalloon(pool, r.RowId);
                    else if (r.Is<CustomTalk>())
                        pool.UnresolvedRefs.Add($"CustomTalk#{r.RowId} (script text)");
                    else if (r.Is<Quest>())
                        pool.UnresolvedRefs.Add($"Quest#{r.RowId} (deferred)");
                    else
                        pool.UnresolvedRefs.Add($"{r.RowType?.Name ?? "?"}#{r.RowId}");
                }
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"[DialogueProbe] resolve failed for NpcId {npcId}.");
        }

        cache[npcId] = pool;
        return pool;
    }

    private void AddBalloon(NpcDialoguePool pool, uint balloonId)
    {
        if (balloonId == 0) return;
        if (data.GetExcelSheet<Balloon>().GetRowOrDefault(balloonId) is { } row)
            AddLine(pool, "Balloon", row.Dialogue);
    }

    private static void AddLine(NpcDialoguePool pool, string type, ReadOnlySeString s)
    {
        var text = s.ExtractText();
        if (string.IsNullOrWhiteSpace(text) || text == "0") return; // "0" = unused DefaultTalk slot sentinel
        pool.Lines.Add(new DialogueLine(type, text, s.ToMacroString().Contains('<')));
    }
}
