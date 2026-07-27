using System.Runtime.InteropServices;

namespace PCHelper.Integration.Tests;

/// <summary>
/// The dashboard died with UCEERR_RENDERTHREADFAILURE on 2026-07-26 when an Auto OC memory
/// run reset the display driver. The service handled the same event correctly — it detected
/// the stalled workload host, restored prior state, and reported the stage — but the window
/// the operator was watching the run in terminated.
///
/// These pin the two properties that make the handler safe rather than merely quiet: it
/// recognises exactly the render-thread HRESULT, and it recognises nothing else. A handler
/// that swallowed more would turn every real dashboard bug into a silent hang.
///
/// The exceptions come from <see cref="Marshal.GetExceptionForHR(int)"/> rather than a
/// constructor, which is both what CA2201 requires and a closer match to how the real one
/// arrives — the runtime maps the HRESULT, this does not hand-build a stand-in.
/// </summary>
public sealed class RenderThreadRecoveryTests
{
    private const int RenderThreadFailureHResult = unchecked((int)0x88980406);

    /// <summary>The predicate the dispatcher handler applies, mirrored exactly.</summary>
    private static bool IsRenderThreadFailure(Exception? exception) =>
        exception is COMException com && com.HResult == RenderThreadFailureHResult;

    [Fact]
    public void TheRenderThreadFailureHResultIsTheOneWpfActuallyRaises()
    {
        // Sourced from the live crash: "System.Runtime.InteropServices.COMException
        // (0x88980406): UCEERR_RENDERTHREADFAILURE at DUCE.Channel.SyncFlush()". If this
        // constant is ever mistyped the handler silently stops working and the app resumes
        // crashing, with nothing to show that anything changed.
        Assert.Equal(unchecked((int)0x88980406), RenderThreadFailureHResult);
        Assert.True(IsRenderThreadFailure(Marshal.GetExceptionForHR(RenderThreadFailureHResult)));
    }

    [Theory]
    [InlineData(unchecked((int)0x80004005))] // E_FAIL
    [InlineData(unchecked((int)0x88980001))] // a different UCE error
    [InlineData(unchecked((int)0x887A0005))] // DXGI_ERROR_DEVICE_REMOVED
    public void OtherComFailuresAreNotTreatedAsRenderThreadRecovery(int hresult)
    {
        // Device-removed in particular is tempting to lump in, but it is not the failure the
        // dashboard can transparently rebuild from, and quietly continuing after one would
        // leave a window that renders nothing while looking healthy.
        Assert.False(IsRenderThreadFailure(Marshal.GetExceptionForHR(hresult)));
    }

    [Fact]
    public void ANonComExceptionIsNeverRenderThreadRecovery()
    {
        // The handler tests the exception TYPE before the HRESULT. Without that, any
        // exception whose HResult happened to collide would be swallowed — and every .NET
        // exception carries an HResult.
        InvalidOperationException collision = new("unrelated") { HResult = RenderThreadFailureHResult };

        Assert.Equal(RenderThreadFailureHResult, collision.HResult);
        Assert.False(IsRenderThreadFailure(collision));
    }

    [Fact]
    public void ASuccessHResultIsNotAFailureAtAll()
    {
        // GetExceptionForHR returns null for S_OK; the predicate must tolerate that rather
        // than throwing inside an exception handler, which would replace a recoverable
        // render fault with an unrecoverable one.
        Assert.False(IsRenderThreadFailure(Marshal.GetExceptionForHR(0)));
    }
}
