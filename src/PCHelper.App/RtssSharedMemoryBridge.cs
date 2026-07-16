using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using PCHelper.Contracts;

namespace PCHelper.App;

/// <summary>
/// A user-session client for the documented RivaTuner Statistics Server shared
/// memory contract (RTSSSharedMemory.h, structure version 2.x). Reads
/// per-application frame statistics and publishes RigPilot's own single OSD
/// text slot using the ownership convention the RTSS SDK defines for
/// third-party clients: claim an empty <c>szOSDOwner</c> slot, never touch a
/// slot owned by anyone else, and bump <c>dwOSDFrame</c> after a write.
/// Every operation re-opens and re-validates the mapping (signature,
/// major-version, entry sizes, array bounds) and fails safe — RTSS absent,
/// unreadable, or carrying an unrecognised ABI yields a status message, never
/// a write.
/// </summary>
public interface IRtssOsdBridge : IDisposable
{
    RtssOsdBridgeStatusV1 Status { get; }

    RtssOsdBridgeStatusV1 Publish(string text);

    RtssOsdBridgeStatusV1 Release();

    RtssFrameStatsV1 ReadFrameStats();
}

public sealed class RtssSharedMemoryBridge : IRtssOsdBridge
{
    // 'RTSS' little-endian: the DWORD value the header stores while the memory is valid.
    internal const uint ExpectedSignature = 0x52545353;
    internal const int SupportedMajorVersion = 2;

    // Fixed offsets inside the v2 header (RTSSSharedMemory.h field order).
    private const int SignatureOffset = 0;
    private const int VersionOffset = 4;
    private const int AppEntrySizeOffset = 8;
    private const int AppArrOffsetOffset = 12;
    private const int AppArrSizeOffset = 16;
    private const int OsdEntrySizeOffset = 20;
    private const int OsdArrOffsetOffset = 24;
    private const int OsdArrSizeOffset = 28;
    private const int OsdFrameOffset = 32;

    // Fixed offsets inside a v2 OSD entry: char szOSD[256]; char szOSDOwner[256].
    private const int OsdTextBytes = 256;
    private const int OsdOwnerOffset = 256;
    private const int OsdOwnerBytes = 256;
    private const int MinimumOsdEntrySize = OsdOwnerOffset + OsdOwnerBytes;

    // Fixed offsets inside a v2 app entry:
    // DWORD dwProcessID; char szName[260]; DWORD dwFlags; DWORD dwTime0; DWORD dwTime1; DWORD dwFrames; DWORD dwFrameTime.
    private const int AppNameOffset = 4;
    private const int AppNameBytes = 260;
    private const int AppTime0Offset = 268;
    private const int AppTime1Offset = 272;
    private const int AppFramesOffset = 276;
    private const int AppFrameTimeOffset = 280;
    private const int MinimumAppEntrySize = AppFrameTimeOffset + 4;

    // Sanity ceilings: the shipped layouts use 8 OSD slots and 256 app slots.
    private const uint MaximumOsdSlots = 32;
    private const uint MaximumAppSlots = 512;
    private const int MaximumReportedApplications = 16;

    private readonly string[] _mapNames;
    private readonly string _ownerName;
    private readonly object _sync = new();
    private RtssOsdBridgeStatusV1 _status;
    private bool _publishing;

    public RtssSharedMemoryBridge(IEnumerable<string>? mapNames = null, string ownerName = "RigPilot")
    {
        _mapNames = mapNames?.ToArray() ?? ["RTSSSharedMemoryV2", @"Global\RTSSSharedMemoryV2"];
        _ownerName = ownerName;
        if (_mapNames.Length == 0 || string.IsNullOrWhiteSpace(_ownerName) || Encoding.ASCII.GetByteCount(_ownerName) >= OsdOwnerBytes)
        {
            throw new ArgumentException("An RTSS bridge needs at least one map name and a short ASCII owner name.");
        }
        _status = Idle("RTSS OSD publishing is idle.");
    }

