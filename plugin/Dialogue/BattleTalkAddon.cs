using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace PopotoVox.Dialogue;

/// <summary>
/// Minimal view over the "_BattleTalk" addon.
///
/// ClientStructs ships a definition for the main "Talk" window but not for
/// "_BattleTalk", so we map only the two nodes we need. The two text slots sit
/// at the same offsets the Talk window uses for speaker/body. Hard offsets are
/// inherently patch-fragile (PRD §5.1) — keeping this view tiny and isolated
/// means a patch shift is a one-line fix here, behind the IDialogueSource layer.
/// </summary>
[StructLayout(LayoutKind.Explicit)]
internal unsafe struct BattleTalkAddon
{
    [FieldOffset(0x000)] public AtkUnitBase AtkUnitBase;
    [FieldOffset(0x238)] public AtkTextNode* Speaker;
    [FieldOffset(0x240)] public AtkTextNode* Text;
}
