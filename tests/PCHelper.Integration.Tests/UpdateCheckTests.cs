using System.Net;
using System.Net.Http;
using System.Text;
using PCHelper.App;

namespace PCHelper.Integration.Tests;

/// <summary>
/// Pins the user-process release check: version parsing across the tag shapes
/// GitHub and RigPilot actually use, payload evaluation without a network, the
/// link-host restriction, and the honest failure paths. The check itself never
/// downloads or installs anything — these tests assert the decision logic only.
/// </summary>
public sealed class UpdateCheckTests
{
    [Theory]
    [InlineData("v0.5.1", 0, 5, 1)]
    [InlineData("0.6.0", 0, 6, 0)]
    [InlineData("0.5.0-alpha-20260717-beta25", 0, 5, 0)]
    [InlineData("V1.0.0", 1, 0, 0)]
    public void TryParseVersionAcceptsRealTagShapes(string value, int major, int minor, int build)
    {
        Assert.True(GitHubUpdateCheck.TryParseVersion(value, out Version version));
        Assert.Equal(new Version(major, minor, build), version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("beta25")]
    [InlineData("v")]
    [InlineData(null)]
    public void TryParseVersionRefusesNonNumericTags(string? value)
    {
        Assert.False(GitHubUpdateCheck.TryParseVersion(value, out _));
    }

    [Fact]
    public void EvaluateReportsANewerReleaseAsAvailable()
    {
        UpdateCheckResult result = GitHubUpdateCheck.Evaluate(
            "0.5.0-alpha-20260717-beta25",
            """{"tag_name":"v0.6.0","html_url":"https://github.com/NiccTM/rigpilot/releases/tag/v0.6.0"}""");

        Assert.True(result.Succeeded);
        Assert.True(result.UpdateAvailable);
        Assert.Equal("v0.6.0", result.LatestVersion);
        Assert.Equal("https://github.com/NiccTM/rigpilot/releases/tag/v0.6.0", result.ReleaseUrl);
    }

    [Theory]
    [InlineData("0.5.0-alpha", "v0.5.0")] // same release line
    [InlineData("0.6.0", "v0.5.9")]       // running build is newer
    public void EvaluateReportsUpToDateWhenNothingNewerExists(string current, string tag)
    {
        UpdateCheckResult result = GitHubUpdateCheck.Evaluate(
            current, $$"""{"tag_name":"{{tag}}","html_url":"https://github.com/NiccTM/rigpilot/releases"}""");

        Assert.True(result.Succeeded);
        Assert.False(result.UpdateAvailable);
    }

    [Fact]
    public void EvaluateNeverSurfacesALinkOutsideGitHub()
    {
        UpdateCheckResult result = GitHubUpdateCheck.Evaluate(
            "0.5.0", """{"tag_name":"v9.9.9","html_url":"https://evil.example/download"}""");

        Assert.True(result.UpdateAvailable);
        Assert.Equal(GitHubUpdateCheck.ReleasesPageUri, result.ReleaseUrl);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("""{"tag_name":"weird-tag"}""")]
    [InlineData("""{"html_url":"https://github.com/x"}""")]
    public void EvaluateFailsSafelyOnUnexpectedPayloads(string json)
    {
        UpdateCheckResult result = GitHubUpdateCheck.Evaluate("0.5.0", json);

        Assert.False(result.Succeeded);
        Assert.False(result.UpdateAvailable);
        Assert.Equal(GitHubUpdateCheck.ReleasesPageUri, result.ReleaseUrl);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Fact]
    public void EvaluateFailsSafelyWhenTheRunningVersionIsUnrecognisable()
    {
        UpdateCheckResult result = GitHubUpdateCheck.Evaluate(
            "dev-build", """{"tag_name":"v0.6.0","html_url":"https://github.com/NiccTM/rigpilot/releases"}""");

        Assert.False(result.Succeeded);
        Assert.False(result.UpdateAvailable);
    }

    [Fact]
    public async Task CheckReadsAWellFormedFeed()
    {
        using StubHandler handler = new(
            HttpStatusCode.OK,
            """{"tag_name":"v9.9.9","html_url":"https://github.com/NiccTM/rigpilot/releases/tag/v9.9.9"}""");
        GitHubUpdateCheck check = new(handler);

        UpdateCheckResult result = await check.CheckAsync("0.8.0-beta.1", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(result.UpdateAvailable);
        Assert.Equal("v9.9.9", result.LatestVersion);
    }

    /// <summary>
    /// The response body is remote input. Buffering it whole means whatever the
    /// endpoint sends is what gets allocated in the dashboard process, so the
    /// read is bounded and an oversized body is refused rather than consumed.
    /// </summary>
    [Fact]
    public async Task CheckRefusesAnOversizedFeedInsteadOfBufferingIt()
    {
        using StubHandler handler = new(HttpStatusCode.OK, new string('a', 2 * 1024 * 1024));
        GitHubUpdateCheck check = new(handler);

        UpdateCheckResult result = await check.CheckAsync("0.8.0-beta.1", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(result.UpdateAvailable);
        Assert.Contains("large", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(GitHubUpdateCheck.ReleasesPageUri, result.ReleaseUrl);
    }

    [Fact]
    public async Task CheckReportsAnUnsuccessfulStatusWithoutFailing()
    {
        using StubHandler handler = new(HttpStatusCode.TooManyRequests, "rate limited");
        GitHubUpdateCheck check = new(handler);

        UpdateCheckResult result = await check.CheckAsync("0.8.0-beta.1", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("429", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckDegradesToAnOfflineMessageWhenTheHostIsUnreachable()
    {
        using StubHandler handler = new(new HttpRequestException("No such host is known."));
        GitHubUpdateCheck check = new(handler);

        UpdateCheckResult result = await check.CheckAsync("0.8.0-beta.1", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("offline", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Answers one request from memory; no socket is opened.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        private readonly Exception? _failure;

        public StubHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        public StubHandler(Exception failure)
        {
            _status = HttpStatusCode.OK;
            _body = string.Empty;
            _failure = failure;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (_failure is not null)
            {
                return Task.FromException<HttpResponseMessage>(_failure);
            }

            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            });
        }
    }
}

/// <summary>
/// The first-run tour flag: absent file means the tour shows, a persisted
/// completion suppresses it, and unreadable state fails toward showing the
/// tour again rather than throwing.
/// </summary>
public sealed class OnboardingStateTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"rigpilot-onboarding-{Guid.NewGuid():N}");

    private string StatePath => Path.Combine(_directory, "onboarding-state.json");

    [Fact]
    public void MissingFlagMeansTheTourIsNotCompleted()
    {
        Assert.False(OnboardingState.IsTourCompleted(StatePath));
    }

    [Fact]
    public void MarkingCompletionPersistsAcrossReads()
    {
        OnboardingState.MarkTourCompleted(StatePath);

        Assert.True(OnboardingState.IsTourCompleted(StatePath));
    }

    [Fact]
    public void GarbageStateFailsTowardShowingTheTour()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(StatePath, "{ this is not json");

        Assert.False(OnboardingState.IsTourCompleted(StatePath));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Temp cleanup is best-effort.
        }
    }
}
