using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using PCHelper.Contracts;

namespace PCHelper.Core;

public static partial class LocalGameScanner
{
    private const long MaximumManifestBytes = 4 * 1024 * 1024;
    private const int MaximumManifestsPerRoot = 10_000;

    public static GameScanResult Scan(IReadOnlyList<GameScanRoot> roots)
    {
        List<GameEntryV1> games = [];
        List<string> warnings = [];
        foreach (GameScanRoot root in roots)
        {
            try
            {
                string path = Path.GetFullPath(root.Path);
                if (!Directory.Exists(path))
                {
                    warnings.Add($"{root.Store} scan root does not exist: {path}");
                    continue;
                }

                switch (root.Store)
                {
                    case GameStoreKind.Steam:
                        ScanSteam(path, games, warnings);
                        break;
                    case GameStoreKind.Epic:
                        ScanEpic(path, games, warnings);
                        break;
                    case GameStoreKind.Gog:
                        ScanGog(path, games, warnings);
                        break;
                    case GameStoreKind.MicrosoftXbox:
                        ScanMicrosoft(path, games, warnings);
                        break;
                    case GameStoreKind.Standalone:
                        ScanStandalone(path, games);
                        break;
                    case GameStoreKind.BattleNet:
                        ScanBattleNet(path, games, warnings);
                        break;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or System.Xml.XmlException or InvalidDataException)
            {
                warnings.Add($"{root.Store} scan failed: {exception.Message}");
            }
        }

        GameEntryV1[] distinct = games
            .Where(game => Path.IsPathFullyQualified(game.ExecutablePath))
            .GroupBy(game => game.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        return new GameScanResult(distinct, warnings.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static void ScanSteam(string root, List<GameEntryV1> games, List<string> warnings)
    {
        string steamApps = Directory.Exists(Path.Combine(root, "steamapps")) ? Path.Combine(root, "steamapps") : root;
        foreach (string manifest in Enumerate(steamApps, "appmanifest_*.acf", SearchOption.TopDirectoryOnly))
        {
            string text = ReadBoundedText(manifest);
            string? appId = VdfValue(text, "appid");
            string? name = VdfValue(text, "name");
            string? installDir = VdfValue(text, "installdir");
            if (appId is null || name is null || installDir is null)
            {
                warnings.Add($"Steam manifest is incomplete: {Path.GetFileName(manifest)}");
                continue;
            }
            string installPath = Path.Combine(steamApps, "common", installDir);
            string? executable = FindGameExecutable(installPath);
            if (executable is null)
            {
                warnings.Add($"Steam game '{name}' has no local executable candidate.");
                continue;
            }
            games.Add(Create($"steam.{appId}", name, executable, $"steam://rungameid/{appId}", FindArtwork(installPath)));
        }
    }

    private static void ScanEpic(string root, List<GameEntryV1> games, List<string> warnings)
    {
        foreach (string manifest in Enumerate(root, "*.item", SearchOption.TopDirectoryOnly))
        {
            using JsonDocument document = JsonDocument.Parse(ReadBoundedText(manifest));
            JsonElement value = document.RootElement;
            string? name = Property(value, "DisplayName");
            string? install = Property(value, "InstallLocation");
            string? launch = Property(value, "LaunchExecutable");
            string? appName = Property(value, "AppName") ?? Path.GetFileNameWithoutExtension(manifest);
            string? executable = install is null || launch is null ? null : SafeCombine(install, launch);
            if (name is null || executable is null || !File.Exists(executable))
            {
                warnings.Add($"Epic manifest is incomplete or not installed: {Path.GetFileName(manifest)}");
                continue;
            }
            games.Add(Create($"epic.{Sanitise(appName)}", name, executable, $"com.epicgames.launcher://apps/{Uri.EscapeDataString(appName)}?action=launch", FindArtwork(install!)));
        }
    }

    private static void ScanGog(string root, List<GameEntryV1> games, List<string> warnings)
    {
        foreach (string manifest in Enumerate(root, "goggame-*.info", SearchOption.AllDirectories))
        {
            using JsonDocument document = JsonDocument.Parse(ReadBoundedText(manifest));
            JsonElement value = document.RootElement;
            string? name = Property(value, "name");
            string? gameId = Property(value, "gameId") ?? Path.GetFileNameWithoutExtension(manifest);
            string? executable = null;
            if (value.TryGetProperty("playTasks", out JsonElement tasks) && tasks.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement task in tasks.EnumerateArray())
                {
                    string? relative = Property(task, "path");
                    if (relative is not null)
                    {
                        string candidate = SafeCombine(Path.GetDirectoryName(manifest)!, relative);
                        if (File.Exists(candidate))
                        {
                            executable = candidate;
                            break;
                        }
                    }
                }
            }
            if (name is null || executable is null)
            {
                warnings.Add($"GOG manifest is incomplete or not installed: {Path.GetFileName(manifest)}");
                continue;
            }
            games.Add(Create($"gog.{Sanitise(gameId)}", name, executable, null, FindArtwork(Path.GetDirectoryName(manifest)!)));
        }
    }

    private static void ScanMicrosoft(string root, List<GameEntryV1> games, List<string> warnings)
    {
        foreach (string manifest in Enumerate(root, "MicrosoftGame.config", SearchOption.AllDirectories))
        {
            XDocument document = XDocument.Load(manifest, LoadOptions.None);
            XElement? game = document.Root;
            string name = game?.Attribute("Name")?.Value
                ?? Path.GetFileName(Path.GetDirectoryName(manifest))
                ?? "Xbox game";
            string? executableValue = game?.Descendants().Attributes("Executable").Select(attribute => attribute.Value).FirstOrDefault();
            if (executableValue is null)
            {
                warnings.Add($"Xbox manifest has no desktop executable: {manifest}");
                continue;
            }
            string executable = SafeCombine(Path.GetDirectoryName(manifest)!, executableValue);
            if (File.Exists(executable))
            {
                games.Add(Create($"xbox.{StableId(executable)}", name, executable, null, FindArtwork(Path.GetDirectoryName(manifest)!)));
            }
        }
    }

    /// <summary>
    /// Battle.net installs have no per-game JSON/XML manifest; each installed
    /// game directory carries an NGDP marker file (`.build.info` and/or
    /// `.flavor.info`). The walk is access-safe (a denied subdirectory is
    /// skipped, not fatal), depth- and count-bounded, and skips the launcher's
    /// own directory.
    /// </summary>
    private static void ScanBattleNet(string root, List<GameEntryV1> games, List<string> warnings)
    {
        foreach (string directory in EnumerateDirectoriesSafely(root, maximumDepth: 3, maximumDirectories: 5_000))
        {
            if (Path.GetFileName(directory).Equals("Battle.net", StringComparison.OrdinalIgnoreCase)
                || (!File.Exists(Path.Combine(directory, ".build.info")) && !File.Exists(Path.Combine(directory, ".flavor.info"))))
            {
                continue;
            }

            string name = Path.GetFileName(directory);
            string? executable = FindGameExecutable(directory);
            if (executable is null)
            {
                warnings.Add($"Battle.net game '{name}' has no local executable candidate.");
                continue;
            }
            games.Add(Create($"battlenet.{StableId(directory)}", name, executable, null, FindArtwork(directory)));
        }
    }

    /// <summary>Breadth-first directory walk that treats an inaccessible subdirectory as skippable rather than fatal.</summary>
    private static IEnumerable<string> EnumerateDirectoriesSafely(string root, int maximumDepth, int maximumDirectories)
    {
        Queue<(string Path, int Depth)> pending = new([(root, 0)]);
        int visited = 0;
        while (pending.Count > 0 && visited < maximumDirectories)
        {
            (string current, int depth) = pending.Dequeue();
            visited++;
            yield return current;
            if (depth >= maximumDepth)
            {
                continue;
            }

            string[] children;
            try
            {
                children = Directory.GetDirectories(current);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string child in children)
            {
                pending.Enqueue((child, depth + 1));
            }
        }
    }

    private static void ScanStandalone(string root, List<GameEntryV1> games)
    {
        foreach (string executable in Enumerate(root, "*.exe", SearchOption.TopDirectoryOnly))
        {
            games.Add(Create($"standalone.{StableId(executable)}", Path.GetFileNameWithoutExtension(executable), executable, null, FindArtwork(root)));
        }
    }

    private static IEnumerable<string> Enumerate(string root, string pattern, SearchOption option) =>
        Directory.EnumerateFiles(root, pattern, option).Take(MaximumManifestsPerRoot);

    private static string ReadBoundedText(string path)
    {
        FileInfo file = new(path);
        if (file.Length is < 0 or > MaximumManifestBytes)
        {
            throw new InvalidDataException($"Manifest exceeds {MaximumManifestBytes} bytes: {path}");
        }
        return File.ReadAllText(path);
    }

    private static string? FindGameExecutable(string root)
    {
        if (!Directory.Exists(root))
        {
            return null;
        }
        return Directory.EnumerateFiles(root, "*.exe", SearchOption.AllDirectories)
            .Take(5_000)
            .Where(path => !path.Contains("_CommonRedist", StringComparison.OrdinalIgnoreCase)
                && !path.Contains("redist", StringComparison.OrdinalIgnoreCase)
                && !path.Contains("unins", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(path => new FileInfo(path).Length)
            .FirstOrDefault();
    }

    private static string? FindArtwork(string root)
    {
        if (!Directory.Exists(root))
        {
            return null;
        }
        return Directory.EnumerateFiles(root, "*.*", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(path => Path.GetExtension(path).Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase));
    }

    private static string? VdfValue(string text, string key)
    {
        foreach (Match match in VdfPairRegex().Matches(text))
        {
            if (match.Groups[1].Value.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return match.Groups[2].Value.Replace("\\\\", "\\", StringComparison.Ordinal);
            }
        }
        return null;
    }

    private static string? Property(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string SafeCombine(string root, string relative)
    {
        string rootPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string result = Path.GetFullPath(Path.Combine(rootPath, relative));
        if (!result.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Game manifest executable escapes its installation directory.");
        }
        return result;
    }

    private static GameEntryV1 Create(string id, string name, string executable, string? launchUri, string? artwork) => new(
        GameEntryV1.CurrentSchemaVersion,
        id,
        name,
        Path.GetFullPath(executable),
        launchUri,
        artwork,
        null,
        null,
        null,
        null,
        []);

    private static string StableId(string value) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(value).ToLowerInvariant())))[..16];

    private static string Sanitise(string value) => new(value.ToLowerInvariant()
        .Select(character => char.IsLetterOrDigit(character) ? character : '-')
        .ToArray());

    [GeneratedRegex("\\\"([^\\\"]+)\\\"\\s+\\\"([^\\\"]*)\\\"", RegexOptions.CultureInvariant)]
    private static partial Regex VdfPairRegex();
}
