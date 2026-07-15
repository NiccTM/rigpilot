using System.Diagnostics;
using System.Management;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Service;

/// <summary>
/// Executes only INF-based Windows driver packages that were explicitly staged
/// inside the RigPilot update root. It is deliberately not a generic vendor
/// installer runner: EXE/MSI packages, firmware, BIOS, and unsigned service
/// images remain outside this executor.
/// </summary>
public sealed class WindowsDriverUpdateExecutor : IUpdatePackageExecutor
{
    private static readonly string[] OfficialVendorHostSuffixes =
    [
        "amd.com",
        "asrock.com",
        "asus.com",
        "gigabyte.com",
        "intel.com",
        "msi.com",
        "nvidia.com"
    ];

    private readonly string _stateRoot;
    private readonly string _stagingRoot;
    private readonly string _rollbackRoot;
    private readonly string _sentinelRoot;
    private readonly string _serviceImagePath;
    private readonly IWindowsDriverUpdatePlatform _platform;
    private readonly IAuthenticodeInspector _authenticode;
    private readonly Func<string, bool> _isProductionSigned;

    public WindowsDriverUpdateExecutor(
        string stateRoot,
        string serviceImagePath,
        IWindowsDriverUpdatePlatform? platform = null,
        IAuthenticodeInspector? authenticode = null,
        Func<string, bool>? isProductionSigned = null)
    {
        _stateRoot = Path.GetFullPath(stateRoot);
        _stagingRoot = Path.Combine(_stateRoot, "Staged");
        _rollbackRoot = Path.Combine(_stateRoot, "Rollback");
        _sentinelRoot = Path.Combine(_stateRoot, "RebootSentinels");
        _serviceImagePath = Path.GetFullPath(serviceImagePath);
        _platform = platform ?? new WindowsDriverUpdatePlatform();
        _authenticode = authenticode ?? new WindowsAuthenticodeInspector();
        _isProductionSigned = isProductionSigned ?? (path => AuthenticodeVerifier.TryVerify(path, out _));
    }

    public bool ProductionExecutionReady => OperatingSystem.IsWindows() && _isProductionSigned(_serviceImagePath);

    public string ExecutionMessage => ProductionExecutionReady
        ? "The installed service image has a valid Authenticode signature. Exact package, device, rollback, power, and confirmation checks still apply."
        : "Driver installation is blocked because the running RigPilot service image is not Authenticode-signed.";

