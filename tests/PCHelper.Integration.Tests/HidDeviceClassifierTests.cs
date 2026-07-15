using PCHelper.Adapters;

namespace PCHelper.Integration.Tests;

/// <summary>
/// Covers the pure HID usage-page/usage classification used to label read-only peripheral
/// inventory. Classification never grants a write capability; it only names the device kind.
/// </summary>
public sealed class HidDeviceClassifierTests
{
    [Theory]
    // Generic Desktop (usage page 0x01)
    [InlineData(0x01, 0x02, "Mouse")]
    [InlineData(0x01, 0x04, "Joystick")]
    [InlineData(0x01, 0x05, "GamePad")]
    [InlineData(0x01, 0x06, "Keyboard")]
    [InlineData(0x01, 0x07, "Keypad")]
    [InlineData(0x01, 0x80, "SystemControl")]
    [InlineData(0x01, 0x00, "GenericDesktop")]
    // Consumer / digitizer / lamp array
    [InlineData(0x0C, 0x01, "ConsumerControl")]
    [InlineData(0x0D, 0x02, "Digitizer")]
    [InlineData(0x59, 0x01, "LampArray")]
    // Vendor-defined ranges (RGB controllers, AIO, dongles)
    [InlineData(0xFF00, 0x01, "VendorDefined")]
    [InlineData(0xFF72, 0xA1, "VendorDefined")]
    [InlineData(0xFFFF, 0x00, "VendorDefined")]
    // Unknown pages fall through to Other
    [InlineData(0x05, 0x01, "Other")]
    public void ClassifiesByUsagePageAndUsage(int usagePage, int usage, string expected) =>
        Assert.Equal(expected, HidDeviceClassifier.Classify(usagePage, usage));

    [Fact]
    public void RealWorldControllersClassifyAsExpected()
    {
        // NZXT AIO and an ASUS AURA controller both present as vendor-defined HID.
        Assert.Equal("VendorDefined", HidDeviceClassifier.Classify(0xFF00, 0x01));
        Assert.Equal("VendorDefined", HidDeviceClassifier.Classify(0xFF72, 0xA1));
        // A Razer/Lian Li keyboard and a HyperX mouse present as standard desktop devices.
        Assert.Equal("Keyboard", HidDeviceClassifier.Classify(0x01, 0x06));
        Assert.Equal("Mouse", HidDeviceClassifier.Classify(0x01, 0x02));
    }
}
