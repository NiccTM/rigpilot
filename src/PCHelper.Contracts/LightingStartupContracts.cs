namespace PCHelper.Contracts;

/// <summary>
/// A saved static lighting colour to re-drive at every service start.
///
/// <para>RGB controllers hold their colour in volatile lighting registers, so a power cycle
/// leaves them at whatever their firmware defaults to. This records the colour the operator
/// last applied and the exact routes it was applied through, so the service can put it back
/// before anyone signs in.</para>
///
/// <para>Only a static colour is stored. Animated effect graphs are rendered by the signed-in
/// user agent and cannot run at the login screen, so persisting one here would promise
/// something this path cannot deliver.</para>
/// </summary>
public sealed record LightingStartupProfileV1(
    int SchemaVersion,
    string Colour,
    DateTimeOffset SavedAt,
    IReadOnlyList<string> RouteIds)
{
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// Enables or clears the saved startup lighting. Enabling writes the colour to every named
/// route first: a colour that cannot be driven now is never saved for an unattended boot.
/// </summary>
public sealed record SetLightingStartupPersistenceRequest(
    bool Enable,
    string Colour,
    IReadOnlyList<string> RouteIds);

public sealed record LightingStartupPersistenceStatus(
    bool Enabled,
    string Colour,
    int RouteCount,
    string Message);
