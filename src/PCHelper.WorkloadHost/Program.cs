using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using PCHelper.Contracts;
using PCHelper.Ipc;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;

namespace PCHelper.WorkloadHost;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (!OperatingSystem.IsWindows() || WindowsIdentity.GetCurrent().IsSystem)
            {
                throw new InvalidOperationException("The workload host must run in the signed-in user's Windows session.");
            }

            HostArguments parsed = HostArguments.Parse(args);
            using CancellationTokenSource shutdown = new();
            using GpuWorkload workload = new(parsed.VendorId, parsed.AdapterIndex);
            Task parentMonitor = MonitorParentAsync(parsed.ParentProcessId, shutdown);
            Task workloadLoop = workload.RunAsync(shutdown.Token);
            await ServeAsync(parsed, workload, shutdown).ConfigureAwait(false);
            shutdown.Cancel();
            await Task.WhenAll(IgnoreCancellation(parentMonitor), IgnoreCancellation(workloadLoop)).ConfigureAwait(false);
            return workload.Fault is null ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static async Task ServeAsync(HostArguments args, GpuWorkload workload, CancellationTokenSource shutdown)
    {
        while (!shutdown.IsCancellationRequested)
        {
            await using NamedPipeServerStream server = CreateServer(args.PipeName);
            await server.WaitForConnectionAsync(shutdown.Token).ConfigureAwait(false);
            WorkloadHostRequestV1 request = await PipeFraming
                .ReadAsync<WorkloadHostRequestV1>(server, shutdown.Token)
                .ConfigureAwait(false);
            bool authenticated = request.SchemaVersion == WorkloadHostRequestV1.CurrentSchemaVersion
                && string.Equals(request.SessionId, args.SessionId, StringComparison.Ordinal)
                && FixedTimeEquals(request.AuthenticationToken, args.AuthenticationToken);
            if (authenticated)
            {
                switch (request.Command)
                {
                    case WorkloadHostCommand.SetMode:
                        if (request.Mode == AutoOcWorkloadMode.Stopped)
                        {
                            throw new InvalidDataException("SetMode requires a running workload mode.");
                        }
                        workload.SetMode(request.Mode);
                        break;
                    case WorkloadHostCommand.Stop:
                        workload.SetMode(AutoOcWorkloadMode.Stopped);
                        break;
                    case WorkloadHostCommand.Ping:
                        break;
                    default:
                        throw new InvalidDataException("Unknown workload-host command.");
                }
            }

            await PipeFraming.WriteAsync(server, workload.Status(args.SessionId, authenticated), CancellationToken.None).ConfigureAwait(false);
            if (authenticated && request.Command == WorkloadHostCommand.Stop)
            {
                shutdown.Cancel();
            }
        }
    }

    private static NamedPipeServerStream CreateServer(string pipeName)
    {
        PipeSecurity security = new();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        SecurityIdentifier localSystem = new(WellKnownSidType.LocalSystemSid, null);
        security.AddAccessRule(new PipeAccessRule(localSystem, PipeAccessRights.FullControl, AccessControlType.Allow));
        SecurityIdentifier currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The signed-in workload-host identity has no Windows SID.");
        security.AddAccessRule(new PipeAccessRule(currentUser, PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
            64 * 1024,
            64 * 1024,
            security);
    }

    private static bool FixedTimeEquals(string candidate, string expected)
    {
        byte[] left = Encoding.UTF8.GetBytes(candidate);
        byte[] right = Encoding.UTF8.GetBytes(expected);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }

    private static async Task MonitorParentAsync(int parentProcessId, CancellationTokenSource shutdown)
    {
        try
        {
            using Process parent = Process.GetProcessById(parentProcessId);
            await parent.WaitForExitAsync(shutdown.Token).ConfigureAwait(false);
            shutdown.Cancel();
        }
        catch (ArgumentException)
        {
            shutdown.Cancel();
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
        }
    }

    private static async Task IgnoreCancellation(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    private sealed record HostArguments(
        string SessionId,
        string PipeName,
        string AuthenticationToken,
        int VendorId,
        uint AdapterIndex,
        int ParentProcessId)
    {
        public static HostArguments Parse(string[] args)
        {
            string Read(string key)
            {
                int index = Array.FindIndex(args, item => string.Equals(item, key, StringComparison.OrdinalIgnoreCase));
                return index >= 0 && index + 1 < args.Length
                    ? args[index + 1]
                    : throw new ArgumentException($"Missing {key}.");
            }

            string sessionId = Read("--session");
            string pipe = Read("--pipe");
            string token = Read("--token");
            if (sessionId.Length is < 16 or > 128
                || pipe.Length is < 16 or > 128
                || token.Length is < 32 or > 256
                || !pipe.StartsWith("pchelper.workload.", StringComparison.Ordinal))
            {
                throw new ArgumentException("The workload-host session arguments are invalid.");
            }

            return new HostArguments(
                sessionId,
                pipe,
                token,
                Convert.ToInt32(Read("--vendor"), 16),
                uint.Parse(Read("--adapter-index"), System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(Read("--parent"), System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private sealed class GpuWorkload : IDisposable
    {
        private const long Mebibyte = 1024L * 1024L;
        private readonly IDXGIFactory2 _factory;
        private readonly IDXGIAdapter1 _adapter;
        private readonly ID3D11Device _device;
        private readonly ID3D11DeviceContext _context;
        private readonly ID3D11Buffer _buffer;
        private readonly ID3D11UnorderedAccessView _view;
        private readonly ID3D11ComputeShader _coreShader;
        private readonly ID3D11ComputeShader _memoryShader;
        private readonly ID3D11Query _completionQuery;
        private readonly uint _memoryGroupsX;
        private readonly uint _memoryGroupsY;
        private readonly object _stateGate = new();
        private AutoOcWorkloadMode _mode;
        private DateTimeOffset _heartbeat = DateTimeOffset.UtcNow;
        private long _dispatchCount;
        private string? _fault;

        public GpuWorkload(int vendorId, uint requestedAdapterIndex)
        {
            _factory = CreateDXGIFactory1<IDXGIFactory2>();
            List<(IDXGIAdapter1 Adapter, AdapterDescription1 Description, uint MatchIndex)> matches = [];
            uint physicalIndex = 0;
            for (uint index = 0; _factory.EnumAdapters1(index, out IDXGIAdapter1? candidate).Success; index++)
            {
                if (candidate is null) continue;
                AdapterDescription1 description = candidate.Description1;
                if ((description.Flags & AdapterFlags.Software) != 0 || description.VendorId != vendorId)
                {
                    candidate.Dispose();
                    continue;
                }

                matches.Add((candidate, description, physicalIndex++));
            }

            MatchingHardwareAdapterCount = matches.Count;
            (IDXGIAdapter1 Adapter, AdapterDescription1 Description, uint MatchIndex) selected = matches
                .FirstOrDefault(item => item.MatchIndex == requestedAdapterIndex);
            if (selected.Adapter is null || matches.Count != 1)
            {
                foreach ((IDXGIAdapter1 adapter, _, _) in matches) adapter.Dispose();
                throw new InvalidOperationException($"Auto OC requires one unambiguous vendor 0x{vendorId:X4} hardware adapter; found {matches.Count}.");
            }

            _adapter = selected.Adapter;
            AdapterDescription = selected.Description.Description;
            VendorId = checked((int)selected.Description.VendorId);
            DeviceId = checked((int)selected.Description.DeviceId);
            AdapterIndex = selected.MatchIndex;
            AdapterLuid = selected.Description.Luid;

            FeatureLevel[] featureLevels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];
            D3D11CreateDevice(
                _adapter,
                DriverType.Unknown,
                DeviceCreationFlags.None,
                featureLevels,
                out _device,
                out _,
                out _context).CheckError();

            long dedicatedBytes = checked((long)selected.Description.DedicatedVideoMemory.Value.ToUInt64());
            int bufferBytes = checked((int)Math.Clamp(dedicatedBytes / 20L, 64L * Mebibyte, 512L * Mebibyte));
            int elementCount = bufferBytes / 16;
            BufferDescription bufferDescription = new(
                (uint)bufferBytes,
                BindFlags.UnorderedAccess | BindFlags.ShaderResource,
                ResourceUsage.Default,
                CpuAccessFlags.None,
                ResourceOptionFlags.BufferStructured,
                16);
            _buffer = _device.CreateBuffer(bufferDescription);
            _view = _device.CreateUnorderedAccessView(_buffer);

            string shaderPath = Path.Combine(AppContext.BaseDirectory, "Workload.hlsl");
            ReadOnlyMemory<byte> coreBytecode = Compiler.CompileFromFile(shaderPath, "CoreMain", "cs_5_0");
            ReadOnlyMemory<byte> memoryBytecode = Compiler.CompileFromFile(shaderPath, "MemoryMain", "cs_5_0");
            _coreShader = _device.CreateComputeShader(coreBytecode.Span);
            _memoryShader = _device.CreateComputeShader(memoryBytecode.Span);
            _completionQuery = _device.CreateQuery(new QueryDescription(QueryType.Event, QueryFlags.None));
            _memoryGroupsX = (uint)Math.Min(65535, Math.Ceiling(elementCount / 256d));
            _memoryGroupsY = (uint)Math.Ceiling(elementCount / (65535d * 256d));
        }

        public string AdapterDescription { get; }
        public int VendorId { get; }
        public int DeviceId { get; }
        public long AdapterLuid { get; }
        public uint AdapterIndex { get; }
        public int MatchingHardwareAdapterCount { get; }
        public string? Fault => Volatile.Read(ref _fault);

        public void SetMode(AutoOcWorkloadMode mode)
        {
            lock (_stateGate)
            {
                _mode = mode;
                _heartbeat = DateTimeOffset.UtcNow;
            }
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    AutoOcWorkloadMode mode;
                    lock (_stateGate) mode = _mode;
                    if (mode == AutoOcWorkloadMode.Stopped)
                    {
                        await Task.Delay(20, cancellationToken).ConfigureAwait(false);
                        lock (_stateGate) _heartbeat = DateTimeOffset.UtcNow;
                        continue;
                    }

                    switch (mode)
                    {
                        case AutoOcWorkloadMode.Core:
                            Dispatch(_coreShader, 4096, 1);
                            break;
                        case AutoOcWorkloadMode.Memory:
                            Dispatch(_memoryShader, _memoryGroupsX, _memoryGroupsY);
                            break;
                        case AutoOcWorkloadMode.Combined:
                            Dispatch(_coreShader, 4096, 1);
                            Dispatch(_memoryShader, _memoryGroupsX, _memoryGroupsY);
                            break;
                    }

                    _context.End(_completionQuery);
                    _context.Flush();
                    await WaitForGpuCompletionAsync(cancellationToken).ConfigureAwait(false);
                    lock (_stateGate) _heartbeat = DateTimeOffset.UtcNow;
                    await Task.Delay(1, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                Volatile.Write(ref _fault, exception.Message);
                lock (_stateGate)
                {
                    _mode = AutoOcWorkloadMode.Stopped;
                    _heartbeat = DateTimeOffset.UtcNow;
                }
            }
        }

        private void Dispatch(ID3D11ComputeShader shader, uint groupsX, uint groupsY)
        {
            _context.CSSetShader(shader);
            _context.CSSetUnorderedAccessView(0, _view);
            _context.Dispatch(groupsX, groupsY, 1);
            Interlocked.Increment(ref _dispatchCount);
        }

        private async Task WaitForGpuCompletionAsync(CancellationToken cancellationToken)
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
            while (!_context.IsDataAvailable(_completionQuery, AsyncGetDataFlags.None))
            {
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    throw new TimeoutException("The Direct3D workload did not complete within 5 seconds.");
                }

                await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            }
        }

        public WorkloadHostStatusV1 Status(string sessionId, bool authenticated)
        {
            AutoOcWorkloadMode mode;
            DateTimeOffset heartbeat;
            lock (_stateGate)
            {
                mode = _mode;
                heartbeat = _heartbeat;
            }
            string? fault = Fault;
            return new WorkloadHostStatusV1(
                WorkloadHostStatusV1.CurrentSchemaVersion,
                sessionId,
                authenticated,
                Ready: fault is null,
                Running: fault is null && mode != AutoOcWorkloadMode.Stopped,
                mode,
                AdapterDescription,
                VendorId,
                DeviceId,
                AdapterLuid,
                AdapterIndex,
                MatchingHardwareAdapterCount,
                Interlocked.Read(ref _dispatchCount),
                heartbeat,
                fault);
        }

        public void Dispose()
        {
            _context.CSSetUnorderedAccessView(0, null);
            _context.CSSetShader(null);
            _context.ClearState();
            _context.Flush();
            _completionQuery.Dispose();
            _memoryShader.Dispose();
            _coreShader.Dispose();
            _view.Dispose();
            _buffer.Dispose();
            _context.Dispose();
            _device.Dispose();
            _adapter.Dispose();
            _factory.Dispose();
        }
    }
}
