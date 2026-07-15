using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Win32;
using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Service;

/// <summary>
/// Authenticode is a hard prerequisite for starting a new automatic takeover.
/// The recovery path is deliberately separate: a signed build must be able to
/// give control back after an upgrade even when the replacement build has not
/// yet been signed.
/// </summary>
public sealed class WindowsTakeoverExecutionGate(string serviceImagePath)
{
    private readonly string _serviceImagePath = serviceImagePath;

    public TakeoverExecutionStatusV1 GetStatus()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new TakeoverExecutionStatusV1(false, _serviceImagePath, "Ownership takeover is supported only on Windows.");
        }
        if (string.IsNullOrWhiteSpace(_serviceImagePath) || !File.Exists(_serviceImagePath))
        {
            return new TakeoverExecutionStatusV1(false, _serviceImagePath, "The service image cannot be verified for production takeover.");
        }
        if (!AuthenticodeVerifier.TryVerify(_serviceImagePath, out string message))
        {
            return new TakeoverExecutionStatusV1(false, _serviceImagePath, message);
        }
        return new TakeoverExecutionStatusV1(
            true,
            _serviceImagePath,
            "The service image has a valid Authenticode signature. Exact stored consent and reset verification are still required.");
    }
}

/// <summary>
/// Discovers and manipulates only processes whose loaded image exactly matches
/// a precomputed path/product/publisher/signer/hash identity. It intentionally
/// has no name-only stop operation.
/// </summary>
public sealed class WindowsTakeoverProcessController : ITakeoverProcessController
{
    private readonly Func<string, Process[]> _processLookup;

    public WindowsTakeoverProcessController()
        : this(name => Process.GetProcessesByName(name))
    {
    }

    internal WindowsTakeoverProcessController(Func<string, Process[]> processLookup)
    {
        _processLookup = processLookup;
    }

    public async Task<IReadOnlyList<TakeoverProcessIdentity>> DiscoverAsync(
        IReadOnlyList<ConflictDescriptor> conflicts,
        CancellationToken cancellationToken)
    {
        List<TakeoverProcessIdentity> identities = [];
        foreach (ConflictDescriptor conflict in conflicts.Where(item => item.IsRunning))
        {
            foreach (string processName in conflict.ProcessName.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                foreach (Process process in _processLookup(processName))
                {
                    using (process)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string? path = TryGetExecutablePath(process);
                        if (path is null)
                        {
                            continue;
                        }
                        TakeoverProcessIdentity? identity = await TryReadIdentityAsync(
                            path,
                            conflict.DisplayName,
                            process.ProcessName,
                            conflict.ResourceFamilies,
                            cancellationToken).ConfigureAwait(false);
                        if (identity is not null)
                        {
                            identities.Add(identity);
                        }
                    }
                }
            }
        }

        return identities
            .GroupBy(item => item.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<TakeoverProcessIdentity?> GetCurrentIdentityAsync(string executablePath, CancellationToken cancellationToken)
    {
        string expectedPath;
        try { expectedPath = NormalisePath(executablePath); }
        catch (Exception) { return null; }

        string processName = Path.GetFileNameWithoutExtension(expectedPath);
        foreach (Process process in _processLookup(processName))
        {
            using (process)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? actualPath = TryGetExecutablePath(process);
                if (actualPath is null || !PathsEqual(actualPath, expectedPath))
                {
                    continue;
                }
                return await TryReadIdentityAsync(
                    actualPath,
                    Path.GetFileNameWithoutExtension(actualPath),
                    process.ProcessName,
                    [],
                    cancellationToken).ConfigureAwait(false);
            }
        }
        return null;
    }