    public RtssOsdBridgeStatusV1 Status
    {
        get { lock (_sync) { return _status; } }
    }

    public RtssOsdBridgeStatusV1 Publish(string text)
    {
        string sanitised = SanitiseOsdText(text);
        lock (_sync)
        {
            _status = WithMap(MemoryMappedFileRights.ReadWrite, (accessor, header) =>
            {
                int slot = FindSlot(accessor, header, claimIfAbsent: true);
                if (slot < 0)
                {
                    _publishing = false;
                    return new RtssOsdBridgeStatusV1(
                        RtssOsdBridgeStatusV1.CurrentSchemaVersion,
                        SharedMemoryDetected: true,
                        AbiValidated: true,
                        FormatVersion(header.Version),
                        Publishing: false,
                        SlotIndex: -1,
                        "Every RTSS OSD slot is owned by another application; RigPilot will not overwrite them.");
                }
                long entry = header.OsdArrOffset + (long)slot * header.OsdEntrySize;
                WriteFixedAnsi(accessor, entry + OsdOwnerOffset, OsdOwnerBytes, _ownerName);
                WriteFixedAnsi(accessor, entry, OsdTextBytes, sanitised);
                BumpOsdFrame(accessor);
                _publishing = true;
                return new RtssOsdBridgeStatusV1(
                    RtssOsdBridgeStatusV1.CurrentSchemaVersion,
                    SharedMemoryDetected: true,
                    AbiValidated: true,
                    FormatVersion(header.Version),
                    Publishing: true,
                    SlotIndex: slot,
                    $"Publishing a {sanitised.Length}-character sensor line to RTSS OSD slot {slot}.");
            });
            return _status;
        }
    }

    public RtssOsdBridgeStatusV1 Release()
    {
        lock (_sync)
        {
            _status = WithMap(MemoryMappedFileRights.ReadWrite, (accessor, header) =>
            {
                int slot = FindSlot(accessor, header, claimIfAbsent: false);
                if (slot >= 0)
                {
                    long entry = header.OsdArrOffset + (long)slot * header.OsdEntrySize;
                    WriteFixedAnsi(accessor, entry, OsdTextBytes, string.Empty);
                    WriteFixedAnsi(accessor, entry + OsdOwnerOffset, OsdOwnerBytes, string.Empty);
                    BumpOsdFrame(accessor);
                }
                _publishing = false;
                return Idle(slot >= 0
                    ? $"Released RTSS OSD slot {slot}; the sensor line is no longer published."
                    : "RigPilot owned no RTSS OSD slot; nothing to release.") with
                {
                    SharedMemoryDetected = true,
                    AbiValidated = true,
                    AbiVersion = FormatVersion(header.Version)
                };
            });
            // Releasing while RTSS is gone is success, not failure: nothing is published.
            if (!_status.SharedMemoryDetected || !_status.AbiValidated)
            {
                _publishing = false;
                _status = _status with { Publishing = false };
            }
            return _status;
        }
    }

