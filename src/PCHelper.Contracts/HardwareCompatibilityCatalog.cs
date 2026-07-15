using System.Text.RegularExpressions;

namespace PCHelper.Contracts;

/// <summary>
/// A conservative, versioned catalogue for recognising common desktop
/// hardware families from Windows-provided identity strings. A match is
/// inventory evidence only: it never grants a hardware write capability.
/// </summary>
public sealed record HardwareCompatibilityMatch(
    string FamilyId,
    string DisplayName,
    string Summary,
    bool IsRecognized);

public static partial class HardwareCompatibilityCatalog
{
    private static readonly HardwareCompatibilityMatch UnclassifiedCpu = Unclassified("CPU");
    private static readonly HardwareCompatibilityMatch UnclassifiedGpu = Unclassified("GPU");
    private static readonly HardwareCompatibilityMatch UnclassifiedGpuBoard = Unclassified("GPU board partner");
    private static readonly HardwareCompatibilityMatch UnclassifiedMotherboard = Unclassified("motherboard");
    private static readonly HardwareCompatibilityMatch UnclassifiedPeripheral = Unclassified("peripheral");

    public static HardwareCompatibilityMatch ClassifyCpu(string? manufacturer, string? model)
    {
        string identity = Normalise(manufacturer, model);
        if (identity.Contains("RYZEN", StringComparison.Ordinal)
            || identity.Contains("THREADRIPPER", StringComparison.Ordinal))
        {
            return ExtractAmdDesktopSeries(identity) switch
            {
                1000 => Recognized("amd-zen", "AMD Ryzen / Threadripper 1000 (Zen)"),
                2000 => Recognized("amd-zen-plus", "AMD Ryzen / Threadripper 2000 (Zen+)"),
                3000 => Recognized("amd-zen-2", "AMD Ryzen / Threadripper 3000 (Zen 2)"),
                4000 => Recognized("amd-zen-2", "AMD Ryzen 4000 desktop family (Zen 2)"),
                5000 => Recognized("amd-zen-3", "AMD Ryzen / Threadripper 5000 (Zen 3)"),
                7000 => Recognized("amd-zen-4", "AMD Ryzen / Threadripper 7000 (Zen 4)"),
                8000 => Recognized("amd-zen-4", "AMD Ryzen 8000 desktop family (Zen 4)"),
                9000 => Recognized("amd-zen-5", "AMD Ryzen / Threadripper 9000 (Zen 5)"),
                _ => Recognized("amd-ryzen", "AMD Ryzen / Threadripper desktop family")
            };
        }

        if (identity.Contains("CORE", StringComparison.Ordinal)
            && identity.Contains("ULTRA", StringComparison.Ordinal))
        {
            return CoreUltra200Pattern().IsMatch(identity)
                ? Recognized("intel-core-ultra-200", "Intel Core Ultra 200 desktop family")
                : Recognized("intel-core-ultra", "Intel Core Ultra family") with
                {
                    Summary = "Recognized for read-only inventory and eligibility reporting. The CPU generation or desktop form factor could not be determined from the Windows name alone."
                };
        }

        if (identity.Contains("INTEL", StringComparison.Ordinal)
            && identity.Contains("CORE", StringComparison.Ordinal))
        {
            return ExtractIntelCoreGeneration(identity) switch
            {
                6 => Recognized("intel-core-6th", "Intel Core 6th generation"),
                7 => Recognized("intel-core-7th", "Intel Core 7th generation"),
                8 => Recognized("intel-core-8th", "Intel Core 8th generation"),
                9 => Recognized("intel-core-9th", "Intel Core 9th generation"),
                10 => Recognized("intel-core-10th", "Intel Core 10th generation"),
                11 => Recognized("intel-core-11th", "Intel Core 11th generation"),
                12 => Recognized("intel-core-12th", "Intel Core 12th generation"),
                13 => Recognized("intel-core-13th-14th", "Intel Core 13th generation"),
                14 => Recognized("intel-core-13th-14th", "Intel Core 14th generation"),
                _ => Recognized("intel-core", "Intel Core desktop family")
            };
        }

        return UnclassifiedCpu;
    }

