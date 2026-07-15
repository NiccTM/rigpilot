# CPU tuning (AMD Ryzen / Intel) and Intel Arc GPU control — research and build plan

Status: **Research + safe scaffolding only. No live CPU voltage/tuning write is implemented or enabled. Intel support is read-only feasibility detection.**

This records deep research into the two "build-from-scratch" gaps and the exact, safety-compliant plan for each. It deliberately stops short of any privileged CPU write, per non-negotiable rule 5 and the "keep blocked until audited" policy.

## 1. AMD Ryzen (Zen) CPU tuning

### 1.1 What the ecosystem actually uses
- **The privileged layer already exists as open source.** `namazso/PawnIO.Modules` ships **`RyzenSMU.p`**, a module for the **signed PawnIO** kernel driver that implements the AMD SMU (System Management Unit) mailbox protocol plus MSR/SMN access. PawnIO natives include `read_msr`, MSR/CR writes, `cpuid`, `pci_config_read`, `physical_read`. This is the **WinRing0-free** path and is exactly what our safety rules require (signed driver, not a vulnerable blocklisted one).
- Reference implementations: `irusanov/ZenStates-Core` (Windows, **WinRing0-based → excluded**), `leogx9r/ryzen_smu` (Linux kernel driver), PBO2 Tuner (Windows). The Linux driver and ZenStates document the MP1/HSMP mailbox layout and SMU command IDs per Zen family.
- **What "tuning" means concretely:** Curve Optimizer (per-core VF-curve offsets, typically negative for undervolt), and PBO limits (PPT/TDC/EDC watts/amps, PBO scalar). These are executed as **SMU mailbox commands** and are **per-family** (Zen 2 / Zen 3 / Zen 4 mailbox addresses and command IDs differ).

### 1.2 Why this stays BLOCKED (not built as a live write)
- **Rule 5:** never increase voltage automatically or at startup; manual positive voltage needs exact vendor bounds + a fresh per-session device acknowledgement. Curve Optimizer is VF-curve manipulation; an over-aggressive negative offset is a *stability* hazard, not a bricking one, but it reliably produces **WHEA machine-check errors, silent data corruption, and hard freezes** — which is precisely the "no unauthorised/unqualified write" line.
- **No audited bounds or reset evidence.** Safe application needs: exact per-CPU SMU mailbox map, validated offset bounds, read-back of the applied curve, a guaranteed default-reset (restore stock curve), and crash-recovery (a bad offset can prevent boot → needs a boot-time revert sentinel). None of this is qualified for the reference 5800X.
- Our `VendorControlEligibilityAdapter` already surfaces **"AMD Zen tuning" as `Blocked`** with this exact rationale, and **"AMD Zen transport feasibility" as `ReadOnly`**. Research confirms those labels are correct.

### 1.3 The safety-compliant build plan (future, gated — mirrors the GPU-fan path)
1. **Read-only SMU/PawnIO feasibility** (safe now): detect signed PawnIO + `RyzenSMU.p`, read the SMU interface version and current PBO/curve values read-only. No write. (Today this is covered by the Blocked/ReadOnly eligibility cards.)
2. **`ISmuTuningTransport` seam + fake** (safe): the same pattern as `IGpuFanCoolerTransport` — prepare/apply/verify/rollback/reset over an injectable transport, fake-tested for bounds clamping, offset-range rejection, rollback, and reset-to-stock. **No real SMU write in the transport.**
3. **Boot-recovery sentinel** (mandatory before any live write): a persisted "pending CPU tune" journal that reverts to stock on the next boot if the machine didn't post cleanly — the CPU analogue of the fan's fail-to-firmware behaviour.
4. **Acknowledged arm + Experimental gate** (same as GPU fan): `SetCpuTuningArmed` requiring `ConfirmExperimental` + exact-CPU confirmation; read-only until armed.
5. **Controlled live pass** (only after 1–4 + explicit operator authorisation): a single small negative curve offset on one core, stability-screened, WHEA-monitored, with verified stock restore. This is an irreversible-ish physical action and must be surfaced for approval — never automatic.

**Bottom line:** the driver layer is available and rule-compliant, but a live Ryzen tuner is a multi-step, boot-recovery-gated, physically-qualified effort — not a dependency to drop in. It stays `Blocked`.

## 2. Intel Arc / Xe GPU control (IGCL)

### 2.1 Findings
- **IGCL** (`intel/drivers.gpu.control-library`, MIT) ships as **`ControlLib.dll`** with the Intel graphics driver. Handle-based API: `ctlInit()` → enumerate devices → typed handles. Telemetry (temperature/power/frequency/fan) is **read-only and safe**; there is also an Overclock API (write).
- **No official C# wrapper** — Intel provides C/C++ headers + `cApiWrapper.cpp` (dynamic `ControlLib.dll` loading). A managed consumer must P/Invoke, marshalling several nested version-tagged structs (`ctl_init_args_t`, telemetry structs), all of which are **64-bit only**.

### 2.2 What was built now (safe, verifiable)
- **`IntelGraphicsControlAdapter`** — a **read-only feasibility detector** that checks for `ControlLib.dll` and its `ctlInit` export by *presence only* (never calls `ctlInit`, never creates an IGCL handle, exposes no write). On Intel systems it surfaces a `ReadOnly` `igcl.feasibility` card; on non-Intel systems (like the RTX 3090 reference machine) it stays silent and fails safe. Wired into the service/CLI/App; write methods throw. Tested.
- **Why not the full telemetry adapter yet:** the reference machine has **no Intel GPU**, so complex IGCL struct-marshalling would be shipped **untested** into the LocalSystem service — a worse outcome than not shipping it. Presence detection is honest, verifiable, and low-risk.

### 2.3 Build plan for full Intel support (needs an Intel Arc test system)
1. P/Invoke `ctl_init_args_t` + `ctlInit`/`ctlClose` with the exact `Size`/`Version` fields; verify `ctlInit` succeeds on a real Arc driver.
2. Read-only telemetry: `ctlEnumerateDevices` → temperature/power/frequency/fan structs → `SensorSample`s. Promote `igcl.feasibility` to real telemetry.
3. Only after telemetry read-back is proven: a gated Overclock transport following the GPU-fan acknowledged-arm pattern, with bounds/read-back/reset and physical qualification.

## 3. What did not change
- No CPU/GPU tuning voltage or clock write is enabled. The takeover signature gate, pump protections, and the GPU-fan acknowledged-arm gate are unchanged.
- The AMD Zen and Intel CPU tuning capabilities remain `Blocked`; Intel Arc GPU control is now `ReadOnly` feasibility instead of silent.

## Sources
- PawnIO modules / RyzenSMU.p — https://github.com/namazso/PawnIO.Modules ; PawnIO driver — https://github.com/namazso/PawnIO
- ZenStates-Core (WinRing0, excluded) — https://github.com/irusanov/ZenStates-Core ; ryzen_smu — https://github.com/leogx9r/ryzen_smu
- Intel IGCL — https://github.com/intel/drivers.gpu.control-library ; spec — https://intel.github.io/drivers.gpu.control-library/
