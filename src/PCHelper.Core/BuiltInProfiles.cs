using PCHelper.Contracts;

namespace PCHelper.Core;

public static class BuiltInProfiles
{
    public static IReadOnlyList<ProfileV1> Create() =>
    [
        Create("quiet", "Quiet", "Lower-noise stock-safe policy."),
        Create("balanced", "Balanced", "Default stock-safe policy."),
        Create("performance", "Performance", "Higher responsiveness without above-stock tuning.")
    ];

    private static ProfileV1 Create(string id, string name, string description) => new(
        ProfileV1.CurrentSchemaVersion,
        id,
        name,
        description,
        [],
        new SafetyLimits(),
        [],
        IsBuiltIn: true,
        IsExperimental: false);
}