    public static HardwareCompatibilityMatch ClassifyGpu(string? manufacturer, string? model)
    {
        string identity = Normalise(manufacturer, model);
        if (identity.Contains("NVIDIA", StringComparison.Ordinal))
        {
            if (identity.Contains("RTX A", StringComparison.Ordinal)
                || identity.Contains("RTX PRO", StringComparison.Ordinal)
                || identity.Contains("RTX ADA", StringComparison.Ordinal)
                || (identity.Contains("RTX", StringComparison.Ordinal)
                    && identity.Contains(" ADA ", StringComparison.Ordinal)))
            {
                return Recognized("nvidia-rtx-professional", "NVIDIA RTX professional graphics");
            }

            return ExtractNvidiaRtxSeries(identity) switch
            {
                20 => Recognized("nvidia-rtx-20", "NVIDIA GeForce RTX 20 series"),
                30 => Recognized("nvidia-rtx-30", "NVIDIA GeForce RTX 30 series"),
                40 => Recognized("nvidia-rtx-40", "NVIDIA GeForce RTX 40 series"),
                50 => Recognized("nvidia-rtx-50", "NVIDIA GeForce RTX 50 series"),
                _ when identity.Contains("GTX 16", StringComparison.Ordinal) => Recognized("nvidia-gtx-16", "NVIDIA GeForce GTX 16 series"),
                _ when identity.Contains("GTX 10", StringComparison.Ordinal) => Recognized("nvidia-gtx-10", "NVIDIA GeForce GTX 10 series"),
                _ when identity.Contains("GTX 9", StringComparison.Ordinal) => Recognized("nvidia-gtx-9", "NVIDIA GeForce GTX 900 series"),
                _ when identity.Contains("GTX 7", StringComparison.Ordinal) => Recognized("nvidia-gtx-7", "NVIDIA GeForce GTX 700 series"),
                _ => Recognized("nvidia-gpu", "NVIDIA graphics")
            };
        }

        if (identity.Contains("RADEON", StringComparison.Ordinal) || identity.Contains(" AMD ", StringComparison.Ordinal))
        {
            if (identity.Contains("RADEON PRO", StringComparison.Ordinal)
                || identity.Contains("AMD INSTINCT", StringComparison.Ordinal))
            {
                return Recognized("amd-radeon-professional", "AMD Radeon Pro or Instinct graphics");
            }
            return ExtractAmdRadeonSeries(identity) switch
            {
                400 => Recognized("amd-radeon-rx-400-500", "AMD Radeon RX 400/500 series"),
                5000 => Recognized("amd-radeon-rx-5000", "AMD Radeon RX 5000 series"),
                6000 => Recognized("amd-radeon-rx-6000", "AMD Radeon RX 6000 series"),
                7000 => Recognized("amd-radeon-rx-7000", "AMD Radeon RX 7000 series"),
                9000 => Recognized("amd-radeon-rx-9000", "AMD Radeon RX 9000 series"),
                _ when identity.Contains("RADEON", StringComparison.Ordinal) => Recognized("amd-radeon", "AMD Radeon graphics"),
                _ => Recognized("amd-gpu", "AMD graphics")
            };
        }

        if (identity.Contains("INTEL", StringComparison.Ordinal))
        {
            if (identity.Contains("ARC PRO", StringComparison.Ordinal))
            {
                return Recognized("intel-arc-professional", "Intel Arc Pro graphics");
            }
            if (ArcBPattern().IsMatch(identity))
            {
                return Recognized("intel-arc-b", "Intel Arc B-series graphics");
            }
            if (ArcAPattern().IsMatch(identity))
            {
                return Recognized("intel-arc-a", "Intel Arc A-series graphics");
            }
            if (identity.Contains("ARC", StringComparison.Ordinal))
            {
                return Recognized("intel-arc", "Intel Arc graphics");
            }
            return Recognized("intel-integrated-graphics", "Intel integrated graphics");
        }

        return UnclassifiedGpu;
    }