    public RtssFrameStatsV1 ReadFrameStats()
    {
        RtssFrameStatsV1? stats = null;
        RtssOsdBridgeStatusV1 outcome = WithMap(MemoryMappedFileRights.Read, (accessor, header) =>
        {
            List<RtssAppFrameStatsV1> applications = [];
            for (uint index = 0; index < header.AppArrSize && applications.Count < MaximumReportedApplications; index++)
            {
                long entry = header.AppArrOffset + (long)index * header.AppEntrySize;
                uint processId = accessor.ReadUInt32(entry);
                if (processId == 0)
                {
                    continue;
                }
                uint time0 = accessor.ReadUInt32(entry + AppTime0Offset);
                uint time1 = accessor.ReadUInt32(entry + AppTime1Offset);
                uint frames = accessor.ReadUInt32(entry + AppFramesOffset);
                uint frameTimeMicroseconds = accessor.ReadUInt32(entry + AppFrameTimeOffset);
                double framesPerSecond = time1 > time0 && frames > 0
                    ? frames * 1000.0 / (time1 - time0)
                    : 0.0;
                applications.Add(new RtssAppFrameStatsV1(
                    (int)processId,
                    ReadFixedAnsi(accessor, entry + AppNameOffset, AppNameBytes),
                    Math.Round(framesPerSecond, 1),
                    Math.Round(frameTimeMicroseconds / 1000.0, 2)));
            }
            stats = new RtssFrameStatsV1(
                RtssFrameStatsV1.CurrentSchemaVersion,
                SharedMemoryDetected: true,
                applications,
                applications.Count == 0
                    ? "RTSS shared memory is valid but no monitored application is presenting frames."
                    : $"RTSS is reporting frame statistics for {applications.Count} application(s).");
            return Status;
        });
        return stats ?? new RtssFrameStatsV1(
            RtssFrameStatsV1.CurrentSchemaVersion,
            outcome.SharedMemoryDetected,
            [],
            outcome.Message);
    }

    public void Dispose()
    {
        bool publishing;
        lock (_sync) { publishing = _publishing; }
        if (publishing)
        {
            try { Release(); }
            catch (IOException) { /* RTSS disappeared first; nothing remains published. */ }
        }
    }

    private RtssOsdBridgeStatusV1 WithMap(
        MemoryMappedFileRights rights,
        Func<MemoryMappedViewAccessor, Header, RtssOsdBridgeStatusV1> action)
    {
        foreach (string mapName in _mapNames)
        {
            MemoryMappedFile map;
            try
            {
                map = MemoryMappedFile.OpenExisting(mapName, rights);
            }
            catch (FileNotFoundException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                return Idle("RTSS shared memory exists but is not accessible to the signed-in user; RigPilot will not bypass its access controls.") with { SharedMemoryDetected = true };
            }
            using (map)
            {
                MemoryMappedFileAccess access = rights == MemoryMappedFileRights.Read
                    ? MemoryMappedFileAccess.Read
                    : MemoryMappedFileAccess.ReadWrite;
                using MemoryMappedViewAccessor accessor = map.CreateViewAccessor(0, 0, access);
                if (!TryValidateHeader(accessor, out Header header, out string failure))
                {
                    return Idle(failure) with { SharedMemoryDetected = true };
                }
                return action(accessor, header);
            }
        }
        return Idle("RTSS shared memory was not found. Start RTSS to enable the OSD bridge; RigPilot does not bundle or launch it.");
    }

    internal static bool TryValidateHeader(MemoryMappedViewAccessor accessor, out Header header, out string failure)
    {
        header = default;
        long capacity = accessor.Capacity;
        if (capacity < OsdFrameOffset + 4)
        {
            failure = "The RTSS shared memory region is smaller than its own v2 header; refusing to touch it.";
            return false;
        }
        uint signature = accessor.ReadUInt32(SignatureOffset);
        if (signature != ExpectedSignature)
        {
            failure = $"The RTSS shared memory signature 0x{signature:X8} is not the documented 'RTSS' marker; refusing to touch it.";
            return false;
        }
        uint version = accessor.ReadUInt32(VersionOffset);
        if (version >> 16 != SupportedMajorVersion)
        {
            failure = $"RTSS shared memory reports ABI {FormatVersion(version)}; RigPilot only speaks the audited v2 layout and stays hands-off.";
            return false;
        }
        header = new Header(
            version,
            accessor.ReadUInt32(AppEntrySizeOffset),
            accessor.ReadUInt32(AppArrOffsetOffset),
            accessor.ReadUInt32(AppArrSizeOffset),
            accessor.ReadUInt32(OsdEntrySizeOffset),
            accessor.ReadUInt32(OsdArrOffsetOffset),
            accessor.ReadUInt32(OsdArrSizeOffset));
        bool osdBoundsValid =
            header.OsdEntrySize >= MinimumOsdEntrySize &&
            header.OsdArrSize is > 0 and <= MaximumOsdSlots &&
            header.OsdArrOffset >= OsdFrameOffset + 4 &&
            header.OsdArrOffset + (long)header.OsdArrSize * header.OsdEntrySize <= capacity;
        bool appBoundsValid =
            header.AppEntrySize >= MinimumAppEntrySize &&
            header.AppArrSize <= MaximumAppSlots &&
            header.AppArrOffset >= OsdFrameOffset + 4 &&
            header.AppArrOffset + (long)header.AppArrSize * header.AppEntrySize <= capacity;
        if (!osdBoundsValid || !appBoundsValid)
        {
            failure = "The RTSS shared memory header describes entries outside its own mapping; refusing to touch it.";
            return false;
        }
        failure = string.Empty;
        return true;
    }

