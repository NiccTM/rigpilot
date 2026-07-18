using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PCHelper.App;

public sealed record OpenRgbController(uint Id, string Name, int LedCount);

public sealed record OpenRgbConnectionResult(
    int ProtocolVersion,
    IReadOnlyList<OpenRgbController> Controllers,
    string Message,
    IReadOnlyList<uint>? UpdatedControllerIds = null);

public sealed class OpenRgbSdkClient(
    string host = "127.0.0.1",
    int port = 6742,
    TimeSpan? timeout = null,
    TimeSpan? operationTimeout = null)
{
    private const uint RequestControllerCount = 0;
    private const uint RequestControllerData = 1;
    private const uint RequestProtocolVersion = 40;
    private const uint SetClientName = 50;
    private const uint UpdateLeds = 1050;
    private const uint SetCustomMode = 1100;
    private const uint HighestSupportedProtocol = 5;
    private const int HeaderSize = 16;
    private const int MaximumPacketBytes = 2 * 1024 * 1024;
    private readonly IPAddress _address = ValidateHost(host);
    private readonly int _port = port is > 0 and <= 65535
        ? port
        : throw new ArgumentOutOfRangeException(nameof(port));
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(2);
    private readonly TimeSpan _operationTimeout = operationTimeout ?? TimeSpan.FromSeconds(10);

    public async Task<OpenRgbConnectionResult> ProbeAsync(CancellationToken cancellationToken)
    {
        await using OpenRgbSession session = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        return new OpenRgbConnectionResult(
            session.ProtocolVersion,
            session.Controllers,
            $"Connected to OpenRGB SDK protocol {session.ProtocolVersion}; {session.Controllers.Count} controller(s) detected.");
    }

    public async Task<OpenRgbConnectionResult> SetStaticColourAsync(
        string rgbHex,
        int brightnessPercent,
        CancellationToken cancellationToken) => await SetStaticColourAsync(
            rgbHex,
            brightnessPercent,
            controllerIds: null,
            cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Writes only the controller IDs selected by the current route plan. This
    /// prevents a local OpenRGB bridge from touching an endpoint whose vendor
    /// application still owns it.
    /// </summary>
    public async Task<OpenRgbConnectionResult> SetStaticColourAsync(
        string rgbHex,
        int brightnessPercent,
        IReadOnlyCollection<uint>? controllerIds,
        CancellationToken cancellationToken)
    {
        uint colour = ParseColour(rgbHex, brightnessPercent);
        await using OpenRgbSession session = await ConnectForMutationAsync(cancellationToken).ConfigureAwait(false);
        OpenRgbController[] selected = SelectControllers(session.Controllers, controllerIds);
        using CancellationTokenSource operation = CreateOperationTimeout(cancellationToken);
        try
        {
            foreach (OpenRgbController controller in selected)
            {
                await WritePacketAsync(
                    session.Stream,
                    controller.Id,
                    SetCustomMode,
                    ReadOnlyMemory<byte>.Empty,
                    operation.Token).ConfigureAwait(false);
                byte[] payload = new byte[checked(6 + (controller.LedCount * 4))];
                BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), (uint)payload.Length);
                BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4, 2), checked((ushort)controller.LedCount));
                for (int index = 0; index < controller.LedCount; index++)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(6 + (index * 4), 4), colour);
                }

                await WritePacketAsync(
                    session.Stream,
                    controller.Id,
                    UpdateLeds,
                    payload,
                    operation.Token).ConfigureAwait(false);
            }

            return new OpenRgbConnectionResult(
                session.ProtocolVersion,
                session.Controllers,
                $"Applied {rgbHex.ToUpperInvariant()} at {brightnessPercent}% to {selected.Length} controller(s).",
                selected.Select(controller => controller.Id).ToArray());
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"OpenRGB did not finish the lighting operation within {_operationTimeout.TotalSeconds:0.#} seconds. Some selected controllers may have changed; their state is unknown.",
                exception);
        }
    }

    /// <summary>
    /// Applies a static per-LED colourway frame to every controller through the
    /// same custom-mode + UpdateLeds path as <see cref="SetStaticColourAsync"/>.
    /// The frame is deterministic and written once; no animation loop runs.
    /// </summary>
    public async Task<OpenRgbConnectionResult> SetColourwayAsync(
        string colourwayId,
        string rgbHex,
        int brightnessPercent,
        CancellationToken cancellationToken) => await SetColourwayAsync(
            colourwayId,
            rgbHex,
            brightnessPercent,
            controllerIds: null,
            cancellationToken).ConfigureAwait(false);

    public async Task<OpenRgbConnectionResult> SetColourwayAsync(
        string colourwayId,
        string rgbHex,
        int brightnessPercent,
        IReadOnlyCollection<uint>? controllerIds,
        CancellationToken cancellationToken)
    {
        uint staticColour = ParseColour(rgbHex, 100);
        (byte R, byte G, byte B) staticRgb = ((byte)staticColour, (byte)(staticColour >> 8), (byte)(staticColour >> 16));
        await using OpenRgbSession session = await ConnectForMutationAsync(cancellationToken).ConfigureAwait(false);
        OpenRgbController[] selected = SelectControllers(session.Controllers, controllerIds);
        using CancellationTokenSource operation = CreateOperationTimeout(cancellationToken);
        try
        {
            foreach (OpenRgbController controller in selected)
            {
                await WritePacketAsync(
                    session.Stream, controller.Id, SetCustomMode, ReadOnlyMemory<byte>.Empty, operation.Token).ConfigureAwait(false);
                uint[] frame = LightingColourways.Generate(colourwayId, controller.LedCount, staticRgb, brightnessPercent);
                byte[] payload = new byte[checked(6 + (controller.LedCount * 4))];
                BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), (uint)payload.Length);
                BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4, 2), checked((ushort)controller.LedCount));
                for (int index = 0; index < controller.LedCount; index++)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(6 + (index * 4), 4), frame[index]);
                }

                await WritePacketAsync(session.Stream, controller.Id, UpdateLeds, payload, operation.Token).ConfigureAwait(false);
            }

            return new OpenRgbConnectionResult(
                session.ProtocolVersion,
                session.Controllers,
                $"Applied the '{colourwayId}' colourway at {brightnessPercent}% to {selected.Length} controller(s).",
                selected.Select(controller => controller.Id).ToArray());
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"OpenRGB did not finish the lighting operation within {_operationTimeout.TotalSeconds:0.#} seconds. Some selected controllers may have changed; their state is unknown.",
                exception);
        }
    }

    private async Task<OpenRgbSession> ConnectForMutationAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"OpenRGB could not complete its local connection and inventory handshake before any lighting write. No lighting output was sent. {exception.Message}",
                exception);
        }
    }

    private CancellationTokenSource CreateOperationTimeout(CancellationToken cancellationToken)
    {
        CancellationTokenSource operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operation.CancelAfter(_operationTimeout);
        return operation;
    }

    private static OpenRgbController[] SelectControllers(
        IReadOnlyList<OpenRgbController> controllers,
        IReadOnlyCollection<uint>? controllerIds)
    {
        OpenRgbController[] writable = controllers.Where(controller => controller.LedCount > 0).ToArray();
        if (controllerIds is null)
        {
            return writable;
        }

        HashSet<uint> requested = controllerIds.ToHashSet();
        uint[] missing = requested.Except(controllers.Select(controller => controller.Id)).Order().ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"OpenRGB controller route(s) {string.Join(", ", missing)} disappeared before apply. No lighting output was sent.");
        }

        OpenRgbController[] selected = writable.Where(controller => requested.Contains(controller.Id)).ToArray();
        if (selected.Length == 0)
        {
            throw new InvalidOperationException("No selected OpenRGB controller exposes a writable LED. No lighting output was sent.");
        }

        return selected;
    }

    /// <summary>
    /// Opens a persistent session for the screen-ambient loop: one loopback
    /// connection, custom mode selected once per controller, then repeated
    /// per-LED frames without reconnecting. Disposing the session closes the
    /// connection; OpenRGB then owns its devices again.
    /// </summary>
    public async Task<OpenRgbAmbientSession> OpenAmbientSessionAsync(CancellationToken cancellationToken)
    {
        OpenRgbSession session = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (OpenRgbController controller in session.Controllers.Where(item => item.LedCount > 0))
            {
                await WritePacketAsync(
                    session.Stream, controller.Id, SetCustomMode, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new OpenRgbAmbientSession(session);
    }

    /// <summary>Persistent frame-writing session used only by the ambient loop.</summary>
    public sealed class OpenRgbAmbientSession : IAsyncDisposable
    {
        private readonly OpenRgbSession _session;

        internal OpenRgbAmbientSession(OpenRgbSession session) => _session = session;

        public IReadOnlyList<OpenRgbController> Controllers => _session.Controllers;

        /// <summary>
        /// Writes one frame to every controller. The callback supplies the
        /// per-LED colours (packed R | G&lt;&lt;8 | B&lt;&lt;16, the OpenRGB
        /// wire order) for a given LED count.
        /// </summary>
        public async Task WriteFrameAsync(Func<int, uint[]> frameForLedCount, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(frameForLedCount);
            foreach (OpenRgbController controller in _session.Controllers.Where(item => item.LedCount > 0))
            {
                uint[] frame = frameForLedCount(controller.LedCount);
                if (frame.Length < controller.LedCount)
                {
                    throw new InvalidDataException("The ambient frame is shorter than the controller's LED count.");
                }

                byte[] payload = new byte[checked(6 + (controller.LedCount * 4))];
                BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), (uint)payload.Length);
                BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4, 2), checked((ushort)controller.LedCount));
                for (int index = 0; index < controller.LedCount; index++)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(6 + (index * 4), 4), frame[index]);
                }

                await WritePacketAsync(_session.Stream, controller.Id, UpdateLeds, payload, cancellationToken).ConfigureAwait(false);
            }
        }

        public ValueTask DisposeAsync() => _session.DisposeAsync();
    }

    private async Task<OpenRgbSession> ConnectAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        TcpClient client = new(_address.AddressFamily);
        try
        {
            await client.ConnectAsync(_address, _port, timeout.Token).ConfigureAwait(false);
            NetworkStream stream = client.GetStream();
            byte[] versionRequest = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(versionRequest, HighestSupportedProtocol);
            await WritePacketAsync(stream, 0, RequestProtocolVersion, versionRequest, timeout.Token).ConfigureAwait(false);
            Packet versionResponse = await ReadPacketAsync(stream, timeout.Token).ConfigureAwait(false);
            if (versionResponse.Id != RequestProtocolVersion || versionResponse.Payload.Length != 4)
            {
                throw new InvalidDataException("OpenRGB returned an invalid protocol-version response.");
            }

            uint serverVersion = BinaryPrimitives.ReadUInt32LittleEndian(versionResponse.Payload);
            int negotiatedVersion = checked((int)Math.Min(serverVersion, HighestSupportedProtocol));
            byte[] clientName = Encoding.UTF8.GetBytes("RigPilot\0");
            await WritePacketAsync(stream, 0, SetClientName, clientName, timeout.Token).ConfigureAwait(false);
            await WritePacketAsync(stream, 0, RequestControllerCount, ReadOnlyMemory<byte>.Empty, timeout.Token).ConfigureAwait(false);
            Packet countResponse = await ReadPacketAsync(stream, timeout.Token).ConfigureAwait(false);
            if (countResponse.Id != RequestControllerCount || countResponse.Payload.Length < 4)
            {
                throw new InvalidDataException("OpenRGB returned an invalid controller-count response.");
            }

            uint count = BinaryPrimitives.ReadUInt32LittleEndian(countResponse.Payload.AsSpan(0, 4));
            if (count > 1_024)
            {
                throw new InvalidDataException("OpenRGB reported an unreasonable controller count.");
            }

            uint[] controllerIds = new uint[count];
            bool uniqueIdsPresent = countResponse.Payload.Length >= 4 + (count * 4);
            for (uint index = 0; index < count; index++)
            {
                controllerIds[index] = uniqueIdsPresent
                    ? BinaryPrimitives.ReadUInt32LittleEndian(countResponse.Payload.AsSpan(4 + checked((int)index * 4), 4))
                    : index;
            }

            List<OpenRgbController> controllers = [];
            foreach (uint controllerId in controllerIds)
            {
                byte[] protocolPayload = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(protocolPayload, (uint)negotiatedVersion);
                await WritePacketAsync(stream, controllerId, RequestControllerData, protocolPayload, timeout.Token).ConfigureAwait(false);
                Packet dataResponse = await ReadPacketAsync(stream, timeout.Token).ConfigureAwait(false);
                if (dataResponse.Id != RequestControllerData || dataResponse.DeviceId != controllerId)
                {
                    throw new InvalidDataException("OpenRGB returned controller data for the wrong request.");
                }

                controllers.Add(ParseController(controllerId, dataResponse.Payload, negotiatedVersion));
            }

            return new OpenRgbSession(client, stream, negotiatedVersion, controllers);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static OpenRgbController ParseController(uint id, byte[] payload, int protocolVersion)
    {
        PacketReader reader = new(payload);
        uint dataSize = reader.ReadUInt32();
        if (dataSize > payload.Length || dataSize < 4)
        {
            throw new InvalidDataException("OpenRGB controller data has an invalid size prefix.");
        }

        _ = reader.ReadInt32();
        string name = reader.ReadString();
        if (protocolVersion >= 1)
        {
            _ = reader.ReadString();
        }

        _ = reader.ReadString();
        _ = reader.ReadString();
        _ = reader.ReadString();
        _ = reader.ReadString();
        ushort modeCount = reader.ReadUInt16();
        _ = reader.ReadInt32();
        for (int index = 0; index < modeCount; index++)
        {
            SkipMode(ref reader, protocolVersion);
        }

        ushort zoneCount = reader.ReadUInt16();
        for (int index = 0; index < zoneCount; index++)
        {
            SkipZone(ref reader, protocolVersion);
        }

        ushort ledCount = reader.ReadUInt16();
        for (int index = 0; index < ledCount; index++)
        {
            _ = reader.ReadString();
            if (protocolVersion <= 5)
            {
                _ = reader.ReadUInt32();
            }
        }

        return new OpenRgbController(id, string.IsNullOrWhiteSpace(name) ? $"Controller {id}" : name, ledCount);
    }

    private static void SkipMode(ref PacketReader reader, int protocolVersion)
    {
        _ = reader.ReadString();
        if (protocolVersion <= 5)
        {
            _ = reader.ReadInt32();
        }

        _ = reader.ReadUInt32();
        _ = reader.ReadUInt32();
        _ = reader.ReadUInt32();
        if (protocolVersion >= 3)
        {
            _ = reader.ReadUInt32();
            _ = reader.ReadUInt32();
        }

        _ = reader.ReadUInt32();
        _ = reader.ReadUInt32();
        _ = reader.ReadUInt32();
        if (protocolVersion >= 3)
        {
            _ = reader.ReadUInt32();
        }

        _ = reader.ReadUInt32();
        _ = reader.ReadUInt32();
        ushort colourCount = reader.ReadUInt16();
        reader.Skip(checked(colourCount * 4));
    }

    private static void SkipZone(ref PacketReader reader, int protocolVersion)
    {
        _ = reader.ReadString();
        _ = reader.ReadInt32();
        _ = reader.ReadUInt32();
        _ = reader.ReadUInt32();
        _ = reader.ReadUInt32();
        ushort matrixLength = reader.ReadUInt16();
        reader.Skip(matrixLength);
        if (protocolVersion >= 4)
        {
            ushort segmentCount = reader.ReadUInt16();
            for (int index = 0; index < segmentCount; index++)
            {
                _ = reader.ReadString();
                _ = reader.ReadInt32();
                _ = reader.ReadUInt32();
                _ = reader.ReadUInt32();
            }
        }

        if (protocolVersion >= 5)
        {
            _ = reader.ReadUInt32();
        }
    }

    private static async Task WritePacketAsync(
        NetworkStream stream,
        uint deviceId,
        uint packetId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        if (payload.Length > MaximumPacketBytes - HeaderSize)
        {
            throw new InvalidDataException("OpenRGB packet exceeds the RigPilot size limit.");
        }

        byte[] header = new byte[HeaderSize];
        "ORGB"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), deviceId);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8, 4), packetId);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12, 4), (uint)payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (!payload.IsEmpty)
        {
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<Packet> ReadPacketAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        byte[] header = new byte[HeaderSize];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        if (!header.AsSpan(0, 4).SequenceEqual("ORGB"u8))
        {
            throw new InvalidDataException("OpenRGB packet magic is invalid.");
        }

        uint deviceId = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
        uint packetId = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4));
        uint payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12, 4));
        if (payloadLength > MaximumPacketBytes - HeaderSize)
        {
            throw new InvalidDataException("OpenRGB response exceeds the RigPilot size limit.");
        }

        byte[] payload = new byte[payloadLength];
        if (payload.Length > 0)
        {
            await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        }

        return new Packet(deviceId, packetId, payload);
    }

    private static uint ParseColour(string rgbHex, int brightnessPercent)
    {
        if (brightnessPercent is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(brightnessPercent), "Brightness must be between 0 and 100%.");
        }

        string value = rgbHex.Trim().TrimStart('#');
        if (value.Length != 6 || !uint.TryParse(value, System.Globalization.NumberStyles.HexNumber, null, out uint rgb))
        {
            throw new ArgumentException("Colour must use #RRGGBB format.", nameof(rgbHex));
        }

        byte red = (byte)(rgb >> 16);
        byte green = (byte)(rgb >> 8);
        byte blue = (byte)rgb;
        double scale = brightnessPercent / 100d;
        uint scaledRed = (uint)Math.Round(red * scale);
        uint scaledGreen = (uint)Math.Round(green * scale);
        uint scaledBlue = (uint)Math.Round(blue * scale);
        return scaledRed | (scaledGreen << 8) | (scaledBlue << 16);
    }

    private static IPAddress ValidateHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return IPAddress.Loopback;
        }

        if (IPAddress.TryParse(host, out IPAddress? address) && IPAddress.IsLoopback(address))
        {
            return address;
        }

        throw new ArgumentException("RigPilot version 1 permits OpenRGB connections only to the local machine.", nameof(host));
    }

    private sealed record Packet(uint DeviceId, uint Id, byte[] Payload);

    internal sealed class OpenRgbSession(
        TcpClient client,
        NetworkStream stream,
        int protocolVersion,
        IReadOnlyList<OpenRgbController> controllers) : IAsyncDisposable
    {
        public NetworkStream Stream { get; } = stream;

        public int ProtocolVersion { get; } = protocolVersion;

        public IReadOnlyList<OpenRgbController> Controllers { get; } = controllers;

        public ValueTask DisposeAsync()
        {
            Stream.Dispose();
            client.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private ref struct PacketReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _offset;

        public ushort ReadUInt16()
        {
            EnsureAvailable(2);
            ushort value = BinaryPrimitives.ReadUInt16LittleEndian(_data.Slice(_offset, 2));
            _offset += 2;
            return value;
        }

        public uint ReadUInt32()
        {
            EnsureAvailable(4);
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(_offset, 4));
            _offset += 4;
            return value;
        }

        public int ReadInt32() => unchecked((int)ReadUInt32());

        public string ReadString()
        {
            ushort length = ReadUInt16();
            if (length == 0 || length > 16_384)
            {
                throw new InvalidDataException("OpenRGB returned an invalid string length.");
            }

            EnsureAvailable(length);
            ReadOnlySpan<byte> bytes = _data.Slice(_offset, length);
            _offset += length;
            int contentLength = bytes[^1] == 0 ? length - 1 : length;
            return Encoding.UTF8.GetString(bytes[..contentLength]);
        }

        public void Skip(int length)
        {
            EnsureAvailable(length);
            _offset += length;
        }

        private readonly void EnsureAvailable(int length)
        {
            if (length < 0 || _offset > _data.Length - length)
            {
                throw new InvalidDataException("OpenRGB controller data is truncated.");
            }
        }
    }
}
