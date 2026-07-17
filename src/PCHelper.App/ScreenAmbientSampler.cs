using System.Drawing;
using System.Drawing.Imaging;

namespace PCHelper.App;

/// <summary>
/// The screen-ambient lighting source: samples the primary display into a tiny
/// thumbnail, reduces it to a ring of edge-zone average colours, and maps that
/// ring onto any LED count. Privacy boundary: the thumbnail lives only in this
/// process's memory for the duration of one tick — it is never written to
/// disk, logged, transmitted, or exposed over IPC; the only thing that leaves
/// the process is per-LED colour values on the local OpenRGB socket. The loop
/// runs only after an explicit start action and shows a visible running status.
/// </summary>
public static class ScreenAmbientSampler
{
    /// <summary>Thumbnail size — small enough that per-tick cost is trivial.</summary>
    public const int SampleWidth = 32;
    public const int SampleHeight = 18;

    /// <summary>
    /// Zones around the screen border, clockwise from the top-left corner:
    /// 8 across the top, 4 down the right, 8 across the bottom (right to
    /// left), 4 up the left. Matches the physical layout of case/strip
    /// lighting better than a full-image average.
    /// </summary>
    public const int ZoneCount = 24;

    private const int TopZones = 8;
    private const int SideZones = 4;
    private const int BottomZones = 8;

    /// <summary>
    /// Computes the clockwise edge-zone averages from a 32-bit BGRA pixel
    /// buffer (row-major, <paramref name="width"/>×<paramref name="height"/>).
    /// Pure function so tests can drive it with synthetic frames. Colours are
    /// packed R | G&lt;&lt;8 | B&lt;&lt;16 (the OpenRGB wire order), scaled by
    /// <paramref name="brightnessPercent"/>.
    /// </summary>
    public static uint[] ComputeEdgeZones(ReadOnlySpan<byte> bgraPixels, int width, int height, int brightnessPercent)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 4);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 4);
        if (bgraPixels.Length < width * height * 4)
        {
            throw new ArgumentException("The pixel buffer is smaller than width × height × 4.", nameof(bgraPixels));
        }

        double scale = Math.Clamp(brightnessPercent, 0, 100) / 100.0;
        int edgeBand = Math.Max(1, height / 6); // sample a band along each edge, not a single pixel row
        uint[] zones = new uint[ZoneCount];
        int zone = 0;

        // Top edge, left → right.
        for (int index = 0; index < TopZones; index++, zone++)
        {
            zones[zone] = AverageRegion(bgraPixels, width,
                x0: width * index / TopZones, x1: width * (index + 1) / TopZones,
                y0: 0, y1: edgeBand, scale);
        }

        // Right edge, top → bottom.
        for (int index = 0; index < SideZones; index++, zone++)
        {
            zones[zone] = AverageRegion(bgraPixels, width,
                x0: width - edgeBand, x1: width,
                y0: height * index / SideZones, y1: height * (index + 1) / SideZones, scale);
        }

        // Bottom edge, right → left (clockwise continuation).
        for (int index = BottomZones - 1; index >= 0; index--, zone++)
        {
            zones[zone] = AverageRegion(bgraPixels, width,
                x0: width * index / BottomZones, x1: width * (index + 1) / BottomZones,
                y0: height - edgeBand, y1: height, scale);
        }

        // Left edge, bottom → top.
        for (int index = SideZones - 1; index >= 0; index--, zone++)
        {
            zones[zone] = AverageRegion(bgraPixels, width,
                x0: 0, x1: edgeBand,
                y0: height * index / SideZones, y1: height * (index + 1) / SideZones, scale);
        }

        return zones;
    }

    /// <summary>
    /// Maps the zone ring onto an arbitrary LED count: each LED takes the zone
    /// at its proportional position around the ring, so a 9-LED cooler and a
    /// 126-key keyboard both follow the same screen regions.
    /// </summary>
    public static uint[] MapZonesToLeds(uint[] zones, int ledCount)
    {
        ArgumentNullException.ThrowIfNull(zones);
        ArgumentOutOfRangeException.ThrowIfLessThan(zones.Length, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(ledCount, 1);
        uint[] frame = new uint[ledCount];
        for (int index = 0; index < ledCount; index++)
        {
            frame[index] = zones[(int)((long)index * zones.Length / ledCount)];
        }

        return frame;
    }

    /// <summary>
    /// Captures the primary screen into the fixed thumbnail and returns the
    /// BGRA pixels. GDI-based (the same technology as the existing explicit
    /// snapshots) but downscaled to 32×18 at capture time, so no full-resolution
    /// copy of the screen is retained anywhere.
    /// </summary>
    public static byte[] CapturePrimaryThumbnail()
    {
        Rectangle bounds = System.Windows.Forms.Screen.PrimaryScreen?.Bounds
            ?? throw new InvalidOperationException("No primary display is available.");
        using Bitmap full = new(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(full))
        {
            graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        }

        using Bitmap thumbnail = new(SampleWidth, SampleHeight, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(thumbnail))
        {
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            graphics.DrawImage(full, new Rectangle(0, 0, SampleWidth, SampleHeight));
        }

        BitmapData data = thumbnail.LockBits(
            new Rectangle(0, 0, SampleWidth, SampleHeight), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[] pixels = new byte[SampleWidth * SampleHeight * 4];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            return pixels;
        }
        finally
        {
            thumbnail.UnlockBits(data);
        }
    }

    private static uint AverageRegion(ReadOnlySpan<byte> bgraPixels, int width, int x0, int x1, int y0, int y1, double scale)
    {
        long red = 0, green = 0, blue = 0, count = 0;
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                int offset = ((y * width) + x) * 4;
                blue += bgraPixels[offset];
                green += bgraPixels[offset + 1];
                red += bgraPixels[offset + 2];
                count++;
            }
        }

        if (count == 0)
        {
            return 0;
        }

        uint r = (uint)Math.Round(red / (double)count * scale);
        uint g = (uint)Math.Round(green / (double)count * scale);
        uint b = (uint)Math.Round(blue / (double)count * scale);
        return r | (g << 8) | (b << 16);
    }
}
