using RadeonGuard.Core.Hardware;
using RadeonGuard.Core.IPC;
using RadeonGuard.Diagnostics;
using RadeonGuard.Guardian;
using RadeonGuard.Persistence;
using Serilog;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Xunit;

namespace RadeonGuard.Tests;

public sealed class IpcRoundTripTests : IDisposable
{
    private readonly string _tempDir;

    public IpcRoundTripTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "RadeonGuardIpcTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task NamedPipeServer_RespondsToGetStatusSnapshot_Quickly()
    {
        string dbPath = Path.Combine(_tempDir, "rg.db");
        string backupsDir = Path.Combine(_tempDir, "backups");
        string crumbPath = Path.Combine(_tempDir, "crumb.json");

        using var mock = new MockGpuHardwareAdapter();
        using var repo = new RadeonGuardRepository(dbPath);
        var backups = new BackupManager(dbPath, backupsDir);
        var crumbs = new CrashBreadcrumbManager(crumbPath);
        var log = new LoggerConfiguration().CreateLogger();

        using var orch = new ProfileOrchestrator(mock, repo, backups, crumbs, log);
        await orch.StartAsync();

        string testPipeName = "RadeonGuard.Test." + Guid.NewGuid().ToString("N");
        using var ipc = new NamedPipeIpcServer(orch, repo, backups, log, testPipeName);
        ipc.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var pipe = new NamedPipeClientStream(".", testPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(2000, cts.Token);

        var utf8NoBom = new UTF8Encoding(false);
        await using var writer = new StreamWriter(pipe, utf8NoBom, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, utf8NoBom, leaveOpen: true);

        await writer.WriteLineAsync(JsonSerializer.Serialize(new IpcRequest("GetStatusSnapshot")));
        string? line = await reader.ReadLineAsync(cts.Token);

        Assert.False(string.IsNullOrWhiteSpace(line));
        var resp = JsonSerializer.Deserialize<IpcResponse>(line!);
        Assert.NotNull(resp);
        Assert.True(resp!.Success);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }
}
