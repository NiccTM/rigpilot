using System.Reflection;
using PCHelper.Service;

namespace PCHelper.Integration.Tests;

/// <summary>
/// Locks in the two properties users most consistently ask this category of
/// software for, and which vendor suites most consistently break.
///
/// <para><b>No network from the privileged process.</b> RigPilot's stated
/// contract is that the SYSTEM-level service never talks to the network — no
/// telemetry, no accounts, no silent update channel. That is currently true by
/// construction, and this test is what keeps it true: adding an HttpClient to
/// the service fails the build rather than shipping quietly.</para>
///
/// <para><b>No unbounded native handle growth.</b> The single most-cited
/// technical failure in competing suites is a lighting service leaking handles
/// until kernel resources starve. RigPilot's long-lived privileged types must
/// therefore own their disposables explicitly.</para>
///
/// These are architecture guards: they assert a design boundary, not behaviour,
/// so they cost nothing at runtime and fail loudly at the moment of regression.
/// </summary>
public sealed class PrivilegedServiceBoundaryTests
{
    private static readonly Assembly ServiceAssembly = typeof(PCHelperRuntime).Assembly;

    /// <summary>
    /// Assemblies that would give the privileged service outbound reach.
    /// System.Net.Primitives is excluded deliberately: it carries types like
    /// IPAddress that named-pipe and identity code may legitimately touch
    /// without any socket being opened.
    /// </summary>
    private static readonly string[] ForbiddenNetworkAssemblies =
    [
        "System.Net.Http",
        "System.Net.Sockets",
        "System.Net.Requests",
        "System.Net.WebClient",
        "System.Net.Mail",
        "System.Net.NetworkInformation",
        "System.Net.WebSockets",
        "System.Net.WebSockets.Client",
        "System.Net.Ftp",
    ];

    [Fact]
    public void PrivilegedServiceReferencesNoNetworkingAssembly()
    {
        string[] violations = [.. ServiceAssembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => ForbiddenNetworkAssemblies.Contains(name, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)];

        Assert.True(
            violations.Length == 0,
            $"The privileged service must make no network connections, but it now references: {string.Join(", ", violations)}. "
            + "If an outbound call is genuinely required it belongs in the user-session agent, never in the SYSTEM service.");
    }

    [Fact]
    public void PrivilegedServiceExposesNoPublicNetworkSurface()
    {
        string[] offenders = [.. ServiceAssembly
            .GetTypes()
            .Where(type => type.FullName is string name
                && (name.Contains("HttpClient", StringComparison.Ordinal)
                    || name.Contains("WebClient", StringComparison.Ordinal)
                    || name.Contains("TcpClient", StringComparison.Ordinal)
                    || name.Contains("TcpListener", StringComparison.Ordinal)))
            .Select(type => type.FullName!)];

        Assert.Empty(offenders);
    }

    /// <summary>
    /// Every long-lived privileged type that owns unmanaged or OS-backed
    /// resources must be disposable. This is the structural guard against the
    /// handle-leak failure mode that has starved kernel resources in competing
    /// lighting services.
    /// </summary>
    [Fact]
    public void LongLivedPrivilegedRuntimeTypesAreDisposable()
    {
        Assert.True(
            typeof(IDisposable).IsAssignableFrom(typeof(PCHelperRuntime))
            || typeof(IAsyncDisposable).IsAssignableFrom(typeof(PCHelperRuntime)),
            "PCHelperRuntime owns adapter, store, and host resources for the process lifetime and must release them deterministically.");
    }
}