    /// <summary>
    /// Identifies a graphics-board partner only when Windows reports a known
    /// PCI subsystem vendor or an explicit board name. This is inventory
    /// evidence; it does not identify the GPU RGB controller or qualify a
    /// vendor lighting protocol.
    /// </summary>
    public static HardwareCompatibilityMatch ClassifyGpuBoardPartner(
        string? pnpId,
        string? manufacturer,
        string? model)
    {
        string identity = Normalise(manufacturer, model);
        GpuBoardPartnerDefinition? explicitPartner = FindGpuBoardPartnerByName(identity);
        if (explicitPartner is not null)
        {
            return RecognizedGpuBoard(explicitPartner, "an explicit board or sub-brand name");
        }

        Match subsystemMatch = PciSubsystemVendorPattern().Match(pnpId ?? string.Empty);
        if (subsystemMatch.Success)
        {
            string subsystemVendor = subsystemMatch.Groups["vendor"].Value.ToUpperInvariant();
            GpuBoardPartnerDefinition? pciPartner = FindGpuBoardPartnerByPciSubsystemVendor(subsystemVendor);
            if (pciPartner is not null)
            {
                return RecognizedGpuBoard(pciPartner, $"PCI subsystem vendor ID {subsystemVendor}");
            }
        }

        return UnclassifiedGpuBoard;
    }

    public static HardwareCompatibilityMatch ClassifyMotherboard(string? manufacturer, string? model)
    {
        string identity = Normalise(manufacturer, model);
        if (ContainsAny(identity, "ASUSTEK", " ASUS "))
        {
            return Recognized("asus-motherboard", "ASUS desktop motherboard");
        }
        if (ContainsAny(identity, "MICRO STAR", " MSI "))
        {
            return Recognized("msi-motherboard", "MSI desktop motherboard");
        }
        if (identity.Contains("GIGABYTE", StringComparison.Ordinal))
        {
            return Recognized("gigabyte-motherboard", "Gigabyte desktop motherboard");
        }
        if (identity.Contains("ASROCK", StringComparison.Ordinal))
        {
            return Recognized("asrock-motherboard", "ASRock desktop motherboard");
        }
        if (identity.Contains("BIOSTAR", StringComparison.Ordinal))
        {
            return Recognized("biostar-motherboard", "Biostar desktop motherboard");
        }
        if (identity.Contains("EVGA", StringComparison.Ordinal))
        {
            return Recognized("evga-motherboard", "EVGA desktop motherboard");
        }
        if (ContainsAny(identity, "SUPERMICRO", "SUPER MICRO"))
        {
            return Recognized("supermicro-motherboard", "Supermicro desktop or workstation motherboard");
        }
        if (identity.Contains("COLORFUL", StringComparison.Ordinal))
        {
            return Recognized("colorful-motherboard", "Colorful desktop motherboard");
        }
        if (identity.Contains("MAXSUN", StringComparison.Ordinal))
        {
            return Recognized("maxsun-motherboard", "Maxsun desktop motherboard");
        }
        if (ContainsAny(identity, "DELL", "ALIENWARE", "HP", "HEWLETT PACKARD", "LENOVO", "ACER"))
        {
            return Recognized("oem-motherboard", "OEM desktop motherboard");
        }
        return UnclassifiedMotherboard;
    }

    public static HardwareCompatibilityMatch ClassifyPeripheral(string? manufacturer, string? name, string? hardwareIdentity)
    {
        string identity = Normalise(manufacturer, name, hardwareIdentity);
        if (identity.Contains("LAMPARRAY", StringComparison.Ordinal))
        {
            return Recognized("hid-lamparray", "HID LampArray / Windows Dynamic Lighting device");
        }

        foreach ((string familyId, string displayName, string[] aliases) in PeripheralVendors)
        {
            if (aliases.Any(alias => identity.Contains(alias, StringComparison.Ordinal)))
            {
                return Recognized(familyId, displayName);
            }
        }

        if (ContainsAny(identity, " RGB ", " LIGHTING ", " LED ", " CHROMA ", " LIGHTSYNC "))
        {
            return Recognized("generic-rgb-peripheral", "Generic RGB or lighting peripheral");
        }
        return UnclassifiedPeripheral;
    }

    public static bool IsUsbOrHidTransport(string? pnpId, IEnumerable<string>? hardwareIds = null)
    {
        if (IsUsbOrHidIdentity(pnpId))
        {
            return true;
        }
        return hardwareIds?.Any(IsUsbOrHidIdentity) == true;
    }

    public static void AddToProperties(IDictionary<string, string> properties, HardwareCompatibilityMatch match)
    {
        ArgumentNullException.ThrowIfNull(properties);
        if (!match.IsRecognized)
        {
            return;
        }
        properties["compatibilityFamily"] = match.FamilyId;
        properties["compatibilityLabel"] = match.DisplayName;
        properties["compatibilityEvidence"] = "Observed name, SMBIOS, or PnP identity only; no write capability is inferred.";
    }

