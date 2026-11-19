using RadeonGuard.Core.Models;

namespace RadeonGuard.Guardian;

public sealed record RunningProcessSnapshot(
    int Pid,
    string FileName,
    string? FullPath,
    bool HasWindow = true,
    DateTimeOffset? StartTimeUtc = null);

public sealed record ActiveGameMatch(
    GameProfile Game,
    int MatchedPid,
    string MatchedExeName);

/// <summary>
/// Deterministic, Alt-Tab-immune game session tracker.
/// Strictly ignores denylisted apps (Python, Zoom, Office, browsers), ignores zombie windowless
/// processes left running after exit (such as hung EasyAntiCheat/UE5 teardown processes),
/// tracks PrimaryRenderer lifetime, and applies a 3-second exit debounce before reverting to Global Default.
/// </summary>
public sealed class GameProcessTracker
{
    private readonly TimeSpan _exitDebounce;
    private readonly TimeSpan _launchWindowGrace;
    private readonly TimeSpan _windowCloseGrace;
    private readonly HashSet<int> _pidsEverSeenWithWindow = new();
    private readonly Dictionary<int, DateTimeOffset> _firstSeenWindowlessUtc = new();
    private readonly Dictionary<int, DateTimeOffset> _windowLostSinceUtc = new();
    private ActiveGameMatch? _lockedGame;
    private DateTimeOffset? _lastSeenAliveUtc;

    public GameProcessTracker(
        TimeSpan? exitDebounce = null,
        TimeSpan? launchWindowGrace = null,
        TimeSpan? windowCloseGrace = null)
    {
        _exitDebounce = exitDebounce ?? TimeSpan.FromSeconds(3);
        _launchWindowGrace = launchWindowGrace ?? TimeSpan.FromSeconds(25);
        _windowCloseGrace = windowCloseGrace ?? TimeSpan.FromSeconds(4);
    }

    public ActiveGameMatch? CurrentMatch => _lockedGame;

    public ActiveGameMatch? Evaluate(
        IReadOnlyList<RunningProcessSnapshot> runningProcesses,
        int? foregroundPid,
        IReadOnlyList<GameProfile> gameProfiles,
        ISet<string> denylist,
        DateTimeOffset nowUtc)
    {
        PruneClosedPids(runningProcesses);

        // Build index of enabled games' PrimaryRenderer executables (strictly excluding denylisted names)
        var candidates = new List<(GameProfile Game, RunningProcessSnapshot Proc, bool IsForeground)>();

        foreach (var proc in runningProcesses)
        {
            string exeLower = proc.FileName.ToLowerInvariant();
            if (denylist.Contains(exeLower)) continue;
            if (!IsLiveWindowedOrLaunchingGame(proc, nowUtc)) continue;

            foreach (var game in gameProfiles)
            {
                if (!game.IsEnabled) continue;

                bool matchesPrimary = game.Executables.Any(e =>
                    e.Role == ExecutableRole.PrimaryRenderer &&
                    MatchesExecutable(e, proc));

                if (matchesPrimary)
                {
                    candidates.Add((game, proc, foregroundPid.HasValue && proc.Pid == foregroundPid.Value));
                }
            }
        }

        if (candidates.Count > 0)
        {
            // Prefer foreground game if multiple games are open, else keep already-locked game if still alive
            var fgMatch = candidates.FirstOrDefault(c => c.IsForeground);
            if (fgMatch.Game is not null)
            {
                _lockedGame = new ActiveGameMatch(fgMatch.Game, fgMatch.Proc.Pid, fgMatch.Proc.FileName);
                _lastSeenAliveUtc = nowUtc;
                return _lockedGame;
            }

            if (_lockedGame is not null)
            {
                var stillRunning = candidates.FirstOrDefault(c =>
                    c.Game.GameProfileId == _lockedGame.Game.GameProfileId &&
                    c.Proc.Pid == _lockedGame.MatchedPid);

                if (stillRunning.Game is not null && _lockedGame.Game.LockWhileProcessRunning)
                {
                    // Alt-Tab immunity: game renderer is still running in memory even though another window has focus!
                    _lastSeenAliveUtc = nowUtc;
                    return _lockedGame;
                }

                // If same game respawned under a new PID
                var sameGameNewPid = candidates.FirstOrDefault(c => c.Game.GameProfileId == _lockedGame.Game.GameProfileId);
                if (sameGameNewPid.Game is not null)
                {
                    _lockedGame = new ActiveGameMatch(sameGameNewPid.Game, sameGameNewPid.Proc.Pid, sameGameNewPid.Proc.FileName);
                    _lastSeenAliveUtc = nowUtc;
                    return _lockedGame;
                }
            }

            // Pick first running game candidate
            var first = candidates[0];
            _lockedGame = new ActiveGameMatch(first.Game, first.Proc.Pid, first.Proc.FileName);
            _lastSeenAliveUtc = nowUtc;
            return _lockedGame;
        }

        // No primary renderer currently running; apply exit debounce before releasing lock
        if (_lockedGame is not null && _lastSeenAliveUtc.HasValue)
        {
            if ((nowUtc - _lastSeenAliveUtc.Value) < _exitDebounce)
            {
                return _lockedGame;
            }
            _lockedGame = null;
            _lastSeenAliveUtc = null;
        }

        return null;
    }

