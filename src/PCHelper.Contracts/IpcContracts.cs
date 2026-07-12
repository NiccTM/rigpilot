using System.Text.Json;
using System.Text.Json.Serialization;

namespace PCHelper.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<IpcCommand>))]
public enum IpcCommand
{
    Handshake,
    GetInventory,
    SubscribeSensors,
    GetProfiles,
    ValidateProfile,
    ApplyProfile,
    ResetHardware,
    StartCalibration,
    StartTune,
    AbortOperation,
    ExportReport,
    GetServiceStatus
}

public static class ProtocolConstants
{
    public const int Version = 1;
    public const string ServicePipeName = "pchelper.service.v1";
    public const string AdapterHostPipeName = "pchelper.adapterhost.v1";
    public const int MaximumMessageBytes = 2 * 1024 * 1024;
}

public sealed record IpcRequest(
    int ProtocolVersion,
    string RequestId,
    IpcCommand Command,
    long? ExpectedStateRevision,
    string? IdempotencyKey,
    JsonElement? Payload);

public sealed record IpcResponse(
    int ProtocolVersion,
    string RequestId,
    bool Success,
    long StateRevision,
    string? ErrorCode,
    string? Error,
    JsonElement? Payload);

public sealed record ServiceStatus(
    string Version,
    DateTimeOffset StartedAt,
    long StateRevision,
    string? ActiveProfileId,
    bool WritesEnabled,
    bool EmergencyMode,
    string Message);

public sealed record ProfileValidationResult(
    bool Valid,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> SkippedOptionalActions);

public sealed record ApplyProfileRequest(ProfileV1 Profile, bool ConfirmExperimental);

public sealed record ApplyProfileResult(ProfileTransaction Transaction, string? ActiveProfileId);

public sealed record HandshakeRequest(string ClientName, string ClientVersion);

public sealed record HandshakeResponse(int ProtocolVersion, string ServiceVersion, long StateRevision);

public sealed record CompatibilityReportV1(
    int SchemaVersion,
    string ReportId,
    DateTimeOffset CreatedAt,
    string AppVersion,
    HardwareSnapshot Snapshot,
    IReadOnlyDictionary<string, string> Runtime,
    IReadOnlyList<string> SanitisedLogLines,
    bool UserApproved);
