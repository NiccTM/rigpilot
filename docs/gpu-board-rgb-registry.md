# GPU-board RGB identity registry

This registry separates four different facts that desktop-control utilities often blur together:

1. The GPU silicon vendor (for example NVIDIA, AMD, or Intel).
2. The graphics-board partner or OEM that assembled the card.
3. The marketing lighting ecosystem associated with some board lines.
4. A proven way to send a lighting command to the exact controller.

Only the first three are represented by the catalogue. The fourth requires an independently qualified control path.

## Evidence sources

RigPilot reads the PCI subsystem vendor portion of Windows' `SUBSYS_` hardware ID when it is known, then prefers an explicit board or sub-brand name when Windows reports one. AMD documents that the subsystem vendor part of the hardware ID identifies the graphics-card vendor; this is useful inventory evidence, but it is not a controller protocol or firmware identifier. [AMD graphics-card identification](https://www.amd.com/en/resources/support-articles/faqs/gpu-55.html)

The registry records the following documented lighting ecosystems as route hints only:

| Board family | Explicit sub-brand / ecosystem recognition | Important boundary |
| --- | --- | --- |
| Gigabyte | AORUS and RGB Fusion | Gigabyte documents RGB Fusion for compatible graphics cards, but a card label alone does not prove the exact controller or its firmware. [AORUS RGB Fusion product example](https://www.gigabyte.com/us/Graphics-Card/GV-N166SAORUS-6GD) |
| ASUS | ROG, TUF Gaming, Aura Sync | ASUS lists Aura graphics-card support, and its own documentation says effects vary by model. [ASUS Aura Sync](https://www.asus.com/campaign/aura/id/index.php) |
| MSI | Mystic Light | Mystic Light auto-detects compatible devices; the available effects depend on the detected device. [MSI Mystic Light guide](https://www.msi.com/support/technical_details/VGA_MSI_Utility_MysticLight) |
| ZOTAC | SPECTRA | ZOTAC describes SPECTRA as proprietary and limited to select hardware. [ZOTAC SPECTRA 2.0](https://www.zotac.com/us/news/spectra-20-lighting-capabilities?l=en) |
| EVGA | K|NGP|N and LED Sync | Precision X1 documents RGB control for supported cards/NVLink bridges, not every EVGA board. [EVGA Precision X1](https://www.evga.com/px1) |
| ASRock | Polychrome, Phantom Gaming, Steel Legend | ASRock's ARGB Link documentation limits compatibility to selected graphics-card models. [ASRock ARGB Link guide](https://download.asrock.com/Manual/ARGBQIG/Radeon%20RX%209070%20GRE%20Steel%20Legend%20Dark%2012GB%20OC.pdf) |
| Sapphire | NITRO and TriXX Glow | Sapphire documents Glow on applicable cards, including NITRO+ generations. [Sapphire TriXX](https://www.sapphiretech.com/en/software) |
| PowerColor | Red Devil, Liquid Devil, Hellhound, DevilZone / Keystone | PowerColor publishes separate utilities for exact Red Devil generations. [PowerColor downloads](https://www.powercolor.com/download24.htm) |
| PNY | XLR8 and EPIC-X | PNY documents EPIC-X lighting only on selected XLR8 editions. [PNY XLR8 REVEL product release](https://www.pny.com/company/press-center/pny-press-releases/pny-geforce-rtx-3050-expanding-pny-s-family-of-gpu-s) |
| Palit | GameRock and ThunderMaster | Palit documents ARGB and ThunderMaster lighting control for specific GameRock products. [Palit GameRock](https://www.palit.com/palit/vgapro.php?id=4578&lang=en) |
| GALAX / KFA2 | Hall of Fame and Xtreme Tuner | GALAX documents ARGB customization only for cards with ARGB features. [GALAX Xtreme Tuner](https://galax.com/en/software/xtreme-tuner-app/) |

Additional identity-only entries cover XFX/Speedster, Gainward, INNO3D/iCHILL, Colorful/iGame, Maxsun/iCraft, Yeston, Manli, Leadtek/WinFast, Sparkle, and common Dell/Alienware, HP OMEN, Lenovo Legion, and Acer Predator OEM boards. These entries deliberately have no native lighting adapter.

## Registry behaviour

The registry uses the following precedence:

1. An explicit sub-brand name, such as `AORUS`, `ROG STRIX`, `SPECTRA`, or `K|NGP|N`.
2. A known PCI subsystem vendor ID, which identifies only the parent board partner.
3. An explicit parent-brand name when Windows exposes it.
4. Otherwise `Unclassified`.

For example, a Gigabyte PCI subsystem ID becomes **Gigabyte graphics board**. It becomes **AORUS graphics board** only when the explicit AORUS name is present. This prevents the registry from inventing a premium line, RGB controller, or LED count from an ID that cannot provide that information.

Every recognised board is still `ReadOnly` until one of these paths enumerates the exact controller:

- Windows Dynamic Lighting through HID LampArray.
- A user-installed loopback OpenRGB server.
- A reviewed RigPilot adapter with bounded apply, read-back, reset, timeout/crash containment, and ownership evidence.

RigPilot does not launch a vendor tool, use an undocumented packet format, or claim a raw USB/HID write because a board brand was recognised.

## Current PCI subsystem mappings

The catalogue maps these parent identities where the subsystem vendor ID is established: ASUS (`1043`), Gigabyte (`1458`), MSI (`1462`), ZOTAC (`19DA`), EVGA (`3842`), ASRock (`1849`), Sapphire (`1DA2`), PowerColor (`148C`), XFX (`1682`), PNY (`196E`), Palit (`1569`), Gainward (`10B0`), Yeston (`1ED3`), Dell (`1028`), HP (`103C`), Lenovo (`17AA`), and Acer (`1025`).

The mapping is an inventory label. A subsystem ID identifies an add-in board vendor, not a vendor lighting controller, zone layout, firmware revision, or safe write protocol.
