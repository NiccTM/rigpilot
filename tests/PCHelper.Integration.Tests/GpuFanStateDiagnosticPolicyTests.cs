using PCHelper.Contracts;
using PCHelper.Service;

namespace PCHelper.Integration.Tests;

/// <summary>
/// The GPU fan diagnostic exists to be readable when the fan is faulted, which is when
/// the service has typically locked hardware writes. A write lock rejects every mutating
/// command, and command classification is an opt-in allowlist, so a diagnostic omitted
/// from it silently becomes unusable in exactly the situation it was added for.
/// </summary>
public sealed class GpuFanStateDiagnosticPolicyTests
{
    [Fact]
    public void GpuFanStateIsReadOnlySoItSurvivesTheWriteLock()
    {
        Assert.True(IpcCommandPolicy.IsReadOnly(IpcCommand.GetGpuFanState));
        Assert.False(IpcCommandPolicy.IsMutation(IpcCommand.GetGpuFanState));
    }

    [Fact]
    public void GpuFanStateIsNotBlockedByThePublicPreviewWriteLock()
    {
        ReleaseTrustPolicy policy = ReleaseTrustPolicy.FromBuildMetadata("true");

        Assert.False(policy.WritesAllowed);
        Assert.Null(policy.GetMutationRejection(IpcCommand.GetGpuFanState));
    }
}
