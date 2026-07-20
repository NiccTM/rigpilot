using PCHelper.WorkloadHost;

namespace PCHelper.Integration.Tests;

/// <summary>
/// The workload host is what makes an Auto OC stability claim meaningful: the
/// screening monitor rejects any sample below its measured target-device load
/// floor, so an under-driven GPU invalidates the entire run rather than merely
/// weakening it. On the reference rig the workload reached only ~37% against a
/// 70% floor because the loop drained the fence queue to idle before every
/// CPU-side sleep, and Windows timer granularity makes that sleep 1-15 ms.
///
/// Measured on the reference 3090 — both changes are required, neither suffices:
///   deep queue + small dispatch   37.3%
///   shallow queue + big dispatch  38.1%
///   deep queue + big dispatch      100%
///
/// These cover the half that can regress silently. Dispatch size is enforced by
/// the shader itself; the ring arithmetic is ordinary code that an innocent-looking
/// edit could invert, and inverting it costs nothing visible except a slow return
/// to a third of the card.
/// </summary>
public sealed class WorkloadQueueTests
{
    private const int QueueDepth = 3;

    [Fact]
    public void NoWaitOccursWhileTheRingIsStillFilling()
    {
        // Blocking before the queue is deep enough would idle the GPU exactly as
        // the original loop did.
        for (long submitted = 1; submitted < QueueDepth; submitted++)
        {
            Assert.Null(WorkloadQueue.WaitSlot(submitted, QueueDepth));
        }
    }

    [Fact]
    public void TheFirstWaitHappensAsSoonAsTheRingIsFull()
    {
        Assert.Equal(0, WorkloadQueue.WaitSlot(QueueDepth, QueueDepth));
    }

    [Fact]
    public void AWaitNeverTargetsTheBatchJustSubmitted()
    {
        // The core invariant. If these ever coincide the loop degenerates into
        // submit-then-drain and utilisation collapses, with nothing else failing.
        for (long submitted = 0; submitted < 500; submitted++)
        {
            int justSubmitted = WorkloadQueue.SubmitSlot(submitted, QueueDepth);
            if (WorkloadQueue.WaitSlot(submitted + 1, QueueDepth) is int waiting)
            {
                Assert.NotEqual(justSubmitted, waiting);
            }
        }
    }

    [Fact]
    public void AWaitAlwaysLeavesQueueDepthMinusOneBatchesInFlight()
    {
        // What actually keeps the device busy: the sleep must overlap real GPU work.
        for (long submitted = QueueDepth; submitted < 500; submitted++)
        {
            int waiting = WorkloadQueue.WaitSlot(submitted, QueueDepth)!.Value;
            int inFlight = 0;
            for (long batch = submitted - QueueDepth + 1; batch < submitted; batch++)
            {
                if (WorkloadQueue.SubmitSlot(batch, QueueDepth) != waiting)
                {
                    inFlight++;
                }
            }

            Assert.Equal(QueueDepth - 1, inFlight);
        }
    }

    [Fact]
    public void SlotsStayInsideTheRingAcrossALongRun()
    {
        // Batches accumulate for the whole screening window; an out-of-range slot
        // would crash the host mid-run and abort the operation.
        foreach (long submitted in new long[] { 0, 1, QueueDepth, 10_000, long.MaxValue - 1 })
        {
            Assert.InRange(WorkloadQueue.SubmitSlot(submitted, QueueDepth), 0, QueueDepth - 1);
            if (WorkloadQueue.WaitSlot(submitted, QueueDepth) is int waiting)
            {
                Assert.InRange(waiting, 0, QueueDepth - 1);
            }
        }
    }
}
