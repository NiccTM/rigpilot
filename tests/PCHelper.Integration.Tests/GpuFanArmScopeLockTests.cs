using PCHelper.Contracts;
using PCHelper.Service;

namespace PCHelper.Integration.Tests;

/// <summary>
/// The arm/disarm transaction resets and read-back-verifies each requested GPU
/// control family. A live cold-start reproduced its GPU-fan reset failing with a
/// transient NVAPI refusal, which used to hard-lock every family. These tests pin
/// that a fan-only family failure is scoped (fan duty is always safety-bounded),
/// while any clock/power failure still hard-locks.
/// </summary>
public sealed class GpuFanArmScopeLockTests
{
    private const string GpuFan = "GPU fan";
    private const string GpuPower = "GPU power limit";
    private const string GpuClock = "GPU clock offset";

    [Fact]
    public void AFanOnlyFamilyFailureIsScoped()
    {
        HardwareControlFamilyResult[] results =
        [
            Family(GpuFan, verified: false),
            Family(GpuPower, verified: true),
            Family(GpuClock, verified: true),
        ];

        Assert.True(PCHelperRuntime.OnlyGpuFanFamilyFailed(results));
    }

    [Fact]
    public void APowerOrClockFailureIsNotScoped()
    {
        HardwareControlFamilyResult[] results =
        [
            Family(GpuFan, verified: false),
            Family(GpuPower, verified: false),
        ];

        Assert.False(PCHelperRuntime.OnlyGpuFanFamilyFailed(results));
    }

    [Fact]
    public void AllVerifiedIsNotAScopedFailure()
    {
        HardwareControlFamilyResult[] results = [Family(GpuFan, verified: true)];

        Assert.False(PCHelperRuntime.OnlyGpuFanFamilyFailed(results));
    }

    private static HardwareControlFamilyResult Family(string name, bool verified) =>
        new(name, Available: true, RequestedStateApplied: verified, ReadBackVerified: verified, RolledBack: !verified, verified ? "ok" : "failed");
}
