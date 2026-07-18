using System.Text.Json.Serialization;

namespace PCHelper.Contracts;

public sealed record HardwareStateVerification(
    string AdapterId,
    string CapabilityId,
    bool Success,
    ControlValue? ObservedValue,
    string Message);

[JsonConverter(typeof(JsonStringEnumConverter<HardwareControlLeaseState>))]
public enum HardwareControlLeaseState
{
    Active,
    CleanShutdown,
    RecoveryRequired
}

public sealed record HardwareControlLeaseItemV1(
    string AdapterId,
    string CapabilityId);

/// <summary>
/// Durable proof boundary for hardware state owned by the service. The marker
/// is written as unclean before IPC starts and becomes clean only after every
/// leased capability has been reset and read back at its default state.
/// </summary>
public sealed record HardwareControlLeaseV1(
    int SchemaVersion,
    string Id,
    string ServiceInstanceId,
    string? ActiveProfileId,
    string? LastTransactionId,
    IReadOnlyList<HardwareControlLeaseItemV1> Controls,
    DateTimeOffset AcquiredAt,
    DateTimeOffset UpdatedAt,
    bool CleanShutdown,
    bool DefaultsVerified,
    HardwareControlLeaseState State,
    string Message)
{
    public const int CurrentSchemaVersion = 1;
    public const string DefaultId = "hardware-control";
}

public sealed record HardwareRecoveryResult(
    bool AllDefaultsVerified,
    IReadOnlyList<HardwareStateVerification> Verifications,
    IReadOnlyList<string> Errors);

/// <summary>
/// Raised when a mutating adapter-host request timed out after transmission.
/// The caller must treat the hardware outcome as unknown until a separate
/// reset and read-back proves a known state.
/// </summary>
public sealed class HardwareStateUnknownException(
    string adapterId,
    string operation,
    string message,
    Exception? innerException = null) : InvalidOperationException(message, innerException)
{
    public string AdapterId { get; } = adapterId;

    public string Operation { get; } = operation;
}
