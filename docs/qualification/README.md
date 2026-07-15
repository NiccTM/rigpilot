# Hardware qualification ledger

This folder contains machine-level evidence for the RigPilot 1.0 release gate.
It is not a compatibility marketing list.

Each record uses a random `systemId`; do not use a hostname, serial number,
Windows account, or network identity. A record must come from a signed
production build and include exact controller, firmware, and driver versions.

Run the local evaluator with:

```powershell
dotnet run --project src/PCHelper.Cli -- qualification --ledger docs/qualification/reference-system.json --json
```

The evaluator requires all of the following before it reports `CanReleaseV1`:

- 18 distinct physical systems tested using signed production binaries.
- Ryzen Zen 3, Zen 4, Zen 5; Intel 12th, 13th/14th, and Core Ultra 200.
- RTX 30/40/50, RX 6000/7000/9000, and Arc A/B.
- ASUS, MSI, Gigabyte, and ASRock on at least one AMD and one Intel platform.
- Two successful, signed, independent physical-system reports for every
  controller family claimed as write-capable.
- No BSOD/unexpected reboot, stuck fan, unauthorised write, or failed rollback.

`reference-system.json` is deliberately incomplete. It records the current
single-system evidence without turning it into a broad support claim.
