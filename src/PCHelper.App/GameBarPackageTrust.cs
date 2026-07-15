using System.Security.Principal;
using Microsoft.Win32;
using Windows.Management.Deployment;

namespace PCHelper.App;

/// <summary>
/// Resolves the exact installed RigPilot Game Bar package SID. The SID is used
/// only to grant a read-only user-agent bridge to the signed widget package;
/// it is never accepted by the hardware service.
/// </summary>
public static class GameBarPackageTrust
{
    public const string PackageName = "RigPilot.GameBarWidget";

    public static IReadOnlyList<SecurityIdentifier> ResolveInstalledPackageSids()
    {
        try
        {
            PackageManager manager = new();
            return manager.FindPackagesForUser(string.Empty)
                .Where(package => string.Equals(package.Id.Name, PackageName, StringComparison.OrdinalIgnoreCase))
                .Select(TryReadPackageSid)
                .OfType<SecurityIdentifier>()
                .GroupBy(sid => sid.Value, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
        }
        catch
        {
            // The widget remains unavailable until Windows can resolve its exact
            // installed package identity. Do not substitute a broad AppContainer ACL.
            return [];
        }
    }

    private static SecurityIdentifier? TryReadPackageSid(Windows.ApplicationModel.Package package)
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                $@"SOFTWARE\Microsoft\SecurityManager\CapAuthz\ApplicationEx\{package.Id.FullName}",
                writable: false);
            object? value = key?.GetValue("PackageSid");
            return value switch
            {
                byte[] binary => new SecurityIdentifier(binary, 0),
                string text when !string.IsNullOrWhiteSpace(text) => new SecurityIdentifier(text),
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }
}
