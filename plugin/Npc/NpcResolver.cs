using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace PopotoVox.Npc;

/// <summary>
/// Turns a captured line into an identified NPC: finds the NpcId of the speaker
/// from the live world (target/object table), then assembles a static
/// <see cref="NpcRecord"/> from Lumina game data (PRD §5.2 / §7.2).
///
/// Everything here is best-effort and defensive — appearance columns are partly
/// cryptic and not every NPC has clean race/gender, so each field is tagged with a
/// confidence level rather than assumed present.
/// </summary>
public sealed class NpcResolver
{
    private readonly IDataManager data;
    private readonly IObjectTable objects;
    private readonly ITargetManager targets;
    private readonly IClientState clientState;
    private readonly EquipmentNamer equipment;
    private readonly IPluginLog log;

    public NpcResolver(
        IDataManager data, IObjectTable objects, ITargetManager targets,
        IClientState clientState, EquipmentNamer equipment, IPluginLog log)
    {
        this.data = data;
        this.objects = objects;
        this.targets = targets;
        this.clientState = clientState;
        this.equipment = equipment;
        this.log = log;
    }

    /// <summary>
    /// Best-effort NpcId for a speaker. The current/focus target is the most
    /// reliable source when the player is talking to someone; otherwise we scan the
    /// object table for a matching event/battle NPC by name. Must be called on the
    /// framework thread (it reads live game objects).
    /// </summary>
    public uint? TryResolveNpcId(string speakerName)
    {
        if (string.IsNullOrWhiteSpace(speakerName)) return null;

        foreach (var candidate in new[] { targets.Target, targets.FocusTarget })
        {
            if (candidate != null && IsNpc(candidate.ObjectKind) &&
                NameMatches(candidate.Name.TextValue, speakerName))
                return candidate.BaseId;
        }

        foreach (var obj in objects)
        {
            if (IsNpc(obj.ObjectKind) && NameMatches(obj.Name.TextValue, speakerName))
                return obj.BaseId;
        }

        return null;
    }

    /// <summary>
    /// Nearby event/battle NPCs (BaseId + name + distance from the player), nearest first — for background
    /// pre-building of voices. Must be called on the framework thread (it reads live game objects).
    /// </summary>
    public IReadOnlyList<(uint BaseId, string Name, float Distance)> NearbyNpcs(int max)
    {
        var origin = objects[0]?.Position; // FFXIV object-table slot 0 is the local player
        var list = new List<(uint, string, float)>();
        foreach (var obj in objects)
        {
            if (!IsNpc(obj.ObjectKind) || obj.BaseId == 0) continue;
            var name = obj.Name.TextValue;
            if (string.IsNullOrWhiteSpace(name)) continue;
            var dist = origin is { } p ? System.Numerics.Vector3.Distance(p, obj.Position) : 0f;
            list.Add((obj.BaseId, name, dist));
        }
        list.Sort((a, b) => a.Item3.CompareTo(b.Item3));
        return max > 0 && list.Count > max ? list.GetRange(0, max) : list;
    }

    /// <summary>Assembles the static record for an NpcId. Safe to call off the framework thread.</summary>
    public NpcRecord ResolveRecord(uint npcId, string fallbackName)
    {
        var record = new NpcRecord
        {
            NpcId = npcId,
            Name = fallbackName,
            SchemaVersion = NpcRecord.CurrentSchemaVersion,
        };
        // The model-hash seed must be STABLE for the same logical character across different
        // NpcIds (PRD D13 cross-link) — so it deliberately does NOT include npcId, and uses only
        // refit-stable customize fields (no equipment models). See PopulateAppearance.
        var seed = new StringBuilder();

        try
        {
            var resident = data.GetExcelSheet<ENpcResident>().GetRowOrDefault(npcId);
            if (resident is { } r)
            {
                var singular = r.Singular.ExtractText();
                if (!string.IsNullOrWhiteSpace(singular)) record.Name = singular;
                var title = r.Title.ExtractText();
                if (!string.IsNullOrWhiteSpace(title)) record.Title = title;
            }

            var baseRow = data.GetExcelSheet<ENpcBase>().GetRowOrDefault(npcId);
            if (baseRow is { } b)
                PopulateAppearance(record, b, seed);
            else
                record.DataConfidence["appearance"] = "missing";

            AddCurrentZone(record);
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"[Npc] Failed to resolve record for NpcId {npcId}.");
            record.DataConfidence["error"] = ex.Message;
        }

