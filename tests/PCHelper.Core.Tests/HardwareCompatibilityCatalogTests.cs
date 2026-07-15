using PCHelper.Contracts;

namespace PCHelper.Core.Tests;

public sealed class HardwareCompatibilityCatalogTests
{
    [Theory]
    [InlineData("AMD", "AMD Ryzen 5 3600 6-Core Processor", "amd-zen-2")]
    [InlineData("AMD", "AMD Ryzen 7 1700 Eight-Core Processor", "amd-zen")]
    [InlineData("AMD", "AMD Ryzen 5 2600 Six-Core Processor", "amd-zen-plus")]
    [InlineData("AMD", "AMD Ryzen 7 5800X 8-Core Processor", "amd-zen-3")]
    [InlineData("AuthenticAMD", "AMD Ryzen 9 7950X3D 16-Core Processor", "amd-zen-4")]
    [InlineData("AMD", "AMD Ryzen 7 8700G w/ Radeon 780M Graphics", "amd-zen-4")]
    [InlineData("AMD", "AMD Ryzen 9 9950X 16-Core Processor", "amd-zen-5")]
    [InlineData("AMD", "AMD Ryzen Threadripper PRO 7995WX", "amd-zen-4")]
    [InlineData("GenuineIntel", "Intel(R) Core(TM) i7-10700K CPU @ 3.80GHz", "intel-core-10th")]
    [InlineData("GenuineIntel", "Intel(R) Core(TM) i7-6700K CPU @ 4.00GHz", "intel-core-6th")]
    [InlineData("GenuineIntel", "Intel(R) Core(TM) i7-7700K CPU @ 4.20GHz", "intel-core-7th")]
    [InlineData("GenuineIntel", "Intel(R) Core(TM) i7-8700K CPU @ 3.70GHz", "intel-core-8th")]
    [InlineData("GenuineIntel", "Intel(R) Core(TM) i9-9900K CPU @ 3.60GHz", "intel-core-9th")]
    [InlineData("GenuineIntel", "11th Gen Intel(R) Core(TM) i9-11900K", "intel-core-11th")]
    [InlineData("GenuineIntel", "12th Gen Intel(R) Core(TM) i9-12900K", "intel-core-12th")]
    [InlineData("GenuineIntel", "13th Gen Intel(R) Core(TM) i7-13700K", "intel-core-13th-14th")]
    [InlineData("GenuineIntel", "Intel(R) Core(TM) i9-14900K", "intel-core-13th-14th")]
    [InlineData("GenuineIntel", "Intel(R) Core(TM) Ultra 9 285K", "intel-core-ultra-200")]
    public void ClassifyCpuRecognizesMainstreamDesktopFamilies(string manufacturer, string model, string expectedFamily)
    {
        HardwareCompatibilityMatch result = HardwareCompatibilityCatalog.ClassifyCpu(manufacturer, model);

        Assert.True(result.IsRecognized);
        Assert.Equal(expectedFamily, result.FamilyId);
    }

    [Theory]
    [InlineData("NVIDIA", "NVIDIA GeForce RTX 3090", "nvidia-rtx-30")]
    [InlineData("NVIDIA", "NVIDIA GeForce RTX 2060 SUPER", "nvidia-rtx-20")]
    [InlineData("NVIDIA", "NVIDIA GeForce GTX 1660 SUPER", "nvidia-gtx-16")]
    [InlineData("NVIDIA", "NVIDIA GeForce GTX 1080 Ti", "nvidia-gtx-10")]
    [InlineData("NVIDIA", "NVIDIA GeForce GTX 980 Ti", "nvidia-gtx-9")]
    [InlineData("NVIDIA", "NVIDIA GeForce RTX 4090", "nvidia-rtx-40")]
    [InlineData("NVIDIA", "NVIDIA GeForce RTX 5090", "nvidia-rtx-50")]
    [InlineData("NVIDIA", "NVIDIA RTX A6000", "nvidia-rtx-professional")]
    [InlineData("NVIDIA", "NVIDIA RTX 6000 Ada Generation", "nvidia-rtx-professional")]
    [InlineData("AMD", "AMD Radeon RX 580 Series", "amd-radeon-rx-400-500")]
    [InlineData("AMD", "AMD Radeon RX 6800 XT", "amd-radeon-rx-6000")]
    [InlineData("AMD", "AMD Radeon RX 7900 XTX", "amd-radeon-rx-7000")]
    [InlineData("AMD", "AMD Radeon RX 9070 XT", "amd-radeon-rx-9000")]
    [InlineData("Intel", "Intel(R) Arc(TM) A770 Graphics", "intel-arc-a")]
    [InlineData("Intel", "Intel(R) Arc(TM) B580 Graphics", "intel-arc-b")]
    [InlineData("Intel", "Intel Arc Pro A60 Graphics", "intel-arc-professional")]
    [InlineData("AMD", "AMD Radeon Pro W7900", "amd-radeon-professional")]
    public void ClassifyGpuRecognizesMainstreamDesktopFamilies(string manufacturer, string model, string expectedFamily)
    {
        HardwareCompatibilityMatch result = HardwareCompatibilityCatalog.ClassifyGpu(manufacturer, model);

        Assert.True(result.IsRecognized);
        Assert.Equal(expectedFamily, result.FamilyId);
    }

