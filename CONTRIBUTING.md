# Contributing

Hardware-control changes require more evidence than ordinary UI changes.

1. Open an issue describing the exact hardware identifiers, firmware, driver, and competing control software.
2. Add or update adapter probe, read-back, apply, verify, and reset tests.
3. Attach a redacted `pchelper-cli probe --json` result or compatibility report.
4. Keep new write controls `Experimental` until two independent physical systems pass qualification.
5. Do not add WinRing0, arbitrary command execution, silent process termination, or instructions to weaken Windows security.

Run `dotnet build`, `dotnet test`, and the read-only CLI probe before submitting a change.
