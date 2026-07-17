using PCHelper.Adapters;
using PCHelper.Contracts;

namespace PCHelper.Integration.Tests;

/// <summary>
/// Pins the SMBus RGB safety envelope: the default-deny address policy (the
/// brick-class guardrail), the two write gates (live transport + audited kit),
/// the witnessed-first-light exception's exact limits, the ENE transaction
/// plan, the PawnIO ioctl encoding, and the purely read-only detection probe.
/// No real SMBus is touched — fakes record what would reach the bus.
/// </summary>
public sealed class SmbusRgbTests
{
    [Theory]
    [InlineData(0x70)] // first RGB controller
    [InlineData(0x73)]
    [InlineData(0x77)] // last RGB controller
    public void AddressPolicyAllowsOnlyTheRgbControllerRange(byte address)
    {
        Assert.True(SmbusAddressPolicy.IsRgbControllerAddress(address));
        Assert.Null(SmbusAddressPolicy.DenyReason(address));
    }

    [Theory]
    [InlineData(0x50, "SPD")]   // SPD EEPROM — brick class
    [InlineData(0x57, "SPD")]
    [InlineData(0x18, "thermal")]
    [InlineData(0x1F, "thermal")]
    [InlineData(0x30, "write-protect")]
    [InlineData(0x4C, "PMIC")]  // DDR5 PMIC range
    [InlineData(0x00, "outside")]
    [InlineData(0x6F, "outside")] // just below the RGB range
    [InlineData(0x78, "outside")] // just above the RGB range
    public void AddressPolicyDeniesEverySensitiveOrUnknownAddressWithAReason(byte address, string expected)
    {
        string? reason = SmbusAddressPolicy.DenyReason(address);

        Assert.NotNull(reason);
        Assert.Contains(expected, reason, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<SmbusSafetyException>(() => SmbusAddressPolicy.EnsureWritable(address));
    }

    [Fact]
    public void EnePlanTargetsOnlyTheGivenControllerAndFollowsTheDocumentedSequence()
    {
        IReadOnlyList<SmbusTransaction> plan = EneSmbusRgbProtocol.BuildStaticColour(0x72, 0x0A, 0x84, 0xFF);

        // Direct-on (2) + colours pointer (1) + 15 colour bytes + apply (2).
        Assert.Equal(20, plan.Count);
        Assert.All(plan, transaction =>
        {
            Assert.Equal((byte)0x72, transaction.Address);
            Assert.Null(SmbusAddressPolicy.DenyReason(transaction.Address));
        });

        // Pointer words are byte-swapped so the ENE high byte transmits first.
        Assert.Equal(SmbusTransactionKind.WriteWord, plan[0].Kind);
        Assert.Equal(EneSmbusRgbProtocol.PointerCommand, plan[0].Command);
        Assert.Equal(EneSmbusRgbProtocol.PointerWord(0x8020), plan[0].Value);
        Assert.Equal((ushort)0x2080, plan[0].Value);

        // Direct mode on.
        Assert.Equal(SmbusTransactionKind.WriteByte, plan[1].Kind);
        Assert.Equal(EneSmbusRgbProtocol.DataCommand, plan[1].Command);
        Assert.Equal((ushort)0x01, plan[1].Value);

        // Colour pointer, then R,B,G per LED for five LEDs via auto-increment.
        Assert.Equal(EneSmbusRgbProtocol.PointerWord(0x8000), plan[2].Value);
        for (int led = 0; led < EneSmbusRgbProtocol.LedCount; led++)
        {
            Assert.Equal((ushort)0x0A, plan[3 + (led * 3)].Value); // R
            Assert.Equal((ushort)0xFF, plan[4 + (led * 3)].Value); // B
            Assert.Equal((ushort)0x84, plan[5 + (led * 3)].Value); // G
        }

        // Apply.
        Assert.Equal(EneSmbusRgbProtocol.PointerWord(0x80A0), plan[18].Value);
        Assert.Equal((ushort)0x01, plan[19].Value);
    }

    [Fact]
    public void EnePlanRefusesAForbiddenAddress()
    {
        Assert.Throws<SmbusSafetyException>(() => EneSmbusRgbProtocol.BuildStaticColour(0x50, 1, 2, 3));
    }

    [Fact]
    public void WriterReportsNoTransportInTheProductionConfiguration()
    {
        SmbusRgbWriter writer = new(transport: null);

        SmbusRgbResult result = writer.WriteStaticColour([0x70], "F4-4000C15-8GTZR", 0x0A, 0x84, 0xFF);

        Assert.Equal(SmbusRgbOutcome.NoTransport, result.Outcome);
        Assert.Equal(0, result.WritesIssued);
        Assert.Contains("PawnIO SMBus", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WriterRefusesAnUnauditedKitEvenWithALiveTransport()
    {
        FakeSmbusTransport transport = new();
        SmbusRgbWriter writer = new(transport);

        // A kit that has never had a witnessed first-light.
        SmbusRgbResult result = writer.WriteStaticColour([0x70], "F4-3600C16-8GVKC", 0x0A, 0x84, 0xFF);

        Assert.Equal(SmbusRgbOutcome.ProtocolNotAudited, result.Outcome);
        Assert.Empty(transport.Transactions); // nothing reached the bus
    }

    [Fact]
    public void AuditedReferenceKitWritesThroughTheNormalGatedPath()
    {
        // F4-4000C15-8GTZR passed its witnessed first-light on 2026-07-17
        // (DIMM_LED-0103 at 0x77, operator visually confirmed).
        Assert.True(EneSmbusRgbProtocol.IsKitAudited(EneSmbusRgbProtocol.ReferenceKitPartNumber));

        FakeSmbusTransport transport = new();
        SmbusRgbResult result = new SmbusRgbWriter(transport).WriteStaticColour(
            [0x77], EneSmbusRgbProtocol.ReferenceKitPartNumber, 0xFF, 0x00, 0x00);

        Assert.Equal(SmbusRgbOutcome.WriteIssued, result.Outcome);
        Assert.Equal(20, result.WritesIssued);
    }

    [Fact]
    public void WitnessedFirstLightBypassesOnlyTheAuditGate()
    {
        FakeSmbusTransport transport = new();
        SmbusRgbWriter writer = new(transport);

        SmbusRgbResult result = writer.WriteStaticColour(
            [0x71], "F4-4000C15-8GTZR", 0x10, 0x20, 0x30, witnessedFirstLight: true);

        Assert.Equal(SmbusRgbOutcome.WriteIssued, result.Outcome);
        Assert.Equal(20, result.WritesIssued);
        Assert.All(transport.Transactions, transaction => Assert.Equal((byte)0x71, transaction.Address));
    }

    [Fact]
    public void WitnessedFirstLightStillCannotReachAForbiddenAddress()
    {
        FakeSmbusTransport transport = new();
        SmbusRgbWriter writer = new(transport);

        SmbusRgbResult result = writer.WriteStaticColour(
            [0x50], "F4-4000C15-8GTZR", 1, 2, 3, witnessedFirstLight: true);

        Assert.Equal(SmbusRgbOutcome.AddressRefused, result.Outcome);
        Assert.Empty(transport.Transactions);
    }

    [Fact]
    public void TransportEncodesTheDocumentedPiix4IoctlLayout()
    {
        FakePawnIoSession session = new();
        using PawnIoSmbusTransport transport = new(session);

        transport.WriteWord(0x70, 0x00, 0x2080);
        transport.WriteByte(0x70, 0x01, 0xAB);
        session.NextRead = 0x10;
        byte value = transport.ReadByte(0x70, 0xA0);

        Assert.Equal(0x10, value);
        Assert.Equal(3, session.Calls.Count);
        // [address, read/write, command, protocol, data...]
        Assert.Equal([0x70, 0, 0x00, 3, 0x2080], session.Calls[0]);
        Assert.Equal([0x70, 0, 0x01, 2, 0xAB], session.Calls[1]);
        Assert.Equal([0x70, 1, 0xA0, 2], session.Calls[2]);
    }

    [Fact]
    public void TransportRefusesWritesAndReadsOutsideTheRgbRange()
    {
        FakePawnIoSession session = new();
        using PawnIoSmbusTransport transport = new(session);

        Assert.Throws<SmbusSafetyException>(() => transport.WriteByte(0x50, 0x00, 0x01));
        Assert.Throws<SmbusSafetyException>(() => transport.WriteWord(0x18, 0x00, 0x0001));
        // Reads: refused everywhere except RGB controllers and SPD presence.
        Assert.Throws<SmbusSafetyException>(() => transport.ReadByte(0x18, 0x00));
        Assert.Throws<SmbusSafetyException>(() => transport.ReadByte(0x30, 0x00));
        Assert.Empty(session.Calls); // nothing reached the bus

        // SPD reads are allowed (read-only presence evidence) — writes never are.
        session.NextRead = 0x0C;
        Assert.Equal(0x0C, transport.ReadByte(0x50, 0x02));
        Assert.Equal([0x50, 1, 0x02, 2], session.Calls.Single());
        Assert.Throws<SmbusSafetyException>(() => transport.WriteByte(0x50, 0x02, 0x0C));
    }

    [Fact]
    public void DetectionReportsAControllerOnlyWhenThePatternMatches()
    {
        FakeSmbusTransport transport = new();
        transport.ReadableDevices[0x70] = [0x10, 0x11, 0x12, 0x13]; // ENE pattern
        transport.ReadableDevices[0x73] = [0x00, 0x00, 0x00, 0x00]; // acknowledges, wrong pattern

        SmbusRgbProbeResultV1 result = SmbusRgbDetection.ProbeWithTransport(transport);

        Assert.Equal(SmbusRgbProbeOutcome.ControllersFound, result.Outcome);
        Assert.Equal(2, result.Sightings.Count);
        Assert.True(result.Sightings.Single(sighting => sighting.Address == 0x70).PatternMatched);
        Assert.False(result.Sightings.Single(sighting => sighting.Address == 0x73).PatternMatched);
        Assert.Empty(transport.Transactions); // detection is purely read-only
    }

    [Fact]
    public void DetectionReportsNoControllersOnAnEmptyBus()
    {
        FakeSmbusTransport transport = new();

        SmbusRgbProbeResultV1 result = SmbusRgbDetection.ProbeWithTransport(transport);

        Assert.Equal(SmbusRgbProbeOutcome.NoControllers, result.Outcome);
        Assert.Empty(result.Sightings);
        Assert.Empty(transport.Transactions);
    }

    [Theory]
    [InlineData(new byte[] { 0x10, 0x11, 0x12, 0x13 }, true)]  // documented pattern base
    [InlineData(new byte[] { 0x00, 0x01, 0x02, 0x03 }, true)]  // DIMM_LED-0103 firmware base
    [InlineData(new byte[] { 0x00, 0x01, 0xFF, 0x03 }, true)]  // one live-bus read glitch tolerated
    [InlineData(new byte[] { 0x00, 0x00, 0x00, 0x00 }, false)] // flat constant window
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, false)] // no-device / all-ones window
    [InlineData(new byte[] { 0x20, 0x00, 0x2F, 0x2F }, false)] // the unrelated 0x76 device
    public void DetectionWindowHeuristicAcceptsIncrementingSequencesOnly(byte[] window, bool expected)
    {
        Assert.Equal(expected, EneSmbusRgbProtocol.LooksLikeDetectionWindow(window));
    }

    [Theory]
    [InlineData("DIMM_LED-0103", true)] // the reference kit's live identity
    [InlineData("AUDA0-E6K5-0101", true)]
    [InlineData("", false)]
    [InlineData("................", false)]
    public void KnownDeviceNameGateMatchesEneDramIdentitiesOnly(string name, bool expected)
    {
        Assert.Equal(expected, EneSmbusRgbProtocol.IsKnownDeviceName(name));
    }

    [Fact]
    public void IdentityReadIssuesOnlyThePointerWriteAndDecodesTheName()
    {
        FakeSmbusTransport transport = new();
        foreach (char letter in "AUDA0-E6K5-0101\0")
        {
            transport.DataReads.Enqueue((byte)letter);
        }

        SmbusRgbIdentityResultV1 result = SmbusRgbIdentify.ReadIdentity(transport, 0x77);

        Assert.True(result.Succeeded);
        Assert.Equal("AUDA0-E6K5-0101", result.DeviceName);
        SmbusTransaction write = Assert.Single(transport.Transactions);
        Assert.Equal(SmbusTransactionKind.WriteWord, write.Kind);
        Assert.Equal((byte)0x77, write.Address);
        Assert.Equal(EneSmbusRgbProtocol.PointerCommand, write.Command);
        Assert.Equal(EneSmbusRgbProtocol.PointerWord(SmbusRgbIdentify.DeviceNameRegister), write.Value);
    }

    private sealed class FakeSmbusTransport : ISmbusTransport
    {
        public List<SmbusTransaction> Transactions { get; } = [];

        public Dictionary<byte, byte[]> ReadableDevices { get; } = [];

        public Queue<byte> DataReads { get; } = [];

        public void WriteByte(byte address, byte commandCode, byte value)
        {
            SmbusAddressPolicy.EnsureWritable(address);
            Transactions.Add(new SmbusTransaction(SmbusTransactionKind.WriteByte, address, commandCode, value));
        }

        public void WriteWord(byte address, byte commandCode, ushort value)
        {
            SmbusAddressPolicy.EnsureWritable(address);
            Transactions.Add(new SmbusTransaction(SmbusTransactionKind.WriteWord, address, commandCode, value));
        }

        public byte ReadByte(byte address, byte commandCode)
        {
            if (commandCode is EneSmbusRgbProtocol.DataCommand or EneSmbusRgbProtocol.DataReadCommand && DataReads.Count > 0)
            {
                return DataReads.Dequeue();
            }

            if (!ReadableDevices.TryGetValue(address, out byte[]? pattern))
            {
                throw new PawnIoException($"No device acknowledged at 0x{address:X2}.", -1);
            }

            int offset = commandCode - EneSmbusRgbProtocol.DetectionCommandFirst;
            return offset >= 0 && offset < pattern.Length ? pattern[offset] : (byte)0x00;
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakePawnIoSession : IPawnIoModuleSession
    {
        public List<ulong[]> Calls { get; } = [];

        public ulong NextRead { get; set; }

        public ulong[] Execute(string functionName, ReadOnlySpan<ulong> input, int maximumOutputCount)
        {
            Assert.Equal("ioctl_smbus_xfer", functionName);
            Calls.Add(input.ToArray());
            return maximumOutputCount > 0 ? [NextRead] : [];
        }

        public void Dispose()
        {
        }
    }
}
