using System;

namespace PopotoVox.Dialogue;

/// <summary>
/// Thin abstraction over each game-side dialogue surface PopotoVox listens to.
/// All hooks live behind this interface so a game-patch break is a one-file fix
/// (per PRD §5.1 game-update fragility mitigation).
/// </summary>
public interface IDialogueSource : IDisposable
{
    string Name { get; }

    event Action<DialogueEvent>? Captured;

    /// <summary>
    /// Returns true if the underlying hook target is reachable on this game build.
    /// Implementations should be cheap and safe to call before <see cref="Start"/>.
    /// </summary>
    bool SanityCheck(out string error);

    void Start();
    void Stop();
}
