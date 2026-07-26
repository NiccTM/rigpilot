using PCHelper.Adapters;
using PCHelper.Service;

namespace PCHelper.Integration.Tests;

/// <summary>
/// The runtime prefers the NVAPI fan transport over NVML. NVML's fan setters
/// return NVML_ERROR_NO_PERMISSION on this class of GeForce card, so the NVML
/// transport reported valid bounds yet every fan write was refused — which broke
/// the Auto OC fan assist and the manual fan slider alike. NVAPI is the standard
/// NVIDIA fan-write path (and the surface the clock-offset control already uses
/// successfully). This locks in the preference and the disposal contract, since a
/// silent regression to NVML would compile, pass every other test, and only fail
/// on real hardware at write time.
/// </summary>
public sealed class GpuFanTransportSelectionTests
{
    private static readonly GpuFanBounds ValidBounds = new(30, 100);

    [Fact]
    public async Task TheFirstUsableCandidateWinsAndLaterCandidatesAreNeverCreated()
    {
        // NVAPI is first and usable, so NVML must not even be constructed — loading
        // the fallback's native runtime when it is not needed is pure waste.
        RecordingFan nvapi = new(canWrite: true, ValidBounds);
        bool nvmlCreated = false;

        IGpuFanCoolerTransport? selected = await PCHelperRuntime.SelectUsableFanTransportAsync(
            [
                _ => Task.FromResult<IGpuFanCoolerTransport?>(nvapi),
                _ => { nvmlCreated = true; return Task.FromResult<IGpuFanCoolerTransport?>(new RecordingFan(true, ValidBounds)); },
            ],
            CancellationToken.None);

        Assert.Same(nvapi, selected);
        Assert.False(nvmlCreated);
        Assert.False(nvapi.Disposed);
    }

    [Fact]
    public async Task AnUnusableFirstCandidateFallsBackAndIsDisposed()
    {
        // NVAPI reporting no controllable cooler (invalid bounds) must fall through
        // to NVML rather than leaving the machine with no fan control at all — and
        // the rejected NVAPI transport must be disposed, not leaked.
        RecordingFan nvapi = new(canWrite: true, bounds: null);
        RecordingFan nvml = new(canWrite: true, ValidBounds);

        IGpuFanCoolerTransport? selected = await PCHelperRuntime.SelectUsableFanTransportAsync(
            [
                _ => Task.FromResult<IGpuFanCoolerTransport?>(nvapi),
                _ => Task.FromResult<IGpuFanCoolerTransport?>(nvml),
            ],
            CancellationToken.None);

        Assert.Same(nvml, selected);
        Assert.True(nvapi.Disposed);
        Assert.False(nvml.Disposed);
    }

    [Fact]
    public async Task ATransportThatCannotWriteIsRejectedEvenWithValidBounds()
    {
        // Bounds alone are not enough — the NVML transport reported valid bounds
        // while CanWrite was false (setters not exported), which is exactly the
        // "looks available, every write refused" trap.
        RecordingFan unusable = new(canWrite: false, ValidBounds);
        RecordingFan usable = new(canWrite: true, ValidBounds);

        IGpuFanCoolerTransport? selected = await PCHelperRuntime.SelectUsableFanTransportAsync(
            [
                _ => Task.FromResult<IGpuFanCoolerTransport?>(unusable),
                _ => Task.FromResult<IGpuFanCoolerTransport?>(usable),
            ],
            CancellationToken.None);

        Assert.Same(usable, selected);
        Assert.True(unusable.Disposed);
    }

    [Fact]
    public async Task NoUsableCandidateReturnsNullAndDisposesEveryOne()
    {
        RecordingFan a = new(canWrite: false, ValidBounds);
        RecordingFan b = new(canWrite: true, bounds: null);

        IGpuFanCoolerTransport? selected = await PCHelperRuntime.SelectUsableFanTransportAsync(
            [
                _ => Task.FromResult<IGpuFanCoolerTransport?>(a),
                _ => Task.FromResult<IGpuFanCoolerTransport?>(b),
                _ => Task.FromResult<IGpuFanCoolerTransport?>(null),
            ],
            CancellationToken.None);

        Assert.Null(selected);
        Assert.True(a.Disposed);
        Assert.True(b.Disposed);
    }

    private sealed class RecordingFan(bool canWrite, GpuFanBounds? bounds) : IGpuFanCoolerTransport
    {
        public bool Disposed { get; private set; }

        public bool CanWrite => canWrite;

        public void SetArmed(bool armed) { }

        public void Dispose() => Disposed = true;

        public Task<GpuFanBounds?> ReadBoundsAsync(string channelId, CancellationToken cancellationToken) =>
            Task.FromResult(bounds);

        public Task<GpuFanChannelState> ReadStateAsync(string channelId, CancellationToken cancellationToken) =>
            Task.FromResult(new GpuFanChannelState(GpuFanControlPolicy.Automatic, null, null));

        public Task SetManualDutyAsync(string channelId, int dutyPercent, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RestoreAutomaticAsync(string channelId, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
