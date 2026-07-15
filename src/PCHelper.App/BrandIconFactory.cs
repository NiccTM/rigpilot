using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace PCHelper.App;

/// <summary>
/// Produces the small-notification-area version of the RigPilot mark without
/// introducing a separate bitmap asset. The dashboard uses the matching XAML
/// DrawingImage declared in App.xaml.
/// </summary>
internal static class BrandIconFactory
{
    public static Icon CreateTrayIcon()
    {
        using Bitmap bitmap = new(32, 32);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);

        using SolidBrush background = new(Color.FromArgb(16, 30, 50));
        using SolidBrush outerRing = new(Color.FromArgb(105, 173, 255));
        using SolidBrush innerRing = new(Color.FromArgb(12, 22, 38));
        using SolidBrush arrow = new(Color.FromArgb(243, 246, 250));
        using SolidBrush status = new(Color.FromArgb(80, 214, 160));
        using GraphicsPath roundedSquare = CreateRoundedSquare();

        graphics.FillPath(background, roundedSquare);
        graphics.FillPolygon(outerRing,
        [
            new PointF(16, 2.5f), new PointF(27.5f, 9), new PointF(27.5f, 22),
            new PointF(16, 29.5f), new PointF(4.5f, 22), new PointF(4.5f, 9)
        ]);
        graphics.FillPolygon(innerRing,
        [
            new PointF(16, 5.7f), new PointF(24.5f, 10.5f), new PointF(24.5f, 20.5f),
            new PointF(16, 26.3f), new PointF(7.5f, 20.5f), new PointF(7.5f, 10.5f)
        ]);
        graphics.FillPolygon(arrow,
        [
            new PointF(16, 8.5f), new PointF(22, 15.5f), new PointF(18, 15.5f),
            new PointF(18, 23), new PointF(14, 23), new PointF(14, 15.5f), new PointF(10, 15.5f)
        ]);
        graphics.FillEllipse(status, 22.5f, 4.2f, 4.2f, 4.2f);

        IntPtr nativeHandle = bitmap.GetHicon();
        try
        {
            using Icon temporaryIcon = Icon.FromHandle(nativeHandle);
            return (Icon)temporaryIcon.Clone();
        }
        finally
        {
            _ = DestroyIcon(nativeHandle);
        }
    }

    private static GraphicsPath CreateRoundedSquare()
    {
        const float radius = 7f;
        const float diameter = radius * 2;
        GraphicsPath path = new();
        path.AddArc(0.5f, 0.5f, diameter, diameter, 180, 90);
        path.AddArc(31.5f - diameter, 0.5f, diameter, diameter, 270, 90);
        path.AddArc(31.5f - diameter, 31.5f - diameter, diameter, diameter, 0, 90);
        path.AddArc(0.5f, 31.5f - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
