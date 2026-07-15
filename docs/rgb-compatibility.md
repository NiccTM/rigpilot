# RGB compatibility and manufacturer coverage

RigPilot cannot honestly promise direct RGB output for every manufacturer, model, firmware revision, and controller. The desktop RGB ecosystem uses a mixture of open HID, vendor software, and undocumented USB protocols. This implementation therefore guarantees a safe decision for every detected RGB-class device, not an unsafe direct-write attempt.

## Routing policy

| Route | When used | What can be controlled | Evidence required |
| --- | --- | --- | --- |
| Windows Dynamic Lighting | Windows enumerates a HID LampArray endpoint and it is enabled. | Per-lamp static colour, brightness, and saved physical zones. | Windows device discovery and a successful application attempt. Windows may still deny ownership due to its foreground/background priority policy. |
| Local OpenRGB SDK | The user enables the loopback bridge and an already-installed server enumerates the controller. | Static colour, brightness, and off state for controllers reported by that server. | Loopback protocol negotiation and exact controller enumeration for the current session. |
| Qualified built-in adapter | A reviewed adapter exposes the exact controller as `Verified`. | Only the capability’s typed output. | Apply, read-back, reset, containment, ownership, and certified physical evidence. |
| Direct qualification | A controller family is recognised but no qualified adapter exists. | Nothing; inventory and qualification workflow only. | Static-scene, read-back, reset, timeout/crash containment, and ownership tests for that exact device. |

The app shows these routes per endpoint in **Lighting → RGB compatibility routing**. `Ready` means the selected standard bridge is currently prepared; it does not make an adjacent product or firmware version certified. `Blocked` means another lighting writer owns the overlapping domain. `Read-only` means RigPilot intentionally will not send a raw USB/HID command.

For safety, RigPilot pauses Windows Dynamic Lighting while its own local OpenRGB bridge has active controller routes. It cannot prove the two paths address different physical LEDs, so it will not drive both concurrently.

## Recognised inventory families

The catalogue identifies common desktop RGB controller and peripheral names from ASUS/ROG, MSI, Gigabyte/Aorus, ASRock, EVGA/K|NGP|N, Corsair, Logitech, Razer, SteelSeries, NZXT, Cooler Master, Lian Li, Thermaltake, G.Skill, HyperX, ADATA/XPG, Apacer, Antec, BitFenix, Crucial, Ducky, Endgame Gear, Elgato, Glorious, ID-COOLING, Jonsbo, Keychron, Kingston FURY, Montech, Patriot/Viper, Redragon, Raijintek, SilverStone, TEAMGROUP/T-FORCE, Varmilo, Vetroo, Zalman, Wooting, Turtle Beach/Roccat, Fractal, Phanteks, DeepCool, Arctic, EK, Aqua Computer, be quiet!, ZOTAC, PNY, Palit, Gainward, Inno3D, GALAX/KFA2, Sapphire, PowerColor, and XFX.

For supported PCI subsystem identities, a GPU card also gets a separate board-partner tag. For example, a `SUBSYS_...19DA` NVIDIA card is labelled **ZOTAC graphics board**; EVGA K|NGP|N, AORUS, ROG, TUF, SPECTRA, Mystic Light, Polychrome, Red Devil, XLR8, GameRock, Hall of Fame, iCHILL, and Speedster are identified only when Windows or a bridge explicitly reports that board or sub-brand name. The tag is useful for the route matrix and qualification ledger, but it is not a protocol match. See [GPU-board RGB identity registry](gpu-board-rgb-registry.md) for the evidence boundary and full identity list.

This list is **recognition coverage**, not an implied direct protocol implementation. Unknown devices stay visible in raw Windows inventory; they do not become writable just because their vendor is popular.

## Why the order is constrained

Microsoft Dynamic Lighting is based on the open HID LampArray standard and provides device/app ownership semantics. It is the lowest-risk cross-manufacturer route when hardware implements the standard. [Microsoft Dynamic Lighting overview](https://learn.microsoft.com/en-us/windows/apps/develop/devices-sensors/lighting-dynamic-lamparray) and [device guidance](https://learn.microsoft.com/en-us/windows-hardware/design/component-guidelines/dynamic-lighting-devices) describe the standard, app priority, and compatible device classes.

OpenRGB exposes a network SDK that lets third-party applications control controllers the local OpenRGB server supports. Its own supported-device list marks devices as fully, partially, problematic, or unsupported, so RigPilot records the exact locally enumerated controller rather than extrapolating from the manufacturer name. [OpenRGB SDK](https://openrgb.org/sdk.html) and [supported-device legend](https://openrgb.org/devices) document that boundary.

## Adding a direct manufacturer adapter

1. Add a precise PnP/controller match and negative recognition tests.
2. Run the native protocol in an isolated Adapter Host with timeouts and crash recovery.
3. Prove static-scene apply, hardware read-back, firmware/default reset, ownership handoff, unplug recovery, and service-stop recovery on the exact model and firmware.
4. Require independent physical evidence before changing the route from `Read-only` to `Verified`.

Do not use vendor names, generic HID reports, or unreviewed packet captures as authority to write lighting hardware.