    // Specific board lines are intentionally listed before their parent brands.
    // A name match is inventory evidence only. The PCI subsystem IDs are used
    // only where the vendor identity is established and do not imply that every
    // board from that vendor includes a lighting controller.
    private static readonly GpuBoardPartnerDefinition[] GpuBoardPartners =
    [
        new("evga-kingpin-gpu-board", "EVGA K|NGP|N graphics board", ["KINGPIN", "K NGP N"], [], "EVGA Precision LED Sync"),
        new("asus-rog-gpu-board", "ASUS ROG graphics board", ["ROG STRIX", "ROG MATRIX", "ROG ASTRAL"], [], "ASUS Aura Sync"),
        new("asus-tuf-gpu-board", "ASUS TUF Gaming graphics board", ["TUF GAMING"], [], "ASUS Aura Sync"),
        new("aorus-gpu-board", "AORUS graphics board", ["AORUS", "RGB FUSION"], [], "Gigabyte RGB Fusion"),
        new("msi-mystic-light-gpu-board", "MSI Mystic Light graphics board", ["MYSTIC LIGHT"], [], "MSI Mystic Light"),
        new("zotac-spectra-gpu-board", "ZOTAC SPECTRA graphics board", ["SPECTRA"], [], "ZOTAC SPECTRA"),
        new("asrock-polychrome-gpu-board", "ASRock Polychrome graphics board", ["POLYCHROME", "PHANTOM GAMING", "STEEL LEGEND"], [], "ASRock Polychrome Sync"),
        new("sapphire-nitro-gpu-board", "Sapphire NITRO graphics board", ["NITRO", "TRIXX GLOW", "SAPPHIRE GLOW"], [], "Sapphire TriXX Glow"),
        new("powercolor-devil-gpu-board", "PowerColor RGB graphics board", ["RED DEVIL", "LIQUID DEVIL", "HELLHOUND", "DEVILZONE"], [], "PowerColor DevilZone or Keystone"),
        new("xfx-speedster-gpu-board", "XFX Speedster graphics board", ["SPEEDSTER", "MERC", "QICK", "SWFT"], [], null),
        new("pny-xlr8-gpu-board", "PNY XLR8 graphics board", ["XLR8", "EPIC X"], [], "PNY EPIC-X RGB"),
        new("palit-gamerock-gpu-board", "Palit GameRock graphics board", ["GAMEROCK", "THUNDERMASTER"], [], "Palit ThunderMaster / ARGB Sync"),
        new("galax-hof-gpu-board", "GALAX Hall of Fame graphics board", ["HALL OF FAME", "XTREME TUNER"], [], "GALAX Xtreme Tuner"),
        new("inno3d-ichill-gpu-board", "INNO3D iCHILL graphics board", ["ICHILL", "I CHILL"], [], null),
        new("colorful-igame-gpu-board", "Colorful iGame graphics board", ["IGAME"], [], null),
        new("maxsun-icraft-gpu-board", "Maxsun iCraft graphics board", ["ICRAFT"], [], null),
        new("yeston-gpu-board", "Yeston graphics board", ["YESTON", "GAMEACE"], ["1ED3"], null),
        new("manli-gpu-board", "Manli graphics board", ["MANLI", "GALLARDO"], [], null),
        new("leadtek-gpu-board", "Leadtek graphics board", ["LEADTEK", "WINFAST"], [], null),
        new("sparkle-gpu-board", "Sparkle graphics board", ["SPARKLE", "TITAN OC"], [], null),
        new("gainward-gpu-board", "Gainward graphics board", ["GAINWARD"], ["10B0"], null),
        new("dell-alienware-gpu-board", "Dell or Alienware OEM graphics board", ["ALIENWARE", "DELL"], ["1028"], null),
        new("hp-omen-gpu-board", "HP OMEN OEM graphics board", ["HP OMEN", "OMEN"], ["103C"], null),
        new("lenovo-legion-gpu-board", "Lenovo Legion OEM graphics board", ["LENOVO LEGION", "LEGION"], ["17AA"], null),
        new("acer-predator-gpu-board", "Acer Predator OEM graphics board", ["ACER PREDATOR", "PREDATOR BIFROST"], ["1025"], null),
        new("asus-gpu-board", "ASUS graphics board", ["ASUS", "ASUSTEK"], ["1043"], "ASUS Aura Sync"),
        new("gigabyte-gpu-board", "Gigabyte graphics board", ["GIGABYTE"], ["1458"], "Gigabyte RGB Fusion"),
        new("msi-gpu-board", "MSI graphics board", ["MSI", "MICRO STAR"], ["1462"], "MSI Mystic Light"),
        new("zotac-gpu-board", "ZOTAC graphics board", ["ZOTAC"], ["19DA"], "ZOTAC SPECTRA"),
        new("evga-gpu-board", "EVGA graphics board", ["EVGA"], ["3842"], "EVGA Precision LED Sync"),
        new("asrock-gpu-board", "ASRock graphics board", ["ASROCK"], ["1849"], "ASRock Polychrome Sync"),
        new("sapphire-gpu-board", "Sapphire graphics board", ["SAPPHIRE"], ["1DA2"], "Sapphire TriXX Glow"),
        new("powercolor-gpu-board", "PowerColor graphics board", ["POWERCOLOR"], ["148C"], "PowerColor DevilZone or Keystone"),
        new("xfx-gpu-board", "XFX graphics board", ["XFX"], ["1682"], null),
        new("pny-gpu-board", "PNY graphics board", ["PNY"], ["196E"], "PNY EPIC-X RGB"),
        new("palit-gpu-board", "Palit graphics board", ["PALIT"], ["1569"], "Palit ThunderMaster / ARGB Sync"),
        new("galax-gpu-board", "GALAX or KFA2 graphics board", ["GALAX", "KFA2"], [], "GALAX Xtreme Tuner"),
        new("inno3d-gpu-board", "INNO3D graphics board", ["INNO3D"], [], null),
        new("colorful-gpu-board", "Colorful graphics board", ["COLORFUL"], [], null),
        new("maxsun-gpu-board", "Maxsun graphics board", ["MAXSUN"], [], null)
    ];

