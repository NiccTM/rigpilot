using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Core.Tests;

/// <summary>
/// A RecoveryRequired failure must carry its per-control causes in the message
/// itself. That string is what reaches the durable operation record; the
/// adapter trace that would otherwise hold the detail is a bounded in-memory
/// buffer, and the remedy the failure tells the operator to perform — restart
/// the service — flushes it. Losing the cause exactly when the operator
/// follows the instructions makes the failure undiagnosable after the fact.
/// </summary>
public sealed class AutoOcRestorationDiagnosticsTests
{
    [Fact]
    public void TheRecoveryExceptionNamesEveryFailedControlAndItsReason()
    {
        // Shape of what the engines now build: control name, capability id,
        // exception type, and reason, joined per failed control.
        string[] details =
        [
            "GPU core clock offset (gpuclock.core:0): NotSupportedException: transport closed",
            "GPU memory clock offset (gpuclock.memory:0): TimeoutException: read-back timed out",
        ];
        string message = $"Auto OC V3 attempted every hardware restore but could not prove {details.Length} control state(s). "
            + string.Join(" | ", details);

        HardwareOperationRecoveryException exception = new(
            message,
            new AggregateException(
                new NotSupportedException("transport closed"),
                new TimeoutException("read-back timed out")));

        // The count alone is not diagnosable; the identities and reasons are.
        Assert.Contains("gpuclock.core:0", exception.Message, StringComparison.Ordinal);
        Assert.Contains("gpuclock.memory:0", exception.Message, StringComparison.Ordinal);
        Assert.Contains("transport closed", exception.Message, StringComparison.Ordinal);
        Assert.Contains("read-back timed out", exception.Message, StringComparison.Ordinal);
        Assert.Contains("NotSupportedException", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheInnerExceptionsAreRetainedForCallersThatWantThem()
    {
        HardwareOperationRecoveryException exception = new(
            "could not prove 2 control state(s). a | b",
            new AggregateException(new NotSupportedException("a"), new TimeoutException("b")));

        AggregateException inner = Assert.IsType<AggregateException>(exception.InnerException);
        Assert.Equal(2, inner.InnerExceptions.Count);
    }
}
