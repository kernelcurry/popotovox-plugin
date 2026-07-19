namespace PopotoVox.Security;

public enum DownloadPhase
{
    Connecting,
    Downloading,
    Verifying,
    Installing,
    Done,
    Failed,
}

/// <summary>Immutable progress snapshot pushed to the UI during a download.</summary>
public sealed record DownloadProgress(
    string AssetId,
    DownloadPhase Phase,
    long Received,
    long Total,
    string? Error = null)
{
    public double Fraction => Total > 0 ? (double)Received / Total : 0;
}
