# Compatibility

This file records evidence, not marketing claims.

## Development reference system

| Component | Detected reference |
| --- | --- |
| Operating system | Windows 11 x64, build 26200 |
| Motherboard | ASUS ROG Strix X570-E Gaming, BIOS 5031 |
| CPU | AMD Ryzen 7 5800X |
| GPU | NVIDIA GeForce RTX 3090 |

The reference system is used for read-only development probes. It is not yet certified for PC Helper hardware writes.

## Evidence levels

- **Verified:** exact device/controller family passed read-back, apply, verification, reset, fault, reboot, and soak checks on at least two independent systems.
- **Experimental:** bounded write path exists but has incomplete qualification.
- **ReadOnly:** monitoring is available; writes are intentionally unavailable.
- **Blocked:** another controller, missing privilege, driver policy, or safety condition prevents ownership.
- **Unsupported:** no adapter exposes the capability.

No CPU, GPU, fan, AIO, or RGB write capability is currently certified.
