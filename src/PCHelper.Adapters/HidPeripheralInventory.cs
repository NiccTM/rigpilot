using HidSharp;
using HidSharp.Reports;
using PCHelper.Contracts;

namespace PCHelper.Adapters;

/// <summary>
/// Read-only HID peripheral enumeration via HidSharp. Reports each device's identity and a
/// classification only; it never opens a device for output, sends a report, or exposes a
/// write capability. Because native HID enumeration can fault, this is intended to run
/// inside the crash-isolated Adapter Host, and every per-device read is individually
/// contained so one unreadable device cannot fail the whole inventory.
/// </summary>
public static class HidPeripheralInventory
{
    public static HidInventoryResultV1 Enumerate()
    {
        try
        {
            List<HidDeviceInventoryItemV1> items = [];
            foreach (HidDevice device in DeviceList.Local.GetHidDevices())
            {
                try
                {
                    items.Add(Describe(device));
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // A single unreadable/exclusively-owned device never fails the inventory.
                }
            }

            return new HidInventoryResultV1(
                HidInventoryOutcome.Succeeded,
                items,
                $"Enumerated {items.Count} HID devices.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return HidInventoryResultV1.Failed($"HID enumeration failed: {exception.GetType().Name}.");
        }
    }

    private static HidDeviceInventoryItemV1 Describe(HidDevice device)
    {
        (int usagePage, int usage) = TryReadTopLevelUsage(device);
        return new HidDeviceInventoryItemV1(
            device.VendorID,
            device.ProductID,
            usagePage,
            usage,
            HidDeviceClassifier.Classify(usagePage, usage),
            TryReadString(device.GetProductName),
            TryReadString(device.GetManufacturer));
    }

    private static (int UsagePage, int Usage) TryReadTopLevelUsage(HidDevice device)
    {
        try
        {
            ReportDescriptor descriptor = device.GetReportDescriptor();
            foreach (DeviceItem item in descriptor.DeviceItems)
            {
                foreach (uint value in item.Usages.GetAllValues())
                {
                    return ((int)(value >> 16), (int)(value & 0xFFFF));
                }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Descriptor unavailable (access denied / malformed): fall back to unknown usage.
        }

        return (0, 0);
    }

    private static string? TryReadString(Func<string> reader)
    {
        try
        {
            string value = reader();
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }
}