    private static readonly (string FamilyId, string DisplayName, string[] Aliases)[] PeripheralVendors =
    [
        ("peripheral-evga-kingpin", "EVGA K|NGP|N GPU or controller", ["KINGPIN", "K NGP N"]),
        ("peripheral-asus", "ASUS / ROG peripheral or controller", ["ASUS", "ASUSTEK", "ROG", "TUF GAMING", "AURA"]),
        ("peripheral-msi", "MSI peripheral or controller", ["MSI", "MICRO STAR"]),
        ("peripheral-gigabyte", "Gigabyte peripheral or controller", ["GIGABYTE", "AORUS"]),
        ("peripheral-asrock", "ASRock peripheral or controller", ["ASROCK"]),
        ("peripheral-evga", "EVGA peripheral or controller", ["EVGA"]),
        ("peripheral-corsair", "Corsair peripheral or controller", ["CORSAIR"]),
        ("peripheral-logitech", "Logitech peripheral or controller", ["LOGITECH"]),
        ("peripheral-razer", "Razer peripheral or controller", ["RAZER"]),
        ("peripheral-steelseries", "SteelSeries peripheral or controller", ["STEELSERIES"]),
        ("peripheral-nzxt", "NZXT peripheral or controller", ["NZXT"]),
        ("peripheral-cooler-master", "Cooler Master peripheral or controller", ["COOLER MASTER"]),
        ("peripheral-lian-li", "Lian Li peripheral or controller", ["LIAN LI"]),
        ("peripheral-thermaltake", "Thermaltake peripheral or controller", ["THERMALTAKE"]),
        ("peripheral-gskill", "G.Skill peripheral or controller", ["G SKILL", "GSKILL"]),
        ("peripheral-hyperx", "HyperX peripheral or controller", ["HYPERX"]),
        ("peripheral-adata-xpg", "ADATA or XPG peripheral or controller", ["ADATA", "XPG"]),
        ("peripheral-apacer", "Apacer peripheral or controller", ["APACER"]),
        ("peripheral-antec", "Antec peripheral or controller", ["ANTEC"]),
        ("peripheral-bitfenix", "BitFenix peripheral or controller", ["BITFENIX"]),
        ("peripheral-crucial", "Crucial peripheral or controller", ["CRUCIAL"]),
        ("peripheral-ducky", "Ducky peripheral or controller", ["DUCKY"]),
        ("peripheral-endgame-gear", "Endgame Gear peripheral or controller", ["ENDGAME GEAR"]),
        ("peripheral-elgato", "Elgato peripheral or controller", ["ELGATO"]),
        ("peripheral-glorious", "Glorious peripheral or controller", ["GLORIOUS"]),
        ("peripheral-id-cooling", "ID-COOLING peripheral or controller", ["ID COOLING"]),
        ("peripheral-jonsbo", "Jonsbo peripheral or controller", ["JONSBO"]),
        ("peripheral-keychron", "Keychron peripheral or controller", ["KEYCHRON"]),
        ("peripheral-kingston-fury", "Kingston FURY peripheral or controller", ["KINGSTON", "FURY"]),
        ("peripheral-montech", "Montech peripheral or controller", ["MONTECH"]),
        ("peripheral-patriot-viper", "Patriot Viper peripheral or controller", ["PATRIOT", "VIPER"]),
        ("peripheral-redragon", "Redragon peripheral or controller", ["REDRAGON"]),
        ("peripheral-raijintek", "Raijintek peripheral or controller", ["RAIJINTEK"]),
        ("peripheral-silverstone", "SilverStone peripheral or controller", ["SILVERSTONE"]),
        ("peripheral-teamgroup", "TEAMGROUP or T-FORCE peripheral or controller", ["TEAMGROUP", "T FORCE"]),
        ("peripheral-varmilo", "Varmilo peripheral or controller", ["VARMILO"]),
        ("peripheral-vetroo", "Vetroo peripheral or controller", ["VETROO"]),
        ("peripheral-zalman", "Zalman peripheral or controller", ["ZALMAN"]),
        ("peripheral-wooting", "Wooting peripheral or controller", ["WOOTING"]),
        ("peripheral-zotac", "ZOTAC peripheral or controller", ["ZOTAC", "SPECTRA"]),
        ("peripheral-pny", "PNY peripheral or controller", [" PNY "]),
        ("peripheral-palit", "Palit peripheral or controller", ["PALIT"]),
        ("peripheral-gainward", "Gainward peripheral or controller", ["GAINWARD"]),
        ("peripheral-inno3d", "Inno3D peripheral or controller", ["INNO3D"]),
        ("peripheral-galax", "GALAX or KFA2 peripheral or controller", ["GALAX", "KFA2"]),
        ("peripheral-sapphire", "Sapphire peripheral or controller", ["SAPPHIRE"]),
        ("peripheral-powercolor", "PowerColor peripheral or controller", ["POWERCOLOR"]),
        ("peripheral-xfx", "XFX peripheral or controller", [" XFX "]),
        ("peripheral-turtle-beach", "Turtle Beach or Roccat peripheral or controller", ["TURTLE BEACH", "ROCCAT"]),
        ("peripheral-fractal", "Fractal Design peripheral or controller", ["FRACTAL DESIGN", "FRACTAL"]),
        ("peripheral-phanteks", "Phanteks peripheral or controller", ["PHANTEKS"]),
        ("peripheral-deepcool", "DeepCool peripheral or controller", ["DEEPCOOL"]),
        ("peripheral-arctic", "ARCTIC peripheral or controller", ["ARCTIC"]),
        ("peripheral-ek", "EK peripheral or controller", ["EKWB", "EK WATER BLOCKS"]),
        ("peripheral-aquacomputer", "Aqua Computer controller", ["AQUACOMPUTER", "AQUA COMPUTER"]),
        ("peripheral-be-quiet", "be quiet! peripheral or controller", ["BE QUIET"])
    ];

