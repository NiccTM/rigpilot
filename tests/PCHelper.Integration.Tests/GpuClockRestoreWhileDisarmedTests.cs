using PCHelper.Adapters;
using PCHelper.Contracts;

namespace PCHelper.Integration.Tests;

/// <summary>
/// Rollback and reset must reach the hardware even when the transport is
/// disarmed. Routing them through the armed write gate produced a real
/// deadlock on the reference rig: Auto OC disarmed on completion, the restore
/// write was refused, the service could not prove a default state, it entered
/// RecoveryRequired and locked writes — leaving it permanently unable to
/// perform the very restore that would clear the lock. A restore only moves
/// hardware toward the state it was already in, so refusing one strictly
/// increases risk.
/// </summary>
public sealed class GpuClockRestoreWhileDisarmedTests
{
    [Fact]
    public async Task RollbackUsesTheRestorePathSoItSurvivesADisarm()
    {
        DisarmAwareTransport transport = new();
        NvidiaGpuClockOffsetAdapter adapter = new(
            transport, GpuClockOffsetDomain.Core, "gpu:0", "0", () => true);

        PreparedAction prepared = await PrepareAsync(adapter, 45);
        await adapter.ApplyAsync(prepared, CancellationToken.None);

        // The operator (or the tuning engine finishing) disarms the transport.
        transport.Armed = false;

        // Rollback must still reach hardware.
        await adapter.RollbackAsync(prepared, CancellationToken.None);

        Assert.Contains(0, transport.RestoreCommands);
        Assert.Equal(0, transport.CurrentKiloHertz);
    }

    [Fact]
    public async Task ResetToDefaultAlsoSurvivesADisarm()
    {
        DisarmAwareTransport transport = new() { CurrentKiloHertz = 90, Armed = false };
        NvidiaGpuClockOffsetAdapter adapter = new(
            transport, GpuClockOffsetDomain.Core, "gpu:0", "0", () => true);

        await adapter.ResetToDefaultAsync("gpuclock.core:0", CancellationToken.None);

        Assert.Equal(0, transport.CurrentKiloHertz);
    }

    [Fact]
    public async Task ANormalApplyIsStillRefusedWhileDisarmed()
    {
        // The gate must still hold for ordinary writes — only restores are exempt.
        DisarmAwareTransport transport = new() { Armed = false };
        NvidiaGpuClockOffsetAdapter adapter = new(
            transport, GpuClockOffsetDomain.Core, "gpu:0", "0", () => true);

        PreparedAction prepared = await PrepareAsync(adapter, 45);

        await Assert.ThrowsAsync<GpuClockSafetyException>(
            () => adapter.ApplyAsync(prepared, CancellationToken.None));
        Assert.Empty(transport.RestoreCommands);
    }

    private static async Task<PreparedAction> PrepareAsync(NvidiaGpuClockOffsetAdapter adapter, double megaHertz) =>
        await adapter.PrepareAsync(
            new ProfileAction(
                $"action-{Guid.NewGuid():N}",
                "nvidia.gpuclock.core",
                "gpuclock.core:0",
                ControlValue.FromNumeric(megaHertz),
                Required: true,
                Order: 0),
            CancellationToken.None);

    /// <summary>Mirrors the real transport: ordinary writes are arm-gated, restores are not.</summary>
    private sealed class DisarmAwareTransport : IGpuClockOffsetTransport
    {
        public bool Armed { get; set; } = true;

        public int CurrentKiloHertz { get; set; }

        public List<int> RestoreCommands { get; } = [];

        public Task<GpuClockOffsetBounds?> ReadBoundsAsync(GpuClockOffsetDomain domain, CancellationToken cancellationToken) =>
            Task.FromResult<GpuClockOffsetBounds?>(new GpuClockOffsetBounds(-1_000_000, 1_000_000));

        public Task<GpuClockOffsetState> ReadStateAsync(GpuClockOffsetDomain domain, CancellationToken cancellationToken) =>
            Task.FromResult(new GpuClockOffsetState(CurrentKiloHertz));

        public Task SetOffsetAsync(GpuClockOffsetDomain domain, int offsetKiloHertz, CancellationToken cancellationToken)
        {
            if (!Armed)
            {
                throw new GpuClockSafetyException("GPU clock-offset writes require an acknowledged arm.");
            }

            CurrentKiloHertz = offsetKiloHertz;
            return Task.CompletedTask;
        }

        public Task RestoreOffsetAsync(GpuClockOffsetDomain domain, int offsetKiloHertz, CancellationToken cancellationToken)
        {
            RestoreCommands.Add(offsetKiloHertz);
            CurrentKiloHertz = offsetKiloHertz;
            return Task.CompletedTask;
        }
    }
}
