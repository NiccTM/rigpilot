using System.Text.Json;
using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Core.Tests;

public sealed class LocalGameScannerTests
{
    [Fact]
    public void ScansSteamAndEpicManifestsWithoutNetworkMetadata()
    {
        using TemporaryDirectory temporary = new();
        string steamRoot = Path.Combine(temporary.Path, "Steam");
        string steamApps = Path.Combine(steamRoot, "steamapps");
        string steamGame = Path.Combine(steamApps, "common", "TestGame");
        Directory.CreateDirectory(steamGame);
        File.WriteAllText(Path.Combine(steamApps, "appmanifest_123.acf"), """
            "AppState"
            {
                "appid" "123"
                "name" "Steam Test Game"
                "installdir" "TestGame"
            }
            """);
        File.WriteAllBytes(Path.Combine(steamGame, "TestGame.exe"), new byte[128]);

        string epicRoot = Path.Combine(temporary.Path, "EpicManifests");
        string epicGame = Path.Combine(temporary.Path, "EpicGame");
        Directory.CreateDirectory(epicRoot);
        Directory.CreateDirectory(epicGame);
        File.WriteAllBytes(Path.Combine(epicGame, "EpicGame.exe"), new byte[64]);
        File.WriteAllText(
            Path.Combine(epicRoot, "test.item"),
            JsonSerializer.Serialize(new
            {
                DisplayName = "Epic Test Game",
                InstallLocation = epicGame,
                LaunchExecutable = "EpicGame.exe",
                AppName = "Epic_Test"
            }));

        GameScanResult result = LocalGameScanner.Scan(
        [
            new GameScanRoot(GameStoreKind.Steam, steamRoot),
            new GameScanRoot(GameStoreKind.Epic, epicRoot)
        ]);

        Assert.Equal(2, result.Games.Count);
        Assert.Contains(result.Games, game => game.Id == "steam.123" && game.LaunchUri == "steam://rungameid/123");
        Assert.Contains(result.Games, game => game.Id == "epic.epic-test" && game.ExecutablePath.EndsWith("EpicGame.exe", StringComparison.Ordinal));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void ScansBattleNetGameDirectoriesByTheirNgdpMarkers()
    {
        using TemporaryDirectory temporary = new();
        string root = Path.Combine(temporary.Path, "BattleNetGames");
        string game = Path.Combine(root, "Test Warcraft");
        string launcher = Path.Combine(root, "Battle.net");
        string plainFolder = Path.Combine(root, "NotAGame");
        Directory.CreateDirectory(game);
        Directory.CreateDirectory(launcher);
        Directory.CreateDirectory(plainFolder);
        File.WriteAllText(Path.Combine(game, ".build.info"), "Branch!STRING:0|Build Key!HEX:16");
        File.WriteAllBytes(Path.Combine(game, "TestWarcraft.exe"), new byte[256]);
        File.WriteAllText(Path.Combine(launcher, ".build.info"), "launcher marker must be skipped");
        File.WriteAllBytes(Path.Combine(plainFolder, "tool.exe"), new byte[64]);

        GameScanResult result = LocalGameScanner.Scan([new GameScanRoot(GameStoreKind.BattleNet, root)]);

        GameEntryV1 entry = Assert.Single(result.Games);
        Assert.StartsWith("battlenet.", entry.Id, StringComparison.Ordinal);
        Assert.Equal("Test Warcraft", entry.Name);
        Assert.EndsWith("TestWarcraft.exe", entry.ExecutablePath, StringComparison.Ordinal);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void WarnsWhenABattleNetGameDirectoryHasNoExecutable()
    {
        using TemporaryDirectory temporary = new();
        string game = Path.Combine(temporary.Path, "Empty Game");
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(game, ".flavor.info"), "Product Flavor!STRING:0");

        GameScanResult result = LocalGameScanner.Scan([new GameScanRoot(GameStoreKind.BattleNet, temporary.Path)]);

        Assert.Empty(result.Games);
        Assert.Contains(result.Warnings, warning => warning.Contains("no local executable", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RejectsManifestExecutablePathEscape()
    {
        using TemporaryDirectory temporary = new();
        string root = Path.Combine(temporary.Path, "Epic");
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, "unsafe.item"),
            JsonSerializer.Serialize(new
            {
                DisplayName = "Unsafe",
                InstallLocation = root,
                LaunchExecutable = "..\\outside.exe",
                AppName = "Unsafe"
            }));

        GameScanResult result = LocalGameScanner.Scan([new GameScanRoot(GameStoreKind.Epic, root)]);

        Assert.Empty(result.Games);
        Assert.Contains(result.Warnings, warning => warning.Contains("escapes", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The Steam half of the containment above, which was missing while the Epic half was
    /// tested. `installdir` comes from an .acf file in a user-writable directory; combined
    /// raw it walked out of the library and indexed executables from anywhere on disk, which
    /// then appear as a launchable entry under a name the same manifest chose.
    /// </summary>
    [Fact]
    public void RejectsSteamInstallDirectoryEscape()
    {
        using TemporaryDirectory temporary = new();
        string steamRoot = Path.Combine(temporary.Path, "Steam");
        string steamApps = Path.Combine(steamRoot, "steamapps");
        Directory.CreateDirectory(steamApps);

        // A real executable outside the library, which the escape would otherwise index.
        string outside = Path.Combine(temporary.Path, "Outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "outside.exe"), "not a real binary");

        File.WriteAllText(Path.Combine(steamApps, "appmanifest_666.acf"), """
            "AppState"
            {
                "appid" "666"
                "name" "Escaping Game"
                "installdir" "..\\..\\..\\Outside"
            }
            """);

        GameScanResult result = LocalGameScanner.Scan([new GameScanRoot(GameStoreKind.Steam, steamRoot)]);

        Assert.DoesNotContain(result.Games, game =>
            game.ExecutablePath.Contains("Outside", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Warnings, warning => warning.Contains("escapes", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The rejection is per-manifest. The only try/catch sits around the whole store scan,
    /// so throwing would have cost the user every other Steam game in the library.
    /// </summary>
    [Fact]
    public void APoisonedSteamManifestDoesNotSuppressTheRestOfTheLibrary()
    {
        using TemporaryDirectory temporary = new();
        string steamRoot = Path.Combine(temporary.Path, "Steam");
        string steamApps = Path.Combine(steamRoot, "steamapps");
        string good = Path.Combine(steamApps, "common", "GoodGame");
        Directory.CreateDirectory(good);
        File.WriteAllText(Path.Combine(good, "GoodGame.exe"), "not a real binary");

        File.WriteAllText(Path.Combine(steamApps, "appmanifest_1.acf"), """
            "AppState"
            {
                "appid" "1"
                "name" "Escaping Game"
                "installdir" "..\\..\\..\\Outside"
            }
            """);
        File.WriteAllText(Path.Combine(steamApps, "appmanifest_2.acf"), """
            "AppState"
            {
                "appid" "2"
                "name" "Good Game"
                "installdir" "GoodGame"
            }
            """);

        GameScanResult result = LocalGameScanner.Scan([new GameScanRoot(GameStoreKind.Steam, steamRoot)]);

        Assert.Contains(result.Games, game => game.Name == "Good Game");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"pchelper-games-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
