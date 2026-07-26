using PCHelper.Service;
using PCHelper.Contracts;

namespace PCHelper.Integration.Tests;

public sealed class ReleaseTrustPolicyTests
{
    [Theory]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData(" true ")]
    public void PublicPreviewMetadataLocksServiceWrites(string value)
    {
        ReleaseTrustPolicy policy = ReleaseTrustPolicy.FromBuildMetadata(value);

        Assert.True(policy.PublicUnsignedPreview);
        Assert.False(policy.WritesAllowed);
        Assert.Contains("build-locked", ReleaseTrustPolicy.WriteLockReason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("false")]
    [InlineData("unexpected")]
    public void NonPreviewMetadataDoesNotApplyReleaseLock(string? value)
    {
        ReleaseTrustPolicy policy = ReleaseTrustPolicy.FromBuildMetadata(value);

        Assert.False(policy.PublicUnsignedPreview);
        Assert.True(policy.WritesAllowed);
    }

    [Fact]
    public void PublicPreviewRejectsEveryMutationAndPreservesReadOnlyCommands()
    {
        ReleaseTrustPolicy policy = ReleaseTrustPolicy.FromBuildMetadata("true");

        foreach (IpcCommand command in Enum.GetValues<IpcCommand>())
        {
            string? rejection = policy.GetMutationRejection(command);
            if (IpcCommandPolicy.IsMutation(command))
            {
                Assert.Equal(ReleaseTrustPolicy.WriteLockReason, rejection);
            }
            else
            {
                Assert.Null(rejection);
            }
        }

        Assert.True(IpcCommandPolicy.IsReadOnly(IpcCommand.PreviewProfileV2));
        Assert.Null(policy.GetMutationRejection(IpcCommand.PreviewProfileV2));
    }
}
