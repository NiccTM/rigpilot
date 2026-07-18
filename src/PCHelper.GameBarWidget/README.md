# RigPilot Game Bar companion

This UWP/MSIX project is the optional, read-only Xbox Game Bar companion for
RigPilot `0.5.5-alpha`.

- It communicates only with `pchelper.useragent.v2`; it has no service-pipe,
  adapter-host, driver, or hardware-control path.
- The tray agent resolves the installed package SID and grants that SID a
  read-only named-pipe ACL. All other AppContainer clients are rejected.
- The widget can request only `GetOverlayStatus`. It cannot change profiles,
  run macros or scripts, control hardware, capture a display, or contact the
  network.
- Production packaging requires the release signing identity to replace the
  development manifest publisher, and the installer must restart the tray
  agent after MSIX installation so the exact package SID is reloaded.

Build with Visual Studio Build Tools after the UWP/MSIX workload is installed:

```powershell
& "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe" .\src\PCHelper.GameBarWidget\PCHelper.GameBarWidget.csproj /restore /p:Configuration=Debug /p:Platform=x64
```
