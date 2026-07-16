# RigPilot localization — pipeline and workflow

Status: **scaffolding live, 2026-07-16.** The resource pipeline, culture switch,
per-key English fallback, and the first extracted string set (portable-mode UI)
ship in the app; bulk extraction of the remaining dashboard strings is the
follow-on mechanical work.

## How it works

- Neutral English strings live in `src/PCHelper.App/Localization/Strings.resx`;
  each language adds a satellite `Strings.<culture>.resx` next to it (German
  `Strings.de.resx` ships as the reference example). The .NET resource fallback
  chain returns English for any key a language has not translated yet, so a
  partially translated language is always safe to ship.
- Code resolves strings with `L10n.Get("Key")`; XAML uses
  `{loc:Loc Key}` (namespace `PCHelper.App.Localization`). A missing key
  renders as `[Key]` instead of crashing, making extraction gaps visible in UI
  review.
- Language selection: OS display language by default; `--culture <name>`
  (e.g. `--culture de`) overrides it at startup, applied process-wide via
  `L10n.ApplyCulture`.

## Extraction workflow (per string)

1. Move the literal into `Strings.resx` with a `Page_Purpose` key
   (`Portable_ServiceStatus`, `Cooling_ArmWarning`, …).
2. Replace the literal with `L10n.Get("Key")` (code) or `{loc:Loc Key}` (XAML).
3. Add the key to each satellite resx that has translations; leave it out
   where untranslated (fallback covers it).
4. Cover new keys in `tests/PCHelper.Integration.Tests/LocalizationTests.cs`
   when they carry safety-relevant wording — translated safety text must keep
   the precise meaning ("read-only", "Experimental", "not qualified") and gets
   the same public-language review as English.

## Target languages (0.9)

German (de), French (fr), Spanish (es), Portuguese-Brazil (pt-BR),
Polish (pl), Japanese (ja), Korean (ko), Simplified Chinese (zh-Hans).

## Rules

- Never machine-translate safety-critical strings without review; a wrong
  translation of an arm warning is a safety defect, not a cosmetic one.
- Keys are stable API: renaming a key orphans every satellite translation.
- Format insertions use composite formatting (`{0}`) so word order can differ
  per language; never concatenate translated fragments.
