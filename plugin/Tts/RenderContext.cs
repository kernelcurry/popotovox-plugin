namespace PopotoVox.Tts;

/// <summary>
/// Per-line context handed to the engine alongside the text: the directed emotion preset (a short
/// free-text delivery phrase the engine performs) and an optional callback the engine fires when it does a
/// one-time voice "design", so the UI can light the Design stage. Optional/null where not applicable, so the
/// non-designing / non-emotion engines simply ignore it.
/// </summary>
public sealed record RenderContext(string? EmotionPreset = null, System.Action? OnDesigning = null);
