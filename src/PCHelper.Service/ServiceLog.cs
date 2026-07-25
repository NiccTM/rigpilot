using PCHelper.Contracts;

namespace PCHelper.Service;

internal static partial class ServiceLog
{
    [LoggerMessage(EventId = 1000, Level = LogLevel.Information, Message = "RigPilot service initialised at {Time}.")]
    public static partial void Initialised(ILogger logger, DateTimeOffset time);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Information, Message = "RigPilot service is stopping; active operations will be cancelled and restored before Adapter Host shutdown.")]
    public static partial void Stopping(ILogger logger);

    [LoggerMessage(EventId = 2000, Level = LogLevel.Warning, Message = "A sensor refresh failed; the service will retry.")]
    public static partial void RefreshFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2001, Level = LogLevel.Warning, Message = "IPC command {Command} failed.")]
    public static partial void CommandFailed(ILogger logger, IpcCommand command, Exception exception);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Warning, Message = "Recovering pending profile transaction {TransactionId}; it will not be reapplied.")]
    public static partial void RecoveringTransaction(ILogger logger, string transactionId);

    [LoggerMessage(EventId = 2003, Level = LogLevel.Warning, Message = "ProgramData was not writable; using development data directory {Directory}.")]
    public static partial void UsingFallbackDirectory(ILogger logger, string directory);

    [LoggerMessage(EventId = 2004, Level = LogLevel.Warning, Message = "The active cooling graph was disabled and returned to firmware/default control.")]
    public static partial void CoolingGraphDeactivated(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2005, Level = LogLevel.Critical, Message = "Cooling graph {GraphId} entered emergency recovery: {Reason}. Recovery details: {RecoveryDetails}")]
    public static partial void CoolingGraphEmergency(ILogger logger, string graphId, string reason, string? recoveryDetails);

    [LoggerMessage(EventId = 2006, Level = LogLevel.Error, Message = "RigPilot could not complete verified default-state recovery during shutdown; the running marker remains unclean.")]
    public static partial void ShutdownRecoveryFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2007, Level = LogLevel.Warning, Message = "Hardware-control state was verified, but the cached capability descriptors could not be refreshed immediately.")]
    public static partial void HardwareControlSnapshotRefreshFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2008, Level = LogLevel.Warning, Message = "GPU fan channel {Channel} could not be returned to the driver curve during disarm; the fan may remain under manual control.")]
    public static partial void GpuFanDisarmRestoreFailed(ILogger logger, string channel, Exception exception);

    [LoggerMessage(EventId = 2010, Level = LogLevel.Information, Message = "An operator cleared the hardware write lock after every leased control re-proved its default state.")]
    public static partial void HardwareRecoveryCleared(ILogger logger);

    [LoggerMessage(EventId = 2009, Level = LogLevel.Warning, Message = "Hardware-control family {Family} failed default-state reset and read-back: {Detail}")]
    public static partial void HardwareControlFamilyResetFailed(ILogger logger, string family, string detail);

    [LoggerMessage(EventId = 2020, Level = LogLevel.Warning, Message = "GPU OC startup recovery: {Message}")]
    public static partial void GpuOcStartupRecovered(ILogger logger, string message);

    [LoggerMessage(EventId = 2021, Level = LogLevel.Information, Message = "GPU OC startup: {Message}")]
    public static partial void GpuOcStartupReapplied(ILogger logger, string message);

    [LoggerMessage(EventId = 2022, Level = LogLevel.Warning, Message = "GPU OC startup: {Message}")]
    public static partial void GpuOcStartupDisabled(ILogger logger, string message);

    [LoggerMessage(EventId = 2023, Level = LogLevel.Warning, Message = "Failed to reset {Capability} to stock during GPU OC recovery.")]
    public static partial void GpuOcStockRestoreFailed(ILogger logger, string capability, Exception exception);

    [LoggerMessage(EventId = 2024, Level = LogLevel.Error, Message = "GPU OC startup reapply failed.")]
    public static partial void GpuOcStartupReapplyFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 3000, Level = LogLevel.Critical, Message = "RigPilot service stopped unexpectedly.")]
    public static partial void ServiceFailed(ILogger logger, Exception exception);
}
