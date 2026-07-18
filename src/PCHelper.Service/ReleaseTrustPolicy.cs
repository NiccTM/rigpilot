using System.Reflection;
using PCHelper.Contracts;

namespace PCHelper.Service;

internal sealed record ReleaseTrustPolicy(bool PublicUnsignedPreview)
{
    internal const string MetadataKey = "RigPilotPublicUnsignedPreview";

    internal bool WritesAllowed => !PublicUnsignedPreview;

    internal static string WriteLockReason =>
        "This unsigned public preview is build-locked to monitoring. Install a signed RigPilot beta to enable service mutations.";

    internal static ReleaseTrustPolicy FromAssembly(Assembly assembly)
    {
        string? value = assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(attribute.Key, MetadataKey, StringComparison.Ordinal))?
            .Value;
        return FromBuildMetadata(value);
    }

    internal static ReleaseTrustPolicy FromBuildMetadata(string? value) =>
        new(string.Equals(value?.Trim(), bool.TrueString, StringComparison.OrdinalIgnoreCase));

    internal string? GetMutationRejection(IpcCommand command) =>
        !WritesAllowed && IpcCommandPolicy.IsMutation(command) ? WriteLockReason : null;
}
