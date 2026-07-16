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
    public void ApplyCultureSetsTheOverrideAndIgnoresUnknownNames()
    {
        L10n.ApplyCulture("de");
        Assert.Equal("de", L10n.CultureOverride!.Name);

        L10n.ApplyCulture("not-a-culture-name-!!");
        Assert.Equal("de", L10n.CultureOverride!.Name); // unchanged, no crash
    }
}
