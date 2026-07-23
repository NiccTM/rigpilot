using PCHelper.Contracts;
using PCHelper.Core;

namespace PCHelper.Core.Tests;

/// <summary>
/// Reproduces the live failure on the reference rig: every NVAPI/NVML write is
/// refused because the service runs as LocalSystem in session 0, so a Full Auto
/// OC run that never moved the GPU still ended RecoveryRequired and locked
/// writes. nvidia-smi confirmed stock power limit and zero offsets throughout —
/// the hardware was never touched, and the read-back could have proven it, but a
/// failed restore write short-circuited the read.
/// </summary>
public sealed class HardwareRestoreVerificationTests
{
    [Fact]
    public async Task ARefusedRestoreWriteIsProvenSafeByTheReadBack()
    {
        // The exact live case: the write is refused, but the control reads back
        // at its captured prior value, so no recovery is required.
        FakeAdapter adapter = new(restoreThrows: true, observedMatchesPrior: true);

        HardwareStateVerification verification = await HardwareRestoreVerification.RestoreAndVerifyAsync(
            Capability, Prepared, adapter);

        Assert.True(verification.Success);
        Assert.Contains("never moved", verification.Message, StringComparison.Ordinal);
        // The cause is still reported, not swallowed.
        Assert.Contains("NVAPI_INVALID_USER_PRIVILEGE", verification.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusedRestoreWriteStillFailsWhenTheReadBackDisagrees()
    {
        // The safety-critical direction: a refused write AND a control observed
        // away from its prior value is a genuine unknown state. This must escalate.
        FakeAdapter adapter = new(restoreThrows: true, observedMatchesPrior: false);

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => HardwareRestoreVerification.RestoreAndVerifyAsync(Capability, Prepared, adapter));

        Assert.Contains("did not match", failure.Message, StringComparison.Ordinal);
        Assert.Contains("restore write also failed", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnreadableControlStillFails()
    {
        // No positive evidence means no downgrade — absence of a read-back is
        // never scored as a successful restore.
        FakeAdapter adapter = new(restoreThrows: true, observedMatchesPrior: true) { VerifyThrows = true };

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => HardwareRestoreVerification.RestoreAndVerifyAsync(Capability, Prepared, adapter));

        Assert.Contains("state is unknown", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAdapterWithNoVerifierStillFails()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => HardwareRestoreVerification.RestoreAndVerifyAsync(Capability, Prepared, new UnverifiableAdapter()));
    }

    [Fact]
    public async Task ASuccessfulRestoreIsUnchanged()
    {
        FakeAdapter adapter = new(restoreThrows: false, observedMatchesPrior: true);

        HardwareStateVerification verification = await HardwareRestoreVerification.RestoreAndVerifyAsync(
            Capability, Prepared, adapter);

        Assert.True(verification.Success);
        Assert.DoesNotContain("refused", verification.Message, StringComparison.Ordinal);
        Assert.True(adapter.RolledBack);
    }

    private static PreparedAction Prepared => new(
        new ProfileAction("action", "adapter", "gpuclock.core:0", ControlValue.FromNumeric(0), Required: true, Order: 0),
        ControlValue.FromNumeric(0),
        DateTimeOffset.UtcNow,
        "token");

    private static CapabilityDescriptor Capability => new(
        "gpuclock.core:0",
        "adapter",
        "nvidia:gpu-0",
        "GPU core clock offset",
        CapabilityAccessState.Experimental,
        AdapterExecutionContext.SystemService,
        ControlValueKind.Numeric,
        new NumericRange(-500, 250, 15),
        "MHz",
        RiskLevel.Experimental,
        EvidenceLevel.Detected,
        null,
        "GPU core clock offset",
        CanResetToDefault: true,
        Domain: ControlDomain.Gpu);

    private class UnverifiableAdapter : IHardwareAdapter
    {
        public AdapterManifest Manifest { get; } = new(
            "adapter", "adapter", "1", "GPL-3.0-only", null, AdapterExecutionContext.SystemService, ["gpu"], ["test"]);

        public Task<PreparedAction> PrepareAsync(ProfileAction action, CancellationToken cancellationToken) =>
            Task.FromResult(Prepared);

        public Task ApplyAsync(PreparedAction action, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public virtual Task RollbackAsync(PreparedAction action, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<ActionVerification> VerifyAsync(PreparedAction action, CancellationToken cancellationToken) =>
            Task.FromResult(new ActionVerification(action.Action.Id, true, action.Action.Value, "Verified."));

        public Task ResetToDefaultAsync(string capabilityId, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<AdapterProbeResult> ProbeAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SensorSample>> ReadSensorsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SensorSample>>([]);

        public Task<AdapterHealth> GetHealthAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new AdapterHealth(Manifest.Id, true, DateTimeOffset.UtcNow, "Healthy", []));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeAdapter(bool restoreThrows, bool observedMatchesPrior)
        : UnverifiableAdapter, IHardwareStateVerifier
    {
        public bool RolledBack { get; private set; }

        public bool VerifyThrows { get; init; }

        public override Task RollbackAsync(PreparedAction action, CancellationToken cancellationToken)
        {
            if (restoreThrows)
            {
                // Mirrors the live driver refusal seen on the reference rig.
                throw new InvalidOperationException("NVAPI_INVALID_USER_PRIVILEGE");
            }

            RolledBack = true;
            return Task.CompletedTask;
        }

        public Task<HardwareStateVerification> VerifyDefaultStateAsync(
            string capabilityId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HardwareStateVerification("adapter", capabilityId, true, null, "default"));

        public Task<HardwareStateVerification> VerifyRollbackStateAsync(
            PreparedAction action,
            CancellationToken cancellationToken)
        {
            if (VerifyThrows)
            {
                throw new InvalidOperationException("the driver did not answer the read");
            }

            return Task.FromResult(new HardwareStateVerification(
                "adapter",
                action.Action.CapabilityId,
                observedMatchesPrior,
                ControlValue.FromNumeric(0),
                observedMatchesPrior
                    ? "GPU core clock rollback state was read back."
                    : "GPU core clock rollback read-back did not match the captured state."));
        }
    }
}
