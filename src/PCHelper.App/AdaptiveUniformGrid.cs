using System.Windows;
using System.Windows.Controls.Primitives;

namespace PCHelper.App;

/// <summary>
/// A UniformGrid that reduces its column count when the available width cannot
/// preserve a usable item width. It keeps dashboard cards readable without
/// hard-coding layouts for one window size.
/// </summary>
public sealed class AdaptiveUniformGrid : UniformGrid
{
    public static readonly DependencyProperty MinimumItemWidthProperty = DependencyProperty.Register(
        nameof(MinimumItemWidth),
        typeof(double),
        typeof(AdaptiveUniformGrid),
        new FrameworkPropertyMetadata(220d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MaximumColumnsProperty = DependencyProperty.Register(
        nameof(MaximumColumns),
        typeof(int),
        typeof(AdaptiveUniformGrid),
        new FrameworkPropertyMetadata(4, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MinimumColumnsProperty = DependencyProperty.Register(
        nameof(MinimumColumns),
        typeof(int),
        typeof(AdaptiveUniformGrid),
        new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinimumItemWidth
    {
        get => (double)GetValue(MinimumItemWidthProperty);
        set => SetValue(MinimumItemWidthProperty, value);
    }

    public int MaximumColumns
    {
        get => (int)GetValue(MaximumColumnsProperty);
        set => SetValue(MaximumColumnsProperty, value);
    }

    public int MinimumColumns
    {
        get => (int)GetValue(MinimumColumnsProperty);
        set => SetValue(MinimumColumnsProperty, value);
    }

    protected override System.Windows.Size MeasureOverride(System.Windows.Size constraint)
    {
        if (!double.IsInfinity(constraint.Width) && constraint.Width > 0)
        {
            int desired = CalculateColumnCount(constraint.Width, MinimumItemWidth, MinimumColumns, MaximumColumns);
            if (Columns != desired)
            {
                Columns = desired;
            }
        }

        return base.MeasureOverride(constraint);
    }

    public static int CalculateColumnCount(double availableWidth, double minimumItemWidth, int minimumColumns, int maximumColumns)
    {
        int minimum = Math.Max(1, minimumColumns);
        int maximum = Math.Max(minimum, maximumColumns);
        double itemWidth = Math.Max(1, minimumItemWidth);
        return Math.Clamp((int)Math.Floor(Math.Max(0, availableWidth) / itemWidth), minimum, maximum);
    }
}
