using System.Diagnostics;
using PCHelper.Contracts;
using PCHelper.Core;
using PCHelper.Ipc;

namespace PCHelper.Service;

internal sealed class WorkloadHostController : IAutoOcWorkloadController
{
    private readonly WorkloadHostDescriptorV1 _descriptor;
    private readonly WorkloadHostClient _client;

    public WorkloadHostController(WorkloadHostDescriptorV1 descriptor)
    {
        _descriptor = descriptor;
        if (descriptor.SchemaVersion != WorkloadHostDescriptorV1.CurrentSchemaVersion
            || descriptor.VendorId != 0x10DE
            || descriptor.AdapterIndex != 0
            || descriptor.SessionId.Length is < 16 or > 128
            || descriptor.AuthenticationToken.Length is < 32 or > 256
            || !descriptor.PipeName.StartsWith("pchelper.workload.", StringComparison.Ordinal)
            || descriptor.PipeName.Length > 128)
        {
            throw new InvalidDataException("The Auto OC workload-host descriptor is invalid or targets an unsupported adapter.");
        }

        using Process process = Process.GetProcessById(descriptor.HostProcessId);
        if (!string.Equals(process.ProcessName, "PCHelper.WorkloadHost", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The declared Auto OC workload process is not PCHelper.WorkloadHost.");
        }

        _client = new WorkloadHostClient(descriptor);
    }

    public async Task<WorkloadHostStatusV1> SetModeAsync(
        AutoOcWorkloadMode mode,
        CancellationToken cancellationToken) => Validate(await _client
            .SendAsync(WorkloadHostCommand.SetMode, mode, cancellationToken)
            .ConfigureAwait(false));

    public async Task<WorkloadHostStatusV1> GetStatusAsync(CancellationToken cancellationToken) =>
        Validate(await _client
            .SendAsync(WorkloadHostCommand.Ping, AutoOcWorkloadMode.Stopped, cancellationToken)
            .ConfigureAwait(false));

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        WorkloadHostStatusV1 status = Validate(await _client
            .SendAsync(WorkloadHostCommand.Stop, AutoOcWorkloadMode.Stopped, cancellationToken)
            .ConfigureAwait(false));
        if (status.Running || status.Mode != AutoOcWorkloadMode.Stopped)
        {
            throw new InvalidOperationException("The Auto OC workload host did not acknowledge stop.");
        }
    }

    private WorkloadHostStatusV1 Validate(WorkloadHostStatusV1 status)
    {
        if (!status.Authenticated
            || !string.Equals(status.SessionId, _descriptor.SessionId, StringComparison.Ordinal)
            || status.VendorId != _descriptor.VendorId
            || status.AdapterIndex != _descriptor.AdapterIndex
            || status.MatchingHardwareAdapterCount != 1
            || status.AdapterLuid == 0)
        {
            throw new InvalidDataException("The Auto OC workload host did not prove the exact requested hardware adapter.");
        }

        return status;
    }
}
