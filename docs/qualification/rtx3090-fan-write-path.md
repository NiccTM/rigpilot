# RTX 3090 GPU-fan write path — scoping and evidence plan

Status: **Steps 1-5 done. Two bounded live passes succeeded on the reference RTX 3090 — the second through the full in-product armed-service path (SingleSystem / Experimental evidence — not full qualification).**
Progress: transport feasibility confirmed on driver `610.62`; the fan adapter, transaction integration, and the triple-gated real NVML transport are built and unit-tested. On 2026-07-14 a single authorized bounded live pass (elevated, standalone runner with the env opt-in) commanded 60% then 80%: the physical fan rose 0%->56%->73%, read-back targets matched exactly, the driver automatic curve was restored, and `nvidia-smi` then read 0% at 44 C with zero WHEA/display-reset/nvlddmkm events. On 2026-07-15 a second bounded pass ran through the **real product path** — the LocalSystem service armed via `SetGpuFanControlArmed` (exact-device Experimental acknowledgement) and driven by `ApplyProfileV2` (75%/55%/90%): each transaction was **accepted and committed** (verification passed), the fan tracked each command, and disarm restored the automatic curve (`nvidia-smi` 0% at 49 C). This second pass also surfaced and fixed a real defect (see below). Both are bounded single-system passes; this is not repeated-cycle, soak, or second-system qualification, and the capability remains `Experimental`, not `Verified`.

**Defect found and fixed on the 2026-07-15 pass:** `NvmlGpuFanCoolerTransport.ReadStateAsync` hard-coded the returned fan-control policy to `Automatic` (the author believed NVML did not expose a policy getter). Because `NvidiaGpuFanAdapter.Verify` requires `Policy == Manual`, every end-to-end transactional GPU-fan write failed verification and rolled back — the first live pass only "worked" because the standalone runner called the transport's `SetManualDuty` directly, bypassing the transaction's `Verify`. Fixed by marshalling the read-only `nvmlDeviceGetFanControlPolicy_v2` getter and returning the real policy (degrading to `Automatic` only when a driver does not export it). After the fix, the armed-service `ApplyProfileV2` path verifies and commits.

Important finding: NVML fan control on this driver returns `NVML_ERROR_NO_PERMISSION` (4) when not elevated — the setter symbols are present and the operation is supported, but it requires elevation. A non-elevated attempt failed safe with no physical change. The Windows service runs as LocalSystem (elevated), so it satisfies this; a standard-user process does not.
Target capability: `nvml.fan:<deviceId>:<fan>` (currently `ReadOnly`, `RiskLevel.Critical`, conservative 50 % floor).
Reference board: ZOTAC RTX 3090, driver `32.0.16.1062`, NVML `610.62`, on the ASUS X570-E / Ryzen 5800X reference system.

This plan describes exactly what must be built and proven before that capability can move from `ReadOnly` to a bounded `Experimental` write, without violating any safety rule. It is the disciplined route to un-gating a single write; it does not enable one now.

## 1. Why this is the safest candidate

- A GPU fan cannot damage hardware at any commanded duty. The worst outcome of a bad write is under-cooling, which the GPU answers with thermal throttling and, at its limit, a driver-enforced protective shutdown — all recoverable.
- Reset is well defined: returning fan control to the driver/firmware automatic curve is a first-class operation.
- Evidence already exists: two controlled reference passes observed physical RTX 3090 fan response through the LibreHardwareMonitor path, and NVML has discovered the exact public fan range.

Contrast with the pump (rejected): a pump stall can thermal-runaway the CPU in seconds, has no audited USB/HID protocol, and zero-RPM is rule-prohibited.

## 2. The gap that is easy to under-state

**There is no fan *setter* today.** `NvmlTelemetryAdapter` is telemetry-only by name and design: every write method throws `NotSupportedException`, and NVML itself is primarily a *query* API. Setting a GPU fan requires one of:

- **NVAPI** `NvAPI_GPU_SetCoolerLevels` / the newer fan-control-policy cooler APIs (reverse-engineered/undocumented on consumer boards), or
- **NVML** `nvmlDeviceSetFanSpeed_v2` paired with `nvmlDeviceSetFanControlPolicy`, which exists only on some driver branches and must be probed for on the exact driver.

