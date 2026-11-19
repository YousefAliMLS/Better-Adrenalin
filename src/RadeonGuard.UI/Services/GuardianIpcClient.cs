using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using RadeonGuard.Core.Hardware;
using RadeonGuard.Core.IPC;
using RadeonGuard.Core.Models;
using RadeonGuard.Diagnostics;
using RadeonGuard.Persistence;

namespace RadeonGuard.UI.Services;

public sealed class GuardianIpcClient
{
    public async Task<IpcResponse> SendAsync(string command, object? payload = null, CancellationToken ct = default)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(3000);

            using var pipe = new NamedPipeClientStream(".", IpcConstants.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(1200, timeoutCts.Token).ConfigureAwait(false);

            string? payloadJson = payload is null ? null : (payload is string s ? s : JsonSerializer.Serialize(payload));
            var req = new IpcRequest(command, payloadJson);

            var utf8NoBom = new UTF8Encoding(false);
            await using var writer = new StreamWriter(pipe, utf8NoBom, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, utf8NoBom, leaveOpen: true);

            await writer.WriteLineAsync(JsonSerializer.Serialize(req)).ConfigureAwait(false);
            string? line = await reader.ReadLineAsync(timeoutCts.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(line))
            {
                return new IpcResponse(false, "Empty response from RadeonGuard Guardian.");
            }
            return JsonSerializer.Deserialize<IpcResponse>(line) ?? new IpcResponse(false, "Invalid IPC response.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new IpcResponse(false, "RadeonGuard Guardian request timed out.");
        }
        catch (TimeoutException)
        {
            return new IpcResponse(false, "RadeonGuard Guardian is not running or did not respond in time.");
        }
        catch (Exception ex)
        {
            return new IpcResponse(false, ex.Message);
        }
    }

    public async Task<GuardianStatusSnapshot?> GetStatusAsync(CancellationToken ct = default)
    {
        var resp = await SendAsync("GetStatusSnapshot", null, ct).ConfigureAwait(false);
        if (!resp.Success || string.IsNullOrWhiteSpace(resp.PayloadJson)) return null;
        return JsonSerializer.Deserialize<GuardianStatusSnapshot>(resp.PayloadJson);
    }

    public async Task<HardwareApplyResult> ApplyFanProfileAsync(string fanProfileId, CancellationToken ct = default)
    {
        var resp = await SendAsync("ApplyFanProfile", JsonSerializer.Serialize(fanProfileId), ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(resp.PayloadJson))
        {
            var res = JsonSerializer.Deserialize<HardwareApplyResult>(resp.PayloadJson);
            if (res is not null) return res;
        }
        return new HardwareApplyResult(resp.Success, resp.Success ? "APPLIED" : "FAILED", AdlxResultCode.Fail, resp.ErrorMessage, "{}");
    }

    public async Task<HardwareApplyResult> ApplyTuningProfileAsync(string tuningProfileId, bool setAsGlobalDefault, CancellationToken ct = default)
    {
        var resp = await SendAsync("ApplyTuningProfile", new { TuningProfileId = tuningProfileId, SetAsGlobalDefault = setAsGlobalDefault }, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(resp.PayloadJson))
        {
            var res = JsonSerializer.Deserialize<HardwareApplyResult>(resp.PayloadJson);
            if (res is not null) return res;
        }
        return new HardwareApplyResult(resp.Success, resp.Success ? "APPLIED" : "FAILED", AdlxResultCode.Fail, resp.ErrorMessage, "{}");
    }

    public async Task<AdrenalinImportSummary?> ImportFromAdrenalinAsync(CancellationToken ct = default)
    {
        var resp = await SendAsync("ImportFromAdrenalin", null, ct).ConfigureAwait(false);
        if (resp.Success && !string.IsNullOrWhiteSpace(resp.PayloadJson))
        {
            return JsonSerializer.Deserialize<AdrenalinImportSummary>(resp.PayloadJson);
        }
        return null;
    }

    public async Task<int> RescanGamesAsync(CancellationToken ct = default)
    {
        var resp = await SendAsync("RescanGames", null, ct).ConfigureAwait(false);
        if (!resp.Success || string.IsNullOrWhiteSpace(resp.PayloadJson)) return 0;
        using var doc = JsonDocument.Parse(resp.PayloadJson);
        return doc.RootElement.TryGetProperty("AddedCount", out var c) ? c.GetInt32() : 0;
    }

    public async Task<ConflictReport?> RunDiagnosticsAsync(CancellationToken ct = default)
    {
        var resp = await SendAsync("RunDiagnostics", null, ct).ConfigureAwait(false);
        if (resp.Success && !string.IsNullOrWhiteSpace(resp.PayloadJson))
        {
            return JsonSerializer.Deserialize<ConflictReport>(resp.PayloadJson);
        }
        return ConflictScanner.ScanSystem();
    }

    public static string? ResolveGuardianExecutablePath()
    {
        string baseDir = AppContext.BaseDirectory;
        string candidate1 = Path.Combine(baseDir, "RadeonGuard.Guardian.exe");
        string candidate2 = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "RadeonGuard.Guardian", "bin", "Release", "net8.0-windows", "RadeonGuard.Guardian.exe"));
        return File.Exists(candidate1) ? candidate1 : (File.Exists(candidate2) ? candidate2 : null);
    }

    public static bool EnsureGuardianStarted()
    {
        var existing = Process.GetProcessesByName("RadeonGuard.Guardian");
        bool running = existing.Length > 0;
        foreach (var p in existing) p.Dispose();
        if (running) return true;

        string? exePath = ResolveGuardianExecutablePath();
        if (exePath is null) return false;

        Process.Start(new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = Path.GetDirectoryName(exePath)!,
            UseShellExecute = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
        return true;
    }
}
