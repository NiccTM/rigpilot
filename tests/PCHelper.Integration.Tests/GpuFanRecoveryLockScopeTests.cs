using PCHelper.Adapters;
using PCHelper.Contracts;
using PCHelper.Service;

namespace PCHelper.Integration.Tests;

/// <summary>
/// The startup/shutdown recovery path restores every leased hardware control, not
/// just cooling — a live cold-start reproduced it locking every GPU/fan capability
/// service-wide even though the only control it could not prove default state for
/// was the GPU fan (always duty-percent bounded, never clock/power/voltage). These
/// tests pin that the global write lock is scoped away only when every failed
/// verification belongs to the GPU fan adapter, and still applies the moment any
/// other adapter is involved.
/// </summary>
public sealed class GpuFanRecoveryLockScopeTests
{
    [Fact]
    public void AllVerifiedMeansNoLockScopingNeeded()
    {
        HardwareRecoveryResult recovery = new(
            AllDefaultsVerified: true,
            Verifications: [Verification(NvidiaGpuFanAdapter.AdapterId, true)],
            Errors: []);

        Assert.False(PCHelperRuntime.OnlyGpuFanRecoveryFailed(recovery));
    }

    [Fact]
    public void AFanOnlyFailureIsScoped()
    {
        HardwareRecoveryResult recovery = new(
            AllDefaultsVerified: false,
            Verifications:
            [
                Verification(NvidiaGpuFanAdapter.AdapterId, false),
                Verification("nvidia.gpupower", true),
            ],
            Errors: ["gpufan.duty:0: NVAPI_INVALID_USER_PRIVILEGE"]);

        Assert.True(PCHelperRuntime.OnlyGpuFanRecoveryFailed(recovery));
    }

    [Fact]
    public void AFailureOnAnyOtherAdapterIsNotScoped()
    {
        // A clock-offset or power-limit failure must still hard-lock — those
        // domains do not have the fan's structural safety bound.
        HardwareRecoveryResult recovery = new(
            AllDefaultsVerified: false,
            Verifications:
            [
                Verification(NvidiaGpuFanAdapter.AdapterId, false),
                Verification("nvidia.gpuclock.core", false),
            ],
            Errors: ["gpuclock.core:0: NVAPI_INVALID_USER_PRIVILEGE"]);

        Assert.False(PCHelperRuntime.OnlyGpuFanRecoveryFailed(recovery));
    }

    [Fact]
    public void AFailureOnlyOnAnUnrelatedAdapterIsNotScoped()
    {
        HardwareRecoveryResult recovery = new(
            AllDefaultsVerified: false,
            Verifications: [Verification("lhm.control", false)],
            Errors: ["lhm.control:/lpc/nct6798d/0/control/0: adapter unavailable"]);

        Assert.False(PCHelperRuntime.OnlyGpuFanRecoveryFailed(recovery));
    }

    private static HardwareStateVerification Verification(string adapterId, bool success) =>
        new(adapterId, "capability", success, null, success ? "Verified." : "Not verified.");
}
