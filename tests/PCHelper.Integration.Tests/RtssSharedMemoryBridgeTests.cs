using System.IO;
using System.IO.MemoryMappedFiles;
using System.Security.Principal;
using System.Text;
using PCHelper.App;
using PCHelper.Contracts;
using PCHelper.Ipc;

namespace PCHelper.Integration.Tests;

/// <summary>
/// Exercises the RTSS shared-memory bridge against a faithful in-memory copy of
/// the documented v2 layout: header (signature, version, entry sizes, array
/// offsets), 8 OSD slots of 256+256+4096 bytes, and app entries carrying frame
/// statistics. No RTSS installation is required or touched.
/// </summary>
public sealed class RtssSharedMemoryBridgeTests : IDisposable
{
    private const int OsdEntrySize = 256 + 256 + 4096;
    private const int OsdSlots = 8;
    private const int AppEntrySize = 284;
    private const int AppSlots = 4;
    private const int HeaderSize = 64;
    private const int OsdArrOffset = HeaderSize;
    private const int AppArrOffset = OsdArrOffset + OsdSlots * OsdEntrySize;
    private const int Capacity = AppArrOffset + AppSlots * AppEntrySize;

    private readonly string _mapName = $"pchelper-test-rtss-{Guid.NewGuid():N}";
    private readonly MemoryMappedFile _map;

    public RtssSharedMemoryBridgeTests()
    {
        _map = MemoryMappedFile.CreateNew(_mapName, Capacity);
        WriteHeader(version: 0x0002000E);
    }

    public void Dispose() => _map.Dispose();

    [Fact]
    public void PublishClaimsAnEmptySlotUpdatesInPlaceAndReleasesCleanly()
    {
        using RtssSharedMemoryBridge bridge = new([_mapName]);

        RtssOsdBridgeStatusV1 published = bridge.Publish("RigPilot: CPU 62.5C  GPU 71C");
        RtssOsdBridgeStatusV1 updated = bridge.Publish("RigPilot: CPU 63C  GPU 72C");
        string textAfterUpdate = ReadOsdText(published.SlotIndex);
        string ownerAfterUpdate = ReadOsdOwner(published.SlotIndex);
        uint frameCounter = ReadOsdFrame();
        RtssOsdBridgeStatusV1 released = bridge.Release();

        Assert.True(published.Publishing);
        Assert.True(published.AbiValidated);
        Assert.Equal("2.14", published.AbiVersion);
        Assert.True(published.SlotIndex >= 1);
        Assert.Equal(published.SlotIndex, updated.SlotIndex);
        Assert.Equal("RigPilot: CPU 63C  GPU 72C", textAfterUpdate);
        Assert.Equal("RigPilot", ownerAfterUpdate);
        Assert.Equal(2u, frameCounter);
        Assert.False(released.Publishing);
        Assert.Equal(string.Empty, ReadOsdOwner(published.SlotIndex));
        Assert.Equal(string.Empty, ReadOsdText(published.SlotIndex));
    }

    [Fact]
    public void PublishNeverTouchesSlotsOwnedByOtherApplicationsIncludingSlotZero()
    {
        WriteOsdSlot(0, "RTSS", "60 FPS");
        WriteOsdSlot(1, "HWiNFO", "CPU 90C");
        using RtssSharedMemoryBridge bridge = new([_mapName]);

        RtssOsdBridgeStatusV1 published = bridge.Publish("RigPilot line");

        Assert.True(published.Publishing);
        Assert.Equal(2, published.SlotIndex);
        Assert.Equal("HWiNFO", ReadOsdOwner(1));
        Assert.Equal("CPU 90C", ReadOsdText(1));
        Assert.Equal("RTSS", ReadOsdOwner(0));
    }

    [Fact]
    public void PublishRefusesWhenEverySlotIsForeignOwned()
    {
        for (int slot = 0; slot < OsdSlots; slot++)
        {
            WriteOsdSlot(slot, $"Other{slot}", "text");
        }
        using RtssSharedMemoryBridge bridge = new([_mapName]);

        RtssOsdBridgeStatusV1 status = bridge.Publish("RigPilot line");

        Assert.False(status.Publishing);
        Assert.Contains("owned by another application", status.Message, StringComparison.OrdinalIgnoreCase);
        for (int slot = 0; slot < OsdSlots; slot++)
        {
            Assert.Equal($"Other{slot}", ReadOsdOwner(slot));
        }
    }

