using RadeonGuard.Persistence;
using Xunit;

namespace RadeonGuard.Tests;

public sealed class AdrenalinImporterTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    public AdrenalinImporterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "RadeonGuardImportTests_" + Guid.NewGuid().ToString("N"));
        _dbPath = Path.Combine(_tempDir, "radeonguard.db");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task ImportFromGmdb_RecoversFortniteFanCurve_AndFiltersNonGames()
    {
        string gmdbPath = Path.Combine(_tempDir, "gmdb.blb");
        string sampleGmdb = """
        {
          "games": [
            {
              "title": "Python 3.12 (64-bit)",
              "exe_path": "C:\\Python312\\Python.exe",
              "rendering_process": "",
              "profileEnabled": "FALSE"
            },
            {
              "title": "Zoom Workplace",
              "exe_path": "C:\\Zoom\\Zoom.exe",
              "rendering_process": "",
              "profileEnabled": "FALSE"
            },
            {
              "title": "Fortnite",
              "exe_path": "E:\\Fortnite\\FortniteGame\\Binaries\\Win64\\FortniteClient-Win64-Shipping.exe",
              "rendering_process": "FortniteClient-Win64-Shipping.exe",
              "profileEnabled": "FALSE",
              "overdrive_enabled": "TRUE",
              "overdrive": [
                {
                  "settings8": [
                    {
                      "featureId": 22,
                      "featureValue": {
                        "Enabled_": "TRUE",
                        "settings": [
                          { "featureId": 0, "featureValue": { "enabled_": "FALSE", "expectedValue_": 30 } },
                          { "featureId": 1, "featureValue": { "enabled_": "FALSE", "expectedValue_": 15 } },
                          { "featureId": 2, "featureValue": { "enabled_": "FALSE", "expectedValue_": 50 } },
                          { "featureId": 3, "featureValue": { "enabled_": "FALSE", "expectedValue_": 30 } },
                          { "featureId": 4, "featureValue": { "enabled_": "FALSE", "expectedValue_": 63 } },
                          { "featureId": 5, "featureValue": { "enabled_": "FALSE", "expectedValue_": 46 } },
                          { "featureId": 6, "featureValue": { "enabled_": "FALSE", "expectedValue_": 72 } },
                          { "featureId": 7, "featureValue": { "enabled_": "FALSE", "expectedValue_": 48 } },
                          { "featureId": 8, "featureValue": { "enabled_": "FALSE", "expectedValue_": 80 } },
                          { "featureId": 9, "featureValue": { "enabled_": "FALSE", "expectedValue_": 65 } }
                        ]
                      }
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;
        await File.WriteAllTextAsync(gmdbPath, sampleGmdb);

        using var repo = new RadeonGuardRepository(_dbPath);
        await repo.InitializeAsync();
        var importer = new AdrenalinImporter(repo);
        var summary = await importer.ImportFromGmdbAsync(gmdbPath, "RX6800XT");

        Assert.Equal(1, summary.GamesImported);
        Assert.Equal(2, summary.NonGamesFilteredOut);
        Assert.Equal(1, summary.FanCurvesRecovered);

        var games = await repo.GetGameProfilesAsync();
        var fortnite = Assert.Single(games);
        Assert.Equal("Fortnite", fortnite.Title);
        Assert.NotNull(fortnite.OverrideFanProfileId);

        var fan = await repo.GetFanProfileByIdAsync(fortnite.OverrideFanProfileId!);
        Assert.NotNull(fan);
        Assert.Equal(5, fan!.Points.Count);
        Assert.Equal(46, fan.Points[2].FanSpeedPercent);
        Assert.Equal(65, fan.Points[4].FanSpeedPercent);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }
}
