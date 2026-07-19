using System;
using System.IO;
using System.Text;

namespace PopotoVox.Security;

/// <summary>
/// Append-only, human-readable record of every download attempt (PRD §8.1 #7):
/// what was requested, the expected vs. actual hash, and the outcome. The user can
/// read it or attach it to a bug report. Best-effort — a logging failure never
/// blocks or aborts a download.
/// </summary>
public sealed class AuditLog
{
    private readonly string path;
    private readonly object gate = new();

    public AuditLog(string path) => this.path = path;

    public void Write(string assetId, string url, string expectedSha, string? actualSha, string outcome)
    {
        try
        {
            var line = new StringBuilder()
                .Append(DateTime.UtcNow.ToString("o")).Append('\t')
                .Append(assetId).Append('\t')
                .Append(outcome).Append('\t')
                .Append("expected=").Append(expectedSha).Append('\t')
                .Append("actual=").Append(actualSha ?? "-").Append('\t')
                .Append(url)
                .Append(Environment.NewLine)
                .ToString();

            lock (gate)
                File.AppendAllText(path, line);
        }
        catch
        {
            // Auditing is best-effort; never let it break the actual work.
        }
    }
}