    [Theory]
    [InlineData("ASUSTeK COMPUTER INC.", "ROG STRIX X570-E GAMING", "asus-motherboard")]
    [InlineData("Micro-Star International Co., Ltd.", "MAG B650 TOMAHAWK WIFI", "msi-motherboard")]
    [InlineData("Gigabyte Technology Co., Ltd.", "X870E AORUS MASTER", "gigabyte-motherboard")]
    [InlineData("ASRock", "X670E Taichi", "asrock-motherboard")]
    [InlineData("BIOSTAR Group", "B650E VALKYRIE", "biostar-motherboard")]
    [InlineData("EVGA International", "Z790 DARK K|NGP|N", "evga-motherboard")]
    [InlineData("Super Micro Computer, Inc.", "X13SAE-F", "supermicro-motherboard")]
    [InlineData("Colorful Technology", "CVN Z790D5 GAMING FROZEN", "colorful-motherboard")]
    [InlineData("MAXSUN", "MS-iCraft Z790 WIFI", "maxsun-motherboard")]
    [InlineData("Dell Inc.", "0K3CM7", "oem-motherboard")]
    public void ClassifyMotherboardRecognizesCommonDesktopVendors(string manufacturer, string model, string expectedFamily)
    {
        HardwareCompatibilityMatch result = HardwareCompatibilityCatalog.ClassifyMotherboard(manufacturer, model);

        Assert.True(result.IsRecognized);
        Assert.Equal(expectedFamily, result.FamilyId);
    }

    [Theory]
    [InlineData("Corsair", "K70 RGB PRO", "HID\\VID_1B1C&PID_1B7A", "peripheral-corsair")]
    [InlineData("Unknown", "HID LampArray", "HID\\VID_1234&PID_5678", "hid-lamparray")]
    [InlineData("Razer", "BlackWidow V4", "HID\\VID_1532&PID_0F13", "peripheral-razer")]
    [InlineData("Fractal Design", "Adjust 2 RGB Controller", "USB\\VID_1B1C&PID_1B7A", "peripheral-fractal")]
    [InlineData("Aqua Computer", "OCTO", "USB\\VID_0C70&PID_F00D", "peripheral-aquacomputer")]
    [InlineData("ADATA", "XPG PRIME ARGB controller", "USB\\VID_125F&PID_A123", "peripheral-adata-xpg")]
    [InlineData("Keychron", "Q6 Max RGB", "HID\\VID_3434&PID_0110", "peripheral-keychron")]
    [InlineData("Glorious", "Model O 2 Wireless", "HID\\VID_258A&PID_0049", "peripheral-glorious")]
    [InlineData("TEAMGROUP", "T-FORCE DELTA RGB", "USB\\VID_0C45&PID_7403", "peripheral-teamgroup")]
    [InlineData("Kingston", "FURY CTRL RGB", "USB\\VID_0951&PID_16A4", "peripheral-kingston-fury")]
    [InlineData("ZOTAC", "SPECTRA RGB", "USB\\VID_19DA&PID_BEEF", "peripheral-zotac")]
    [InlineData("PowerColor", "Devil RGB", "USB\\VID_1DA2&PID_731F", "peripheral-powercolor")]
    [InlineData("Sapphire", "NITRO+ RGB", "USB\\VID_1DA2&PID_E445", "peripheral-sapphire")]
    [InlineData("EVGA", "GeForce RTX 3090 FTW3", "USB\\VID_3842&PID_3090", "peripheral-evga")]
    [InlineData("EVGA", "GeForce RTX 3090 K|NGP|N", "USB\\VID_3842&PID_3090", "peripheral-evga-kingpin")]
    [InlineData("Unknown", "ROG STRIX RGB Controller", "USB\\VID_0B05&PID_1ABC", "peripheral-asus")]
    public void ClassifyPeripheralRecognizesSafeInventoryFamilies(string manufacturer, string name, string identity, string expectedFamily)
    {
        HardwareCompatibilityMatch result = HardwareCompatibilityCatalog.ClassifyPeripheral(manufacturer, name, identity);

        Assert.True(result.IsRecognized);
        Assert.Equal(expectedFamily, result.FamilyId);
    }

    [Fact]
    public void UnclassifiedIdentityDoesNotInventSupport()
    {
        HardwareCompatibilityMatch result = HardwareCompatibilityCatalog.ClassifyGpu("Example", "Example Accelerator 123");
        Dictionary<string, string> properties = [];

        HardwareCompatibilityCatalog.AddToProperties(properties, result);

        Assert.False(result.IsRecognized);
        Assert.Empty(properties);
        Assert.Equal("unclassified", result.FamilyId);
    }

