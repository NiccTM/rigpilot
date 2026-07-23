# RigPilot adapter-pack SDK (.pcha) — format, containment, and signing

Status: **public draft, 2026-07-16.** The verifier described here has shipped since
0.6.0-beta.1 (`pchelper-cli pack-inspect` / `pack-install` / `pack-list` / `pack-remove`).
Production publisher-key enrolment and the community signing service are part of the
0.9 rollout; until a production Ed25519 publisher key is enrolled, only the explicit
development-trust route can install a pack (see "Trust routes" below).

## 1. What an adapter pack is

A `.pcha` file is a size-bounded ZIP archive containing a hardware adapter that
RigPilot loads **only after** cryptographic and structural verification. Packs let
the community add device families (RGB controllers, AIOs, peripheral protocols)
without touching the core suite — and without inheriting its write privileges:
every capability a pack surfaces still goes through the same capability-state,
arm-gating, and transaction machinery as built-in adapters.

Hard limits enforced by the verifier (`AdapterPackManager`):

| Limit | Value |
|---|---|
| Archive size | 64 MiB |
| Expanded size (total and per entry) | 128 MiB |
| Entries | 1–256 |
| Extension | `.pcha` (required) |
| Entry paths | relative, no `..`/`.`/empty segments, no rooted paths |

## 2. Archive layout

```
my-pack.pcha (ZIP)
├── manifest.json           # identity, protocol range, permissions, payload hashes
├── signature.ed25519       # Ed25519 signature over the exact bytes of manifest.json
└── adapter/…               # your payload files (every one listed in payloadHashes)
```

Rules the verifier enforces:
- `manifest.json` is mandatory; `signature.ed25519` holds a raw 64-byte or base64
  Ed25519 signature **over the manifest bytes exactly as stored in the archive**.
- Every file other than the manifest and signature MUST appear in
  `payloadHashes` with its lower-case hex SHA-256 — undeclared payloads,
  missing payloads, and hash mismatches are each hard errors.
- The declared `entryPoint` must exist in the archive.
- Duplicate entry names (case-insensitive) are rejected.

## 3. manifest.json (schema version 1)

JSON is parsed web-style (camelCase, case-insensitive); enums are strings.

```json
{
  "schemaVersion": 1,
  "id": "community.example.rgbfamily",
  "name": "Example RGB family",
  "version": "1.0.0",
  "publisher": "Example Community Author",
  "publisherKeyId": "example-author-2026",
  "licence": "GPL-3.0-only",
  "minimumProtocolVersion": 2,
  "maximumProtocolVersion": 2,
  "entryPoint": "adapter/Example.RgbFamily.dll",
  "supportedHardwareIds": ["usb:vid_1234&pid_5678", "usb:vid_1234&pid_56*"],
  "permissions": "Telemetry, Hid",
  "payloadHashes": {
    "adapter/Example.RgbFamily.dll": "<64 hex chars: sha256 of the file>"
  }
}
```

Field rules:
- `id`: lower-case letters, digits, dots, hyphens only. `id`+`version` becomes the
  install directory under the service data root (`AdapterPacks/<id>/<version>`),
  path-containment-checked.
- `minimumProtocolVersion`/`maximumProtocolVersion` must bracket the running suite
  protocol (currently **2**).
- `supportedHardwareIds`: at least one exact or wildcard hardware ID.
- `permissions` (flags — combine with commas): `Telemetry`, `HardwareWrite`,
  `PawnIo`, `Hid`, `RawUsb`, `VendorLibrary`, `DriverInstall`, `FirmwareUpdate`.
  Declaring `None` is rejected: a pack must state what it needs so users can
  refuse it. Declaring a permission does **not** grant it — write-class
  permissions additionally require the pack's containment-test evidence at
  signing time, and firmware updates remain disabled for development-trusted
  packs unconditionally.

## 4. Trust routes

1. **Publisher-signed (production).** `signature.ed25519` verifies against the
   enrolled Ed25519 public key matching `publisherKeyId`. Key enrolment ships
   with the 0.9 signing service: community packs are signed **after** automated
   containment tests pass. Until the production key set is enrolled, no pack can
   pass this route (the trusted-key dictionary is deliberately empty).
2. **Development trust (local testing only).** A DEBUG service build reads
   `PCHELPER_DEV_PACK_HASHES` (semicolon-separated lower-case hex SHA-256 of the
   whole `.pcha` file) and treats matching packages as trusted with a warning.
   Firmware updates stay disabled on this route. Release builds ignore the
   variable entirely.

## 5. Authoring workflow

1. Copy `docs/sdk-template/` and edit `manifest.template.json`.
2. Build your adapter payload into `payload/` (the template treats every file
   under `payload/` as a pack payload).
3. Run `scripts/New-AdapterPack.ps1 -TemplateRoot <copy> -Output my-pack.pcha`.
   The script computes all payload hashes, writes the final `manifest.json`,
   zips the archive, and prints the package SHA-256 for the development-trust
   allowlist. It does not sign — signing is the publisher service's job.
4. Verify locally: `pchelper-cli pack-inspect --file my-pack.pcha --json`
   (expect `valid=false` with only the signature error until signed/allowlisted).
5. Install on a DEBUG build:
   `$env:PCHELPER_DEV_PACK_HASHES = "<printed hash>"` then
   `pchelper-cli pack-install --file my-pack.pcha --confirm-development-trust`.

## 6. Safety contract for pack authors

Same rules as the core suite; violations fail review:
- No WinRing0 or any blocklisted/vulnerable driver. Privileged access is signed
  PawnIO modules or documented vendor APIs only.
- Never construct a voltage increase. Never write automatically or at startup;
  writes happen only inside prepare/apply/verify/rollback/reset with read-back.
- Native/fragile probing belongs in the contained Adapter Host child, not the
  service process.
- Declare the narrowest permission set that works; every hardware ID you claim
  must be one you have evidence for.
