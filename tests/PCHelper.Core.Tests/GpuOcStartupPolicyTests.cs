using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Core.Tests;

/// <summary>
/// Persisting a GPU overclock for startup overrides the default session-only stance, so every
/// guard on the opt-in is pinned: the restart risk must be accepted, the exact device
/// confirmed, and only the documented clock/power controls saved — never anything resembling
/// voltage, and never a value that is not a real number.
/// </summary>
public sealed class GpuOcStartupPolicyTests
{
    private const string DeviceId = "nvidia:gpu-0";

    private static IReadOnlyList<GpuOcStartupOutputV1> ValidOutputs =>
    [
        new("gpuclock.core:0", 150),
        new("gpupower.limit:0", 350_000),
    ];

    [Fact]
    public void AcceptsAFullyConfirmedRiskAcceptedOverclock()
    {
        Assert.Null(GpuOcStartupPolicy.ValidateEnable(DeviceId, ValidOutputs, [DeviceId], confirmRestartRisk: true));
    }

    [Fact]
    public void RefusesWithoutTheRestartRiskAccepted()
    {
        string? error = GpuOcStartupPolicy.ValidateEnable(DeviceId, ValidOutputs, [DeviceId], confirmRestartRisk: false);
        Assert.Contains("restart risk", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RefusesWithoutAnExactDeviceConfirmation()
    {
        string? error = GpuOcStartupPolicy.ValidateEnable(DeviceId, ValidOutputs, ["nvidia:gpu-1"], confirmRestartRisk: true);
        Assert.Contains("exact-device", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RefusesAnEmptyOverclock()
    {
        string? error = GpuOcStartupPolicy.ValidateEnable(DeviceId, [], [DeviceId], confirmRestartRisk: true);
        Assert.Contains("no applied overclock", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("gpu.voltage:0")]
    [InlineData("gpufan.duty:0")]
    [InlineData("cooling.pump:0")]
    public void RefusesAnyCapabilityThatIsNotAClockOrPowerControl(string capabilityId)
    {
        string? error = GpuOcStartupPolicy.ValidateEnable(
            DeviceId, [new GpuOcStartupOutputV1(capabilityId, 10)], [DeviceId], confirmRestartRisk: true);
        Assert.Contains("not one of them", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RefusesANonFiniteValue()
    {
        string? error = GpuOcStartupPolicy.ValidateEnable(
            DeviceId, [new GpuOcStartupOutputV1("gpuclock.core:0", double.NaN)], [DeviceId], confirmRestartRisk: true);
        Assert.Contains("finite", error, StringComparison.OrdinalIgnoreCase);
    }
}
