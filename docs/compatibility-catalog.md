# Read-only hardware compatibility catalogue

RigPilot classifies common desktop hardware families from Windows-reported CPU, GPU, SMBIOS, and USB/HID identity strings. A match improves inventory, diagnostics, and capability explanations. It does **not** enable fan, tuning, RGB, firmware, driver, or peripheral writes.

## Current family coverage

| Hardware | Recognised families | Result |
| --- | --- | --- |
| AMD desktop CPUs | Ryzen and Threadripper 1000 (Zen), 2000 (Zen+), 3000/4000 (Zen 2), 5000 (Zen 3), 7000/8000 (Zen 4), 9000 (Zen 5) | Read-only inventory and a blocked CPU-tuning eligibility card. |
| Intel desktop CPUs | Core 6th through 14th generation; Core Ultra 200 where the Windows name identifies the family | Read-only inventory and a blocked CPU-tuning eligibility card. |
| NVIDIA GPUs | GeForce GTX 700, 900, 10, 16; GeForce RTX 20, 30, 40, 50; RTX professional including Ada | Read-only inventory and eligibility. NVML telemetry/bounds are separately discovered when the installed driver exports them. |
| AMD GPUs | Radeon RX 400/500, 5000, 6000, 7000, 9000; Radeon Pro/Instinct; other Radeon | Read-only inventory and ADLX runtime eligibility only. |
| Intel GPUs | Arc A, Arc B, Arc Pro, other Arc, integrated Intel graphics | Read-only inventory and IGCL runtime eligibility only. |
| GPU-board partner | ASUS/ROG/TUF, Gigabyte/AORUS, MSI/Mystic Light, ZOTAC/SPECTRA, EVGA/K|NGP|N, ASRock/Polychrome, Sapphire/NITRO, PowerColor/Red Devil, XFX/Speedster, PNY/XLR8, Palit/GameRock, Gainward, INNO3D/iCHILL, GALAX/KFA2/Hall of Fame, Colorful/iGame, Maxsun/iCraft, Yeston, Manli, Leadtek, Sparkle, and common Dell/HP/Lenovo/Acer OEMs. Parent board partners are tagged only when Windows exposes a known PCI subsystem vendor; sub-brands require an explicit name. | Read-only board identity and RGB eligibility guidance only. It does not identify a GPU RGB controller or enable a vendor protocol. See [GPU-board RGB identity registry](gpu-board-rgb-registry.md). |
| Desktop boards | ASUS, MSI, Gigabyte, ASRock, Biostar, EVGA, Supermicro, Colorful, Maxsun; Dell, HP, Lenovo, Acer, Alienware OEM boards | Read-only SMBIOS inventory only. |
| RGB and peripherals | HID LampArray plus ASUS/ROG, MSI, Gigabyte/Aorus, ASRock, EVGA/K|NGP|N, Corsair, Logitech, Razer, SteelSeries, NZXT, Cooler Master, Lian Li, Thermaltake, G.Skill, HyperX, ADATA/XPG, Apacer, Antec, BitFenix, Crucial, Ducky, Endgame Gear, Elgato, Glorious, ID-COOLING, Jonsbo, Keychron, Kingston FURY, Montech, Patriot/Viper, Redragon, Raijintek, SilverStone, TEAMGROUP/T-FORCE, Varmilo, Vetroo, Zalman, Wooting, Turtle Beach/Roccat, Fractal, Phanteks, DeepCool, Arctic, EK, Aqua Computer, be quiet!, ZOTAC, PNY, Palit, Gainward, Inno3D, GALAX/KFA2, Sapphire, PowerColor, and XFX USB/HID identity strings | Read-only controller/lighting inventory only. Direct USB/HID protocols stay disabled until the exact controller passes containment, ownership, static-scene read-back, reset, and physical evidence gates. |

Recognition is deliberately conservative. An unknown name stays **Unclassified**, retains its raw inventory entry, and does not receive an inferred capability.

## RGB route selection

The Lighting page evaluates every discovered RGB endpoint through the following order. This is a control-routing decision, not a claim that a name-matched device is certified.

1. **Windows Dynamic Lighting** for a Windows-enumerated HID LampArray device. Windows owns app priority and the device must be enabled.
2. **Local OpenRGB bridge** only when the user explicitly enables a loopback SDK connection to an already-installed OpenRGB server and that server enumerates the controller.
3. **Qualified built-in adapter** only when the exact controller has a verified capability with apply, read-back, reset, and ownership evidence.
4. **Direct qualification / read-only** for every other recognised vendor family. RigPilot records inventory and explains the next test; it does not send a proprietary USB/HID command.

If another lighting writer is running, all overlapping Windows/OpenRGB routes are shown as blocked. RigPilot never terminates a lighting application merely to make RGB work.

## Evidence boundary

| Label | What it proves | What it does not prove |
| --- | --- | --- |
| Catalogue match | Windows identity resembles a common desktop family. | Exact board revision, controller, firmware, safe ranges, or write behaviour. |
| `ReadOnly` capability | Monitoring, inventory, or vendor-runtime eligibility can be shown. | Apply, read-back, rollback, reset, or ownership safety. |
| `Experimental` capability | A bounded write path has limited exact-device evidence. | Broad support or production qualification. |
| `Verified` capability | The exact certified evidence gate has passed. | Support for an adjacent model or firmware. |

## Source basis

The recognised generations reflect current official product families: AMD lists Ryzen 5000/7000/9000 desktop families and Zen 5 Ryzen 9000, Intel identifies Core Ultra 200S desktop processors and current Arc A/B driver coverage, NVIDIA lists GeForce RTX 20/30/40/50 series, AMD lists Radeon RX 9000 and prior desktop RX families, and Microsoft defines Dynamic Lighting around HID LampArray devices.

- [AMD Ryzen desktop processors](https://www.amd.com/en/products/processors/desktops/ryzen.html)
- [Intel Core Ultra 200S desktop processors](https://newsroom.intel.com/client-computing/core-ultra-200s-series-desktop)
- [NVIDIA GeForce RTX 50 series and prior generations](https://www.nvidia.com/en-us/geforce/graphics-cards/50-series/)
- [AMD Radeon desktop graphics](https://www.amd.com/en/products/graphics/desktops/radeon.html)
- [Intel Arc graphics driver coverage](https://www.intel.com/content/www/us/en/support/products/238847/graphics/processor-graphics/intel-graphics/intel-graphics-for-intel-core-ultra-processors-series-1.html)
- [Windows Dynamic Lighting devices](https://learn.microsoft.com/en-us/windows-hardware/design/component-guidelines/dynamic-lighting-devices)

## Adding a family safely

1. Add a precise name/SMBIOS/PNP rule to `HardwareCompatibilityCatalog`.
2. Add recognition and negative tests; do not make a generic vendor rule that can reclassify unrelated ACPI devices.
3. Keep the family `ReadOnly` until a reviewed adapter provides exact bounds, apply/read-back/reset behaviour, conflict ownership, and recovery.
4. Add signed, independent physical evidence before changing a hardware write claim.
