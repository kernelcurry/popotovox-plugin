using System;
using System.Linq;
using System.Text.RegularExpressions;
using Dalamud.Plugin.Services;
using PopotoVox.Dialogue;
using PopotoVox.Npc;

namespace PopotoVox.Pipeline;

/// <summary>
/// SPIKE (M10) diagnostics: measures whether an NPC's STATIC dialogue (from <see cref="NpcDialogueProbe"/>)
/// matches what they actually SAY at runtime — to decide if pre-rendering lines into the cache is worth
/// building. OFF by default (toggle via <c>/pvox dialogueprobe</c>); when on, every captured line is
/// classified EXACT / NORMALIZED / MISS against the speaker's resolved pool and tallied. Pure observation —
/// it never changes playback. Throwaway; remove once the question is answered.
/// </summary>
public sealed class DialogueProbe
{
    private readonly NpcDialogueProbe probe;
    private readonly ITargetManager targets;
    private readonly IChatGui chat;
    private readonly IPluginLog log;

    public bool Enabled;

    private int total, exact, normalized, miss, noNpc, poolEmpty, missWithMacroNearby;

    public DialogueProbe(NpcDialogueProbe probe, ITargetManager targets, IChatGui chat, IPluginLog log)
    {
        this.probe = probe;
        this.targets = targets;
        this.chat = chat;
        this.log = log;
    }

    public void Toggle()
    {
        Enabled = !Enabled;
        var msg = Enabled ? "Dialogue probe ON — talk to NPCs, then /pvox dialoguestats." : "Dialogue probe OFF.";
        try { chat.Print($"[PopotoVox] {msg}"); } catch { /* ignore */ }
        log.Information($"[DialogueProbe] {msg}");
    }

    public void Observe(uint? npcId, string speaker, string text, DialogueSourceKind source)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(text)) return;
        total++;
        if (npcId is not { } id) { noNpc++; return; }

        var pool = probe.Resolve(id);
        if (pool.Lines.Count == 0)
        {
            poolEmpty++;
            log.Information($"[DialogueProbe] MISS(no-pool) {source} \"{Clip(text)}\" npc={id} " +
                           $"unresolved=[{string.Join(", ", pool.UnresolvedRefs.Take(4))}]");
            return;
        }

        var capN = Norm(text);
        var capTrim = text.Trim();
        var exactHit = pool.Lines.FirstOrDefault(l => l.Text.Trim() == capTrim);
        if (exactHit != null)
        {
            exact++;
            log.Information($"[DialogueProbe] MATCH(exact,{exactHit.Type}) {source} \"{Clip(text)}\"");
            return;
        }
        var normHit = pool.Lines.FirstOrDefault(l => Norm(l.Text) == capN);
        if (normHit != null)
        {
            normalized++;
            log.Information($"[DialogueProbe] MATCH(norm,{normHit.Type}) {source} \"{Clip(text)}\" ~ \"{Clip(normHit.Text)}\"");
            return;
        }

        miss++;
        var closest = pool.Lines
            .Select(l => (l, score: SharedWords(capN, Norm(l.Text))))
            .OrderByDescending(x => x.score).First();
        if (closest.l.HasMacro) missWithMacroNearby++;
        log.Information($"[DialogueProbe] MISS {source} \"{Clip(text)}\" | closest({closest.l.Type}," +
                       $"macro={closest.l.HasMacro},shared={closest.score}): \"{Clip(closest.l.Text)}\"");
    }

    public void DumpTarget()
    {
        var t = targets.Target;
        if (t == null) { Say("Target an NPC first, then /pvox dialoguedump."); return; }
        var pool = probe.Resolve(t.BaseId);
        log.Information($"[DialogueProbe] === {t.Name.TextValue} (NpcId {t.BaseId}) — {pool.Lines.Count} lines, " +
                       $"{pool.UnresolvedRefs.Count} unresolved refs ===");
        foreach (var grp in pool.Lines.GroupBy(l => l.Type))
        {
            log.Information($"[DialogueProbe]  {grp.Key}: {grp.Count()}");
            foreach (var l in grp) log.Information($"[DialogueProbe]    {(l.HasMacro ? "~" : " ")}\"{Clip(l.Text)}\"");
        }
        foreach (var u in pool.UnresolvedRefs) log.Information($"[DialogueProbe]  unresolved: {u}");
        Say($"{t.Name.TextValue}: {pool.Lines.Count} static lines, {pool.UnresolvedRefs.Count} unresolved — see /xllog.");
    }

    public void DumpStats()
    {
        var matched = exact + normalized;
        var classifiable = total - noNpc - poolEmpty;
        var rate = classifiable > 0 ? 100.0 * matched / classifiable : 0;
        var summary = $"probe: {total} lines | EXACT {exact}, NORMALIZED {normalized}, MISS {miss} " +
                      $"(of {classifiable} with a pool) → {rate:0.#}% matched | no-npc {noNpc}, no-pool {poolEmpty} | " +
                      $"misses-near-macro {missWithMacroNearby}";
        log.Information($"[DialogueProbe] {summary}");
        Say(summary);
    }

    private void Say(string m) { try { chat.Print($"[PopotoVox] {m}"); } catch { /* ignore */ } }

    private static string Clip(string s) => s.Length > 70 ? s[..70] + "…" : s;

    private static string Norm(string s) =>
        Regex.Replace(Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9 ]", " "), "\\s+", " ").Trim();

    private static int SharedWords(string a, string b)
    {
        var bw = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        return a.Split(' ', StringSplitOptions.RemoveEmptyEntries).Count(bw.Contains);
    }
}