So this is **new native-interop code**, not a flag flip. Per safety rule 5 and the hardware-write policy ADR, any NVIDIA reverse-engineered write path must stay `Experimental` and driver-gated.

## 3. Required implementation (all against fake adapters first)

The write must replicate the proven `LibreHardwareMonitorAdapter` shape (`Prepare` → `Apply` → `Verify` → `Rollback` / `ResetToDefault`). Concretely, a new `NvidiaGpuFanAdapter` (or an extension of the NVML adapter with a real cooler transport) must implement:

1. **Exact-transport probe.** Detect whether the *installed driver* exposes a usable fan setter (NVAPI cooler entry or `nvmlDeviceSetFanSpeed_v2` + policy). If not present, the capability stays `ReadOnly` with a precise reason. Never assume a setter exists because a runtime is installed.
2. **Bounds from the device, not a constant.** Use the NVML-discovered `fanMinimum..fanMaximum`, clamped to the conservative restart-validated floor (currently 50 %). Reject any request outside `[floor, 100]`. Duty is `Manual Only` — never applied at startup, never automatic (rule 5 / rule 4).
3. **`Prepare`** captures rollback state: the current fan-control policy (auto vs. manual) and the current duty, so `Rollback` and `ResetToDefault` can restore the exact prior state — mirroring `ControlRollbackState` in the LHM adapter.
4. **`Apply`** sets manual policy then the clamped duty.
5. **`Verify`** reads back the achieved duty/RPM within a tolerance and a settling window; a mismatch fails the transaction.
6. **`Rollback`** restores captured policy+duty; **`ResetToDefault`** returns the fan to the driver automatic curve. A failed reset must fail toward the automatic curve and raise a safety event (rule 9 / rule 10).
7. **Conflict handling.** If MSI Afterburner / a vendor writer owns the fan, the capability converts to `Blocked` (rule 7); no takeover.

## 4. Evidence gate before `ReadOnly` → `Experimental` write

Following the `qualificationBlocker` string already in the adapter, un-gating requires all of:

| Evidence | Definition of done |
| --- | --- |
| Exact transport | **[MET, elevated]** `nvmlDeviceSetFanSpeed_v2` + `nvmlDeviceSetFanControlPolicy` present and callable on NVML `610.62` when elevated (NO_PERMISSION otherwise). |
| Apply | **[MET, 1 pass]** 60% and 80% commands produced measured fan rise 0%->56%->73%. |
| Read-back | **[MET, 1 pass]** target fan speed matched each command (60/60, 80/80). |
| Default reset | **[MET, 1 pass]** RestoreAutomatic returned targets to 30%; `nvidia-smi` then read 0% at 44 C. |
| Conflict | **[MET, fake-adapter + live]** competing writer -> `Blocked` (test); confirmed live on 2026-07-15 when MSI Afterburner / FanControl owned the fans (`OwnedByAnotherApplication`), and cleared to `Experimental` once they were closed. |
| Physical safety | **[MET, 2 passes]** 0 WHEA, 0 display-driver reset, 0 nvlddmkm events across both bounded passes; no thermal protective event. The 2026-07-15 repeated-cycle pass ran 20 writes with GPU temperature falling 55 C -> 44 C as the fan drove. |
| Acknowledgement | **[MET, 1 pass]** the 2026-07-15 pass armed via `SetGpuFanControlArmed` with exact-device Experimental acknowledgement, then wrote through `ApplyProfileV2` (`ConfirmExperimental` + `ConfirmedDeviceIds`); no env opt-in was used. |
| End-to-end verify | **[MET, 1 pass]** after the policy read-back fix, three armed-service transactions (75/55/90%) were accepted and committed — verification (`Policy == Manual` and duty within 5%) passed on real hardware. |
| Repeated cycles | **[MET, 1 session]** 2026-07-15 repeated-cycle pass: 20/20 armed-service `ApplyProfileV2` writes committed across duties 50-100%, the fan tracked 16/16 Phase-A commands, and 4/4 full arm -> write -> disarm cycles succeeded, each ending on the automatic curve. This is one multi-cycle session, not the multi-day soak. |
| Rollback | **[MET, fake-adapter]** injected verification failure restores prior policy+duty (integration test). Not naturally reproducible live: the real transport reads back the true target, so a healthy fan never fails `Verify`. A live rollback would require an out-of-service elevated harness with a deliberately-failing sibling action — artificial and not yet run. |

