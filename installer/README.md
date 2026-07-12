# Installer

The WiX 5 MSI installs the app, CLI, service, and adapter host as framework-dependent x64 binaries. WiX 5.0.2 is pinned because WiX 6 introduced the Open Source Maintenance Fee and WiX 7 requires an explicit EULA acceptance. The build must not accept third-party legal terms on a contributor's behalf.

The installer invokes a narrow, elevated service maintenance mode after files are installed. That mode uses `NetLocalGroupAdd` and `NetLocalGroupAddMembers` directly to create the local `PC Helper Operators` group and add the installing user. It accepts no arbitrary command or script. The group remains on uninstall so removing PC Helper cannot break another installation's access policy.

Build an MSI:

```powershell
.\scripts\build-installer.ps1
```

Build the MSI and Burn bundle after downloading the official .NET 10 Desktop Runtime x64 installer:

```powershell
.\scripts\build-installer.ps1 -RuntimeInstaller C:\path\windowsdesktop-runtime-10.0.x-win-x64.exe
```

The bundle detects a .NET 10 desktop runtime and installs the supplied official runtime only when needed. Release automation must Authenticode-sign the app, service, adapter host, MSI, and bundle. Development artifacts are unsigned and must not be published as Stable.

Windows 10 22H2 is compatibility-only and out of standard support. The release installer UI must retain that warning; the MSI does not block Windows 10.
