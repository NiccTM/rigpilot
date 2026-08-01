using PCHelper.Contracts;
using PCHelper.Service;

namespace PCHelper.Integration.Tests;

/// <summary>
/// The service replays a cached response when a request carries an idempotency
/// key it has already completed. That cache has to be keyed by the command as
/// well as the key: a key on its own does not identify a request, so a key
/// replayed under a different command would return the earlier command's
/// success without the new command ever running — a verified-success message for
/// hardware work that never happened.
/// </summary>
public sealed class IdempotencyScopeTests
{
    [Fact]
    public void SameKeyUnderDifferentCommandsDoesNotShareACacheEntry()
    {
        string? saveRule = PCHelperRuntime.IdempotencyCacheKey(Request(IpcCommand.SaveHealthRule, "shared-key"));
        string? applyProfile = PCHelperRuntime.IdempotencyCacheKey(Request(IpcCommand.ApplyProfile, "shared-key"));

        Assert.NotNull(saveRule);
        Assert.NotNull(applyProfile);
        Assert.NotEqual(saveRule, applyProfile);
    }

    [Fact]
    public void SameKeyUnderTheSameCommandStillReplays()
    {
        string? first = PCHelperRuntime.IdempotencyCacheKey(Request(IpcCommand.ApplyProfile, "retry-key"));
        string? second = PCHelperRuntime.IdempotencyCacheKey(Request(IpcCommand.ApplyProfile, "retry-key"));

        Assert.NotNull(first);
        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AbsentOrBlankKeysAreNeverCached(string? key)
    {
        Assert.Null(PCHelperRuntime.IdempotencyCacheKey(Request(IpcCommand.ApplyProfile, key)));
    }

    [Fact]
    public void DistinctKeysUnderOneCommandStayDistinct()
    {
        Assert.NotEqual(
            PCHelperRuntime.IdempotencyCacheKey(Request(IpcCommand.ApplyProfile, "a")),
            PCHelperRuntime.IdempotencyCacheKey(Request(IpcCommand.ApplyProfile, "b")));
    }

    private static IpcRequest Request(IpcCommand command, string? idempotencyKey) => new(
        ProtocolConstants.Version,
        Guid.NewGuid().ToString("N"),
        command,
        null,
        idempotencyKey,
        null);
}
