using System.Globalization;
using PCHelper.App.Localization;

namespace PCHelper.Integration.Tests;

public sealed class LocalizationTests : IDisposable
{
    public LocalizationTests() => L10n.CultureOverride = CultureInfo.GetCultureInfo("en-US");

    public void Dispose() => L10n.CultureOverride = CultureInfo.GetCultureInfo("en-US");

    [Fact]
    public void NeutralEnglishResolvesEveryExtractedPortableString()
    {
        Assert.StartsWith("Portable mode:", L10n.Get("Portable_ServiceStatus"), StringComparison.Ordinal);
        Assert.Contains("read-only by design", L10n.Get("Portable_SafetySummary"), StringComparison.Ordinal);
        Assert.Equal("Portable (read-only)", L10n.Get("Portable_DataSourceLabel"));
    }

    [Fact]
    public void GermanSatelliteResolvesTranslationsForTheSameKeys()
    {
        L10n.CultureOverride = CultureInfo.GetCultureInfo("de-DE");

        Assert.StartsWith("Portabler Modus:", L10n.Get("Portable_ServiceStatus"), StringComparison.Ordinal);
        Assert.Equal("Portabel (schreibgeschützt)", L10n.Get("Portable_DataSourceLabel"));
    }

    [Fact]
    public void UntranslatedCulturesFallBackToEnglishAndMissingKeysAreVisibleNotFatal()
    {
        L10n.CultureOverride = CultureInfo.GetCultureInfo("ja-JP"); // no satellite yet
        Assert.Equal("Portable (read-only)", L10n.Get("Portable_DataSourceLabel"));

        Assert.Equal("[Not_A_Real_Key]", L10n.Get("Not_A_Real_Key"));
    }

    [Fact]
    public void NavigationTitlesResolveInNeutralEnglishAndGerman()
    {
        string[] keys =
        [
            "Nav_Overview", "Nav_Profiles", "Nav_Cooling", "Nav_Performance", "Nav_Lighting",
            "Nav_Automation", "Nav_GamesTools", "Nav_Devices", "Nav_Diagnostics",
        ];

        foreach (string key in keys)
        {
            Assert.DoesNotContain('[', L10n.Get(key)); // every key extracted, none missing
        }
        Assert.Equal("Games & tools", L10n.Get("Nav_GamesTools"));

        L10n.CultureOverride = CultureInfo.GetCultureInfo("de-DE");
        Assert.Equal("Übersicht", L10n.Get("Nav_Overview"));
        Assert.Equal("Spiele & Tools", L10n.Get("Nav_GamesTools"));
    }

    [Fact]
    public void AShortcutTooltipComposesTheTranslatedPageNameWithTheTranslatedModifier()
    {
        // The nav tooltip is composed rather than stored whole so a page name exists once.
        // That only pays off if BOTH halves follow the culture: an English "Ctrl" beside a
        // German page name would name a key a German keyboard does not have ("Strg").
        Assert.Equal("Overview (Ctrl+1)", (string)new LocShortcutExtension("Nav_Overview", "1").ProvideValue(null!));

        L10n.CultureOverride = CultureInfo.GetCultureInfo("de-DE");
        Assert.Equal("Übersicht (Strg+1)", (string)new LocShortcutExtension("Nav_Overview", "1").ProvideValue(null!));
    }

    [Fact]
    public void AFormatCachedUnderOneCultureDoesNotLeakIntoAnother()
    {
        // Composite formats are cached, and caching them by KEY would pin whichever language
        // loaded first — every later culture would then render the first one's word order.
        Assert.Equal("Overview (Ctrl+1)", L10n.Format("Nav_ShortcutTooltipFormat", "Overview", "Ctrl", "1"));

        L10n.CultureOverride = CultureInfo.GetCultureInfo("de-DE");
        Assert.Equal("Übersicht (Strg+2)", L10n.Format(
            "Nav_ShortcutTooltipFormat",
            L10n.Get("Nav_Overview"),
            L10n.Get("Shell_ModifierCtrl"),
            "2"));
    }

    [Fact]
    public void ApplyCultureSetsTheOverrideAndIgnoresUnknownNames()
    {
        L10n.ApplyCulture("de");
        Assert.Equal("de", L10n.CultureOverride!.Name);

        L10n.ApplyCulture("not-a-culture-name-!!");
        Assert.Equal("de", L10n.CultureOverride!.Name); // unchanged, no crash
    }
}