    public async Task<UpdateValidationContext> InspectStagedPackageAsync(
        UpdatePlanV1 plan,
        CancellationToken cancellationToken)
    {
        if (plan.Candidate.Kind != UpdateKind.Driver)
        {
            return InvalidContext(plan, "This executor supports only INF-based driver updates.");
        }

        DriverUpdateTarget? target = await _platform.GetExactDriverAsync(plan.Candidate.DeviceId, cancellationToken).ConfigureAwait(false);
        if (target is null)
        {
            return InvalidContext(plan, "The exact target PnP device and its installed driver were not found.");
        }

        DriverPackageInspection package;
        try
        {
            package = await InspectPackageAsync(plan.StagedPackagePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return InvalidContext(plan, exception.Message, target.DeviceInstanceId);
        }

        bool stablePower = await _platform.HasStableExternalPowerAsync(cancellationToken).ConfigureAwait(false);
        bool bitLockerRecovery = !plan.Candidate.RequiresBitLockerSuspension
            || await _platform.HasBitLockerRecoveryKeyAsync(cancellationToken).ConfigureAwait(false);
        bool allowedHost = IsOfficialVendorHost(plan.Candidate.DownloadUri.IdnHost);
        return new UpdateValidationContext(
            allowedHost
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { plan.Candidate.DownloadUri.IdnHost }
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { target.DeviceInstanceId },
            package.PackageSha256,
            package.Publisher,
            package.SignatureValid,
            stablePower,
            bitLockerRecovery,
            DeveloperBuild: !_isProductionSigned(_serviceImagePath));
    }

    public async Task ExportRollbackPackageAsync(UpdatePlanV1 plan, CancellationToken cancellationToken)
    {
        DriverUpdateTarget target = await RequireExactTargetAsync(plan, cancellationToken).ConfigureAwait(false);
        if (!IsExportableOemInf(target.InfName))
        {
            throw new InvalidOperationException("The current driver has no exportable OEM INF, so automatic rollback is unavailable.");
        }

        string rollbackDirectory = GetRollbackDirectory(plan.Id);
        if (Directory.Exists(rollbackDirectory))
        {
            throw new InvalidOperationException("A rollback package already exists for this update plan. Create a new plan before retrying.");
        }
        Directory.CreateDirectory(rollbackDirectory);
        try
        {
            DriverProcessResult export = await _platform.ExportDriverAsync(target.InfName, rollbackDirectory, cancellationToken).ConfigureAwait(false);
            if (export.ExitCode != 0)
            {
                throw new InvalidOperationException($"PnPUtil failed to export the current driver (exit {export.ExitCode}): {export.Diagnostic}");
            }
            string[] infs = Directory.EnumerateFiles(rollbackDirectory, "*.inf", SearchOption.AllDirectories).ToArray();
            if (infs.Length != 1)
            {
                throw new InvalidDataException("The rollback export must contain exactly one INF package.");
            }
            DriverRollbackManifest manifest = new(
                plan.Id,
                target.DeviceInstanceId,
                target.DriverVersion,
                target.InfName,
                infs[0],
                DateTimeOffset.UtcNow);
            await WriteJsonAtomicAsync(Path.Combine(rollbackDirectory, "rollback.json"), manifest, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryDeleteDirectory(rollbackDirectory);
            throw;
        }
    }

    public async Task ApplyAsync(UpdatePlanV1 plan, CancellationToken cancellationToken)
    {
        if (plan.Candidate.Kind != UpdateKind.Driver)
        {
            throw new NotSupportedException("RigPilot has no generic firmware or BIOS writer. Use only an exact-model vendor updater, ESRT capsule, or documented UEFI workflow.");
        }
        if (!_isProductionSigned(_serviceImagePath))
        {
            throw new UnauthorizedAccessException("Driver installation requires an Authenticode-signed installed RigPilot service image.");
        }
        if (!plan.UserConfirmed)
        {
            throw new UnauthorizedAccessException("Driver installation requires an explicit confirmed update plan.");
        }

        UpdateValidationContext context = await InspectStagedPackageAsync(plan, cancellationToken).ConfigureAwait(false);
        SuiteValidationResult validation = UpdatePlanValidator.Validate(plan, context);
        if (!validation.IsValid)
        {
            throw new InvalidDataException(string.Join(" ", validation.Errors));
        }
        if (!File.Exists(Path.Combine(GetRollbackDirectory(plan.Id), "rollback.json")))
        {
            throw new InvalidOperationException("The current driver was not exported for rollback. Installation was not started.");
        }

        DriverPackageInspection package = await InspectPackageAsync(plan.StagedPackagePath, cancellationToken).ConfigureAwait(false);
        DriverProcessResult install = await _platform.InstallDriverAsync(package.InfPath, cancellationToken).ConfigureAwait(false);
        if (install.ExitCode is not 0 and not 3010 and not 1641)
        {
            throw new InvalidOperationException($"PnPUtil failed to install the exact driver package (exit {install.ExitCode}): {install.Diagnostic}");
        }
    }

    public async Task<bool> VerifyInstalledVersionAsync(UpdateCandidateV1 candidate, CancellationToken cancellationToken)
    {
        DriverUpdateTarget? target = await _platform.GetExactDriverAsync(candidate.DeviceId, cancellationToken).ConfigureAwait(false);
        return target is not null
            && string.Equals(target.DriverVersion, candidate.TargetVersion, StringComparison.OrdinalIgnoreCase);
    }

    public async Task RollbackAsync(UpdatePlanV1 plan, CancellationToken cancellationToken)
    {
        string manifestPath = Path.Combine(GetRollbackDirectory(plan.Id), "rollback.json");
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException("No rollback manifest exists for this update plan.", manifestPath);
        }
        DriverRollbackManifest manifest = await ReadJsonAsync<DriverRollbackManifest>(manifestPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(manifest.DeviceInstanceId, plan.Candidate.DeviceId, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(manifest.InfPath)
            || !IsPathWithin(manifest.InfPath, GetRollbackDirectory(plan.Id)))
        {
            throw new InvalidDataException("The rollback manifest does not match the exact update target.");
        }

        DriverProcessResult restore = await _platform.InstallDriverAsync(manifest.InfPath, cancellationToken).ConfigureAwait(false);
        if (restore.ExitCode is not 0 and not 3010 and not 1641)
        {
            throw new InvalidOperationException($"PnPUtil failed to restore the exported driver (exit {restore.ExitCode}): {restore.Diagnostic}");
        }
    }

    public async Task WriteRebootSentinelAsync(UpdateTransactionV1 transaction, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_sentinelRoot);
        string sentinelPath = Path.Combine(_sentinelRoot, $"{SanitisePlanId(transaction.Id)}.json");
        await WriteJsonAtomicAsync(sentinelPath, transaction, cancellationToken).ConfigureAwait(false);
    }

    public Task ClearRebootSentinelAsync(string transactionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string sentinelPath = Path.Combine(_sentinelRoot, $"{SanitisePlanId(transactionId)}.json");
        if (File.Exists(sentinelPath))
        {
            File.Delete(sentinelPath);
        }
        return Task.CompletedTask;
    }

    private async Task<DriverUpdateTarget> RequireExactTargetAsync(UpdatePlanV1 plan, CancellationToken cancellationToken)
    {
        if (plan.Candidate.Kind != UpdateKind.Driver)
        {
            throw new NotSupportedException("Only an INF-based driver update can use the Windows driver executor.");
        }
        DriverUpdateTarget? target = await _platform.GetExactDriverAsync(plan.Candidate.DeviceId, cancellationToken).ConfigureAwait(false);
        if (target is null)
        {
            throw new InvalidOperationException("The exact target PnP device and its current driver could not be found.");
        }
        return target;
    }

    private async Task<DriverPackageInspection> InspectPackageAsync(string stagedInfPath, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows driver packages can be inspected only on Windows.");
        }
        if (string.IsNullOrWhiteSpace(stagedInfPath)
            || !Path.IsPathFullyQualified(stagedInfPath)
            || !Path.GetExtension(stagedInfPath).Equals(".inf", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The staged driver package must identify one absolute INF path.");
        }

        string infPath = Path.GetFullPath(stagedInfPath);
        if (!File.Exists(infPath) || !IsPathWithin(infPath, _stagingRoot))
        {
            throw new UnauthorizedAccessException("The driver INF must be staged below the RigPilot update root.");
        }
        string packageRoot = Path.GetDirectoryName(infPath) ?? throw new InvalidDataException("The driver INF has no package directory.");
        RejectReparsePoints(packageRoot, _stagingRoot);
        string catalogPath = ResolveCatalogPath(infPath);
        AuthenticodeInspection signature = _authenticode.Inspect(catalogPath);
        string packageHash = await ComputeDirectoryHashAsync(packageRoot, cancellationToken).ConfigureAwait(false);
        return new DriverPackageInspection(infPath, packageRoot, catalogPath, packageHash, signature.Publisher, signature.Valid, signature.Message);
    }

    private static UpdateValidationContext InvalidContext(UpdatePlanV1 plan, string message, string? exactDeviceId = null) => new(
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        string.IsNullOrWhiteSpace(exactDeviceId)
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase) { exactDeviceId },
        string.Empty,
        message,
        PackageSignatureValid: false,
        StablePower: false,
        BitLockerRecoveryKeyAvailable: false,
        DeveloperBuild: true);

