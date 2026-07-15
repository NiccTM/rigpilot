using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Core.Tests;

public sealed class OwnershipControlTests
{
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void ConsentIsBoundToExactBinaryIdentityAndPermissions()
    {
        TakeoverProcessIdentity identity = Identity(Hash);
        OwnershipConsentV1 consent = TakeoverConsentValidator.Create(
            identity,
            allowForceTermination: true,
            disableStartup: false,
            DateTimeOffset.UtcNow);

        TakeoverAuthorizationResult normal = TakeoverConsentValidator.Validate(
            identity,
            consent,
            requireForceTermination: true,
            requireStartupDisable: false);
        TakeoverAuthorizationResult startup = TakeoverConsentValidator.Validate(
            identity,
            consent,
            requireForceTermination: true,
            requireStartupDisable: true);
        TakeoverAuthorizationResult changed = TakeoverConsentValidator.Validate(
            identity with { Sha256 = new string('b', 64) },
            consent,
            requireForceTermination: true,
            requireStartupDisable: false);

        Assert.True(normal.Authorized);
        Assert.False(startup.Authorized);
        Assert.Contains(startup.Errors, error => error.Contains("startup", StringComparison.OrdinalIgnoreCase));
        Assert.False(changed.Authorized);
        Assert.Contains(changed.Errors, error => error.Contains("hash changed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LeaseManagerBlocksOverlappingOwnersAndReleasesExpiredLease()
    {
        OwnershipLeaseManager manager = new();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        OwnershipLeaseV1 lease = manager.Acquire("PC Helper", ["gpu.clock", "gpu.fan"], TimeSpan.FromMinutes(1), now);

        InvalidOperationException conflict = Assert.Throws<InvalidOperationException>(() =>
            manager.Acquire("MSI Afterburner", ["gpu.fan"], TimeSpan.FromMinutes(1), now));
        OwnershipLeaseV1 afterExpiry = manager.Acquire(
            "MSI Afterburner",
            ["gpu.fan"],
            TimeSpan.FromMinutes(1),
            now.AddMinutes(2));

        Assert.Contains("PC Helper", conflict.Message, StringComparison.Ordinal);
        Assert.Equal("MSI Afterburner", afterExpiry.Owner);
        Assert.DoesNotContain(manager.GetActive(now.AddMinutes(2)), item => item.Id == lease.Id);
    }

    [Fact]
    public void LeaseManagerRestoresOnlyActivePcHelperLeases()
    {
        OwnershipLeaseManager manager = new();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        OwnershipLeaseV1 active = new(
            OwnershipLeaseV1.CurrentSchemaVersion,
            "lease.persisted",
            "RigPilot",
            ["GpuFan"],
            now.AddMinutes(-1),
            now.AddHours(1),
            OwnershipState.OwnedByPcHelper,
            "Recovered after service restart.");
        OwnershipLeaseV1 expired = active with { Id = "lease.expired", ExpiresAt = now.AddSeconds(-1) };

        manager.Restore(active, now);
        manager.Restore(expired, now);

        Assert.Contains(manager.GetActive(now), item => item.Id == active.Id);
        Assert.DoesNotContain(manager.GetActive(now), item => item.Id == expired.Id);
        Assert.Throws<InvalidOperationException>(() =>
            manager.Acquire("Another owner", ["GpuFan"], TimeSpan.FromMinutes(1), now));
    }

    private static TakeoverProcessIdentity Identity(string hash) => new(
        "MSI Afterburner",
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MSIAfterburner.exe"),
        "MSI Afterburner",
        "MICRO-STAR INTERNATIONAL CO., LTD.",
        "00112233445566778899AABBCCDDEEFF00112233",
        hash,
        "MSIAfterburner",
        ["gpu.clock", "gpu.fan", "gpu.power"]);
}