    private static HardwareCompatibilityMatch Recognized(string familyId, string displayName) => new(
        familyId,
        displayName,
        "Recognized for read-only inventory and capability reporting. This family match does not qualify any hardware write.",
        IsRecognized: true);

    private static HardwareCompatibilityMatch RecognizedGpuBoard(
        GpuBoardPartnerDefinition definition,
        string source)
    {
        string ecosystem = string.IsNullOrWhiteSpace(definition.RgbEcosystem)
            ? string.Empty
            : $" {definition.RgbEcosystem} is only a route hint when this exact model exposes a lighting controller.";
        return new HardwareCompatibilityMatch(
            definition.FamilyId,
            definition.DisplayName,
            $"The board partner is recognised from {source}.{ecosystem} This does not identify a GPU RGB controller or qualify native lighting output.",
            IsRecognized: true);
    }

    private static GpuBoardPartnerDefinition? FindGpuBoardPartnerByName(string identity) => GpuBoardPartners
        .FirstOrDefault(definition => definition.ExplicitAliases.Any(alias => HasIdentityAlias(identity, alias)));

    private static GpuBoardPartnerDefinition? FindGpuBoardPartnerByPciSubsystemVendor(string vendor) => GpuBoardPartners
        .FirstOrDefault(definition => definition.PciSubsystemVendorIds.Contains(vendor, StringComparer.OrdinalIgnoreCase));

