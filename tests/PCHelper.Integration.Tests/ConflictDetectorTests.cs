using PCHelper.Adapters;
using PCHelper.Contracts;

namespace PCHelper.Integration.Tests;

public sealed class ConflictDetectorTests
{
    private static ConflictDescriptor Find(IReadOnlyList<ConflictDescriptor> all, string id) =>
        all.Single(descriptor => descriptor.Id == id);

    [Fact]
    public void DetectsNzxtCamByServiceInstallPathWhenProcessNameIsGeneric()
    {
        // NZXT CAM's background service runs as a generically-named service.exe under the
        // "NZXT CAM" install folder, so a name-only match misses it and the Kraken route
        // silently loses to CAM's continuous re-driving.
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase) { "service", "explorer" };
        string[] paths =
        [
            @"C:\Program Files\NZXT CAM\resources\app.asar.unpacked\node_modules\@nzxt\cam-core\dist\service.exe",
            @"C:\Windows\explorer.exe",
        ];

        IReadOnlyList<ConflictDescriptor> detected = ConflictDetector.DetectFrom(names, paths);

        Assert.True(Find(detected, "nzxt-cam").IsRunning);
    }

    [Fact]
    public void StillDetectsByProcessName()
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase) { "MSIAfterburner" };
        IReadOnlyList<ConflictDescriptor> detected = ConflictDetector.DetectFrom(names, []);

        Assert.True(Find(detected, "afterburner").IsRunning);
        Assert.False(Find(detected, "nzxt-cam").IsRunning);
    }

    [Fact]
    public void ReportsNothingRunningWhenNeitherNameNorPathMatches()
    {
        IReadOnlyList<ConflictDescriptor> detected = ConflictDetector.DetectFrom(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            [@"C:\Windows\System32\svchost.exe"]);

        Assert.All(detected, descriptor => Assert.False(descriptor.IsRunning));
    }
}