    private static bool MatchesExecutable(GameExecutable configured, RunningProcessSnapshot proc)
    {
        if (!string.IsNullOrWhiteSpace(configured.ExeFullPath) &&
            !string.IsNullOrWhiteSpace(proc.FullPath) &&
            Path.IsPathRooted(configured.ExeFullPath))
        {
            if (string.Equals(configured.ExeFullPath, proc.FullPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return string.Equals(configured.ExeFileName, proc.FileName, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsLiveWindowedOrLaunchingGame(RunningProcessSnapshot proc, DateTimeOffset nowUtc)
    {
        if (proc.HasWindow)
        {
            _pidsEverSeenWithWindow.Add(proc.Pid);
            _firstSeenWindowlessUtc.Remove(proc.Pid);
            _windowLostSinceUtc.Remove(proc.Pid);
            return true;
        }

        if (_pidsEverSeenWithWindow.Contains(proc.Pid))
        {
            // Game previously had a visible window and closed it (likely hanging EAC/UE teardown process).
            if (!_windowLostSinceUtc.TryGetValue(proc.Pid, out var lostAt))
            {
                lostAt = nowUtc;
                _windowLostSinceUtc[proc.Pid] = lostAt;
            }
            return (nowUtc - lostAt) < _windowCloseGrace;
        }

        // Process has never shown a window yet; allow startup splash grace before treating it as zombie.
        DateTimeOffset firstSeen = proc.StartTimeUtc ??
            (_firstSeenWindowlessUtc.TryGetValue(proc.Pid, out var tracked)
                ? tracked
                : (_firstSeenWindowlessUtc[proc.Pid] = nowUtc));
        return (nowUtc - firstSeen) < _launchWindowGrace;
    }

    private void PruneClosedPids(IReadOnlyList<RunningProcessSnapshot> running)
    {
        if (_pidsEverSeenWithWindow.Count == 0 && _firstSeenWindowlessUtc.Count == 0 && _windowLostSinceUtc.Count == 0)
        {
            return;
        }

        var alive = new HashSet<int>(running.Count);
        foreach (var p in running) alive.Add(p.Pid);

        _pidsEverSeenWithWindow.RemoveWhere(pid => !alive.Contains(pid));
        foreach (var pid in _firstSeenWindowlessUtc.Keys.Where(pid => !alive.Contains(pid)).ToList())
            _firstSeenWindowlessUtc.Remove(pid);
        foreach (var pid in _windowLostSinceUtc.Keys.Where(pid => !alive.Contains(pid)).ToList())
            _windowLostSinceUtc.Remove(pid);
    }
}
