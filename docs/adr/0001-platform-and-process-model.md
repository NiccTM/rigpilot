# ADR 0001: Windows, .NET 10, and split-process model

Status: Accepted

RigPilot targets Windows x64 with .NET 10. WPF provides a mature low-overhead desktop and tray stack. A Windows service owns privileged policy and a restartable adapter host contains native interop. Local named pipes avoid a remotely reachable control surface.

Windows 11 is primary. Windows 10 22H2 remains a compatibility target but receives an end-of-support warning.
