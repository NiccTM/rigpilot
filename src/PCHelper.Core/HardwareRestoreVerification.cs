using PCHelper.Contracts;

namespace PCHelper.Core;

/// <summary>
/// Restores a control and proves the result by reading the hardware back.
/// </summary>
/// <remarks>
/// <para>
/// Both Auto OC engines used to let a failed restore <em>write</em> short-circuit
/// the read-back, so a control whose write was refused was reported as being in
/// an unknown state and escalated to RecoveryRequired. On the reference rig every
/// NVAPI/NVML write is refused outright (the service runs as LocalSystem in
/// session 0), so a run that never modified the GPU at all still ended
/// RecoveryRequired and locked writes — with nvidia-smi confirming stock power
/// limit and zero offsets throughout.
/// </para>
/// <para>
/// The proof is a read, and reads are not privileged. A refused write is not
/// evidence that the hardware moved; it is close to the opposite. So the
/// read-back is now always attempted, even when the write threw, and a control
/// observed sitting at its captured prior value is proven restored regardless of
/// what the write did.
/// </para>
/// <para>
/// This only ever <em>downgrades</em> on positive evidence. A read-back that
/// mismatches, throws, or is unavailable still fails, and the original write
/// error is carried into the message so the cause is never lost. Note that a
/// verified read-back is a strictly stronger proof than a write returning
/// success — which is why this codebase never trusted the write alone.
/// </para>
/// </remarks>
public static class HardwareRestoreVerification
{
    public static async Task<HardwareStateVerification> RestoreAndVerifyAsync(
        CapabilityDescriptor capability,
        PreparedAction original,
        IHardwareAdapter adapter,
        CancellationToken cancellationToken = default)
    {
        Exception? restoreError = null;
        try
        {
            await adapter.RollbackAsync(original, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            restoreError = exception;
        }

        if (adapter is not IHardwareStateVerifier verifier)
        {
            throw new InvalidOperationException(
                $"'{capability.Name}' has no rollback read-back verifier, so its state cannot be proven."
                + Describe(restoreError));
        }

        HardwareStateVerification verification;
        try
        {
            verification = await verifier.VerifyRollbackStateAsync(original, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"'{capability.Name}' could not be read back, so its state is unknown: {exception.Message}"
                + Describe(restoreError),
                restoreError ?? exception);
        }

        if (!verification.Success)
        {
            throw new InvalidOperationException(verification.Message + Describe(restoreError), restoreError!);
        }

        return restoreError is null
            ? verification
            : verification with
            {
                Message = verification.Message
                    + $" The restore write was refused ({restoreError.GetType().Name}: {restoreError.Message}), "
                    + "but the control was read back at its captured prior value, so the hardware was never moved."
            };
    }

    private static string Describe(Exception? restoreError) => restoreError is null
        ? string.Empty
        : $" The restore write also failed: {restoreError.GetType().Name}: {restoreError.Message}.";
}
