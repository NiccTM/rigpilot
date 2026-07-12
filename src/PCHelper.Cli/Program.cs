using System.Text.Json;
using PCHelper.Adapters;
using PCHelper.Contracts;
using PCHelper.Core;
using PCHelper.Ipc;

return await Cli.RunAsync(args);

internal static class Cli
{
    public static async Task<int> RunAsync(string[] args)
    {
        string command = args.FirstOrDefault()?.ToLowerInvariant() ?? "help";
        bool json = args.Contains("--json", StringComparer.OrdinalIgnoreCase);
        try
        {
            return command switch
            {
                "probe" => await ProbeAsync(json),
                "status" => await ServiceCommandAsync<ServiceStatus>(IpcCommand.GetServiceStatus, json),
                "profiles" => await ServiceCommandAsync<IReadOnlyList<ProfileV1>>(IpcCommand.GetProfiles, json),
                "report" => await ExportReportAsync(args, json),
                "help" or "--help" or "-h" => PrintHelp(),
                _ => Unknown(command)
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Operation cancelled.");
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"PC Helper CLI failed: {exception.Message}");
            return 1;
        }
    }

    private static async Task<int> ProbeAsync(bool json)
    {
        HardwareSnapshot snapshot;
        try
        {
            snapshot = await SendAsync<HardwareSnapshot>(IpcCommand.GetInventory);
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            await using AdapterCoordinator coordinator = new(
            [
                new SystemInventoryAdapter(),
                new WindowsPowerAdapter(),
                new LibreHardwareMonitorAdapter()
            ]);
            snapshot = await coordinator.CaptureAsync(CancellationToken.None);
        }

        Write(snapshot, json, PrintSnapshot);
        return snapshot.AdapterHealth.Any(health => !health.Healthy) ? 3 : 0;
    }

    private static async Task<int> ServiceCommandAsync<T>(IpcCommand command, bool json)
    {
        T payload = await SendAsync<T>(command);
        Write(payload, json, value => Console.WriteLine(value));
        return 0;
    }

    private static async Task<int> ExportReportAsync(string[] args, bool json)
    {
        CompatibilityReportV1 report;
        try
        {
            report = await SendAsync<CompatibilityReportV1>(IpcCommand.ExportReport);
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            await using AdapterCoordinator coordinator = new(
            [
                new SystemInventoryAdapter(),
                new WindowsPowerAdapter(),
                new LibreHardwareMonitorAdapter()
            ]);
            HardwareSnapshot snapshot = await coordinator.CaptureAsync(CancellationToken.None);
            report = CompatibilityReportBuilder.Build(
                snapshot,
                "0.2.0",
                new Dictionary<string, string>
                {
                    ["framework"] = Environment.Version.ToString(),
                    ["osVersion"] = Environment.OSVersion.VersionString
                },
                [],
                userApproved: false);
        }

        int outputIndex = Array.FindIndex(args, item => string.Equals(item, "--output", StringComparison.OrdinalIgnoreCase));
        if (outputIndex >= 0 && outputIndex + 1 < args.Length)
        {
            string path = Path.GetFullPath(args[outputIndex + 1]);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, JsonDefaults.Options));
            Console.WriteLine($"Redacted report preview written to {path}. It has not been uploaded or approved.");
        }
        else
        {
            Write(report, json: true, _ => { });
        }

        return 0;
    }

    private static async Task<T> SendAsync<T>(IpcCommand command)
    {
        NamedPipeRequestClient client = new(ProtocolConstants.ServicePipeName);
        IpcRequest request = NamedPipeRequestClient.CreateRequest(command);
        IpcResponse response = await client.SendAsync(request, CancellationToken.None);
        if (!response.Success)
        {
            throw new InvalidOperationException($"{response.ErrorCode}: {response.Error}");
        }

        return IpcJson.FromElement<T>(response.Payload)
            ?? throw new InvalidDataException("Service returned an empty payload.");
    }

    private static void Write<T>(T value, bool json, Action<T> humanWriter)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(value, JsonDefaults.Options));
        }
        else
        {
            humanWriter(value);
        }
    }

    private static void PrintSnapshot(HardwareSnapshot snapshot)
    {
        Console.WriteLine($"PC Helper read-only probe at {snapshot.CapturedAt:O}");
        Console.WriteLine($"Devices: {snapshot.Devices.Count}; sensors: {snapshot.Sensors.Count}; capabilities: {snapshot.Capabilities.Count}");
        foreach (HardwareDevice device in snapshot.Devices)
        {
            Console.WriteLine($"  [{device.Kind}] {device.Name}");
        }

        Console.WriteLine("Adapters:");
        foreach (AdapterHealth health in snapshot.AdapterHealth)
        {
            Console.WriteLine($"  {(health.Healthy ? "OK" : "DEGRADED")} {health.AdapterId}: {health.Message}");
        }

        foreach (DiagnosticWarning warning in snapshot.Warnings)
        {
            Console.WriteLine($"  {warning.Severity} {warning.Code}: {warning.Message}");
        }

        ConflictDescriptor[] running = snapshot.Conflicts.Where(conflict => conflict.IsRunning).ToArray();
        if (running.Length > 0)
        {
            Console.WriteLine("Running controllers:");
            foreach (ConflictDescriptor conflict in running)
            {
                Console.WriteLine($"  {conflict.DisplayName}: {string.Join(", ", conflict.ResourceFamilies)}");
            }
        }
    }

    private static int PrintHelp()
    {
        Console.WriteLine("""
            PC Helper CLI 0.2

            pchelper-cli probe [--json]             Run a read-only hardware and conflict probe.
            pchelper-cli status [--json]            Read service status.
            pchelper-cli profiles [--json]          List stored profiles.
            pchelper-cli report [--output FILE]     Generate a redacted, unapproved report preview.
            """);
        return 0;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'. Use 'help'.");
        return 64;
    }
}