        record.ModelHashSeed = seed.ToString();
        record.DataConfidence.TryAdd("name", record.Name == fallbackName ? "low" : "high");
        return record;
    }

    private void PopulateAppearance(NpcRecord record, ENpcBase b, StringBuilder seed)
    {
        record.Gender = b.Gender switch { 0 => "Male", 1 => "Female", _ => null };
        record.DataConfidence["gender"] = record.Gender != null ? "high" : "missing";

        record.BodyType = b.BodyType == 0 ? null : $"type {b.BodyType}";
        // Body type encodes a coarse age model (convention: 1/2 = adult, 3 = elderly, 4 = child).
        record.ApparentAge = b.BodyType switch { 4 => "child", 3 => "elderly", 1 or 2 => "adult", _ => null };
        record.DataConfidence["bodyType"] = record.ApparentAge != null ? "low" : "missing";

        // Model scale → build, but only when it deviates enough from the racial default to matter.
        record.Stature = DescribeStature(b.Scale);

        var feminine = record.Gender == "Female";
        record.Race = ResolveRaceName(b.Race.RowId, feminine, isRace: true);
        record.DataConfidence["race"] = record.Race != null ? "high" : "missing";
        record.Tribe = ResolveRaceName(b.Tribe.RowId, feminine, isRace: false);
        record.DataConfidence["tribe"] = record.Tribe != null ? "high" : "missing";

        // Many NPCs leave the inline armour slots at 0 and carry their look on a shared
        // NpcEquip template instead — fall back to it so those NPCs aren't read as naked.
        uint head = b.ModelHead, body = b.ModelBody, hands = b.ModelHands, legs = b.ModelLegs, feet = b.ModelFeet;
        uint ears = b.ModelEars, neck = b.ModelNeck, wrists = b.ModelWrists, ringR = b.ModelRightRing, ringL = b.ModelLeftRing;
        ulong mainHand = b.ModelMainHand, offHand = b.ModelOffHand;
        if (head == 0 && body == 0 && hands == 0 && legs == 0 && feet == 0 && mainHand == 0 &&
            b.NpcEquip.ValueNullable is { } eq)
        {
            head = eq.ModelHead; body = eq.ModelBody; hands = eq.ModelHands;
            legs = eq.ModelLegs; feet = eq.ModelFeet; mainHand = eq.ModelMainHand;
            ears = eq.ModelEars; neck = eq.ModelNeck; wrists = eq.ModelWrists;
            ringR = eq.ModelRightRing; ringL = eq.ModelLeftRing; offHand = eq.ModelOffHand;
        }

        // Resolve equipment to readable gear + dye colour — a rich signal for the LLM's
        // tone judgement (rags vs. robes vs. armour; mournful black vs. cheery pastel), and the
        // full worn list for the character card.
        AddEquip(record, "Head", EquipmentNamer.Slot.Head, head);
        AddEquip(record, "Body", EquipmentNamer.Slot.Body, body);
        AddEquip(record, "Hands", EquipmentNamer.Slot.Hands, hands);
        AddEquip(record, "Legs", EquipmentNamer.Slot.Legs, legs);
        AddEquip(record, "Feet", EquipmentNamer.Slot.Feet, feet);
        AddEquip(record, "Ears", EquipmentNamer.Slot.Ears, ears);
        AddEquip(record, "Neck", EquipmentNamer.Slot.Neck, neck);
        AddEquip(record, "Wrists", EquipmentNamer.Slot.Wrists, wrists);
        AddEquip(record, "Right ring", EquipmentNamer.Slot.Finger, ringR);
        AddEquip(record, "Left ring", EquipmentNamer.Slot.Finger, ringL);

        // The main-hand weapon is the strongest class/personality cue (staff = mage, sword = knight),
        // and names the NPC's class when the item maps to one.
        if (mainHand != 0)
        {
            string? mainName = null;
            if (equipment.DescribeWeapon(mainHand) is { } w)
            {
                mainName = w.Category;
                record.Weapon = w.Category;
                record.Role = w.Role;
                record.Job = w.Class;
                record.DataConfidence["weapon"] = "low";
            }
            record.Gear.Add(new GearSlot { Slot = "Main hand", Item = mainName });
        }
        if (offHand != 0)
        {
            string? offName = null;
            if (equipment.DescribeWeapon(offHand) is { } oh)
            {
                offName = oh.Category;
                record.Equipment.Add($"Off-hand: {oh.Category}");
            }
            record.Gear.Add(new GearSlot { Slot = "Off-hand", Item = offName });
        }

        // Seed the model-hash from refit-stable customize fields only (race/tribe/gender/body type +
        // base model). Equipment is intentionally excluded so the same character hashes identically
        // across gear changes; npcId is excluded so it hashes identically across NpcIds — both are
        // what PRD §13's cross-link needs. The "stable subset" lives in IdentityFingerprint.
        seed.Append(IdentityFingerprint.ModelHashSeed(
            b.Race.RowId, b.Tribe.RowId, b.Gender, b.BodyType, b.ModelChara.RowId));
    }

    /// <summary>Model scale → a build label, only when notably off the racial default (~1.0).</summary>
    private static string? DescribeStature(float scale) => scale switch
    {
        <= 0f => null,                 // unset / invalid
        >= 1.25f => "towering",
        >= 1.10f => "large, imposing",
        <= 0.70f => "child-sized",
        <= 0.90f => "small, slight",
        _ => null,                     // average for their race — no extra signal
    };

    private string? ResolveRaceName(uint rowId, bool feminine, bool isRace)
    {
        if (rowId == 0) return null;
        try
        {
            if (isRace)
            {
                var row = data.GetExcelSheet<Race>().GetRowOrDefault(rowId);
                if (row is { } r) return Pick(r.Masculine.ExtractText(), r.Feminine.ExtractText(), feminine);
            }
            else
            {
                var row = data.GetExcelSheet<Tribe>().GetRowOrDefault(rowId);
                if (row is { } t) return Pick(t.Masculine.ExtractText(), t.Feminine.ExtractText(), feminine);
            }
        }
        catch
        {
            // fall through to null
        }
        return null;
    }

    private static string? Pick(string masculine, string feminine, bool useFeminine)
    {
        var chosen = useFeminine && !string.IsNullOrWhiteSpace(feminine) ? feminine : masculine;
        return string.IsNullOrWhiteSpace(chosen) ? null : chosen;
    }

    private void AddEquip(NpcRecord record, string label, EquipmentNamer.Slot slot, uint modelId)
    {
        if (modelId == 0) return;                       // empty slot → absent from Gear (shows "--")
        var described = equipment.Describe(slot, modelId);
        record.Gear.Add(new GearSlot { Slot = label, Item = described }); // Item null = unidentified ("?")
        if (described != null) record.Equipment.Add($"{label}: {described}");
    }

    private void AddCurrentZone(NpcRecord record)
    {
        try
        {
            var territoryId = clientState.TerritoryType;
            if (territoryId == 0) return;
            if (data.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>().GetRowOrDefault(territoryId) is { } t &&
                t.PlaceName.ValueNullable is { } place)
            {
                var name = place.Name.ExtractText();
                if (!string.IsNullOrWhiteSpace(name) && !record.Zones.Contains(name))
                    record.Zones.Add(name);
            }
        }
        catch { /* zone is best-effort context */ }
    }

    private static bool IsNpc(ObjectKind kind) =>
        kind is ObjectKind.EventNpc or ObjectKind.BattleNpc;

    private static bool NameMatches(string a, string b) =>
        !string.IsNullOrWhiteSpace(a) && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}
