# RGB control architecture — map, gaps, and a proposed unified model

Investigation deliverable, 2026-07-20. Written to explain why selecting the
**Sunset** colourway "did not apply", and to map the whole RGB control surface
before any fix. No code was changed to produce this document.

---

## 1. TL;DR — why Sunset did nothing

- **You clicked "Apply colourway", which is bridge-only.** Its enable condition is
  `OpenRgbEnabled && OpenRgbConnected && HasReadyOpenRgbRoutes && AreOpenRgbInputsValid`
  (`MainViewModel.cs:462`). The OpenRGB bridge is **Disabled**, so the button is
  inert. Clicking a disabled button does nothing and shows **no explanation** —
  the silent dead-button is the immediate bug.
- **Gradient colourways only reach hardware through the OpenRGB bridge (or Dynamic
  Lighting).** Your actual devices — Kraken, Aura, DIMM, Razer — are driven by the
  *native* path, which applies **one flat colour** (the `#RRGGBB` field), never a
  colourway. So even via "Sync everything", picking Sunset would have turned every
  native device **blue (`#4EA1FF`)**, not amber.
- **The gradient colours are computable client-side and simply unused for native
  devices.** `LightingColourways.Generate()` (`LightingColourways.cs:31`) already
  produces per-LED colours for every colourway including Sunset. Nothing bridges
  that data to a representative single colour for flat-colour devices.

The root problem is not "the bridge is off." It is that **colourways and native
devices were built as separate islands**, and on any machine not running OpenRGB,
part of the lighting UI silently does nothing.

---

## 2. Entry points (what each UI action actually triggers)

| UI action | Command | Path it drives | Works without the bridge? |
| --- | --- | --- | --- |
| **Apply colourway** (Static lighting) | `ApplyOpenRgbCommand` → `ApplyOpenRgbCoreAsync` | OpenRGB bridge only | **No** — button disabled |
| **Turn lighting off** (Static lighting) | `TurnOffOpenRgbCommand` | OpenRGB bridge only | No — button disabled |
| **Start ambience / Music mode** | ambient loop | OpenRGB bridge only | No |
| **Sync everything** | `SyncAllRgbCommand` → `ApplyUnifiedRgbAsync` | bridge → Dynamic Lighting → native, per endpoint | **Yes** (native tier) |
| **Enable the local OpenRGB SDK bridge** | checkbox | toggles `OpenRgbEnabled` + SDK connect | — |
| **Test local SDK server** | probe | OpenRGB SDK reachability | — |
| Dynamic Lighting per-device | WDL controls | Windows LampArray | Yes (WDL only) |

**Two different "apply" surfaces exist, and they behave very differently.**
"Apply colourway" is bridge-only and silently disabled when the bridge is off.
"Sync everything" is the graceful, multi-tier path. A user reasonably expects the
prominent "Apply colourway" button to *apply the colourway*; instead it is the
most fragile path.

---

## 3. Transport tiers

Three independent transports, in the order `Sync everything` tries them:

1. **OpenRGB SDK bridge** (`OpenRgbSdkClient`) — loopback `127.0.0.1:6742`, opt-in,
   never bundled. The **only** tier that renders per-LED **gradients**
   (`SetColourwayAsync` sends frames from `LightingColourways.Generate`). Off by
   default; requires the user to run OpenRGB.
2. **Windows Dynamic Lighting** (`DynamicLightingBridge`) — LampArray. Applies
   **static colour / static scene only** (`ApplyStaticColourAsync`,
   `ApplyStaticSceneAsync`). No gradient colourways.
3. **Native writers** (`KrakenX3LightingWriter`, `AuraUsbLightingWriter`,
   `RazerUsbRgbWriter`, DIMM/SMBus) via the service/adapter-host — each writes a
   **single flat colour** to an exact, audited device. No gradients. This is the
   tier that actually covers *your* hardware today.

**Consequence:** gradients are a property of exactly one optional, off-by-default
tier. Tiers 2 and 3 — the ones that work out of the box — are flat-colour only.

---

## 4. The evidence layer (this part is good)

