using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace PopotoVox.Npc;

/// <summary>
/// Turns an NPC's raw equipment model integers into human-readable gear — item name
/// plus dye colour — so the casting LLM can read the character's actual glamour and
/// let it inform tone (rags vs. fine robes vs. armour; a mournful black dye vs. a
/// cheery pastel).
///
/// Each ENpcBase equipment value packs <c>primaryId | variant&lt;&lt;16 | stain&lt;&lt;24</c>.
/// Items are matched by <c>(slot, primaryId|variant&lt;&lt;16)</c> against a reverse index
/// built once from the Item sheet; the stain byte resolves against the Stain sheet.
///
/// NOTE: the bit layout is inferred, not yet game-verified — if names/dyes look
/// wrong in the bio card this is the one place to adjust. Resolution is best-effort:
/// anything it can't identify is simply omitted rather than shown as a raw number.
/// </summary>
public sealed class EquipmentNamer
{
    public enum Slot { Head, Body, Hands, Legs, Feet, Ears, Neck, Wrists, Finger }

    private readonly IDataManager data;
    private readonly object gate = new();
    private Dictionary<(Slot, uint), string>? index;
    // Weapon model primary-id → (category name, classified role). The weapon is the single
    // strongest class/personality cue (staff=mage, sword=knight), so it gets its own index.
    private Dictionary<uint, (string category, string role, string? cls)>? weaponIndex;

    public EquipmentNamer(IDataManager data) => this.data = data;

    /// <summary>
    /// Classifies an NPC's main-hand weapon model into (category, role), e.g. ("Conjurer's Arm",
    /// "mage"). The weapon model's primary id is the low 16 bits; matched against the Item sheet.
    /// Null if nothing identifiable. Like the armor decode, the bit layout is inferred — spot-check.
    /// </summary>
    public (string Category, string Role, string? Class)? DescribeWeapon(ulong weaponModel)
    {
        if (weaponModel == 0) return null;
        EnsureIndex();
        var primary = (uint)(weaponModel & 0xFFFF);
        return weaponIndex!.TryGetValue(primary, out var w) ? w : null;
    }

    /// <summary>Maps a weapon's ItemUICategory name (e.g. "Two-handed Conjurer's Arm") to a coarse role.</summary>
    private static string ClassifyRole(string category)
    {
        var c = category.ToLowerInvariant();
        bool Has(params string[] keys) => System.Array.Exists(keys, k => c.Contains(k));
        if (Has("conjurer", "white mage", "thaumaturge", "black mage", "arcanist", "summoner",
                "scholar", "astrologian", "red mage", "blue mage", "sage", "pictomancer", "grimoire", "wand", "staff"))
            return "mage/caster";
        if (Has("gladiator", "paladin", "marauder", "warrior", "dark knight", "gunbreaker",
                "lancer", "dragoon", "samurai", "reaper", "viper", "sword", "axe", "shield"))
            return "warrior/knight";
        if (Has("archer", "bard", "machinist", "dancer", "bow", "gun"))
            return "archer/ranged";
        if (Has("pugilist", "monk", "rogue", "ninja", "fist", "knuckle"))
            return "martial/rogue";
        if (Has("carpenter", "blacksmith", "armorer", "goldsmith", "leatherworker", "weaver",
                "alchemist", "culinarian", "tool"))
            return "artisan/crafter";
        if (Has("miner", "botanist", "fisher"))
            return "laborer/gatherer";
        return "warrior/fighter";
    }

    /// <summary>Returns e.g. "Hempen Tunic (Soot Black)" or null if nothing identifiable.</summary>
    public string? Describe(Slot slot, uint model)
    {
        if (model == 0) return null;

        EnsureIndex();
        var key = model & 0xFFFFFF;          // primaryId | variant<<16
        var stainId = (model >> 24) & 0xFF;  // dye channel

        index!.TryGetValue((slot, key), out var name);
        var dye = ResolveStain(stainId);

        if (name == null && dye == null) return null;
        if (name == null) return $"(dyed {dye})";
        return dye == null ? name : $"{name} ({dye})";
    }

    private void EnsureIndex()
    {
        if (index != null) return;
        lock (gate)
        {
            if (index != null) return;
            var idx = new Dictionary<(Slot, uint), string>();
            var weapons = new Dictionary<uint, (string, string, string?)>();
            try
            {
                foreach (var item in data.GetExcelSheet<Item>())
                {
                    var name = item.Name.ExtractText();
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    if (MapSlot(item.EquipSlotCategory) is { } slot)
                    {
                        var key = (uint)(item.ModelMain & 0xFFFFFF);
                        idx.TryAdd((slot, key), name); // first (usually canonical) item wins
                    }
                    else if ((IsMainHand(item.EquipSlotCategory) || IsOffHand(item.EquipSlotCategory)) &&
                             item.ItemUICategory.ValueNullable is { } cat)
                    {
                        var category = cat.Name.ExtractText();
                        if (string.IsNullOrWhiteSpace(category)) continue;
                        var primary = (uint)(item.ModelMain & 0xFFFF);
                        var cls = item.ClassJobUse.ValueNullable is { } cj ? Capitalize(cj.Name.ExtractText()) : null;
                        weapons.TryAdd(primary, (category, ClassifyRole(category), cls));
                    }
                }
            }
            catch
            {
                // Leave whatever we gathered; resolution just degrades to "unknown".
            }
            weaponIndex = weapons;
            index = idx;
        }
    }

    private static bool IsMainHand(Lumina.Excel.RowRef<EquipSlotCategory> categoryRef) =>
        categoryRef.ValueNullable is { } c && c.MainHand > 0;

    private static bool IsOffHand(Lumina.Excel.RowRef<EquipSlotCategory> categoryRef) =>
        categoryRef.ValueNullable is { } c && c.OffHand > 0;

    private static string? Capitalize(string s) =>
        string.IsNullOrWhiteSpace(s) ? null : char.ToUpperInvariant(s[0]) + s[1..];

    private static Slot? MapSlot(Lumina.Excel.RowRef<EquipSlotCategory> categoryRef)
    {
        if (categoryRef.ValueNullable is not { } c) return null;
        if (c.Head > 0) return Slot.Head;
        if (c.Body > 0) return Slot.Body;
        if (c.Gloves > 0) return Slot.Hands;
        if (c.Legs > 0) return Slot.Legs;
        if (c.Feet > 0) return Slot.Feet;
        if (c.Ears > 0) return Slot.Ears;
        if (c.Neck > 0) return Slot.Neck;
        if (c.Wrists > 0) return Slot.Wrists;
        if (c.FingerR > 0 || c.FingerL > 0) return Slot.Finger;
        return null;
    }

    private string? ResolveStain(uint stainId)
    {
        if (stainId == 0) return null;
        try
        {
            if (data.GetExcelSheet<Stain>().GetRowOrDefault(stainId) is { } s)
            {
                var name = s.Name.ExtractText();
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
        }
        catch { /* ignore */ }
        return null;
    }
}
