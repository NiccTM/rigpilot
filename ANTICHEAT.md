# Anti-cheat and antivirus posture

RigPilot is a local, offline hardware-control suite. It is not a cheat, a trainer, or an
overlay that touches games. This document states, precisely, what RigPilot does and does
not do, so that anti-cheat vendors, antivirus vendors, and users can assess it — and so a
false-positive report has the facts it needs.

No guarantee is offered that heuristic engines will never flag RigPilot; false positives
happen to signed, reputable software. What follows is the honest basis for review and the
steps taken to minimise the risk.

## What RigPilot never does

- **No game process access.** It never reads or writes another process's memory, never
  calls `WriteProcessMemory`/`ReadProcessMemory`/`CreateRemoteThread`/`VirtualAllocEx`, and
  never opens a game process for anything. Verified by absence in the source tree.
- **No injection or hooking of games.** No DLL injection, no `SetWindowsHookEx` into other
  processes, no import-table or inline patching. The on-screen display is a separate,
  transparent, non-activating desktop window plus the documented RivaTuner (RTSS) shared-
  memory bridge — the same non-injecting model MSI Afterburner uses — never an in-game hook
  placed by RigPilot.
- **No WinRing0 or inpout.** These vulnerable, widely-blocklisted kernel drivers are the
  usual reason a hardware tool is banned by anti-cheat and blocked by Microsoft's vulnerable-
  driver list. RigPilot does not ship or load them. The privileged register access it needs
  goes through **signed PawnIO**, a Microsoft-attested driver, and only for documented
  vendor operations.
- **No obfuscation or packing.** The binaries are ordinary managed .NET assemblies built by
  the standard SDK. Packers and obfuscators are themselves a top antivirus heuristic; RigPilot
  avoids them so the code is transparent to a scanner.
- **No silent elevation.** The dashboard runs `asInvoker` (see `src/PCHelper.App/app.manifest`).
  Privileged work is done by a separate, user-installed Windows service; hardware writes are
  transactional, acknowledged, and reversible.

## The one input-synthesis surface, and how it is guarded

RigPilot can record and replay desktop macros (keyboard and mouse) for productivity, using
the standard `SendInput` API in the signed-in user session. Synthesizing input is the only
RigPilot behaviour that could resemble a cheat.

It is guarded: **macro playback refuses to run while known anti-cheat software is active.**
Before the first synthesized event, `AntiCheatDetection` (in `PCHelper.Core`) checks the
running processes against a curated set of documented anti-cheat clients and kernel services
(Easy Anti-Cheat, BattlEye, Riot Vanguard, nProtect GameGuard, Xigncode3, FACEIT, ESEA,
HoYoverse mhyprot, Tencent ACE). If any is running, playback is blocked with a message and
no event reaches the OS. The guard fails safe: if the process list cannot be read, playback
is refused rather than allowed. Macros are for the desktop, never for defeating game input.

## Why some behaviours look privileged but are legitimate

- **Kernel driver (PawnIO).** Signed and attested; used only for documented sensor/RGB/SMU
  operations, never for game interaction. Keep to released PawnIO versions so its own
  signature and reputation carry.
- **Process termination** (`ConflictProcessTerminator`). Used only to stop a *conflicting
  writer* the user chose to close (e.g. a competing RGB app), gated on exact
  path/product/publisher/signer/hash identity — never a game or anti-cheat process.
- **Global hotkeys / session notifications.** `RegisterHotKey` and WTS session
  notifications are benign, in-process, and do not hook other applications.

## Reducing false positives — the checklist

1. **Sign every binary (the dominant lever).** Authenticode-sign all executables, DLLs, and
   installers. An **EV certificate** grants immediate SmartScreen trust; a standard OV cert
   accrues reputation over downloads. Unsigned binaries that touch hardware, spawn UAC
   children, and stage drivers are the classic antivirus heuristic trigger — signing removes
   it. This is tracked as the release keystone.
2. **Keep publisher metadata and the manifest on every binary.** CompanyName, ProductName,
   and version resources plus the embedded `asInvoker` manifest reduce "unknown publisher"
   heuristics. Enforced by `scripts\Test-DistributionHygiene.ps1`.
3. **Never pack or obfuscate** release binaries.
4. **After signing, submit for review:**
   - Microsoft — Windows Defender false-positive / software submission portal
     (`https://www.microsoft.com/wdsi/filesubmission`).
   - Major antivirus vendor false-positive portals as reports arrive.
   - Anti-cheat vendors (Easy Anti-Cheat, BattlEye) allowlist/whitelist requests, citing
     this document: no injection, no game memory, no WinRing0, signed driver, input
     synthesis blocked while anti-cheat is active.
5. **Publish the source and the qualification ledger.** RigPilot is GPL-3.0 and open; a
   reviewer can read exactly what it does. Link the repository in any submission.

## Observed: one real Defender detection, and what it was actually about

On 2026-07-20 Microsoft Defender reported `Trojan:Win32/Steanoz.Z!MTB` on this
development machine. It is worth recording precisely, because the honest reading
is not "RigPilot was flagged" — no shipped binary was involved.

The flagged resource was an **ad-hoc developer command line**, not a file:

```text
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command
  … [System.Reflection.Assembly]::LoadFile("…\nvapiwrapper.net\…\NvAPIWrapper.dll")
  … $asm.GetType("NvAPIWrapper.GPU.GPUCooler") … GetProperties() …
```

That was an assistant-issued exploratory command used to enumerate the
NvAPIWrapper cooler API surface. The `!MTB` suffix marks a machine-learning
behavioural detection, and the behaviour it matched is a textbook loader
signature: `-ExecutionPolicy Bypass` combined with `-NonInteractive`, reflective
`Assembly::LoadFile` of a DLL from a user-writable path, and runtime type
inspection. Malware droppers do exactly this. Defender was not wrong about the
shape; it was wrong about the intent.

Three things follow, and the third is the one that matters:

1. **Nothing was lost.** Defender terminated the command; the NvAPIWrapper
   package in the NuGet cache was untouched (verified by hash-relevant size and
   original 2020 timestamp, and by a clean publish immediately afterwards). "A
   threat or app was removed" referred to the in-flight process, not a file.
2. **The pattern does not exist in the product.** No RigPilot script uses
   `-ExecutionPolicy Bypass`, and the only reflective `LoadFile` in the tree is
   inside a Microsoft-generated Game Bar sideloading telemetry script from the
   packaging template.
3. **Investigating hardware APIs is itself detection-adjacent.** Reflecting over
   a GPU-control DLL from PowerShell looks, to a behavioural classifier, exactly
   like staging one. Prefer a throwaway compiled console project or an existing
   test for API exploration, rather than reflective loads from a bypassed-policy
   shell. This costs nothing and avoids generating detections that later have to
   be explained to a user.

This is also a preview of the reputation problem the release faces: the signal
that fired here was behaviour, not a signature, so signing (checklist item 1)
removes the "unknown publisher" half of the risk but not the behavioural half.
Anything RigPilot does that resembles staging code at runtime should be
avoidable by design, not by allowlisting.

## For users seeing a flag

A flag is almost always a heuristic false positive on an unsigned build. You can verify the
binary hash against the published release, read the source, and submit the file to your
antivirus vendor's false-positive portal. RigPilot performs no network access from its
service and uploads nothing without an explicit, per-action choice.
