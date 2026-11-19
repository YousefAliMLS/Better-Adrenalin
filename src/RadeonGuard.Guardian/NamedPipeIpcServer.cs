using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using RadeonGuard.Core.IPC;
using RadeonGuard.Core.Models;
using RadeonGuard.Diagnostics;
using RadeonGuard.Persistence;
using Serilog;

namespace RadeonGuard.Guardian;

public sealed class NamedPipeIpcServer : IDisposable
{
    private readonly ProfileOrchestrator _orchestrator;
    private readonly RadeonGuardRepository _repo;
    private readonly BackupManager _backups;
    private readonly ILogger _log;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();
    private Task? _listenLoopTask;

    public NamedPipeIpcServer(
        ProfileOrchestrator orchestrator,
        RadeonGuardRepository repo,
        BackupManager backups,
        ILogger log,
        string? pipeName = null)
    {
        _orchestrator = orchestrator;
        _repo = repo;
        _backups = backups;
        _log = log;
        _pipeName = pipeName ?? IpcConstants.PipeName;
    }

    public void Start()
    {
        _listenLoopTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var pipeSecurity = BuildAcl();
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = NamedPipeServerStreamAcl.Create(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    4096,
                    4096,
                    pipeSecurity);

                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                var connectedPipe = server;
                server = null; // Ownership transferred before queueing Task.Run
                _ = Task.Run(() => HandleClientAsync(connectedPipe, ct), ct);
            }
            catch (OperationCanceledException)
            {
                server?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                server?.Dispose();
                _log.Error(ex, "Error in IPC Named Pipe accept loop.");
                await Task.Delay(250, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        var utf8NoBom = new UTF8Encoding(false);
        await using (pipe)
        {
            try
            {
                using var reader = new StreamReader(pipe, utf8NoBom, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, utf8NoBom, leaveOpen: true) { AutoFlush = true };

                string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(line)) return;

                IpcResponse response;
                try
                {
                    var req = JsonSerializer.Deserialize<IpcRequest>(line);
                    if (req is null)
                    {
                        response = new IpcResponse(false, "Malformed IPC request.");
                    }
                    else
                    {
                        response = await DispatchAsync(req, ct).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "IPC dispatch failed.");
                    response = new IpcResponse(false, ex.Message);
                }

                await writer.WriteLineAsync(JsonSerializer.Serialize(response)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Error(ex, "Unhandled IPC client handler error.");
            }
        }
    }

    private async Task<IpcResponse> DispatchAsync(IpcRequest req, CancellationToken ct)
    {
        switch (req.Command)
        {
            case "GetStatusSnapshot":
                var snap = _orchestrator.BuildStatusSnapshot();
                return new IpcResponse(true, null, JsonSerializer.Serialize(snap));

            case "ApplyFanProfile":
                if (string.IsNullOrWhiteSpace(req.PayloadJson)) return new IpcResponse(false, "Missing FanProfileId.");
                var fanRes = await _orchestrator.ApplyUserFanProfileAsync(req.PayloadJson.Trim('"'), ct).ConfigureAwait(false);
                return new IpcResponse(fanRes.Success, fanRes.ErrorMessage, JsonSerializer.Serialize(fanRes));

            case "ApplyTuningProfile":
                if (string.IsNullOrWhiteSpace(req.PayloadJson)) return new IpcResponse(false, "Missing payload.");
                var applyArgs = JsonSerializer.Deserialize<ApplyTuningArgs>(req.PayloadJson);
                if (applyArgs is null) return new IpcResponse(false, "Invalid payload.");
                var tRes = await _orchestrator.ApplyUserTuningProfileAsync(applyArgs.TuningProfileId, applyArgs.SetAsGlobalDefault, ct).ConfigureAwait(false);
                return new IpcResponse(tRes.Success, tRes.ErrorMessage, JsonSerializer.Serialize(tRes));

            case "RescanGames":
                var status = _orchestrator.BuildStatusSnapshot();
                int count = await GameLauncherScanner.ScanAndRegisterInstalledGamesAsync(_repo, status.GpuInfo?.HardwareKey ?? "", ct).ConfigureAwait(false);
                return new IpcResponse(true, null, JsonSerializer.Serialize(new { AddedCount = count }));

            case "ImportFromAdrenalin":
                var importer = new AdrenalinImporter(_repo);
                var s = _orchestrator.BuildStatusSnapshot();
                var summary = await importer.ImportFromGmdbAsync(gpuHardwareKey: s.GpuInfo?.HardwareKey ?? "", ct: ct).ConfigureAwait(false);
                return new IpcResponse(true, null, JsonSerializer.Serialize(summary));

            case "SetConflictPolicy":
                if (Enum.TryParse<ConflictResolutionPolicy>(req.PayloadJson?.Trim('"'), out var pol))
                {
                    await _orchestrator.SetConflictPolicyAsync(pol, ct).ConfigureAwait(false);
                    return new IpcResponse(true);
                }
                return new IpcResponse(false, "Invalid ConflictResolutionPolicy.");

            case "CreateBackup":
                var bk = await _backups.CreateBackupAsync(req.PayloadJson?.Trim('"') ?? "manual", ct).ConfigureAwait(false);
                return new IpcResponse(true, null, JsonSerializer.Serialize(bk));

            case "RunDiagnostics":
                var report = ConflictScanner.ScanSystem();
                return new IpcResponse(true, null, JsonSerializer.Serialize(report));

            case "RefreshActiveGame":
                await _orchestrator.RefreshActiveGameAsync(ct).ConfigureAwait(false);
                return new IpcResponse(true);

            default:
                return new IpcResponse(false, $"Unknown command '{req.Command}'.");
        }
    }

    private static PipeSecurity BuildAcl()
    {
        var ps = new PipeSecurity();
        var currentUser = WindowsIdentity.GetCurrent().User;
        if (currentUser is not null)
        {
            ps.AddAccessRule(new PipeAccessRule(currentUser, PipeAccessRights.FullControl, AccessControlType.Allow));
        }
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        ps.AddAccessRule(new PipeAccessRule(admins, PipeAccessRights.FullControl, AccessControlType.Allow));
        return ps;
    }

    private sealed record ApplyTuningArgs(string TuningProfileId, bool SetAsGlobalDefault);

    public void Dispose()
    {
        _cts.Cancel();
        try { _listenLoopTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
    }
}
