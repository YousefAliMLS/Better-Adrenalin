using RadeonGuard.Core.Models;

namespace RadeonGuard.Guardian;

public sealed record RunningProcessSnapshot(int Pid, string FileName, string? FullPath);

public sealed record ActiveGameMatch(
    GameProfile Game,
    int MatchedPid,
    string MatchedExeName);

/// <summary>
/// Deterministic, Alt-Tab-immune game session tracker.
/// Strictly ignores denylisted apps (Python, Zoom, Office, browsers), tracks PrimaryRenderer lifetime,
/// and applies a 3-second exit debounce before reverting to Global Default.
/// </summary>
public sealed class GameProcessTracker
{
    private readonly TimeSpan _exitDebounce;
    private ActiveGameMatch? _lockedGame;
    private DateTimeOffset? _lastSeenAliveUtc;

    public GameProcessTracker(TimeSpan? exitDebounce = null)
    {
        _exitDebounce = exitDebounce ?? TimeSpan.FromSeconds(3);
    }

    public ActiveGameMatch? CurrentMatch => _lockedGame;

    public ActiveGameMatch? Evaluate(
        IReadOnlyList<RunningProcessSnapshot> runningProcesses,
        int? foregroundPid,
        IReadOnlyList<GameProfile> gameProfiles,
        ISet<string> denylist,
        DateTimeOffset nowUtc)
    {
        // Build index of enabled games' PrimaryRenderer executables (strictly excluding denylisted names)
        var candidates = new List<(GameProfile Game, RunningProcessSnapshot Proc, bool IsForeground)>();

        foreach (var proc in runningProcesses)
        {
            string exeLower = proc.FileName.ToLowerInvariant();
            if (denylist.Contains(exeLower)) continue;

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
}
