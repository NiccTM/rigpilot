using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;
using PCHelper.Contracts;

namespace PCHelper.App;

/// <summary>
/// Same-user, opt-in still-image capture. This is deliberately separate from
/// video recording: it creates a PNG only after an explicit UI confirmation,
/// always writes below the user's Pictures\RigPilot directory, and never uses
/// the service, injection, or a network path.
/// </summary>
public interface IDesktopSnapshotBackend
{
    IReadOnlyList<CaptureTargetV1> DiscoverTargets();

    Task<CaptureSnapshotResultV1> CaptureAsync(
        CaptureSnapshotRequestV1 request,
        CancellationToken cancellationToken);
}

public sealed class WindowsDesktopSnapshotBackend : IDesktopSnapshotBackend
{
    private const int MaximumDimension = 7_680;
    private const long MaximumPixelCount = 33_177_600; // 8K UHD.
    private const uint Srccopy = 0x00CC0020;
    private const uint CaptureBlt = 0x40000000;
    private const uint PrintWindowRenderFullContent = 0x00000002;
    private readonly string _outputDirectory;
    private readonly Func<DateTimeOffset> _clock;

    public WindowsDesktopSnapshotBackend(string? outputDirectory = null, Func<DateTimeOffset>? clock = null)
    {
        string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (string.IsNullOrWhiteSpace(pictures))
        {
            pictures = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }
        _outputDirectory = Path.GetFullPath(outputDirectory
            ?? Path.Combine(pictures, "RigPilot", "Snapshots"));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public IReadOnlyList<CaptureTargetV1> DiscoverTargets()
    {
        List<CaptureTargetV1> targets = Forms.Screen.AllScreens
            .OrderBy(screen => screen.Primary ? 0 : 1)
            .ThenBy(screen => screen.DeviceName, StringComparer.OrdinalIgnoreCase)
            .Select(screen => new CaptureTargetV1(
                CaptureTargetKind.Display,
                $"display:{screen.DeviceName}",
                $"{(screen.Primary ? "Primary " : string.Empty)}display {screen.DeviceName} ({screen.Bounds.Width} x {screen.Bounds.Height})"))
            .ToList();

        HashSet<long> seenHandles = [];
        _ = EnumWindows((window, _) =>
        {
            try
            {
                if (!ShouldExposeWindow(window) || !seenHandles.Add(window.ToInt64()))
                {
                    return true;
                }

                NativeRect bounds;
                if (!GetWindowRect(window, out bounds) || bounds.Width < 80 || bounds.Height < 60)
                {
                    return true;
                }

                string title = ReadWindowText(window);
                if (string.IsNullOrWhiteSpace(title))
                {
                    return true;
                }

                string processName = ReadProcessName(window);
                string label = string.IsNullOrWhiteSpace(processName)
                    ? title
                    : $"{processName}: {title}";
                targets.Add(new CaptureTargetV1(
                    CaptureTargetKind.Window,
                    $"window:0x{window.ToInt64():X}",
                    TrimDisplayLabel(label)));
            }
            catch
            {
                // A window can disappear or deny inspection while EnumWindows is
                // running. It is simply not offered as a capture target.
            }
            return true;
        }, IntPtr.Zero);
        return targets;
    }

    public Task<CaptureSnapshotResultV1> CaptureAsync(
        CaptureSnapshotRequestV1 request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRequest(request);

        CapturedBitmap captured = request.Target.Kind switch
        {
            CaptureTargetKind.Display => CaptureDisplay(request.Target),
            CaptureTargetKind.Window => CaptureWindow(request.Target),
            _ => throw new InvalidDataException("Only desktop display and window targets can be captured.")
        };
        cancellationToken.ThrowIfCancellationRequested();

        Directory.CreateDirectory(_outputDirectory);
        string output = CreateOutputPath(request.Target, _clock());
        using (FileStream stream = new(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            PngBitmapEncoder encoder = new();
            encoder.Frames.Add(BitmapFrame.Create(captured.Bitmap));
            encoder.Save(stream);
        }

        FileInfo file = new(output);
        return Task.FromResult(new CaptureSnapshotResultV1(
            CaptureSnapshotResultV1.CurrentSchemaVersion,
            $"snapshot.{Guid.NewGuid():N}",
            request.Target,
            output,
            _clock(),
            captured.Bitmap.PixelWidth,
            captured.Bitmap.PixelHeight,
            file.Length,
            captured.Backend,
            captured.Warning));
    }

    private static CapturedBitmap CaptureDisplay(CaptureTargetV1 target)
    {
        if (!target.StableId.StartsWith("display:", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The selected display target is invalid.");
        }
        string deviceName = target.StableId["display:".Length..];
        Forms.Screen? screen = Forms.Screen.AllScreens.FirstOrDefault(screen =>
            string.Equals(screen.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
        if (screen is null)
        {
            throw new InvalidOperationException("The selected display is no longer connected.");
        }
        ValidateBounds(screen.Bounds.Width, screen.Bounds.Height);
        return new CapturedBitmap(
            CaptureDesktopRegion(screen.Bounds),
            CaptureSnapshotBackend.GdiDesktop,
            null);
    }

    private static CapturedBitmap CaptureWindow(CaptureTargetV1 target)
    {
        if (!target.StableId.StartsWith("window:", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The selected window target is invalid.");
        }
        if (!TryParseWindowHandle(target.StableId, out IntPtr window) || !IsWindow(window))
        {
            throw new InvalidOperationException("The selected window is no longer available.");
        }
        NativeRect bounds;
        if (!GetWindowRect(window, out bounds))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read the selected window bounds.");
        }
        ValidateBounds(bounds.Width, bounds.Height);
        return CaptureWindowImage(window, bounds);
    }

    private static CapturedBitmap CaptureWindowImage(IntPtr window, NativeRect bounds)
    {
        IntPtr desktopDc = GetDC(IntPtr.Zero);
        if (desktopDc == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open the desktop device context.");
        }

        IntPtr memoryDc = IntPtr.Zero;
        IntPtr bitmap = IntPtr.Zero;
        IntPtr previous = IntPtr.Zero;
        try
        {
            memoryDc = CreateCompatibleDC(desktopDc);
            bitmap = CreateCompatibleBitmap(desktopDc, bounds.Width, bounds.Height);
            if (memoryDc == IntPtr.Zero || bitmap == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not allocate the window capture surface.");
            }
            previous = SelectObject(memoryDc, bitmap);
            bool printed = PrintWindow(window, memoryDc, PrintWindowRenderFullContent);
            if (printed)
            {
                return new CapturedBitmap(
                    CreateBitmapSource(bitmap),
                    CaptureSnapshotBackend.PrintWindow,
                    null);
            }

            bool copied = BitBlt(
                memoryDc,
                0,
                0,
                bounds.Width,
                bounds.Height,
                desktopDc,
                bounds.Left,
                bounds.Top,
                Srccopy | CaptureBlt);
            if (!copied)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The selected window could not be captured.");
            }
            return new CapturedBitmap(
                CreateBitmapSource(bitmap),
                CaptureSnapshotBackend.PrintWindowWithDesktopFallback,
                "PrintWindow was unavailable; the visible desktop region was captured instead.");
        }
        finally
        {
            if (previous != IntPtr.Zero && memoryDc != IntPtr.Zero)
            {
                _ = SelectObject(memoryDc, previous);
            }
            if (bitmap != IntPtr.Zero)
            {
                _ = DeleteObject(bitmap);
            }
            if (memoryDc != IntPtr.Zero)
            {
                _ = DeleteDC(memoryDc);
            }
            _ = ReleaseDC(IntPtr.Zero, desktopDc);
        }
    }

    private static BitmapSource CaptureDesktopRegion(Rectangle bounds)
    {
        IntPtr desktopDc = GetDC(IntPtr.Zero);
        if (desktopDc == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open the desktop device context.");
        }

        IntPtr memoryDc = IntPtr.Zero;
        IntPtr bitmap = IntPtr.Zero;
        IntPtr previous = IntPtr.Zero;
        try
        {
            memoryDc = CreateCompatibleDC(desktopDc);
            bitmap = CreateCompatibleBitmap(desktopDc, bounds.Width, bounds.Height);
            if (memoryDc == IntPtr.Zero || bitmap == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not allocate the desktop capture surface.");
            }
            previous = SelectObject(memoryDc, bitmap);
            if (!BitBlt(
                memoryDc,
                0,
                0,
                bounds.Width,
                bounds.Height,
                desktopDc,
                bounds.Left,
                bounds.Top,
                Srccopy | CaptureBlt))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The selected display could not be captured.");
            }
            return CreateBitmapSource(bitmap);
        }
        finally
        {
            if (previous != IntPtr.Zero && memoryDc != IntPtr.Zero)
            {
                _ = SelectObject(memoryDc, previous);
            }
            if (bitmap != IntPtr.Zero)
            {
                _ = DeleteObject(bitmap);
            }
            if (memoryDc != IntPtr.Zero)
            {
                _ = DeleteDC(memoryDc);
            }
            _ = ReleaseDC(IntPtr.Zero, desktopDc);
        }
    }

    private static BitmapSource CreateBitmapSource(IntPtr bitmap)
    {
        BitmapSource source = Imaging.CreateBitmapSourceFromHBitmap(
            bitmap,
            IntPtr.Zero,
            Int32Rect.Empty,
            BitmapSizeOptions.FromEmptyOptions());
        source.Freeze();
        return source;
    }

    private static void ValidateRequest(CaptureSnapshotRequestV1 request)
    {
        if (request.SchemaVersion != CaptureSnapshotRequestV1.CurrentSchemaVersion
            || request.Target is null
            || string.IsNullOrWhiteSpace(request.Target.StableId)
            || string.IsNullOrWhiteSpace(request.Target.DisplayName))
        {
            throw new InvalidDataException("The capture target is invalid.");
        }
        if (!request.ConfirmedVisibleCapture)
        {
            throw new UnauthorizedAccessException("Desktop capture requires explicit visible-session confirmation.");
        }
    }

    private static void ValidateBounds(int width, int height)
    {
        if (width is < 1 or > MaximumDimension
            || height is < 1 or > MaximumDimension
            || (long)width * height > MaximumPixelCount)
        {
            throw new InvalidOperationException("The selected capture area exceeds the 8K still-image safety limit.");
        }
    }

    private string CreateOutputPath(CaptureTargetV1 target, DateTimeOffset capturedAt)
    {
        string targetPart = target.Kind == CaptureTargetKind.Display ? "display" : "window";
        string baseName = $"{capturedAt:yyyyMMdd-HHmmssfff}-{targetPart}";
        for (int suffix = 0; suffix < 1000; suffix++)
        {
            string fileName = suffix == 0 ? $"{baseName}.png" : $"{baseName}-{suffix}.png";
            string candidate = Path.Combine(_outputDirectory, fileName);
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
        return Path.Combine(_outputDirectory, $"{baseName}-{Guid.NewGuid():N}.png");
    }

    private static bool ShouldExposeWindow(IntPtr window)
    {
        if (!IsWindowVisible(window) || IsIconic(window))
        {
            return false;
        }
        string className = ReadWindowClass(window);
        return className is not ("Shell_TrayWnd" or "Progman" or "WorkerW");
    }

    private static string ReadWindowText(IntPtr window)
    {
        int length = Math.Min(GetWindowTextLength(window), 512);
        if (length <= 0)
        {
            return string.Empty;
        }
        char[] buffer = new char[length + 1];
        int written = GetWindowText(window, buffer, buffer.Length);
        return written <= 0 ? string.Empty : new string(buffer, 0, written).Trim();
    }

    private static string ReadWindowClass(IntPtr window)
    {
        char[] buffer = new char[256];
        int written = GetClassName(window, buffer, buffer.Length);
        return written <= 0 ? string.Empty : new string(buffer, 0, written);
    }

    private static string ReadProcessName(IntPtr window)
    {
        _ = GetWindowThreadProcessId(window, out uint processId);
        if (processId == 0 || processId == Environment.ProcessId)
        {
            return string.Empty;
        }
        try
        {
            using Process process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }

    private static string TrimDisplayLabel(string value) => value.Length <= 160 ? value : $"{value[..157]}...";

    private static bool TryParseWindowHandle(string stableId, out IntPtr handle)
    {
        handle = IntPtr.Zero;
        const string prefix = "window:0x";
        return stableId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && long.TryParse(
                stableId[prefix.Length..],
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture,
                out long value)
            && (handle = new IntPtr(value)) != IntPtr.Zero;
    }

    private sealed record CapturedBitmap(
        BitmapSource Bitmap,
        CaptureSnapshotBackend Backend,
        string? Warning);

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;

        public int Height => Bottom - Top;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr window, [Out] char[] text, int maximumCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(IntPtr window, [Out] char[] className, int maximumCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PrintWindow(IntPtr window, IntPtr deviceContext, uint flags);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteDC(IntPtr deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr deviceContext, int width, int height);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr graphicsObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool BitBlt(
        IntPtr destinationDeviceContext,
        int destinationX,
        int destinationY,
        int width,
        int height,
        IntPtr sourceDeviceContext,
        int sourceX,
        int sourceY,
        uint rasterOperation);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr graphicsObject);
}
