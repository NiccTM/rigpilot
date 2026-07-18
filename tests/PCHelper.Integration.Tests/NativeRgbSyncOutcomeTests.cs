using PCHelper.App;
using PCHelper.Contracts;

namespace PCHelper.Integration.Tests;

public sealed class NativeRgbSyncOutcomeTests
{
    [Fact]
    public void ContainedDeviceFailureKeepsItsActionableMessage()
    {
        IpcResponse response = Response(success: true);

        NativeRgbSyncOutcome outcome = MainViewModel.BuildNativeRgbSyncOutcome(
            "Aura headers",
            response,
            writeIssued: false,
            "The AURA controller is held by another process.");

        Assert.False(outcome.Succeeded);
        Assert.Equal("The AURA controller is held by another process.", outcome.Message);
    }

    [Fact]
    public void TransportFailureTakesPriorityOverAStaleDeviceMessage()
    {
        IpcResponse response = Response(
            success: false,
            errorCode: "RECOVERY_REQUIRED",
            error: "Hardware writes are locked until recovery completes.");

        NativeRgbSyncOutcome outcome = MainViewModel.BuildNativeRgbSyncOutcome(
            "Kraken",
            response,
            writeIssued: true,
            "Static colour written.");

        Assert.False(outcome.Succeeded);
        Assert.Equal("Hardware writes are locked until recovery completes.", outcome.Message);
    }

    [Fact]
    public void SuccessRequiresBothIpcSuccessAndDeviceWriteIssued()
    {
        NativeRgbSyncOutcome outcome = MainViewModel.BuildNativeRgbSyncOutcome(
            "Razer case",
            Response(success: true),
            writeIssued: true,
            "Firmware ACK 0x02.");

        Assert.True(outcome.Succeeded);
    }

    private static IpcResponse Response(
        bool success,
        string? errorCode = null,
        string? error = null) => new(
            ProtocolConstants.Version,
            "request",
            success,
            1,
            errorCode,
            error,
            null);
}
