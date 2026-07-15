# Third-party notices

The project currently references or is designed to interoperate with:

- LibreHardwareMonitorLib 0.9.6, MPL-2.0.
- PawnIO and PawnIO modules when installed separately, GPL-2.0-or-later / LGPL-2.1 as applicable.
- Microsoft .NET and Windows APIs under their respective licences.
- Microsoft WebView2 SDK 1.0.4078.44 under the Microsoft software licence; used only by the isolated JavaScript lighting Effect Host. The Evergreen WebView2 Runtime is an external prerequisite and is not redistributed by this repository.
- NSec.Cryptography 26.4.0, MIT, and its libsodium runtime dependency, ISC; used only for Ed25519 adapter-pack signature verification.
- NvAPIWrapper.Net 0.8.1.101, LGPL-3.0-only (https://github.com/falahati/NvAPIWrapper); a managed NVIDIA NVAPI wrapper used by the optional NVAPI GPU-fan cooler transport. Used as an unmodified NuGet dependency; per LGPL, it may be replaced by a compatible build. GPU fan writes through it remain acknowledged-arm gated and Experimental.
- ScottPlot / ScottPlot.WPF 5.1.59, MIT (https://github.com/ScottPlot/ScottPlot), and its SkiaSharp rendering dependency, MIT; used by the dashboard for the sensor-comparison chart. Unmodified NuGet dependencies.
- HidSharp 2.x, Apache-2.0 (https://github.com/IntergatedCircuits/HidSharp), previously a transitive dependency of LibreHardwareMonitorLib and now also used directly for read-only HID peripheral enumeration inside the crash-isolated Adapter Host. Used as an unmodified dependency; it opens no device for output and exposes no write capability.
- OpenRGB through its external network protocol; OpenRGB is not bundled. The in-tree client follows `Documentation/OpenRGBSDK.md` and `RGBController/RGBController.h` at upstream commit `21009cdc45611ae3e0fd29b198f87bbe45f71a94` (reviewed 2026-07-12); no OpenRGB source code is copied.
- AMD ADLX, Intel IGCL, NVIDIA NVAPI/NVML, G-Helper, and Universal x86 Tuning Utility as future adapter sources subject to separate licence and provenance review.

Any copied or adapted low-level endpoint must record its upstream repository, commit, source file, licence, modifications, and qualification evidence before merge.
