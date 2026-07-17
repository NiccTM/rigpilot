using System.IO;
using System.Text.Json;
using PCHelper.Contracts;

namespace PCHelper.App;

/// <summary>The on-disk shape of a shared RigPilot profile (.rigpilot-profile.json).</summary>
public sealed record ProfileShareFileV1(
    int SchemaVersion,
    string Application,
    string ExportedByVersion,
    ProfileV2 Profile)
{
    public const int CurrentSchemaVersion = 1;
    public const string ApplicationName = "RigPilot";
}

/// <summary>
/// Export/import of a single typed V2 profile as a shareable JSON file.
/// Sharing carries only the typed hardware actions and safety limits — never
/// scripts (rule 6 keeps those out of profiles entirely) and never
/// machine-local references (cooling graphs, lighting scenes, and OSD layouts
/// are calibrated or configured per machine, so they are stripped on import).
/// Every imported profile is renamed, re-identified, and forced Experimental,
/// so applying it always requires the Experimental acknowledgement and
/// exact-device confirmation, and the transaction engine still clamps every
/// action to the local device's discovered bounds.
/// </summary>
public static class ProfileShareFile
{
    public const string FileExtension = ".rigpilot-profile.json";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static string Export(ProfileV2 profile, string exportedByVersion)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return JsonSerializer.Serialize(
            new ProfileShareFileV1(ProfileShareFileV1.CurrentSchemaVersion, ProfileShareFileV1.ApplicationName, exportedByVersion, profile),
            Options);
    }

    public static ProfileV2 Import(string json)
    {
        ProfileShareFileV1? file;
        try
        {
            file = JsonSerializer.Deserialize<ProfileShareFileV1>(json, Options);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The file is not a valid RigPilot profile: {exception.Message}");
        }

        if (file is null || file.SchemaVersion != ProfileShareFileV1.CurrentSchemaVersion)
        {
            throw new InvalidDataException("The file is not a schema-1 RigPilot profile export.");
        }

        if (!string.Equals(file.Application, ProfileShareFileV1.ApplicationName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The file was not exported by RigPilot.");
        }

        ProfileV2? profile = file.Profile;
        if (profile is null || profile.HardwareActions is null || profile.HardwareActions.Count == 0)
        {
            throw new InvalidDataException("The shared profile contains no typed hardware actions.");
        }

        if (profile.HardwareActions.Any(action => action is null
            || string.IsNullOrWhiteSpace(action.CapabilityId)
            || string.IsNullOrWhiteSpace(action.AdapterId)))
        {
            throw new InvalidDataException("The shared profile contains a malformed hardware action.");
        }

        string name = profile.Name?.Trim() is { Length: > 0 } trimmed ? trimmed : "Shared profile";
        if (!name.EndsWith("(imported)", StringComparison.OrdinalIgnoreCase))
        {
            name = $"{name} (imported)";
        }

        return profile with
        {
            SchemaVersion = ProfileV2.CurrentSchemaVersion,
            Id = $"imported-{Guid.NewGuid():N}",
            Name = name,
            // Machine-local references never travel: graphs need this machine's
            // commissioning/calibration evidence, scenes and layouts its devices.
            CoolingGraphId = null,
            LightingSceneId = null,
            OsdLayoutId = null,
            AutomationReferences = [],
            // Imported content is never built-in and always Experimental: the
            // apply path then demands the acknowledgement + exact-device
            // confirmation regardless of what the file claimed.
            IsBuiltIn = false,
            IsExperimental = true,
        };
    }
}
