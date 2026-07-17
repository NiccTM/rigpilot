using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace PCHelper.App;

/// <summary>Outcome of one release check. Never carries a download or installer action.</summary>
public sealed record UpdateCheckResult(
    bool Succeeded,
    bool UpdateAvailable,
    string LatestVersion,
    string ReleaseUrl,
    string Message);

/// <summary>
/// Read-only "newer release exists" check against the public GitHub releases
/// API for this repository. It runs only in the user process (the Windows
/// service performs no network access, per the safety rules), sends no
/// telemetry or identity beyond the HTTP request itself, and never downloads,
/// stages, or installs anything — the result is a message and a link the user
/// can choose to open. Failures (offline, rate-limited, unexpected payload)
/// degrade to an explanatory message, never an error dialog.
/// </summary>
public sealed class GitHubUpdateCheck(HttpMessageHandler? handler = null)
{
    public const string ReleasesApiUri = "https://api.github.com/repos/NiccTM/rigpilot/releases/latest";
    public const string ReleasesPageUri = "https://github.com/NiccTM/rigpilot/releases";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(6);
    private readonly HttpMessageHandler? _handler = handler;

    /// <summary>
    /// Extracts a comparable numeric version from a release tag or product
    /// version string: an optional leading 'v'/'V' is stripped and the leading
    /// dotted-numeric run is parsed, so "v0.5.1", "0.5.0-alpha-20260717-beta25",
    /// and "0.6.0" all yield versions. Returns false for anything without a
    /// leading numeric component rather than guessing.
    /// </summary>
    public static bool TryParseVersion(string? value, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string trimmed = value.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
        {
            trimmed = trimmed[1..];
        }

        int end = 0;
        while (end < trimmed.Length && (char.IsAsciiDigit(trimmed[end]) || trimmed[end] == '.'))
        {
            end++;
        }

        string numeric = trimmed[..end].Trim('.');
        if (numeric.Length == 0 || !numeric.Contains('.'))
        {
            return false;
        }

        return Version.TryParse(numeric, out Version? parsed) && (version = parsed) is not null;
    }

    /// <summary>
    /// Pure evaluation of the GitHub "latest release" JSON against the running
    /// version. Separated from the HTTP call so tests can exercise every
    /// payload shape without a network.
    /// </summary>
    public static UpdateCheckResult Evaluate(string currentVersion, string latestReleaseJson)
    {
        string tag;
        string url;
        try
        {
            using JsonDocument document = JsonDocument.Parse(latestReleaseJson);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Failure("The release feed returned an unexpected payload.");
            }

            tag = root.TryGetProperty("tag_name", out JsonElement tagElement) && tagElement.ValueKind == JsonValueKind.String
                ? tagElement.GetString() ?? string.Empty
                : string.Empty;
            url = root.TryGetProperty("html_url", out JsonElement urlElement) && urlElement.ValueKind == JsonValueKind.String
                ? urlElement.GetString() ?? ReleasesPageUri
                : ReleasesPageUri;
        }
        catch (JsonException)
        {
            return Failure("The release feed could not be parsed.");
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsedUrl)
            || parsedUrl.Scheme != Uri.UriSchemeHttps
            || !parsedUrl.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            url = ReleasesPageUri; // never surface a link outside the project's release page
        }

        if (!TryParseVersion(tag, out Version latest))
        {
            return Failure($"The latest release tag '{tag}' is not a recognisable version.");
        }

        if (!TryParseVersion(currentVersion, out Version current))
        {
            return Failure($"The running version '{currentVersion}' is not a recognisable version.");
        }

        return latest > current
            ? new UpdateCheckResult(true, true, tag, url,
                $"A newer release ({tag}) is available on GitHub. Nothing is downloaded automatically.")
            : new UpdateCheckResult(true, false, tag, url,
                $"You are up to date. Latest published release: {tag}.");
    }

    /// <summary>One bounded, anonymous HTTPS request to the public releases API.</summary>
    public async Task<UpdateCheckResult> CheckAsync(string currentVersion, CancellationToken cancellationToken)
    {
        using HttpClient http = _handler is null ? new HttpClient() : new HttpClient(_handler, disposeHandler: false);
        http.Timeout = Timeout;
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RigPilot", currentVersion));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        try
        {
            using HttpResponseMessage response = await http.GetAsync(new Uri(ReleasesApiUri), cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Failure($"The release feed answered {(int)response.StatusCode}; try again later.");
            }

            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return Evaluate(currentVersion, json);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return Failure("The update check could not reach github.com. RigPilot works fully offline; check again when online.");
        }
    }

    private static UpdateCheckResult Failure(string message) =>
        new(false, false, string.Empty, ReleasesPageUri, message);
}