Remaining before a `Verified` (not `Experimental`) claim: longer soak (10 active-use hours across 7 calendar days), 3 clean cold boots, and a second independent system. Per change discipline, one multi-cycle session is `Passed a bounded pass`, not `stable`.

### Arm/apply race found during the 2026-07-15 repeated-cycle pass — FIXED and live-verified

`SetGpuFanControlArmed` applied the armed state synchronously but **backgrounded** the
capability re-probe (`Task.Run(RefreshAsync)` in `PCHelperRuntime`, added so the arm
response returns before the client timeout). The command therefore returned success
before the capability snapshot flipped `gpufan.duty:0` from `ReadOnly` to `Experimental`,
so an `ApplyProfileV2` issued *immediately* after arm was rejected as read-only until the
background refresh landed (~1-2 s). A human clicking Arm then Apply never hit this, but
rapid scripted arm->apply did (0/4 zero-settle cycles in the first repeated-cycle run).

**Fix (deployed 2026-07-15, `0.4.0-alpha-20260715-102042`):** after applying the armed
flag the arm handler synchronously re-probes just the GPU-fan adapter and patches its
capabilities into the cached snapshot before returning, reusing the coordinator's
conflict-ownership resolution (so a competing writer still yields `Blocked`, not a false
`Experimental`); the full every-adapter re-probe stays backgrounded. Live re-verified on
the reference RTX 3090: **8/8 zero-settle arm->apply cycles committed** and 3/3
back-to-back applies under a single arm, versus 0/4 before the fix.

Absence of a failure report is **not** qualification (per change discipline). Provisional status still requires the standard 10 active-use hours, 7 calendar days, and 3 clean cold boots.

## 5. Build order (each step independently verifiable, no live write until the last)

1. **[DONE] Transport probe (read-only).** `NvmlTelemetryAdapter` detects `nvmlDeviceSetFanSpeed_v2` + `nvmlDeviceSetFanControlPolicy` by *symbol presence only* (never marshalled) and surfaces a `nvml.fan-transport:<device>` read-only feasibility card. Real probe: driver `610.62` exports both, so NVML is a viable transport (NVAPI not required). *(real-probe confirmed; write methods hard-throw, test-guarded)*
2. **[DONE] Adapter skeleton against a fake cooler.** `NvidiaGpuFanAdapter` implements `Prepare/Apply/Verify/Rollback/ResetToDefault` over `IGpuFanCoolerTransport`. Read-only by default; `Experimental` only with `enableWrites`. 12 fake-adapter tests cover floor/ceiling rejection, manual apply, read-back verify, verify-mismatch, rollback-to-prior, rollback-to-auto, reset-to-auto, conflict→Blocked, unchecked-value refusal, and bounds-unavailable refusal.
3. **[DONE] Transaction integration.** The real adapter runs through the real `ProfileTransactionEngine` (domain `Cooling`): commits on success, rolls the fan back to automatic on verification failure. *(2 integration tests)*
4. **[DONE] Guarded real transport, still gated.** `NvmlGpuFanCoolerTransport` is the only class that marshals the NVML setters, and it is triple-gated: `enableWrites` flag, the `PCHELPER_GPUFAN_REAL_TRANSPORT=1` operator opt-in checked at call time, and driver setter presence. A real bench preflight read bounds `[50,100]%` and duty `32%` and confirmed the write refuses without the opt-in. *(gate tests + real-hardware preflight)*
5. **[NOT DONE] Controlled live pass.** Only after the operator explicitly authorises it and competing writers are closed: set the opt-in, run a bounded low-then-high duty pass on the reference board with WHEA/display-reset monitoring, capture the §4 evidence table. If clean, promote the exact capability to `Experimental` write with the acknowledgement gates. **This is an irreversible physical action and must be surfaced for explicit approval before execution.**

## 6. What stays unchanged / prohibited

- Voltage and clock/VF writes remain out of scope; this is fan-duty only.
- No automatic or at-startup fan write; `Manual Only` with per-session, per-device acknowledgement.
- Zero-RPM stays disabled; the conservative floor holds until restart validation exists.
- The service still performs no network access; nothing here changes the trust boundary.

## 7. First actionable slice

Step 1 (read-only exact-transport probe) is the only part that ships without any write and immediately improves honesty of the capability card. Recommend implementing that first, gated and tested, then re-evaluating before step 2.