    [Theory]
    [InlineData(0x53535452u, 0x0002000Eu)] // wrong signature
    [InlineData(0x52545353u, 0x00030000u)] // unsupported major version
    public void PublishRefusesToTouchAnUnrecognisedAbi(uint signature, uint version)
    {
        WriteHeader(version, signature);
        using RtssSharedMemoryBridge bridge = new([_mapName]);

        RtssOsdBridgeStatusV1 status = bridge.Publish("RigPilot line");

        Assert.False(status.Publishing);
        Assert.False(status.AbiValidated);
        Assert.True(status.SharedMemoryDetected);
        Assert.Equal(0u, ReadOsdFrame());
        Assert.Equal(string.Empty, ReadOsdOwner(1));
    }

    [Fact]
    public void PublishRefusesAHeaderWhoseBoundsEscapeTheMapping()
    {
        using (MemoryMappedViewAccessor accessor = _map.CreateViewAccessor())
        {
            accessor.Write(24, (uint)Capacity - 100); // dwOSDArrOffset points near the end
        }
        using RtssSharedMemoryBridge bridge = new([_mapName]);

        RtssOsdBridgeStatusV1 status = bridge.Publish("RigPilot line");

        Assert.False(status.Publishing);
        Assert.False(status.AbiValidated);
        Assert.Contains("outside its own mapping", status.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadFrameStatsReportsFpsAndFrameTimeFromAppEntries()
    {
        WriteAppEntry(0, processId: 4242, name: @"C:\Games\game.exe", time0: 1000, time1: 2000, frames: 120, frameTimeMicroseconds: 8333);
        using RtssSharedMemoryBridge bridge = new([_mapName]);

        RtssFrameStatsV1 stats = bridge.ReadFrameStats();

        Assert.True(stats.SharedMemoryDetected);
        RtssAppFrameStatsV1 app = Assert.Single(stats.Applications);
        Assert.Equal(4242, app.ProcessId);
        Assert.Equal(@"C:\Games\game.exe", app.ProcessName);
        Assert.Equal(120.0, app.FramesPerSecond);
        Assert.Equal(8.33, app.FrameTimeMilliseconds);
    }

    [Fact]
    public void BridgeFailsSafeWhenNoMappingExists()
    {
        using RtssSharedMemoryBridge bridge = new([$"pchelper-test-absent-{Guid.NewGuid():N}"]);

        RtssOsdBridgeStatusV1 status = bridge.Publish("RigPilot line");
        RtssFrameStatsV1 stats = bridge.ReadFrameStats();
        RtssOsdBridgeStatusV1 released = bridge.Release();

        Assert.False(status.SharedMemoryDetected);
        Assert.False(status.Publishing);
        Assert.False(stats.SharedMemoryDetected);
        Assert.Empty(stats.Applications);
        Assert.False(released.Publishing);
    }

    [Fact]
    public void SanitisationStripsControlCharactersAndBoundsTheLine()
    {
        string sanitised = RtssSharedMemoryBridge.SanitiseOsdText("CPU\t62C\r\nGPU 71C" + new string('x', 400));

        Assert.StartsWith("CPU62C\nGPU 71C", sanitised, StringComparison.Ordinal);
        Assert.Equal(RtssOsdPublishRequestV1.MaximumTextLength, sanitised.Length);
        Assert.DoesNotContain('\t', sanitised);
        Assert.DoesNotContain('\r', sanitised);
    }

    [Fact]
    public async Task UserAgentRequiresExplicitConfirmationBeforePublishing()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"pchelper-user-agent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using UserAgentRuntime runtime = new(directory, rtssOsdBridge: new RtssSharedMemoryBridge([_mapName]));
            await runtime.InitializeAsync(CancellationToken.None);
            IpcClientContext current = new(false, WindowsIdentity.GetCurrent().Name);

            IpcResponse unconfirmed = await runtime.HandleRequestAsync(
                NamedPipeRequestClient.CreateRequest(
                    IpcCommand.PublishRtssOsdText,
                    new RtssOsdPublishRequestV1(RtssOsdPublishRequestV1.CurrentSchemaVersion, "RigPilot line", ConfirmedThirdPartyOsdWrite: false)),
                current,
                CancellationToken.None);
            IpcResponse confirmed = await runtime.HandleRequestAsync(
                NamedPipeRequestClient.CreateRequest(
                    IpcCommand.PublishRtssOsdText,
                    new RtssOsdPublishRequestV1(RtssOsdPublishRequestV1.CurrentSchemaVersion, "RigPilot line", ConfirmedThirdPartyOsdWrite: true)),
                current,
                CancellationToken.None);
            IpcResponse released = await runtime.HandleRequestAsync(
                NamedPipeRequestClient.CreateRequest(IpcCommand.ReleaseRtssOsd),
                current,
                CancellationToken.None);

            Assert.False(unconfirmed.Success);
            Assert.Equal("RTSS_OSD_CONFIRMATION_REQUIRED", unconfirmed.ErrorCode);
            Assert.Equal(string.Empty, ReadOsdOwner(1));

            Assert.True(confirmed.Success);
            RtssOsdBridgeStatusV1 status = IpcJson.FromElement<RtssOsdBridgeStatusV1>(confirmed.Payload)!;
            Assert.True(status.Publishing);

            Assert.True(released.Success);
            Assert.False(IpcJson.FromElement<RtssOsdBridgeStatusV1>(released.Payload)!.Publishing);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private void WriteHeader(uint version, uint signature = 0x52545353)
    {
        using MemoryMappedViewAccessor accessor = _map.CreateViewAccessor();
        accessor.Write(0, signature);
        accessor.Write(4, version);
        accessor.Write(8, (uint)AppEntrySize);
        accessor.Write(12, (uint)AppArrOffset);
        accessor.Write(16, (uint)AppSlots);
        accessor.Write(20, (uint)OsdEntrySize);
        accessor.Write(24, (uint)OsdArrOffset);
        accessor.Write(28, (uint)OsdSlots);
        accessor.Write(32, 0u); // dwOSDFrame
    }

    private void WriteOsdSlot(int slot, string owner, string text)
    {
        using MemoryMappedViewAccessor accessor = _map.CreateViewAccessor();
        long entry = OsdArrOffset + (long)slot * OsdEntrySize;
        WriteFixed(accessor, entry, 256, text);
        WriteFixed(accessor, entry + 256, 256, owner);
    }

    private void WriteAppEntry(int slot, uint processId, string name, uint time0, uint time1, uint frames, uint frameTimeMicroseconds)
    {
        using MemoryMappedViewAccessor accessor = _map.CreateViewAccessor();
        long entry = AppArrOffset + (long)slot * AppEntrySize;
        accessor.Write(entry, processId);
        WriteFixed(accessor, entry + 4, 260, name);
        accessor.Write(entry + 268, time0);
        accessor.Write(entry + 272, time1);
        accessor.Write(entry + 276, frames);
        accessor.Write(entry + 280, frameTimeMicroseconds);
    }

    private string ReadOsdText(int slot) => ReadFixed(OsdArrOffset + (long)slot * OsdEntrySize, 256);

    private string ReadOsdOwner(int slot) => ReadFixed(OsdArrOffset + (long)slot * OsdEntrySize + 256, 256);

    private uint ReadOsdFrame()
    {
        using MemoryMappedViewAccessor accessor = _map.CreateViewAccessor();
        return accessor.ReadUInt32(32);
    }

    private string ReadFixed(long offset, int length)
    {
        using MemoryMappedViewAccessor accessor = _map.CreateViewAccessor();
        byte[] buffer = new byte[length];
        accessor.ReadArray(offset, buffer, 0, length);
        int terminator = Array.IndexOf(buffer, (byte)0);
        return Encoding.ASCII.GetString(buffer, 0, terminator < 0 ? length : terminator);
    }

    private static void WriteFixed(MemoryMappedViewAccessor accessor, long offset, int length, string value)
    {
        byte[] buffer = new byte[length];
        Encoding.ASCII.GetBytes(value, 0, Math.Min(value.Length, length - 1), buffer, 0);
        accessor.WriteArray(offset, buffer, 0, length);
    }
}