`RgbRoutingPolicy.Assess` already produces per-endpoint route assessments with
honest states, and it is worth building *on* rather than replacing:

- `RgbRouteState`: `Ready`, `SetupRequired`, `ReadOnly`, `Blocked`, `Unsupported`.
- `RgbApplyOutcome` / `RgbApplyState`: `AppliedUnverified`, `AppliedVerified`,
  `Skipped`, `Blocked`, `Failed`, `Unknown` — truthful because most RGB has no
  colour read-back, so success is `AppliedUnverified` unless the endpoint proves
  otherwise.
- `RgbConflictPolicy.FindBlockingOwners` blocks a write when another lighting app
  owns the device.

So the system already models "which endpoints are ready, blocked, or owned" well.
The gap is upstream: **what colour/frame each ready endpoint receives**, and
**whether the user is told when nothing applied**.

---

## 5. The gaps (ranked)

1. **Silent dead button.** "Apply colourway" disabled with no feedback when the
   bridge is off. The user gets no signal and no next step.
2. **Colourway → native divergence.** A gradient colourway is ignored by native
   and Dynamic Lighting endpoints; they receive the stale manual colour. Selecting
   Sunset and syncing turns devices blue.
3. **Computable-but-unused gradient data.** `Generate` can produce a representative
   colour for any colourway; native devices never get it.
4. **Two apply surfaces with different power.** The obvious button (Apply
   colourway) is the weak one; the strong one (Sync everything) is a separate,
   less-obvious control.
5. **Selecting a gradient colourway doesn't update the colour field**, so the UI
   gives no hint that native devices will show something unrelated to the name.

---

## 6. Root cause, precisely

> "Apply colourway" is hard-bound to the OpenRGB bridge tier and disabled when that
> tier is unavailable, while the colourway concept (gradients) has no representation
> in the two tiers that cover the user's actual hardware. The gradient colour data
> exists client-side but is never lowered to a representative flat colour for those
> tiers, and the disabled state is surfaced with no explanation.

---

## 7. Proposed unified control model

Goal: **one "apply this look to everything available" that degrades gracefully and
always reports what happened.** Build on the existing evidence layer and the
Phase 1–3 standardization (`RgbColour`, `RgbWriteResult`, the route table).

**Step A — represent every colourway as a colour, not just a gradient.**
Add a `RepresentativeColour` to `LightingColourways.Colourway` (or derive it from
`Generate`'s midpoint). Sunset → a warm amber. This gives flat-colour tiers a
correct-hue fallback and lets a gradient colourway mean *something* everywhere.

**Step B — lower colourways into native/Dynamic Lighting tiers.**
When a colourway is selected and the bridge is unavailable, native and WDL
endpoints receive the colourway's `RepresentativeColour` (via the Phase-3 route
table), not the stale manual colour. Full per-LED gradient still requires the
bridge; single-colour devices get the right colour instead of nothing/blue.

**Step C — make "Apply colourway" degrade, not die.**
Instead of a disabled button, "Apply colourway" routes through the unified apply:
gradient-capable endpoints get frames, flat endpoints get the representative
colour, and a clear notice states what each tier did ("Full gradient needs the
OpenRGB bridge; applied Sunset's amber to 4 native devices"). No silent failure.

**Step D — one apply surface.**
Collapse "Apply colourway" and "Sync everything" into one action with a preview of
which endpoints will get a gradient vs a flat colour, driven by
`RgbRoutingPolicy.Assess`. The evidence layer already has the data.

**Sequencing.** A and C are small and remove the reported bug and the silent
failure. B is the real feature (colourways reach native hardware). D is a UX
consolidation and should come last. Each step is independently shippable and
testable, and none requires a wire/protocol change — the native path already
carries a flat colour; we are only changing *which* colour it carries.

---

## 8. What this does NOT change

- No wire/IPC contract changes (consistent with the Phase 1–3 constraint).
- No new dependency on OpenRGB; the bridge stays opt-in and unbundled.
- The conflict/ownership and read-back-honesty model is preserved as-is.
