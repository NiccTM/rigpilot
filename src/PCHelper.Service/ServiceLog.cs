using PCHelper.Contracts;

namespace PCHelper.Service;

internal static partial class ServiceLog
{
    [LoggerMessage(EventId = 1000, Level = LogLevel.Information, Message = "PC Helper service initialised at {Time}.")]
    public static partial void Initialised(ILogger logger, DateTimeOffset time);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Information, Message = "PC Helper service is stopping; no unqualified hardware controls are owned.")]
    public static partial void Stopping(ILogger logger);

    [LoggerMessage(EventId = 2000, Level = LogLevel.Warning, Message = "A sensor refresh failed; the service will retry.")]
    public static partial void RefreshFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2001, Level = LogLevel.Warning, Message = "IPC command {Command} failed.")]
    public static partial void CommandFailed(ILogger logger, IpcCommand command, Exception exception);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Warning, Message = "Recovering pending profile transaction {TransactionId}; it will not be reapplied.")]
    public static partial void RecoveringTransaction(ILogger logger, string transactionId);

    [LoggerMessage(EventId = 2003, Level = LogLevel.Warning, Message = "ProgramData was not writable; using development data directory {Directory}.")]
    public static partial void UsingFallbackDirectory(ILogger logger, string directory);

    [LoggerMessage(EventId = 3000, Level = LogLevel.Critical, Message = "PC Helper service stopped unexpectedly.")]
    public static partial void ServiceFailed(ILogger logger, Exception exception);
}
