using System.IO;
using System.Text.Json;

namespace PCHelper.App;

/// <summary>
/// Per-user persistence for the one-time first-run tour. The flag lives beside
/// the other dashboard preferences under LocalApplicationData\RigPilot; it
/// records only that the tour was completed (and when), never any hardware or
/// identity data. A missing or unreadable file simply means the tour shows
/// again — failures are never surfaced as errors.
/// </summary>
public static class OnboardingState
{
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RigPilot",
        "onboarding-state.json");

    private sealed record PersistedState(bool TourCompleted, DateTimeOffset CompletedAt);

    public static bool IsTourCompleted(string? path = null)
    {
        try
        {
            string statePath = path ?? DefaultPath;
            if (!File.Exists(statePath))
            {
                return false;
            }

            return JsonSerializer.Deserialize<PersistedState>(File.ReadAllText(statePath))?.TourCompleted == true;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static void MarkTourCompleted(string? path = null)
    {
        try
        {
            string statePath = path ?? DefaultPath;
            Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
            File.WriteAllText(statePath, JsonSerializer.Serialize(new PersistedState(true, DateTimeOffset.Now)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A failed write only means the tour shows once more next launch.
        }
    }
}
