namespace PCHelper.Adapters;

/// <summary>
/// Pure classification of a HID device from its top-level usage page and usage, per the
/// USB HID Usage Tables. Used to label read-only peripheral inventory (keyboard, mouse,
/// headset controls, LampArray, vendor-defined RGB/AIO, etc.). Classification never grants
/// a write capability.
/// </summary>
public static class HidDeviceClassifier
{
    // HID usage pages.
    private const int GenericDesktop = 0x01;
    private const int Consumer = 0x0C;
    private const int Digitizer = 0x0D;
    private const int LampArray = 0x59;
    private const int VendorDefinedFirst = 0xFF00;

    public static string Classify(int usagePage, int usage) => usagePage switch
    {
        GenericDesktop => usage switch
        {
            0x02 => "Mouse",
            0x04 => "Joystick",
            0x05 => "GamePad",
            0x06 => "Keyboard",
            0x07 => "Keypad",
            0x08 => "MultiAxisController",
            0x80 => "SystemControl",
            _ => "GenericDesktop",
        },
        Consumer => "ConsumerControl",
        Digitizer => "Digitizer",
        LampArray => "LampArray",
        >= VendorDefinedFirst => "VendorDefined",
        _ => "Other",
    };
}