    [Fact]
    public void UsbHidTransportFilterRejectsAcpiAndAllowsObservedUsbHidInterfaces()
    {
        Assert.False(HardwareCompatibilityCatalog.IsUsbOrHidTransport("ACPI\\ASUS0100", []));
        Assert.True(HardwareCompatibilityCatalog.IsUsbOrHidTransport("HID\\VID_1532&PID_0F13", []));
        Assert.True(HardwareCompatibilityCatalog.IsUsbOrHidTransport(
            "ROOT\\SOMETHING",
            ["USB\\VID_1B1C&PID_1B7A"]));
    }

    [Fact]
    public void RecognizedIdentityWritesInventoryEvidenceOnly()
    {
        HardwareCompatibilityMatch result = HardwareCompatibilityCatalog.ClassifyGpu("NVIDIA", "NVIDIA GeForce RTX 5090");
        Dictionary<string, string> properties = [];

        HardwareCompatibilityCatalog.AddToProperties(properties, result);

        Assert.Equal("nvidia-rtx-50", properties["compatibilityFamily"]);
        Assert.Equal("NVIDIA GeForce RTX 50 series", properties["compatibilityLabel"]);
        Assert.Contains("no write capability", properties["compatibilityEvidence"], StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("PCI\\VEN_10DE&DEV_2204&SUBSYS_161319DA&REV_A1", "NVIDIA", "NVIDIA GeForce RTX 3090", "zotac-gpu-board")]
    [InlineData("PCI\\VEN_10DE&DEV_2204&SUBSYS_24A43842&REV_A1", "NVIDIA", "NVIDIA GeForce RTX 3090", "evga-gpu-board")]
    [InlineData("PCI\\VEN_10DE&DEV_2684&SUBSYS_88901043&REV_A1", "NVIDIA", "NVIDIA GeForce RTX 4090", "asus-gpu-board")]
    [InlineData(null, "EVGA", "EVGA GeForce RTX 3090 K|NGP|N", "evga-kingpin-gpu-board")]
    [InlineData(null, "Gigabyte", "AORUS GeForce RTX 4090 MASTER", "aorus-gpu-board")]
    [InlineData(null, "ASUS", "ROG STRIX GeForce RTX 4090", "asus-rog-gpu-board")]
    [InlineData(null, "ASUS", "TUF GAMING Radeon RX 9070 XT", "asus-tuf-gpu-board")]
    [InlineData(null, "MSI", "GeForce RTX 4090 SUPRIM X MYSTIC LIGHT", "msi-mystic-light-gpu-board")]
    [InlineData(null, "ZOTAC", "GeForce RTX 5090 AMP Extreme SPECTRA", "zotac-spectra-gpu-board")]
    [InlineData(null, "ASRock", "Radeon RX 9070 XT Steel Legend Polychrome", "asrock-polychrome-gpu-board")]
    [InlineData(null, "PowerColor", "Radeon RX 7900 XTX Red Devil", "powercolor-devil-gpu-board")]
    [InlineData(null, "PNY", "GeForce RTX 4080 XLR8 EPIC-X", "pny-xlr8-gpu-board")]
    [InlineData(null, "Palit", "GeForce RTX 4090 GameRock", "palit-gamerock-gpu-board")]
    [InlineData(null, "GALAX", "GeForce RTX 5090 Hall of Fame", "galax-hof-gpu-board")]
    [InlineData(null, "INNO3D", "GeForce RTX 5090 iCHILL X3", "inno3d-ichill-gpu-board")]
    [InlineData(null, "XFX", "Radeon RX 9070 XT Speedster MERC", "xfx-speedster-gpu-board")]
    [InlineData("PCI\\VEN_10DE&DEV_2782&SUBSYS_1338196E&REV_A1", "NVIDIA", "NVIDIA GeForce RTX 4080", "pny-gpu-board")]
    [InlineData("PCI\\VEN_10DE&DEV_2782&SUBSYS_471A1569&REV_A1", "NVIDIA", "NVIDIA GeForce RTX 4080", "palit-gpu-board")]
    [InlineData("PCI\\VEN_10DE&DEV_2782&SUBSYS_470A10B0&REV_A1", "NVIDIA", "NVIDIA GeForce RTX 4080", "gainward-gpu-board")]
    [InlineData("PCI\\VEN_1002&DEV_744C&SUBSYS_1A2B1ED3&REV_C1", "AMD", "AMD Radeon RX 9070 XT", "yeston-gpu-board")]
    public void ClassifyGpuBoardPartnerUsesExplicitNameOrPciSubsystemVendor(
        string? pnpId,
        string manufacturer,
        string model,
        string expectedFamily)
    {
        HardwareCompatibilityMatch result = HardwareCompatibilityCatalog.ClassifyGpuBoardPartner(pnpId, manufacturer, model);

        Assert.True(result.IsRecognized);
        Assert.Equal(expectedFamily, result.FamilyId);
        Assert.Contains("does not identify a GPU RGB controller", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnbrandedGpuIdentityDoesNotInventBoardOrRgbSupport()
    {
        HardwareCompatibilityMatch result = HardwareCompatibilityCatalog.ClassifyGpuBoardPartner(
            null,
            "NVIDIA",
            "NVIDIA GeForce RTX 5090");

        Assert.False(result.IsRecognized);
        Assert.Equal("unclassified", result.FamilyId);
    }
}
