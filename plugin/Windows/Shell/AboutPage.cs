using System.Numerics;
using System.Reflection;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace PopotoVox.Windows;

/// <summary>
/// About: what PopotoVox is, its version/license, and the credits it's obliged to show. The credit
/// list is a curated, always-present acknowledgement of every freely-licensed component — bundled
/// libraries and downloaded models alike (PRD §9, §10.3). Each name links to its official source so
/// you can see exactly what PopotoVox uses (and dive in if you like).
/// </summary>
public sealed class AboutPage : IShellPage
{
    private static readonly Vector4 LinkColor = new(0.45f, 0.72f, 1f, 1f);

    private readonly string version;

    public AboutPage(Plugin plugin)
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        version = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "";
    }

    public ShellSection Section => ShellSection.About;
    public string Label => "About";
    public FontAwesomeIcon Icon => FontAwesomeIcon.InfoCircle;

    public void Draw()
    {
        Ui.Heading("PopotoVox");
        ImGui.TextDisabled(string.IsNullOrEmpty(version)
            ? "Free & open-source · GPL-3.0 · runs entirely offline"
            : $"{version} · Free & open-source · GPL-3.0 · runs entirely offline");
        ImGui.Spacing();

        Ui.Paragraph(
            "Every popoto gets a voice. PopotoVox gives the NPCs you meet their own distinct, spoken " +
            "voices — picked to suit each character and generated live, right on your PC. No account, " +
            "no cloud, no subscriptions: it runs entirely offline and it's yours to keep.");

        ImGui.Spacing(); ImGui.Separator();

        Ui.Heading("Credits & sources");
        Ui.Paragraph(
            "PopotoVox stands on a lot of generous, freely-licensed work. Every model and tool below is " +
            "downloaded from its official GitHub or Hugging Face home (and verified by checksum). " +
            "Click any name to open its source.");
        ImGui.Spacing();

        Ui.SubHeading("Voices");
        Credit("Kokoro", "Apache-2.0", "TTS model by hexgrad; packaged for sherpa-onnx by k2-fsa (53 voices / 9 accents).",
            "https://huggingface.co/hexgrad/Kokoro-82M");
        Credit("sherpa-onnx", "Apache-2.0", "On-device TTS runtime by k2-fsa.",
            "https://github.com/k2-fsa/sherpa-onnx");
        Credit("Piper", "MIT", "Lightweight TTS engine by Rhasspy.",
            "https://github.com/rhasspy/piper");
        Credit("LibriTTS-high voice", "CC BY 4.0", "Piper voice trained on LibriTTS.",
            "https://huggingface.co/rhasspy/piper-voices");
        Credit("espeak-ng", "GPL-3.0", "Phonemizer used by the voice engines — and why PopotoVox is GPL-3.0.",
            "https://github.com/espeak-ng/espeak-ng");

        ImGui.Spacing();
        Ui.SubHeading("Ultra engine — designed voices");
        Credit("VoxCPM2", "Apache-2.0", "Single model that designs a unique voice per NPC (in the speaker's " +
            "native tongue) and clones it to perform every line, with per-line emotion — by OpenBMB.",
            "https://huggingface.co/openbmb/VoxCPM2");

        ImGui.Spacing();
        Ui.SubHeading("Casting AI");
        Credit("llama.cpp", "MIT", "Local LLM runtime by ggml-org / Georgi Gerganov.",
            "https://github.com/ggml-org/llama.cpp");
        Credit("Qwen2.5-1.5B-Instruct", "Apache-2.0", "Casting model by Alibaba Cloud; GGUF quant by bartowski.",
            "https://huggingface.co/bartowski/Qwen2.5-1.5B-Instruct-GGUF");
        Credit("NVIDIA CUDA runtime", "redistributable", "Optional GPU runtime, packaged with llama.cpp.",
            "https://github.com/ggml-org/llama.cpp");

        ImGui.Spacing();
        Ui.SubHeading("Plugin & libraries");
        Credit("Dalamud", "", "The FFXIV plugin platform PopotoVox runs on.",
            "https://github.com/goatcorp/Dalamud");
        Credit("NAudio", "MIT", "Audio playback.",
            "https://github.com/naudio/NAudio");
        Credit("SharpZipLib", "MIT", "Archive extraction.",
            "https://github.com/icsharpcode/SharpZipLib");
        Credit("ONNX Runtime", "MIT", "Neural-network inference inside Piper and Kokoro (sherpa-onnx).",
            "https://github.com/microsoft/onnxruntime");

        ImGui.Spacing(); ImGui.Separator();
        Ui.Paragraph(
            "PopotoVox is free & open-source under the GPL-3.0. Everything above is used under a " +
            "permissive or copyleft license — nothing non-commercial or research-only. Downloads connect " +
            "only to GitHub and Hugging Face, and every file is checksum-verified after download.");
    }

    private static void Credit(string name, string license, string detail, string? url = null)
    {
        if (url != null) Link(name, url);
        else ImGui.TextColored(Ui.Accent, name);
        ImGui.SameLine(0, 8f);
        var text = string.IsNullOrEmpty(license) ? detail : $"({license})  {detail}";
        ImGui.PushStyleColor(ImGuiCol.Text, Ui.Muted);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
        ImGui.Spacing();
    }

    /// <summary>A clickable, underlined-on-hover link that opens <paramref name="url"/> in the browser.</summary>
    private static void Link(string label, string url)
    {
        ImGui.TextColored(LinkColor, label);
        if (ImGui.IsItemHovered())
        {
            var min = ImGui.GetItemRectMin();
            var max = ImGui.GetItemRectMax();
            ImGui.GetWindowDrawList().AddLine(new Vector2(min.X, max.Y), new Vector2(max.X, max.Y),
                ImGui.ColorConvertFloat4ToU32(LinkColor));
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            ImGui.SetTooltip(url);
        }
        if (ImGui.IsItemClicked())
            Dalamud.Utility.Util.OpenLink(url);
    }
}
