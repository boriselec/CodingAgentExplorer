using System.Diagnostics;
using System.Text.Json;

namespace CodingAgentExplorer.Services;

// Gates the Claude proxy flow (port 8888): requests are only forwarded upstream when
// Tailscale is running and currently routing traffic through an exit node. Detection is
// based on `tailscale status --json`: BackendState must be "Running" and ExitNodeStatus
// must be present (non-null), which is exactly the state reported while an exit node is in use.
//
// The CLI is invoked at most once per CacheTtl so per-request latency stays negligible under load.
public sealed class TailscaleExitNodeGuard(ILogger<TailscaleExitNodeGuard> logger)
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private DateTimeOffset _cachedAt = DateTimeOffset.MinValue;
    private GuardResult _cached = new(false, "Tailscale exit node not checked yet");

    public readonly record struct GuardResult(bool Allowed, string Reason);

    public async Task<GuardResult> CheckAsync(CancellationToken ct = default)
    {
        if (DateTimeOffset.UtcNow - _cachedAt < CacheTtl)
            return _cached;

        await _lock.WaitAsync(ct);
        try
        {
            // Re-check after acquiring the lock: another request may have just refreshed the cache.
            if (DateTimeOffset.UtcNow - _cachedAt < CacheTtl)
                return _cached;

            // Own fixed timeout: the result is cached and shared across requests,
            // so it must outlive whichever single request triggered the refresh.
            var result = await ProbeAsync();
            _cached = result;
            _cachedAt = DateTimeOffset.UtcNow;
            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<GuardResult> ProbeAsync()
    {
        using var cts = new CancellationTokenSource(ProbeTimeout);
        var ct = cts.Token;

        string json;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "tailscale",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("status");
            psi.ArgumentList.Add("--json");

            using var proc = Process.Start(psi);
            if (proc is null)
                return new(false, "Failed to start the tailscale process");

            try
            {
                // Read both streams concurrently so a full stderr pipe can't stall the stdout read.
                var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
                var stderrTask = proc.StandardError.ReadToEndAsync(ct);
                json = await stdoutTask;
                var stderr = await stderrTask;
                await proc.WaitForExitAsync(ct);

                if (proc.ExitCode != 0)
                    return new(false, $"tailscale status exited {proc.ExitCode}: {stderr.Trim()}");
            }
            finally
            {
                // Disposing the Process doesn't stop it, so kill a CLI that's still running or it outlives the probe.
                if (!proc.HasExited)
                    proc.Kill(entireProcessTree: true);
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("'tailscale status --json' timed out after {Timeout}s", ProbeTimeout.TotalSeconds);
            return new(false, $"tailscale status timed out after {ProbeTimeout.TotalSeconds:0}s");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to run 'tailscale status --json'");
            return new(false, $"tailscale CLI unavailable: {ex.Message}");
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var backendState = root.TryGetProperty("BackendState", out var bs) ? bs.GetString() : null;
            if (!string.Equals(backendState, "Running", StringComparison.Ordinal))
                return new(false, $"Tailscale is not running (BackendState={backendState ?? "unknown"})");

            if (!root.TryGetProperty("ExitNodeStatus", out var exitNode)
                || exitNode.ValueKind == JsonValueKind.Null)
                return new(false, "No Tailscale exit node is in use");

            var online = exitNode.TryGetProperty("Online", out var o) && o.ValueKind == JsonValueKind.True;
            return new(true, online
                ? "Tailscale exit node active and online"
                : "Tailscale exit node set (currently offline)");
        }
        catch (JsonException ex)
        {
            return new(false, $"Could not parse tailscale status output: {ex.Message}");
        }
    }
}
