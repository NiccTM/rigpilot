using PCHelper.Contracts;
using PCHelper.Core;
using PCHelper.Service;

namespace PCHelper.Integration.Tests;

public sealed class WindowsDriverUpdateExecutorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"RigPilot.DriverUpdateTests.{Guid.NewGuid():N}");

    [Fact]
    public async Task ExactStagedDriverPackageCanBeValidatedExportedAndInstalled()
    {
        string infPath = CreateStagedPackage();
        FakePlatform platform = new();
        WindowsDriverUpdateExecutor executor = new(
            Path.Combine(_root, "Updates"),
            typeof(WindowsDriverUpdateExecutorTests).Assembly.Location,
            platform,
            new FakeAuthenticodeInspector(),
            _ => true);

        UpdatePlanV1 preliminary = Plan(infPath, "0000000000000000000000000000000000000000000000000000000000000000");
        UpdateValidationContext preflight = await executor.InspectStagedPackageAsync(preliminary, CancellationToken.None);
        UpdatePlanV1 plan = Plan(infPath, preflight.StagedPackageSha256);

        SuiteValidationResult validation = PCHelper.Core.UpdatePlanValidator.Validate(plan, preflight);
        Assert.True(validation.IsValid);
        Assert.True(executor.ProductionExecutionReady);

        await executor.ExportRollbackPackageAsync(plan, CancellationToken.None);
        await executor.ApplyAsync(plan, CancellationToken.None);

        Assert.Equal("oem42.inf", platform.ExportedInf);
        Assert.Equal(infPath, platform.InstalledInf);
        Assert.True(await executor.VerifyInstalledVersionAsync(plan.Candidate, CancellationToken.None));
    }

    [Fact]
    public async Task UnsignedServiceCannotInstallEvenWithValidPackage()
    {
        string infPath = CreateStagedPackage();
        FakePlatform platform = new();
        WindowsDriverUpdateExecutor executor = new(
            Path.Combine(_root, "Updates"),
            typeof(WindowsDriverUpdateExecutorTests).Assembly.Location,
            platform,
            new FakeAuthenticodeInspector(),
            _ => false);
        UpdateValidationContext preflight = await executor.InspectStagedPackageAsync(
            Plan(infPath, "0000000000000000000000000000000000000000000000000000000000000000"),
            CancellationToken.None);
        UpdatePlanV1 plan = Plan(infPath, preflight.StagedPackageSha256);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => executor.ApplyAsync(plan, CancellationToken.None));
        Assert.Null(platform.InstalledInf);
    }

    [Fact]
    public async Task FirmwarePlanIsRejectedByDriverExecutor()
    {
        string infPath = CreateStagedPackage();
        WindowsDriverUpdateExecutor executor = new(
            Path.Combine(_root, "Updates"),
            typeof(WindowsDriverUpdateExecutorTests).Assembly.Location,
            new FakePlatform(),
            new FakeAuthenticodeInspector(),
            _ => true);
        UpdatePlanV1 driverPlan = Plan(infPath, "0000000000000000000000000000000000000000000000000000000000000000");
        UpdatePlanV1 firmwarePlan = driverPlan with
        {
            Candidate = driverPlan.Candidate with
            {
                Kind = UpdateKind.DeviceFirmware,
                RecoveryMethod = "Vendor recovery procedure"
            }
        };

        UpdateValidationContext preflight = await executor.InspectStagedPackageAsync(firmwarePlan, CancellationToken.None);

        Assert.False(PCHelper.Core.UpdatePlanValidator.Validate(firmwarePlan, preflight).IsValid);
        await Assert.ThrowsAsync<NotSupportedException>(() => executor.ApplyAsync(firmwarePlan, CancellationToken.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string CreateStagedPackage()
    {
        string packageDirectory = Path.Combine(_root, "Updates", "Staged", "NvidiaDriver");
        Directory.CreateDirectory(packageDirectory);
        string inf = Path.Combine(packageDirectory, "nvidia.inf");
        File.WriteAllText(inf, """
            [Version]
            Signature="$Windows NT$"
            CatalogFile.ntamd64=nvidia.cat
            """);
        File.WriteAllBytes(Path.Combine(packageDirectory, "nvidia.cat"), [1, 2, 3, 4]);
        return inf;
    }

    private static UpdatePlanV1 Plan(string infPath, string hash)
    {
        UpdateCandidateV1 candidate = new(
            UpdateCandidateV1.CurrentSchemaVersion,
            "update.driver.test",
            UpdateKind.Driver,
            "PCI\\VEN_10DE&DEV_2204",
            "1.0.0",
            "2.0.0",
            new Uri("https://download.nvidia.com/Windows/test"),
            hash,
            "NVIDIA",
            RequiresReboot: false,
            RequiresBitLockerSuspension: false,
            RecoveryMethod: "Windows driver rollback");
        return new UpdatePlanV1(
            UpdatePlanV1.CurrentSchemaVersion,
            "plan.driver.test",
            candidate,
            infPath,
            [],
            ["Restore exported driver package"],
            UserConfirmed: true);
    }

    private sealed class FakeAuthenticodeInspector : IAuthenticodeInspector
    {
        public AuthenticodeInspection Inspect(string filePath) =>
            new(true, "NVIDIA", "Test Authenticode validation succeeded.");
    }

    private sealed class FakePlatform : IWindowsDriverUpdatePlatform
    {
        public string? ExportedInf { get; private set; }
        public string? InstalledInf { get; private set; }

        public Task<DriverUpdateTarget?> GetExactDriverAsync(string deviceInstanceId, CancellationToken cancellationToken) =>
            Task.FromResult<DriverUpdateTarget?>(new(deviceInstanceId, "2.0.0", "oem42.inf"));

        public Task<bool> HasStableExternalPowerAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<bool> HasBitLockerRecoveryKeyAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<DriverProcessResult> ExportDriverAsync(string infName, string destinationDirectory, CancellationToken cancellationToken)
        {
            ExportedInf = infName;
            string exported = Path.Combine(destinationDirectory, "rollback.inf");
            File.WriteAllText(exported, "[Version]");
            return Task.FromResult(new DriverProcessResult(0, "exported", string.Empty));
        }

        public Task<DriverProcessResult> InstallDriverAsync(string infPath, CancellationToken cancellationToken)
        {
            InstalledInf = infPath;
            return Task.FromResult(new DriverProcessResult(0, "installed", string.Empty));
        }
    }
}