    private static bool IsOfficialVendorHost(string host) => !string.IsNullOrWhiteSpace(host)
        && OfficialVendorHostSuffixes.Any(suffix => string.Equals(host, suffix, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith($".{suffix}", StringComparison.OrdinalIgnoreCase));

    private static bool IsExportableOemInf(string value) => value.Length > 0
        && value.StartsWith("oem", StringComparison.OrdinalIgnoreCase)
        && value.EndsWith(".inf", StringComparison.OrdinalIgnoreCase)
        && value[3..^4].All(char.IsDigit);

    private string GetRollbackDirectory(string planId) => Path.Combine(_rollbackRoot, SanitisePlanId(planId));

    private static string SanitisePlanId(string planId)
    {
        if (string.IsNullOrWhiteSpace(planId))
        {
            throw new InvalidDataException("Update plan identity is required.");
        }
        string normalised = new(planId.Where(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-').ToArray());
        if (normalised.Length is 0 or > 128 || !string.Equals(normalised, planId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Update plan identity contains unsupported characters.");
        }
        return normalised;
    }

    private static string ResolveCatalogPath(string infPath)
    {
        string? generic = null;
        List<string> amd64 = [];
        foreach (string rawLine in File.ReadLines(infPath))
        {
            string line = rawLine.Split(';', 2)[0].Trim();
            int equals = line.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }
            string key = line[..equals].Trim();
            if (!key.StartsWith("CatalogFile", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (key.Length > "CatalogFile".Length && key["CatalogFile".Length] != '.')
            {
                continue;
            }
            string candidate = line[(equals + 1)..].Trim();
            if (candidate.Length == 0 || Path.GetFileName(candidate) != candidate)
            {
                throw new InvalidDataException("The driver INF has an unsafe CatalogFile declaration.");
            }
            string suffix = key["CatalogFile".Length..];
            if (suffix.Contains("amd64", StringComparison.OrdinalIgnoreCase))
            {
                amd64.Add(candidate);
            }
            else if (suffix.Length == 0)
            {
                generic ??= candidate;
            }
        }

        string[] preferred = amd64.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        string? catalog = preferred.Length switch
        {
            1 => preferred[0],
            > 1 => throw new InvalidDataException("The driver INF declares more than one x64 catalog file."),
            _ => generic
        };
        if (string.IsNullOrWhiteSpace(catalog))
        {
            throw new InvalidDataException("The driver INF has no x64 or generic CatalogFile declaration.");
        }
        string catalogPath = Path.Combine(Path.GetDirectoryName(infPath)!, catalog);
        if (!File.Exists(catalogPath))
        {
            throw new FileNotFoundException("The catalog declared by the driver INF was not staged with the package.", catalogPath);
        }
        return catalogPath;
    }

    private static void RejectReparsePoints(string packageRoot, string stagingRoot)
    {
        string root = Path.GetFullPath(packageRoot);
        if (!IsPathWithin(root, stagingRoot))
        {
            throw new UnauthorizedAccessException("The driver package lies outside the approved staging root.");
        }
        foreach (string path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Append(root))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Driver packages containing reparse points are rejected.");
            }
        }
    }

    private static bool IsPathWithin(string candidate, string root)
    {
        string normalisedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        string normalisedCandidate = Path.GetFullPath(candidate);
        return normalisedCandidate.StartsWith(normalisedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> ComputeDirectoryHashAsync(string directory, CancellationToken cancellationToken)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        string[] files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (files.Length == 0)
        {
            throw new InvalidDataException("The driver package directory is empty.");
        }
        StringBuilder canonical = new();
        foreach (string file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Driver packages containing reparse-point files are rejected.");
            }
            await using FileStream stream = new(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] fileHash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            string relative = file[root.Length..].Replace(Path.DirectorySeparatorChar, '/').ToLowerInvariant();
            canonical.Append(relative).Append(':').Append(Convert.ToHexString(fileHash).ToLowerInvariant()).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
    }

    private static async Task WriteJsonAtomicAsync<T>(string path, T payload, CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(path) ?? throw new InvalidDataException("State path has no directory.");
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, payload, cancellationToken: cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static async Task<T> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        T? payload = await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return payload ?? throw new InvalidDataException($"The update state file '{path}' is empty or malformed.");
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // The caller receives the primary export error. A failed cleanup is
            // harmless because a plan with a partial rollback directory cannot
            // be reused.
        }
    }
}

public sealed record DriverUpdateTarget(string DeviceInstanceId, string DriverVersion, string InfName);

public sealed record DriverPackageInspection(
    string InfPath,
    string PackageRoot,
    string CatalogPath,
    string PackageSha256,
    string Publisher,
    bool SignatureValid,
    string SignatureMessage);

public sealed record DriverProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public string Diagnostic => string.Join(" ", new[] { StandardError, StandardOutput }
        .Where(value => !string.IsNullOrWhiteSpace(value)))
        .Trim();
}

public sealed record AuthenticodeInspection(bool Valid, string Publisher, string Message);

public interface IAuthenticodeInspector
{
    AuthenticodeInspection Inspect(string filePath);
}

public sealed class WindowsAuthenticodeInspector : IAuthenticodeInspector
{
    public AuthenticodeInspection Inspect(string filePath)
    {
        bool valid = AuthenticodeVerifier.TryVerify(filePath, out string verification);
        bool signerFound = AuthenticodeVerifier.TryGetSigner(filePath, out string publisher, out _);
        return new AuthenticodeInspection(valid && signerFound, publisher, verification);
    }
}

public interface IWindowsDriverUpdatePlatform
{
    Task<DriverUpdateTarget?> GetExactDriverAsync(string deviceInstanceId, CancellationToken cancellationToken);

    Task<bool> HasStableExternalPowerAsync(CancellationToken cancellationToken);

    Task<bool> HasBitLockerRecoveryKeyAsync(CancellationToken cancellationToken);

    Task<DriverProcessResult> ExportDriverAsync(string infName, string destinationDirectory, CancellationToken cancellationToken);

    Task<DriverProcessResult> InstallDriverAsync(string infPath, CancellationToken cancellationToken);
}

public sealed class WindowsDriverUpdatePlatform : IWindowsDriverUpdatePlatform
{
    private static readonly HashSet<ushort> StableBatteryStates = [2, 6, 7, 8, 9];

    public Task<DriverUpdateTarget?> GetExactDriverAsync(string deviceInstanceId, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(deviceInstanceId))
        {
            return Task.FromResult<DriverUpdateTarget?>(null);
        }
        List<DriverUpdateTarget> matches = [];
        using ManagementObjectSearcher searcher = new("SELECT DeviceID, DriverVersion, InfName FROM Win32_PnPSignedDriver");
        using ManagementObjectCollection results = searcher.Get();
        foreach (ManagementObject result in results)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? observedId = result["DeviceID"]?.ToString();
            if (!string.Equals(observedId, deviceInstanceId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            string? version = result["DriverVersion"]?.ToString();
            string? inf = result["InfName"]?.ToString();
            if (!string.IsNullOrWhiteSpace(version) && !string.IsNullOrWhiteSpace(inf))
            {
                matches.Add(new DriverUpdateTarget(observedId!, version, inf));
            }
        }
        return Task.FromResult<DriverUpdateTarget?>(matches.Count == 1 ? matches[0] : null);
    }

    public Task<bool> HasStableExternalPowerAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(false);
        }
        using ManagementObjectSearcher searcher = new("SELECT BatteryStatus FROM Win32_Battery");
        using ManagementObjectCollection batteries = searcher.Get();
        if (batteries.Count == 0)
        {
            return Task.FromResult(true);
        }
        foreach (ManagementObject battery in batteries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (battery["BatteryStatus"] is not ushort status || !StableBatteryStates.Contains(status))
            {
                return Task.FromResult(false);
            }
        }
        return Task.FromResult(true);
    }

