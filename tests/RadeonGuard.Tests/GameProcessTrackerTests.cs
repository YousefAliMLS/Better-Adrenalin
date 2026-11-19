using RadeonGuard.Core.Models;
using RadeonGuard.Guardian;
using Xunit;

namespace RadeonGuard.Tests;

public sealed class GameProcessTrackerTests
{
    private static readonly HashSet<string> Denylist = new(StringComparer.OrdinalIgnoreCase)
    {
        "python.exe", "zoom.exe", "chrome.exe"
    };

    private static readonly GameProfile Fortnite = new()
    {
        GameProfileId = "game-fortnite",
        Title = "Fortnite",
        IsEnabled = true,
        LockWhileProcessRunning = true,
        Executables =
        [
            new GameExecutable
            {
                ExeFullPath = @"E:\Fortnite\FortniteClient-Win64-Shipping.exe",
                ExeFileName = "fortniteclient-win64-shipping.exe",
                Role = ExecutableRole.PrimaryRenderer
            }
        ]
    };

    [Fact]
    public void AltTabToPythonOrZoom_DoesNotDropActiveGameProfile()
    {
        var tracker = new GameProcessTracker(TimeSpan.FromSeconds(3));
        var t0 = DateTimeOffset.UtcNow;

        var running = new List<RunningProcessSnapshot>
        {
            new(100, "FortniteClient-Win64-Shipping.exe", @"E:\Fortnite\FortniteClient-Win64-Shipping.exe"),
            new(200, "python.exe", @"C:\Python312\python.exe"),
            new(300, "zoom.exe", @"C:\Zoom\zoom.exe")
        };

        // Initially Fortnite is foreground
        var match1 = tracker.Evaluate(running, 100, [Fortnite], Denylist, t0);
        Assert.NotNull(match1);
        Assert.Equal("Fortnite", match1!.Game.Title);

        // User Alt-Tabs to Zoom (PID 300) while Fortnite still runs in background
        var match2 = tracker.Evaluate(running, 300, [Fortnite], Denylist, t0.AddSeconds(2));
        Assert.NotNull(match2);
        Assert.Equal("Fortnite", match2!.Game.Title);
    }

    [Fact]
    public void GameExit_WaitsForDebounceBeforeReverting()
    {
        var tracker = new GameProcessTracker(TimeSpan.FromSeconds(3));
        var t0 = DateTimeOffset.UtcNow;

        var withGame = new List<RunningProcessSnapshot>
        {
            new(100, "FortniteClient-Win64-Shipping.exe", null)
        };
        var withoutGame = new List<RunningProcessSnapshot>
        {
            new(200, "python.exe", null)
        };

        Assert.NotNull(tracker.Evaluate(withGame, 100, [Fortnite], Denylist, t0));
        // 1s after exit -> still held by 3s debounce
        Assert.NotNull(tracker.Evaluate(withoutGame, 200, [Fortnite], Denylist, t0.AddSeconds(1)));
        // 4s after exit -> cleanly reverts to Global
        Assert.Null(tracker.Evaluate(withoutGame, 200, [Fortnite], Denylist, t0.AddSeconds(4)));
    }
}