    private static bool HasIdentityAlias(string identity, string alias) =>
        identity.Contains(Normalise(alias), StringComparison.Ordinal);

    private sealed record GpuBoardPartnerDefinition(
        string FamilyId,
        string DisplayName,
        string[] ExplicitAliases,
        string[] PciSubsystemVendorIds,
        string? RgbEcosystem);

    private static HardwareCompatibilityMatch Unclassified(string kind) => new(
        "unclassified",
        $"Unclassified {kind}",
        "No catalogue family matched the observed identity. RigPilot will retain raw inventory and will not infer control support.",
        IsRecognized: false);

    private static int? ExtractAmdDesktopSeries(string identity)
    {
        foreach (Match match in AmdModelPattern().Matches(identity))
        {
            if (int.TryParse(match.Groups["model"].Value, out int model))
            {
                int series = model / 1000 * 1000;
                if (series is 1000 or 2000 or 3000 or 4000 or 5000 or 7000 or 8000 or 9000)
                {
                    return series;
                }
            }
        }
        return null;
    }

    private static int? ExtractIntelCoreGeneration(string identity)
    {
        foreach (Match match in IntelGenerationPattern().Matches(identity))
        {
            if (int.TryParse(match.Groups["generation"].Value, out int generation))
            {
                return generation;
            }
        }
        return null;
    }

    private static int? ExtractNvidiaRtxSeries(string identity)
    {
        Match match = NvidiaRtxPattern().Match(identity);
        if (!match.Success || !int.TryParse(match.Groups["model"].Value, out int model))
        {
            return null;
        }
        return model / 100;
    }

    private static int? ExtractAmdRadeonSeries(string identity)
    {
        Match match = AmdRadeonPattern().Match(identity);
        if (!match.Success || !int.TryParse(match.Groups["model"].Value, out int model))
        {
            return null;
        }
        if (model is >= 400 and <= 599)
        {
            return 400;
        }
        return model / 1000 * 1000;
    }

    private static bool ContainsAny(string value, params string[] candidates) => candidates.Any(candidate => value.Contains(candidate, StringComparison.Ordinal));

    private static bool IsUsbOrHidIdentity(string? value) => !string.IsNullOrWhiteSpace(value)
        && (value.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("HID\\", StringComparison.OrdinalIgnoreCase));

    private static string Normalise(params string?[] values)
    {
        string raw = string.Join(' ', values.Where(value => !string.IsNullOrWhiteSpace(value))).ToUpperInvariant();
        string normalised = new(raw.Select(character => char.IsLetterOrDigit(character) ? character : ' ').ToArray());
        return $" {normalised} ";
    }

    [GeneratedRegex(@"(?<!\d)(?<model>[1-9]\d{3,4})(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex AmdModelPattern();

    [GeneratedRegex(@"(?<!\d)(?<generation>[6-9]|1[0-4])\d{3,4}(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex IntelGenerationPattern();

    [GeneratedRegex(@"\b2\d{2}(?:S|K|KF|F)?\b", RegexOptions.CultureInvariant)]
    private static partial Regex CoreUltra200Pattern();

    [GeneratedRegex(@"\bRTX\s+(?<model>[2-5]\d{3})\b", RegexOptions.CultureInvariant)]
    private static partial Regex NvidiaRtxPattern();

    [GeneratedRegex(@"\bRX\s+(?<model>[4-9]\d{2,3})\b", RegexOptions.CultureInvariant)]
    private static partial Regex AmdRadeonPattern();

    [GeneratedRegex(@"\bARC(?:\s+TM)?\s+B(?:\d{3})?\b", RegexOptions.CultureInvariant)]
    private static partial Regex ArcBPattern();

    [GeneratedRegex(@"\bARC(?:\s+TM)?\s+A(?:\d{3})?\b", RegexOptions.CultureInvariant)]
    private static partial Regex ArcAPattern();

    [GeneratedRegex(@"SUBSYS_[0-9A-F]{4}(?<vendor>[0-9A-F]{4})", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex PciSubsystemVendorPattern();
}
