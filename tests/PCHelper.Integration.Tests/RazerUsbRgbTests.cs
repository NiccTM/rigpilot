using PCHelper.Adapters;
using PCHelper.Contracts;

namespace PCHelper.Integration.Tests;

/// <summary>
/// Pins RigPilot's clean-room Razer 90-byte command encoding: field offsets,
/// the XOR CRC over bytes 2..87, the extended-matrix static-colour argument
/// layout, the audited-device allowlist, and the request contract. No real
/// HID device is touched.
/// </summary>
public sealed class RazerUsbRgbTests
{
    [Fact]
    public void ReportEncodesTheDocumentedFieldLayout()
    {
        byte[] report = RazerUsbRgbWriter.BuildReport(0x0F, 0x02, 0x09, [0x01, 0x00, 0x01]);

        Assert.Equal(90, report.Length);
        Assert.Equal(0x00, report[0]); // status: new command
        Assert.Equal(0x1F, report[1]); // transaction id for modern accessories
        Assert.Equal(0x00, report[2]); // remaining packets, big-endian zero
        Assert.Equal(0x00, report[3]);
        Assert.Equal(0x00, report[4]); // protocol type
        Assert.Equal(0x09, report[5]); // data size
        Assert.Equal(0x0F, report[6]); // command class
        Assert.Equal(0x02, report[7]); // command id
        Assert.Equal(0x01, report[8]); // first argument
        Assert.Equal(0x00, report[89]); // reserved
    }

    [Fact]
    public void ReportCrcXorsBytesTwoThroughEightySeven()
    {
        byte[] report = RazerUsbRgbWriter.BuildReport(0x0F, 0x02, 0x09, [0x01, 0x00, 0x01, 0x00, 0x00, 0x01, 0xAA, 0xBB, 0xCC]);

        byte expected = 0;
        for (int index = 2; index < 88; index++)
        {
            expected ^= report[index];
        }

        Assert.Equal(expected, report[88]);
        Assert.NotEqual(0, report[88]); // this payload has a non-trivial CRC
    }

    [Fact]
    public void StaticColourUsesTheExtendedMatrixStaticEffect()
    {
        byte[] report = RazerUsbRgbWriter.BuildStaticColour(0x12, 0x34, 0x56);

        Assert.Equal(0x0F, report[6]); // extended matrix class
        Assert.Equal(0x02, report[7]); // effect command
        Assert.Equal(0x09, report[5]); // data size
        Assert.Equal(0x01, report[8]);  // variable store
        Assert.Equal(0x00, report[9]);  // LED id 0 = whole device
        Assert.Equal(0x01, report[10]); // static effect id
        Assert.Equal(0x01, report[13]); // one colour follows
        Assert.Equal(0x12, report[14]); // R
        Assert.Equal(0x34, report[15]); // G
        Assert.Equal(0x56, report[16]); // B
    }

    [Fact]
    public void ReportRefusesOversizedArguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RazerUsbRgbWriter.BuildReport(0x0F, 0x02, 0x09, new byte[81]));
    }

    [Fact]
    public void OnlyAuditedRazerProductsAreEverWritten()
    {
        Assert.Contains(0x0F13, RazerUsbRgbWriter.AuditedProducts.Keys); // Lian Li O11 Dynamic Razer Edition
        Assert.Single(RazerUsbRgbWriter.AuditedProducts); // nothing unaudited
    }

    [Theory]
    [InlineData(true, "razer:lianli-o11-dynamic", null)]
    [InlineData(false, "razer:lianli-o11-dynamic", "Experimental")]
    [InlineData(true, "wrong:device", "exact-device")]
    public void RequestContractEnforcesTheDoubleConfirmation(bool experimental, string deviceId, string? expectedRefusal)
    {
        RazerRgbRequestV1 request = new(
            RazerRgbRequestV1.CurrentSchemaVersion,
            "FF0000",
            TurnOff: false,
            experimental,
            deviceId);

        string? refusal = request.Validate();
        if (expectedRefusal is null)
        {
            Assert.Null(refusal);
        }
        else
        {
            Assert.NotNull(refusal);
            Assert.Contains(expectedRefusal, refusal, StringComparison.OrdinalIgnoreCase);
        }
    }
}
