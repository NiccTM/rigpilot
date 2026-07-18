using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using PCHelper.App;

namespace PCHelper.Integration.Tests;

public sealed class OpenRgbSdkClientTests
{
    [Fact]
    public async Task NegotiatesEnumeratesAndWritesScaledStaticColour()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task<uint[]> server = RunFakeServerAsync(listener);
        OpenRgbSdkClient client = new(port: port, timeout: TimeSpan.FromSeconds(5));

        OpenRgbConnectionResult result = await client.SetStaticColourAsync(
            "#FF0000",
            brightnessPercent: 50,
            CancellationToken.None);
        uint[] colours = await server;

        Assert.Equal(5, result.ProtocolVersion);
        OpenRgbController controller = Assert.Single(result.Controllers);
        Assert.Equal("Test Controller", controller.Name);
        Assert.Equal(2, controller.LedCount);
        Assert.Equal([0x00000080u, 0x00000080u], colours);
    }

    [Fact]
    public void RejectsNonLoopbackServer()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => new OpenRgbSdkClient("192.0.2.10"));

        Assert.Contains("local machine", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SelectiveApplyWritesOnlyTheReadyControllerRoute()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task<uint> server = RunSelectiveFakeServerAsync(listener);
        OpenRgbSdkClient client = new(port: port, timeout: TimeSpan.FromSeconds(5));

        OpenRgbConnectionResult result = await client.SetStaticColourAsync(
            "#00FF00",
            brightnessPercent: 100,
            controllerIds: [1],
            CancellationToken.None);
        uint updatedDeviceId = await server;

        Assert.Equal(1u, updatedDeviceId);
        Assert.Equal([1u], result.UpdatedControllerIds);
        Assert.Equal(2, result.Controllers.Count);
    }

    private static async Task<uint[]> RunFakeServerAsync(TcpListener listener)
    {
        using (listener)
        using (TcpClient client = await listener.AcceptTcpClientAsync())
        {
            NetworkStream stream = client.GetStream();
            Packet version = await ReadPacketAsync(stream);
            Assert.Equal(40u, version.Id);
            Assert.Equal(5u, BinaryPrimitives.ReadUInt32LittleEndian(version.Payload));
            await WritePacketAsync(stream, 0, 40, UInt32Payload(5));

            Packet name = await ReadPacketAsync(stream);
            Assert.Equal(50u, name.Id);
        Assert.Equal("RigPilot\0", Encoding.UTF8.GetString(name.Payload));

            Packet count = await ReadPacketAsync(stream);
            Assert.Equal(0u, count.Id);
            await WritePacketAsync(stream, 0, 0, UInt32Payload(1));

            Packet data = await ReadPacketAsync(stream);
            Assert.Equal(1u, data.Id);
            await WritePacketAsync(stream, 0, 1, ControllerPayload());

            Packet custom = await ReadPacketAsync(stream);
            Assert.Equal(1100u, custom.Id);
            Assert.Empty(custom.Payload);

            Packet update = await ReadPacketAsync(stream);
            Assert.Equal(1050u, update.Id);
            Assert.Equal((uint)update.Payload.Length, BinaryPrimitives.ReadUInt32LittleEndian(update.Payload.AsSpan(0, 4)));
            ushort countValue = BinaryPrimitives.ReadUInt16LittleEndian(update.Payload.AsSpan(4, 2));
            uint[] colours = new uint[countValue];
            for (int index = 0; index < colours.Length; index++)
            {
                colours[index] = BinaryPrimitives.ReadUInt32LittleEndian(update.Payload.AsSpan(6 + (index * 4), 4));
            }

            return colours;
        }
    }

    private static async Task<uint> RunSelectiveFakeServerAsync(TcpListener listener)
    {
        using (listener)
        using (TcpClient client = await listener.AcceptTcpClientAsync())
        {
            NetworkStream stream = client.GetStream();
            _ = await ReadPacketAsync(stream);
            await WritePacketAsync(stream, 0, 40, UInt32Payload(5));
            _ = await ReadPacketAsync(stream);
            _ = await ReadPacketAsync(stream);
            await WritePacketAsync(stream, 0, 0, UInt32Payload(2));

            Packet firstData = await ReadPacketAsync(stream);
            Assert.Equal(0u, firstData.Device);
            await WritePacketAsync(stream, 0, 1, ControllerPayload("Blocked Kraken", ledCount: 2));
            Packet secondData = await ReadPacketAsync(stream);
            Assert.Equal(1u, secondData.Device);
            await WritePacketAsync(stream, 1, 1, ControllerPayload("Ready Aura", ledCount: 3));

            Packet custom = await ReadPacketAsync(stream);
            Assert.Equal(1u, custom.Device);
            Assert.Equal(1100u, custom.Id);
            Packet update = await ReadPacketAsync(stream);
            Assert.Equal(1u, update.Device);
            Assert.Equal(1050u, update.Id);
            return update.Device;
        }
    }

    private static byte[] ControllerPayload() => ControllerPayload("Test Controller", ledCount: 2);

    private static byte[] ControllerPayload(string name, int ledCount)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(0u);
        writer.Write(0);
        WriteString(writer, name);
        WriteString(writer, "PC Helper Tests");
        WriteString(writer, "Fake controller");
        WriteString(writer, "1.0");
        WriteString(writer, "redacted");
        WriteString(writer, "local");
        writer.Write((ushort)0);
        writer.Write(0);
        writer.Write((ushort)0);
        writer.Write(checked((ushort)ledCount));
        for (int index = 0; index < ledCount; index++)
        {
            WriteString(writer, $"LED {index + 1}");
            writer.Write((uint)index);
        }
        writer.Flush();
        byte[] payload = stream.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), (uint)payload.Length);
        return payload;
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value + "\0");
        writer.Write(checked((ushort)bytes.Length));
        writer.Write(bytes);
    }

    private static byte[] UInt32Payload(uint value)
    {
        byte[] payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, value);
        return payload;
    }

    private static async Task<Packet> ReadPacketAsync(NetworkStream stream)
    {
        byte[] header = new byte[16];
        await stream.ReadExactlyAsync(header);
        Assert.True(header.AsSpan(0, 4).SequenceEqual("ORGB"u8));
        uint device = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
        uint id = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4));
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12, 4));
        byte[] payload = new byte[size];
        if (payload.Length > 0)
        {
            await stream.ReadExactlyAsync(payload);
        }

        return new Packet(device, id, payload);
    }

    private static async Task WritePacketAsync(NetworkStream stream, uint device, uint id, byte[] payload)
    {
        byte[] header = new byte[16];
        "ORGB"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), device);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8, 4), id);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12, 4), (uint)payload.Length);
        await stream.WriteAsync(header);
        if (payload.Length > 0)
        {
            await stream.WriteAsync(payload);
        }
    }

    private sealed record Packet(uint Device, uint Id, byte[] Payload);
}