    public Task<bool> HasBitLockerRecoveryKeyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // The driver executor never requests BitLocker suspension. Firmware and
        // BIOS workflows require a separate exact-model vendor update path.
        return Task.FromResult(false);
    }

    public Task<DriverProcessResult> ExportDriverAsync(string infName, string destinationDirectory, CancellationToken cancellationToken) =>
        RunPnPUtilAsync(["/export-driver", infName, destinationDirectory], cancellationToken);

    public Task<DriverProcessResult> InstallDriverAsync(string infPath, CancellationToken cancellationToken) =>
        RunPnPUtilAsync(["/add-driver", infPath, "/install"], cancellationToken);

    private static async Task<DriverProcessResult> RunPnPUtilAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        string executable = Path.Combine(Environment.SystemDirectory, "pnputil.exe");
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException("The Windows PnPUtil executable was not found.", executable);
        }
        using Process process = new()
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WindowStyle = ProcessWindowStyle.Hidden
            }
        };
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        if (!process.Start())
        {
            throw new InvalidOperationException("PnPUtil could not be started.");
        }
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            throw;
        }
        return new DriverProcessResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }
}

internal sealed record DriverRollbackManifest(
    string PlanId,
    string DeviceInstanceId,
    string PreviousDriverVersion,
    string PreviousInfName,
    string InfPath,
    DateTimeOffset CreatedAt);

public sealed class SuiteUpdateTransactionJournal(ISuiteStateStore store) : IUpdateTransactionJournal
{
    public Task SaveAsync(UpdateTransactionV1 transaction, CancellationToken cancellationToken) =>
        store.SaveSuiteEntityAsync(SuiteEntityKind.UpdateTransaction, transaction.Id, transaction, cancellationToken);
}
