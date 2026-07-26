using System.Collections.Concurrent;
using System.Globalization;
using System.Resources;
using System.Text;
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
    /// Caches parsed composite formats by their RESOLVED text rather than by resource key.
    /// A format's placeholder order is part of the translation, so caching against the key
    /// would pin whichever language happened to load first; keying on the resolved string
    /// makes a culture change produce a different entry instead of a wrong layout.
    /// </summary>
    private static readonly ConcurrentDictionary<string, CompositeFormat> Formats = new(StringComparer.Ordinal);

    /// <summary>
    /// Resolves a format string and fills it. Arguments are substituted by index, so a
    /// translation is free to reorder them — which is the whole reason a composed string is
    /// one resource with placeholders rather than several concatenated fragments.
    /// </summary>
    public static string Format(string key, params object?[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        CompositeFormat format = Formats.GetOrAdd(Get(key), CompositeFormat.Parse);
        return string.Format(CultureInfo.CurrentCulture, format, arguments);
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

/// <summary>
/// XAML markup extension for a "page name plus keyboard shortcut" tooltip:
/// <c>ToolTip="{loc:LocShortcut Nav_Overview, 1}"</c>.
///
/// This composes rather than storing nine finished tooltip strings, because those would
/// each repeat a page name that is already a resource — and two copies of one word are two
/// chances for a translator to render it differently, leaving a menu item and its tooltip
/// disagreeing about what the page is called.
///
/// The modifier is a resource for the same reason it is not a symbol: German Windows labels
/// that key "Strg". The key pressed is unchanged; only its printed name differs, so showing
/// "Ctrl" to a German user names a key their keyboard does not have.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class LocShortcutExtension(string key, string digit) : MarkupExtension
{
    public string Key { get; } = key;

    public string Digit { get; } = digit;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        L10n.Format("Nav_ShortcutTooltipFormat", L10n.Get(Key), L10n.Get("Shell_ModifierCtrl"), Digit);
}