    private int FindSlot(MemoryMappedViewAccessor accessor, Header header, bool claimIfAbsent)
    {
        int firstEmpty = -1;
        // Slot 0 belongs to the RTSS host itself by SDK convention; never consider it.
        for (uint index = 1; index < header.OsdArrSize; index++)
        {
            long entry = header.OsdArrOffset + (long)index * header.OsdEntrySize;
            string owner = ReadFixedAnsi(accessor, entry + OsdOwnerOffset, OsdOwnerBytes);
            if (string.Equals(owner, _ownerName, StringComparison.Ordinal))
            {
                return (int)index;
            }
            if (owner.Length == 0 && firstEmpty < 0)
            {
                firstEmpty = (int)index;
            }
        }
        return claimIfAbsent ? firstEmpty : -1;
    }

    private static void BumpOsdFrame(MemoryMappedViewAccessor accessor)
    {
        accessor.Write(OsdFrameOffset, unchecked(accessor.ReadUInt32(OsdFrameOffset) + 1));
    }

    public static string SanitiseOsdText(string text)
    {
        StringBuilder builder = new(Math.Min(text.Length, RtssOsdPublishRequestV1.MaximumTextLength));
        foreach (char character in text)
        {
            if (builder.Length >= RtssOsdPublishRequestV1.MaximumTextLength)
            {
                break;
            }
            if (character == '\n' || (character >= 0x20 && character < 0x7F))
            {
                builder.Append(character);
            }
        }
        return builder.ToString();
    }

    private static string ReadFixedAnsi(MemoryMappedViewAccessor accessor, long offset, int length)
    {
        byte[] buffer = new byte[length];
        accessor.ReadArray(offset, buffer, 0, length);
        int terminator = Array.IndexOf(buffer, (byte)0);
        return Encoding.ASCII.GetString(buffer, 0, terminator < 0 ? length : terminator);
    }

    private static void WriteFixedAnsi(MemoryMappedViewAccessor accessor, long offset, int length, string value)
    {
        byte[] buffer = new byte[length];
        Encoding.ASCII.GetBytes(value, 0, Math.Min(value.Length, length - 1), buffer, 0);
        accessor.WriteArray(offset, buffer, 0, length);
    }

    private static string FormatVersion(uint version) => $"{version >> 16}.{version & 0xFFFF}";

    private static RtssOsdBridgeStatusV1 Idle(string message) => new(
        RtssOsdBridgeStatusV1.CurrentSchemaVersion,
        SharedMemoryDetected: false,
        AbiValidated: false,
        AbiVersion: null,
        Publishing: false,
        SlotIndex: -1,
        message);

    internal readonly record struct Header(
        uint Version,
        uint AppEntrySize,
        uint AppArrOffset,
        uint AppArrSize,
        uint OsdEntrySize,
        uint OsdArrOffset,
        uint OsdArrSize);
}
