using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace PopotoVox.Llm;

/// <summary>
/// Talks to the local llama-server over loopback HTTP and returns a validated
/// casting decision. Output is constrained by a JSON schema on the server's
/// <c>/completion</c> endpoint (token sampling is restricted to the schema), and
/// we still validate client-side because schema→grammar conversion can fail open
/// on some builds (PRD §5.5: reject + retry once, then the caller falls back to
/// the rules engine).
/// </summary>
public sealed class LlamaServerClient : ILlmClient
{
    private const int HealthTimeoutSeconds = 180; // first-time model load can be slow on CPU

    private readonly object reconfigureGate = new();
    private LlamaServerProcess server;
    private LlmOptions options;
    private readonly IPluginLog log;
    private readonly HttpClient http;

    public bool Enabled { get; }

    public LlamaServerClient(LlmOptions options, bool enabled, IPluginLog log)
    {
        this.options = options;
        this.log = log;
        Enabled = enabled;
        server = new LlamaServerProcess(options);
        http = new HttpClient { BaseAddress = new Uri(server.BaseUrl), Timeout = TimeSpan.FromMinutes(5) };
    }

    /// <summary>The options the server is currently configured with (compare before <see cref="Reconfigure"/>).</summary>
    public LlmOptions Options => options;

    /// <summary>Swap the server binary/options live — e.g. right after the CUDA runtime installs (or is
    /// removed), so casting moves to the GPU without a plugin reload. Kills the running server; the next
    /// request relaunches it with the new options. The loopback port must be unchanged (the HttpClient's
    /// base address is fixed — port edits still take a plugin reload). A request in flight during the swap
    /// simply fails and falls back to rules, same as any host death.</summary>
    public void Reconfigure(LlmOptions newOptions)
    {
        LlamaServerProcess old;
        lock (reconfigureGate)
        {
            old = server;
            options = newOptions;
            server = new LlamaServerProcess(newOptions);
        }
        old.Dispose();
    }

    /// <summary>True only when the runtime and model are both present on disk.</summary>
    public bool Installed => File.Exists(options.ExePath) && File.Exists(options.ModelPath);

    public async Task<LlmOutput?> CastAsync(CastingRequest request, CancellationToken ct = default)
    {
        if (!Enabled || !Installed) return null; // not set up — caller falls back to rules

        try
        {
            server.StartIfNeeded();
            if (!await WaitForHealthyAsync(ct).ConfigureAwait(false))
            {
                // A model that never loads (bad params/corrupt file) must not be left
                // running and consuming RAM — kill it so we don't orphan a helper.
                log.Warning("[LLM] llama-server did not become healthy in time; stopping it.");
                server.Dispose();
                return null;
            }

            var prompt = CastingPrompt.Build(request);
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                var raw = await CompleteAsync(prompt, request, ct).ConfigureAwait(false);
                var parsed = TryParse(raw);
                if (parsed != null)
                    return Validate(parsed);
                log.Warning($"[LLM] Malformed casting output (attempt {attempt}). Raw: {Truncate(raw)}");
            }
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[LLM] Casting request failed.");
            return null;
        }
    }

    public async Task<string?> CompleteTextAsync(string prompt, int maxTokens, float temperature, CancellationToken ct = default)
    {
        if (!Enabled || !Installed) return null;
        try
        {
            server.StartIfNeeded();
            if (!await WaitForHealthyAsync(ct).ConfigureAwait(false)) return null;

            var body = new JsonObject
            {
                ["prompt"] = prompt,
                ["n_predict"] = maxTokens,
                ["temperature"] = temperature,
                ["top_k"] = 40,
                ["cache_prompt"] = true,
            };
            using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync("/completion", content, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("content", out var c) ? c.GetString() : null;
        }
        catch (OperationCanceledException) { throw; }
        catch (ObjectDisposedException) { return null; } // HttpClient disposed mid-call during unload — expected
        catch (Exception ex)
        {
            log.Warning(ex, "[LLM] CompleteText failed.");
            return null;
        }
    }

    private async Task<bool> WaitForHealthyAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(HealthTimeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var resp = await http.GetAsync("/health", ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode) return true;
            }
            catch (HttpRequestException) { /* not up yet */ }
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
        return false;
    }

    private async Task<string> CompleteAsync(string prompt, CastingRequest request, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["prompt"] = prompt,
            ["n_predict"] = 256, // room for the free-text voice description on top of the other fields
            ["temperature"] = request.Temperature,
            ["seed"] = request.Seed,
            ["top_k"] = 40,
            ["cache_prompt"] = true,
            ["json_schema"] = VoiceSpecSchema.Build(),
        };

        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await http.PostAsync("/completion", content, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
    }

    private static LlmOutput? TryParse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            // Be tolerant of stray prose around the object.
            var start = raw.IndexOf('{');
            var end = raw.LastIndexOf('}');
            if (start < 0 || end <= start) return null;
            var slice = raw.Substring(start, end - start + 1);
            return JsonSerializer.Deserialize<LlmOutput>(slice);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static LlmOutput Validate(LlmOutput o) => new()
    {
        Age = string.IsNullOrWhiteSpace(o.Age) ? "adult" : o.Age.Trim(),
        Timbre = (o.Timbre ?? "").Trim(),
        Accent = (o.Accent ?? "").Trim().ToLowerInvariant(),
        LengthScale = Math.Clamp(o.LengthScale <= 0 ? 1.0f : o.LengthScale, 0.5f, 2.0f),
        Style = string.IsNullOrWhiteSpace(o.Style) ? "neutral" : o.Style.Trim(),
        Description = (o.Description ?? "").Trim(),
    };

    private static string Truncate(string s) => s.Length <= 200 ? s : s[..200];

    public void Dispose()
    {
        http.Dispose();
        server.Dispose();
    }
}