    public async Task<bool> RequestGracefulStopAsync(
        TakeoverProcessIdentity identity,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        bool requested = false;
        foreach (Process process in FindExactProcesses(identity, cancellationToken))
        {
            using (process)
            {
                requested |= process.CloseMainWindow();
                if (requested && await WaitForExitAsync(process, timeout, cancellationToken).ConfigureAwait(false))
                {
                    return true;
                }
            }
        }
        return !await IsRunningAsync(identity, cancellationToken).ConfigureAwait(false);
    }

    public async Task ForceStopAsync(TakeoverProcessIdentity identity, CancellationToken cancellationToken)
    {
        Process[] processes = FindExactProcesses(identity, cancellationToken).ToArray();
        if (processes.Length == 0)
        {
            return;
        }
        try
        {
            foreach (Process process in processes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                process.Kill(entireProcessTree: true);
            }
            foreach (Process process in processes)
            {
                await WaitForExitAsync(process, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }
    }

    public Task<bool> IsRunningAsync(TakeoverProcessIdentity identity, CancellationToken cancellationToken)
    {
        bool running = false;
        foreach (Process process in FindExactProcesses(identity, cancellationToken))
        {
            using (process)
            {
                running = true;
            }
        }
        return Task.FromResult(running);
    }

    public static async Task<TakeoverProcessIdentity?> TryReadIdentityAsync(
        string executablePath,
        string displayName,
        string processName,
        IReadOnlyList<string> resourceFamilies,
        CancellationToken cancellationToken)
    {
        string fullPath;
        try { fullPath = NormalisePath(executablePath); }
        catch (Exception) { return null; }
        if (!File.Exists(fullPath))
        {
            return null;
        }

        FileVersionInfo version;
        try { version = FileVersionInfo.GetVersionInfo(fullPath); }
        catch (Exception) { return null; }

        (string publisher, string? thumbprint) = GetSigner(fullPath);
        string hash;
        try { hash = await TakeoverConsentValidator.ComputeSha256Async(fullPath, cancellationToken).ConfigureAwait(false); }
        catch (Exception) { return null; }

        string product = string.IsNullOrWhiteSpace(version.ProductName)
            ? Path.GetFileNameWithoutExtension(fullPath)
            : version.ProductName.Trim();
        return new TakeoverProcessIdentity(
            string.IsNullOrWhiteSpace(displayName) ? product : displayName,
            fullPath,
            product,
            publisher,
            thumbprint,
            hash,
            processName,
            resourceFamilies.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private IEnumerable<Process> FindExactProcesses(TakeoverProcessIdentity expected, CancellationToken cancellationToken)
    {
        string expectedPath = NormalisePath(expected.ExecutablePath);
        foreach (Process process in _processLookup(expected.ProcessName))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? path = TryGetExecutablePath(process);
            if (path is null || !PathsEqual(path, expectedPath))
            {
                process.Dispose();
                continue;
            }

            TakeoverProcessIdentity? current = TryReadIdentityAsync(
                path,
                expected.DisplayName,
                process.ProcessName,
                expected.ResourceFamilies,
                cancellationToken).GetAwaiter().GetResult();
            if (current is null || !SameIdentity(expected, current))
            {
                process.Dispose();
                throw new UnauthorizedAccessException("The takeover target binary identity changed before process control could occur.");
            }
            yield return process;
        }
    }

    private static bool SameIdentity(TakeoverProcessIdentity expected, TakeoverProcessIdentity actual) =>
        PathsEqual(expected.ExecutablePath, actual.ExecutablePath)
        && string.Equals(expected.ProductName, actual.ProductName, StringComparison.Ordinal)
        && string.Equals(expected.Publisher, actual.Publisher, StringComparison.Ordinal)
        && string.Equals(expected.SignerThumbprint ?? string.Empty, actual.SignerThumbprint ?? string.Empty, StringComparison.OrdinalIgnoreCase)
        && string.Equals(expected.Sha256, actual.Sha256, StringComparison.OrdinalIgnoreCase)
        && string.Equals(expected.ProcessName, actual.ProcessName, StringComparison.OrdinalIgnoreCase);

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await process.WaitForExitAsync(cancellationToken).WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            try { return process.HasExited; }
            catch (InvalidOperationException) { return true; }
        }
    }

    private static string? TryGetExecutablePath(Process process)
    {
        try { return process.MainModule?.FileName; }
        catch (Exception) { return null; }
    }

    private static (string Publisher, string? Thumbprint) GetSigner(string path)
    {
        if (AuthenticodeVerifier.TryGetSigner(path, out string publisher, out string? thumbprint))
        {
            return (publisher, thumbprint);
        }
        return ("Unsigned", null);
    }

    private static string NormalisePath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool PathsEqual(string left, string right) => string.Equals(
        NormalisePath(left),
        NormalisePath(right),
        StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Backs up only Run/RunOnce values that resolve to the consented image. The
/// command text is rechecked immediately before deletion and restoration never
/// overwrites a value created by another program in the meantime.
/// </summary>
public sealed class WindowsRegistryStartupController : ITakeoverStartupController
{
    private static readonly string[] StartupSubKeys =
    [
        @"Software\Microsoft\Windows\CurrentVersion\Run",
        @"Software\Microsoft\Windows\CurrentVersion\RunOnce"
    ];

    public Task<IReadOnlyList<StartupEntryBackupV1>> BackupAsync(
        TakeoverProcessIdentity identity,
        CancellationToken cancellationToken)
    {
        List<StartupEntryBackupV1> entries = [];
        foreach (RegistryStartupLocation location in EnumerateLocations())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using RegistryKey? key = OpenLocation(location, writable: false);
                if (key is null)
                {
                    continue;
                }
                foreach (string valueName in key.GetValueNames())
                {
                    object? raw = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    if (raw is not string command || !WindowsStartupCommandLine.MatchesExactExecutable(command, identity.ExecutablePath))
                    {
                        continue;
                    }
                    entries.Add(new StartupEntryBackupV1(
                        $"{location.Scope}:{valueName}",
                        location.Scope,
                        valueName,
                        command,
                        WasEnabled: true,
                        key.GetValueKind(valueName).ToString()));
                }
            }
            catch (UnauthorizedAccessException)
            {
                // A non-accessible hive is not a candidate; never bypass ACLs.
            }
            catch (SecurityException)
            {
                // Same policy as UnauthorizedAccessException.
            }
        }
        return Task.FromResult<IReadOnlyList<StartupEntryBackupV1>>(entries);
    }

    public Task DisableAsync(IReadOnlyList<StartupEntryBackupV1> entries, CancellationToken cancellationToken)
    {
        foreach (StartupEntryBackupV1 entry in entries.Where(item => item.WasEnabled))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using RegistryKey? key = OpenLocation(RegistryStartupLocation.Parse(entry.Scope), writable: true)
                ?? throw new InvalidOperationException($"Startup entry '{entry.Id}' disappeared before it could be disabled.");
            object? current = key.GetValue(entry.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (current is not string command || !string.Equals(command, entry.Command, StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException($"Startup entry '{entry.Id}' changed after the takeover preview.");
            }
            key.DeleteValue(entry.Name, throwOnMissingValue: true);
        }
        return Task.CompletedTask;
    }

    public Task RestoreAsync(IReadOnlyList<StartupEntryBackupV1> entries, CancellationToken cancellationToken)
    {
        foreach (StartupEntryBackupV1 entry in entries.Where(item => item.WasEnabled))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using RegistryKey key = OpenLocation(RegistryStartupLocation.Parse(entry.Scope), writable: true, create: true)
                ?? throw new InvalidOperationException($"Startup scope '{entry.Scope}' cannot be restored.");
            object? current = key.GetValue(entry.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (current is not null && (current is not string command || !string.Equals(command, entry.Command, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException($"Startup entry '{entry.Id}' is now owned by a different command and will not be overwritten.");
            }
            if (current is null)
            {
                key.SetValue(entry.Name, entry.Command, ParseValueKind(entry.ValueKind));
            }
        }
        return Task.CompletedTask;
    }

    private static RegistryValueKind ParseValueKind(string? valueKind) =>
        Enum.TryParse(valueKind, ignoreCase: true, out RegistryValueKind parsed)
            ? parsed
            : RegistryValueKind.String;

    private static IEnumerable<RegistryStartupLocation> EnumerateLocations()
    {
        foreach (string subKey in StartupSubKeys)
        {
            yield return new RegistryStartupLocation("HKLM", null, subKey);
        }

        using RegistryKey users = Registry.Users;
        foreach (string hive in users.GetSubKeyNames().Where(IsUserHive))
        {
            foreach (string subKey in StartupSubKeys)
            {
                yield return new RegistryStartupLocation("HKU", hive, subKey);
            }
        }
    }

    private static bool IsUserHive(string value) => value.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("S-1-12-1-", StringComparison.OrdinalIgnoreCase);

    private static RegistryKey? OpenLocation(RegistryStartupLocation location, bool writable, bool create = false)
    {
        if (string.Equals(location.Root, "HKLM", StringComparison.Ordinal))
        {
            return create
                ? Registry.LocalMachine.CreateSubKey(location.SubKey, writable: true)
                : Registry.LocalMachine.OpenSubKey(location.SubKey, writable);
        }
        if (!string.Equals(location.Root, "HKU", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(location.Hive))
        {
            throw new InvalidDataException($"Unsupported startup scope '{location.Scope}'.");
        }
        using RegistryKey? user = Registry.Users.OpenSubKey(location.Hive, writable);
        if (user is null)
        {
            return null;
        }
        return create
            ? user.CreateSubKey(location.SubKey, writable: true)
            : user.OpenSubKey(location.SubKey, writable);
    }

    private sealed record RegistryStartupLocation(string Root, string? Hive, string SubKey)
    {
        public string Scope => Root == "HKLM" ? $"HKLM\\{SubKey}" : $"HKU\\{Hive}\\{SubKey}";

        public static RegistryStartupLocation Parse(string scope)
        {
            string[] parts = scope.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && string.Equals(parts[0], "HKLM", StringComparison.OrdinalIgnoreCase))
            {
                return new RegistryStartupLocation("HKLM", null, string.Join('\\', parts.Skip(1)));
            }
            if (parts.Length >= 3 && string.Equals(parts[0], "HKU", StringComparison.OrdinalIgnoreCase))
            {
                return new RegistryStartupLocation("HKU", parts[1], string.Join('\\', parts.Skip(2)));
            }
            throw new InvalidDataException($"Startup scope '{scope}' is malformed.");
        }
    }
}

/// <summary>
/// Conservative parser for conventional Run/RunOnce values. It accepts only a
/// direct executable command; cmd.exe wrappers and ambiguous command lines are
/// intentionally ignored rather than guessed.
/// </summary>
public static class WindowsStartupCommandLine
{
    public static bool MatchesExactExecutable(string command, string executablePath) =>
        TryGetExecutablePath(command, out string candidate)
        && string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(executablePath)),
            StringComparison.OrdinalIgnoreCase);

    public static bool TryGetExecutablePath(string command, out string executablePath)
    {
        executablePath = string.Empty;
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }
        string trimmed = command.Trim();
        if (trimmed[0] == '\"')
        {
            int closing = trimmed.IndexOf('\"', 1);
            if (closing <= 1)
            {
                return false;
            }
            executablePath = trimmed[1..closing];
            return executablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        }
        int extension = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (extension <= 0)
        {
            return false;
        }
        int end = extension + 4;
        if (end < trimmed.Length && !char.IsWhiteSpace(trimmed[end]))
        {
            return false;
        }
        executablePath = trimmed[..end];
        return true;
    }
}

public sealed class RuntimeTakeoverHardwareController(
    Func<string, CancellationToken, Task> resetAndVerify,
    OwnershipLeaseManager leases) : ITakeoverHardwareController
{
    public Task ResetAndVerifyAsync(string capabilityId, CancellationToken cancellationToken) =>
        resetAndVerify(capabilityId, cancellationToken);

    public Task<OwnershipLeaseV1> AcquireAsync(IReadOnlyList<string> resourceFamilies, CancellationToken cancellationToken) =>
        Task.FromResult(leases.Acquire("RigPilot", resourceFamilies, TimeSpan.FromHours(24), DateTimeOffset.UtcNow));

    public Task ReleaseAsync(OwnershipLeaseV1 lease, CancellationToken cancellationToken)
    {
        leases.Release(lease);
        return Task.CompletedTask;
    }
}

public sealed class SuiteTakeoverJournal(ISuiteStateStore store) : ITakeoverJournal
{
    public Task SaveAsync(TakeoverTransactionV1 transaction, CancellationToken cancellationToken) =>
        store.SaveSuiteEntityAsync(SuiteEntityKind.TakeoverTransaction, transaction.Id, transaction, cancellationToken);
}

public static class WindowsTakeoverPlanBuilder
{
    public static async Task<TakeoverPlanV1> CreateAsync(
        HardwareSnapshot snapshot,
        WindowsTakeoverProcessController processes,
        CancellationToken cancellationToken)
    {
        ConflictDescriptor[] runningConflicts = snapshot.Conflicts.Where(conflict => conflict.IsRunning).ToArray();
        IReadOnlyList<TakeoverProcessIdentity> identities = await processes.DiscoverAsync(runningConflicts, cancellationToken).ConfigureAwait(false);
        string[] controls = snapshot.Capabilities
            .Where(capability => capability.State == CapabilityAccessState.Blocked
                && capability.CanResetToDefault
                && !string.IsNullOrWhiteSpace(capability.ConflictOwner)
                && runningConflicts.Any(conflict => capability.ConflictOwner.Contains(conflict.DisplayName, StringComparison.OrdinalIgnoreCase)))
            .Select(capability => capability.Id)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        List<string> warnings = [];
        if (identities.Count == 0)
        {
            warnings.Add("No accessible running competitor binary could be identified by exact path. Takeover remains unavailable.");
        }
        foreach (TakeoverProcessIdentity identity in identities.Where(identity => string.IsNullOrWhiteSpace(identity.SignerThumbprint)))
        {
            warnings.Add($"{identity.DisplayName} is unsigned or its signer could not be read; automatic takeover will reject it.");
        }
        if (controls.Length == 0)
        {
            warnings.Add("No resettable overlapping hardware control was found. RigPilot will not acquire ownership without reset verification.");
        }
        return new TakeoverPlanV1(
            TakeoverPlanV1.CurrentSchemaVersion,
            $"takeover.plan.{Guid.NewGuid():N}",
            DateTimeOffset.UtcNow,
            identities,
            [],
            [],
            controls,
            warnings);
    }
}

internal static class AuthenticodeVerifier
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    public static bool TryVerify(string filePath, out string message)
    {
        return TryInspect(filePath, null, out message);
    }

