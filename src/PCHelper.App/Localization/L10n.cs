using System.Globalization;
using System.Resources;
using System.Windows.Markup;

namespace PCHelper.App.Localization;

/// <summary>
/// The suite's string-resource pipeline. Strings live in
/// Localization/Strings.resx (neutral English) with per-culture satellites
/// (Strings.de.resx, …); .NET resource fallback returns English for any
/// culture or key that has no translation yet, so partially translated
/// languages are always safe to ship. Culture comes from the OS by default and
/// can be overridden with the app's --culture argument (see
/// docs/localization.md for the extraction workflow).
/// </summary>
public static class L10n
{
    private static readonly ResourceManager Resources = new(
        "PCHelper.App.Localization.Strings",
        typeof(L10n).Assembly);

    /// <summary>Optional per-process culture override set once at startup.</summary>
    public static CultureInfo? CultureOverride { get; set; }

    /// <summary>
    /// Resolves a string for the active UI culture. A missing key returns the
    /// bracketed key itself instead of throwing, so an incomplete extraction
    /// shows exactly which resource is absent rather than crashing the UI.
    /// </summary>
    public static string Get(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return Resources.GetString(key, CultureOverride ?? CultureInfo.CurrentUICulture) ?? $"[{key}]";
    }

    /// <summary>
    /// Applies a culture name (e.g. "de", "fr-FR") process-wide so XAML,
    /// bindings, and code-behind all resolve the same language. Invalid names
    /// are ignored rather than crashing startup.
    /// </summary>
    public static void ApplyCulture(string? cultureName)
    {
        if (string.IsNullOrWhiteSpace(cultureName))
        {
            return;
        }

        try
        {
            CultureInfo culture = CultureInfo.GetCultureInfo(cultureName);
            CultureOverride = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            Thread.CurrentThread.CurrentUICulture = culture;
        }
        catch (CultureNotFoundException)
        {
            // An unknown culture falls back to the OS language.
        }
    }
}

/// <summary>
/// XAML markup extension for localized strings:
/// <c>Text="{loc:Loc Portable_DataSourceLabel}"</c>. Resolution happens once
/// at load time for the active culture.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension(string key) : MarkupExtension
{
    public string Key { get; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) => L10n.Get(Key);
}
