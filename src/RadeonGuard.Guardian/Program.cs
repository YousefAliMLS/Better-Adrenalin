using Microsoft.Win32;
using RadeonGuard.Core.Hardware;
using RadeonGuard.Diagnostics;
using RadeonGuard.Persistence;
using Serilog;

namespace RadeonGuard.Guardian;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Any(a => string.Equals(a, "--probe", StringComparison.OrdinalIgnoreCase)))
        {
            return await RunReadOnlyProbeAsync().ConfigureAwait(false);
        }

        using var singleInstance = new Mutex(true, @"Local\RadeonGuard.Guardian.Singleton", out bool createdNew);
        if (!createdNew)
        {
            return 0;
        }

        var log = FlightRecorder.CreateLogger();
        Log.Logger = log;

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            using var hardware = new AdlxHardwareAdapter();
            using var repo = new RadeonGuardRepository();
            var backups = new BackupManager();
            var breadcrumbs = new CrashBreadcrumbManager();
            using var orchestrator = new ProfileOrchestrator(hardware, repo, backups, breadcrumbs, log);

            await orchestrator.StartAsync(cts.Token).ConfigureAwait(false);

            using var ipc = new NamedPipeIpcServer(orchestrator, repo, backups, log);
            ipc.Start();

            SystemEvents.PowerModeChanged += (_, e) =>
            {
                if (e.Mode == PowerModes.Resume)
                {
                    _ = Task.Run(() => orchestrator.OnSystemResumeAsync(cts.Token));
                }
            };

            log.Information("RadeonGuard.Guardian started and listening on pipe \\\\.\\pipe\\RadeonGuard.IPC.");

            int tick = 0;
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    bool runGameCheck = (tick % 2) == 0; // Every ~1s
                    await orchestrator.PollTelemetryTickAsync(runGameCheck, cts.Token).ConfigureAwait(false);

                    if ((tick % 6) == 0) // Every ~3s verify hardware state & guard against external drift
                    {
                        await orchestrator.VerifyAndGuardHardwareTickAsync(cts.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    log.Error(ex, "Guardian loop iteration error.");
                }

                tick++;
                await Task.Delay(500, cts.Token).ConfigureAwait(false);
            }

            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            log.Fatal(ex, "RadeonGuard.Guardian terminated unexpectedly.");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync().ConfigureAwait(false);
        }
    }

    private static async Task<int> RunReadOnlyProbeAsync()
    {
        using var hw = new AdlxHardwareAdapter();
        var res = await hw.InitializeAsync().ConfigureAwait(false);
        Console.WriteLine($"ADLX Init: {res} | Version: {hw.AdlxVersionString}");
        if (!hw.IsInitialized) return 2;

        int count = await hw.GetGpuCountAsync().ConfigureAwait(false);
        Console.WriteLine($"GPU Count: {count}");
        if (count <= 0) return 0;

        var info = await hw.GetGpuInfoAsync(0).ConfigureAwait(false);
        if (info is not null)
        {
            Console.WriteLine($"GPU: {info.Name} | Driver: {info.DriverVersion} | Software: {info.AmdSoftwareVersion} | VRAM: {info.TotalVramMb} MB {info.VramType} | HWKey: {info.HardwareKey}");
        }
        var caps = await hw.GetTuningCapabilitiesAsync(0).ConfigureAwait(false);
        if (caps is not null)
        {
            Console.WriteLine($"Caps: ManualGFX={caps.ManualGfxTuningSupported} V2={caps.IsGfxTuningV2} | CoreMin={caps.GfxMinFreqRange.MinValue}..{caps.GfxMinFreqRange.MaxValue} MHz | CoreMax={caps.GfxMaxFreqRange.MinValue}..{caps.GfxMaxFreqRange.MaxValue} MHz | Volt={caps.GfxVoltageRange.MinValue}..{caps.GfxVoltageRange.MaxValue} mV | VRAM={caps.VramMaxFreqRange.MinValue}..{caps.VramMaxFreqRange.MaxValue} MHz | Power={caps.PowerLimitRange.MinValue}..{caps.PowerLimitRange.MaxValue}% | Fan={caps.FanSpeedRange.MinValue}..{caps.FanSpeedRange.MaxValue}% ZeroRPM={caps.ZeroRpmSupported}");
        }
        var m = await hw.GetCurrentMetricsAsync(0).ConfigureAwait(false);
        if (m is not null)
        {
            Console.WriteLine($"Metrics: Edge={m.EdgeTemperatureC:F1}C | Hotspot={m.HotspotTemperatureC:F1}C | Usage={m.GpuUsagePercent:F1}% | Clk={m.GpuClockMHz} MHz | VRAMClk={m.VramClockMHz} MHz | Power={m.TotalBoardPowerW:F1} W | Fan={m.FanSpeedRpm} RPM | Volt={m.VoltageMv} mV");
        }
        var fan = await hw.GetFanTuningStateAsync(0, false).ConfigureAwait(false);
        if (fan is not null)
        {
            Console.WriteLine($"Live Fan: ZeroRPM={fan.ZeroRpmEnabled} | Points={string.Join(", ", fan.Points.Select(p => $"{p.TemperatureC}C->{p.FanSpeedPercent}%"))}");
        }
        var manual = await hw.GetManualTuningStateAsync(0, false).ConfigureAwait(false);
        if (manual is not null)
        {
            Console.WriteLine($"Live Manual: AtFactory={manual.AtFactory} | MinClk={manual.GfxMinFreqMHz} MHz | MaxClk={manual.GfxMaxFreqMHz} MHz | Volt={manual.GfxVoltageMv} mV | VRAM={manual.VramMaxFreqMHz} MHz | Power={manual.PowerLimitPercent}%");
        }
        var threeD = await hw.Get3DSettingsStateAsync(0).ConfigureAwait(false);
        if (threeD is not null)
        {
            Console.WriteLine($"Live 3D: AntiLag={threeD.AntiLagEnabled} | Chill={threeD.ChillEnabled} | Boost={threeD.BoostEnabled} | RIS={threeD.ImageSharpeningEnabled} | EnhancedSync={threeD.EnhancedSyncEnabled} | FRTC={threeD.FrtcEnabled}");
        }
        return 0;
    }
}
