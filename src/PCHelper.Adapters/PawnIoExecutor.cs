using System.Runtime.InteropServices;

namespace PCHelper.Adapters;

/// <summary>
/// Seam over a loaded PawnIO module so callers can be tested without the
/// signed driver. Implementations execute named module functions with qword
/// buffers; they never expose raw port/MSR/PCI access to callers.
/// </summary>
public interface IPawnIoModuleSession : IDisposable
{
    /// <summary>
    /// Executes a named function from the loaded module. Returns the qwords the
    /// module wrote to the output buffer (possibly empty), or throws
    /// <see cref="PawnIoException"/> on a non-success HRESULT.
    /// </summary>
    ulong[] Execute(string functionName, ReadOnlySpan<ulong> input, int maximumOutputCount);
}

public sealed class PawnIoException(string message, int hresult) : InvalidOperationException(message)
{
    public int HResultCode { get; } = hresult;
}

/// <summary>
/// Minimal P/Invoke wrapper over the signed PawnIO user-mode library
/// (PawnIOLib.dll, API per its installed PawnIOLib.h): open an executor, load
/// one module blob, execute named functions, close. Opening an executor
/// requires administrator rights; on failure <see cref="TryOpen"/> returns
/// false instead of throwing. This wrapper adds no capability of its own —
/// what a session can do is bounded by the loaded module's own ioctl surface
/// and allowlists, and RigPilot only ships read-class module calls.
/// </summary>
public sealed class PawnIoModuleSession : IPawnIoModuleSession
{
    private readonly nint _library;
    private readonly nint _handle;
    private readonly PawnioExecuteDelegate _execute;
    private readonly PawnioCloseDelegate _close;
    private bool _disposed;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PawnioOpenDelegate(out nint handle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PawnioLoadDelegate(nint handle, byte[] blob, nuint size);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PawnioExecuteDelegate(
        nint handle,
        [MarshalAs(UnmanagedType.LPStr)] string name,
        ulong[] input,
        nuint inputCount,
        ulong[] output,
        nuint outputCount,
        out nuint returnedCount);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PawnioCloseDelegate(nint handle);

    private PawnIoModuleSession(nint library, nint handle, PawnioExecuteDelegate execute, PawnioCloseDelegate close)
    {
        _library = library;
        _handle = handle;
        _execute = execute;
        _close = close;
    }

    /// <summary>
    /// Loads PawnIOLib, opens an executor, and loads the supplied module blob.
    /// Returns false (with a reason) when the library is absent, the caller
    /// lacks the required rights, or the driver rejects the module.
    /// </summary>
    public static bool TryOpen(string libraryPath, byte[] moduleBlob, out PawnIoModuleSession session, out string message)
    {
        session = null!;
        nint library = 0;
        nint handle = 0;
        try
        {
            if (!NativeLibrary.TryLoad(libraryPath, out library))
            {
                message = $"PawnIOLib could not be loaded from '{libraryPath}'.";
                return false;
            }

            if (!TryGetExport(library, "pawnio_open", out PawnioOpenDelegate? open)
                || !TryGetExport(library, "pawnio_load", out PawnioLoadDelegate? load)
                || !TryGetExport(library, "pawnio_execute", out PawnioExecuteDelegate? execute)
                || !TryGetExport(library, "pawnio_close", out PawnioCloseDelegate? close))
            {
                message = "PawnIOLib does not export the documented open/load/execute/close surface.";
                return false;
            }

            int openResult = open!(out handle);
            if (openResult < 0)
            {
                message = $"pawnio_open failed with HRESULT 0x{openResult:X8}; administrator rights are required for a PawnIO executor.";
                return false;
            }

            int loadResult = load!(handle, moduleBlob, (nuint)moduleBlob.Length);
            if (loadResult < 0)
            {
                close!(handle);
                handle = 0;
                message = $"pawnio_load rejected the module blob with HRESULT 0x{loadResult:X8}.";
                return false;
            }

            session = new PawnIoModuleSession(library, handle, execute!, close!);
            library = 0; // ownership transferred
            message = "PawnIO module session is open.";
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (handle != 0)
            {
                // Best-effort close; the process boundary is the final cleanup.
            }
            message = $"PawnIO session setup failed: {exception.GetType().Name}.";
            return false;
        }
        finally
        {
            if (library != 0)
            {
                NativeLibrary.Free(library);
            }
        }
    }

    public ulong[] Execute(string functionName, ReadOnlySpan<ulong> input, int maximumOutputCount)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ulong[] inputBuffer = input.ToArray();
        ulong[] outputBuffer = new ulong[Math.Max(0, maximumOutputCount)];
        int result = _execute(
            _handle,
            functionName,
            inputBuffer,
            (nuint)inputBuffer.Length,
            outputBuffer,
            (nuint)outputBuffer.Length,
            out nuint returnedCount);
        if (result < 0)
        {
            throw new PawnIoException($"PawnIO function '{functionName}' failed with HRESULT 0x{result:X8}.", result);
        }

        return outputBuffer[..(int)Math.Min(returnedCount, (nuint)outputBuffer.Length)];
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _close(_handle);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Executor close is best-effort; the process boundary is the real cleanup.
        }
        NativeLibrary.Free(_library);
    }

    private static bool TryGetExport<TDelegate>(nint library, string name, out TDelegate? export)
        where TDelegate : Delegate
    {
        if (NativeLibrary.TryGetExport(library, name, out nint address))
        {
            export = Marshal.GetDelegateForFunctionPointer<TDelegate>(address);
            return true;
        }

        export = null;
        return false;
    }
}