    public static bool TryGetSigner(string filePath, out string publisher, out string? thumbprint)
    {
        string resolvedPublisher = "Unsigned";
        string? resolvedThumbprint = null;
        bool trustedSigner = TryInspect(
            filePath,
            state => TryReadSigner(state, out resolvedPublisher, out resolvedThumbprint),
            out _);
        publisher = resolvedPublisher;
        thumbprint = resolvedThumbprint;
        if (trustedSigner
            && !string.Equals(publisher, "Unsigned", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(thumbprint))
        {
            return true;
        }

        // Some Windows inbox binaries are catalog-signed. WinVerifyTrust
        // validates them, but the provider chain does not consistently expose
        // the catalog signer through WTHelper. The inbox PowerShell cmdlet uses
        // the OS verification path and is invoked by an absolute system path,
        // with a literal escaped file name and no profile or user script.
        return TryReadSignerThroughSystemPowerShell(filePath, out publisher, out thumbprint);
    }

    private static bool TryInspect(string filePath, Func<IntPtr, bool>? inspectVerifiedState, out string message)
    {
        if (!OperatingSystem.IsWindows())
        {
            message = "Authenticode verification is available only on Windows.";
            return false;
        }
        WinTrustFileInfo fileInfo = new()
        {
            cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            pcwszFilePath = filePath,
            hFile = IntPtr.Zero,
            pgKnownSubject = IntPtr.Zero
        };
        IntPtr fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        IntPtr trustDataPointer = IntPtr.Zero;
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
            WinTrustData trustData = new()
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                dwUIChoice = 2,
                fdwRevocationChecks = 0,
                dwUnionChoice = 1,
                pFile = fileInfoPointer,
                dwStateAction = 1,
                dwProvFlags = 0x100,
                dwUIContext = 0
            };
            trustDataPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustData>());
            Marshal.StructureToPtr(trustData, trustDataPointer, false);
            Guid action = GenericVerifyV2;
            uint result = WinVerifyTrust(IntPtr.Zero, ref action, trustDataPointer);
            WinTrustData state = Marshal.PtrToStructure<WinTrustData>(trustDataPointer);
            bool inspected = result == 0 && (inspectVerifiedState?.Invoke(state.hWVTStateData) ?? true);
            state.dwStateAction = 2;
            Marshal.StructureToPtr(state, trustDataPointer, true);
            _ = WinVerifyTrust(IntPtr.Zero, ref action, trustDataPointer);
            if (result == 0 && inspected)
            {
                message = "Authenticode verification succeeded.";
                return true;
            }
            if (result == 0)
            {
                message = "Authenticode verification succeeded but the signer certificate could not be read.";
                return false;
            }
            message = $"The service image is not Authenticode-trusted (WinVerifyTrust 0x{result:X8}).";
            return false;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or ArgumentException)
        {
            message = $"Authenticode verification could not run: {exception.Message}";
            return false;
        }
        finally
        {
            if (trustDataPointer != IntPtr.Zero)
            {
                Marshal.DestroyStructure<WinTrustData>(trustDataPointer);
                Marshal.FreeHGlobal(trustDataPointer);
            }
            Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPointer);
            Marshal.FreeHGlobal(fileInfoPointer);
        }
    }

    private static bool TryReadSigner(IntPtr stateData, out string publisher, out string? thumbprint)
    {
        publisher = "Unsigned";
        thumbprint = null;
        if (stateData == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            IntPtr providerData = WTHelperProvDataFromStateData(stateData);
            if (providerData == IntPtr.Zero)
            {
                return false;
            }
            IntPtr signerPointer = WTHelperGetProvSignerFromChain(providerData, 0, false, 0);
            if (signerPointer == IntPtr.Zero)
            {
                return false;
            }
            CryptProviderSigner signer = Marshal.PtrToStructure<CryptProviderSigner>(signerPointer);
            if (signer.csCertChain == 0 || signer.pasCertChain == IntPtr.Zero)
            {
                return false;
            }
            CryptProviderCertificate signerCertificate = Marshal.PtrToStructure<CryptProviderCertificate>(signer.pasCertChain);
            if (signerCertificate.pCert == IntPtr.Zero)
            {
                return false;
            }
            NativeCertContext context = Marshal.PtrToStructure<NativeCertContext>(signerCertificate.pCert);
            if (context.cbCertEncoded == 0 || context.cbCertEncoded > int.MaxValue || context.pbCertEncoded == IntPtr.Zero)
            {
                return false;
            }
            byte[] rawCertificate = new byte[checked((int)context.cbCertEncoded)];
            Marshal.Copy(context.pbCertEncoded, rawCertificate, 0, rawCertificate.Length);
            using X509Certificate2 certificate = X509CertificateLoader.LoadCertificate(rawCertificate);
            publisher = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
            if (string.IsNullOrWhiteSpace(publisher))
            {
                publisher = certificate.Subject;
            }
            thumbprint = certificate.Thumbprint;
            return !string.IsNullOrWhiteSpace(publisher) && !string.IsNullOrWhiteSpace(thumbprint);
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool TryReadSignerThroughSystemPowerShell(string filePath, out string publisher, out string? thumbprint)
    {
        publisher = "Unsigned";
        thumbprint = null;
        string powerShellPath = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powerShellPath))
        {
            return false;
        }

        string literalPath = filePath.Replace("'", "''", StringComparison.Ordinal);
        string command = "$ErrorActionPreference='Stop';"
            + "$signature=Get-AuthenticodeSignature -LiteralPath '" + literalPath + "';"
            + "$result=[pscustomobject]@{Status=[int]$signature.Status;Subject=[string]$signature.SignerCertificate.Subject;Thumbprint=[string]$signature.SignerCertificate.Thumbprint};"
            + "[Console]::Out.Write(($result|ConvertTo-Json -Compress))";
        ProcessStartInfo startInfo = new(powerShellPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(command);

        try
        {
            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }
            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
            Task<string> standardError = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(milliseconds: 5000))
            {
                process.Kill(entireProcessTree: true);
                return false;
            }
            if (!Task.WaitAll([standardOutput, standardError], millisecondsTimeout: 1000)
                || process.ExitCode != 0)
            {
                return false;
            }
            string output = standardOutput.Result;
            if (output.Length == 0 || output.Length > 4096)
            {
                return false;
            }
            using JsonDocument parsed = JsonDocument.Parse(output);
            JsonElement root = parsed.RootElement;
            if (!root.TryGetProperty("Status", out JsonElement status)
                || status.GetInt32() != 0
                || !root.TryGetProperty("Subject", out JsonElement subject)
                || !root.TryGetProperty("Thumbprint", out JsonElement thumb))
            {
                return false;
            }
            string subjectText = subject.GetString()?.Trim() ?? string.Empty;
            string thumbprintText = thumb.GetString()?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(subjectText) || string.IsNullOrWhiteSpace(thumbprintText))
            {
                return false;
            }
            publisher = subjectText;
            thumbprint = thumbprintText;
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or JsonException)
        {
            return false;
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint WinVerifyTrust(IntPtr hwnd, ref Guid actionId, IntPtr trustData);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr stateData);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvSignerFromChain(
        IntPtr providerData,
        uint signerIndex,
        [MarshalAs(UnmanagedType.Bool)] bool counterSigner,
        uint counterSignerIndex);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptProviderSigner
    {
        public uint cbStruct;
        public long sftVerifyAsOf;
        public uint csCertChain;
        public IntPtr pasCertChain;
        public uint dwSignerType;
        public IntPtr psSigner;
        public uint dwError;
        public uint csCounterSigners;
        public IntPtr pasCounterSigners;
        public IntPtr pChainContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CryptProviderCertificate
    {
        public uint cbStruct;
        public IntPtr pCert;
        [MarshalAs(UnmanagedType.Bool)] public bool fCommercial;
        [MarshalAs(UnmanagedType.Bool)] public bool fTrustedRoot;
        [MarshalAs(UnmanagedType.Bool)] public bool fSelfSigned;
        [MarshalAs(UnmanagedType.Bool)] public bool fTestCert;
        public uint dwRevokedReason;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCertContext
    {
        public uint dwCertEncodingType;
        public IntPtr pbCertEncoded;
        public uint cbCertEncoded;
        public IntPtr pCertInfo;
        public IntPtr hCertStore;
    }
}
