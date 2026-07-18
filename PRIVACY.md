# Privacy policy

RigPilot is local-first and requires no account. The privileged Windows service makes no network connections.

## Data kept on the PC

RigPilot stores configuration, profiles, bounded sensor history, recovery journals, qualification evidence, and service logs locally. Service state is stored beneath `%ProgramData%\PCHelper`; per-user state and explicit exports are stored in the locations shown by the dashboard. Logs are bounded and do not intentionally include usernames, hostnames, serial numbers, unrelated installed applications, or full user file paths.

Uninstall restores controlled hardware to defaults before removing the runtime when verification succeeds. Recovery and evidence data may be retained so an incomplete restoration is not hidden. The user can remove retained data after the dashboard reports a known default state.

## Network activity

Network access occurs only after the user enables or invokes the corresponding action:

- **Check for updates:** the user-process dashboard sends one bounded HTTPS request to the public GitHub Releases API. It sends the normal connection metadata and a `RigPilot/<version>` user-agent; it downloads or installs nothing.
- **Compatibility reports:** reports are generated and redacted locally. No report is uploaded without an explicit preview and submission action. The current preview release does not silently submit telemetry.
- **OpenRGB:** an explicitly enabled bridge connects only to the user-operated loopback server at `127.0.0.1:6742`.
- **Razer Chroma SDK:** when the user invokes that route, the dashboard talks only to Razer Synapse's loopback REST endpoint.

RigPilot does not sell data, run advertising, require cloud services, or add network code to the privileged service. GitHub requests are subject to [GitHub's Privacy Statement](https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement). OpenRGB and Razer software are separately installed third-party programs governed by their own policies.

## Diagnostic and compatibility exports

The user controls where exported files are written and whether they are shared. The exporter removes direct personal identifiers and rejects sensitive fields, but hardware family, driver, firmware, capability, outcome, and build-provenance data may remain because those fields are necessary to evaluate compatibility.

## Questions and reports

Use [GitHub Issues](https://github.com/NiccTM/rigpilot/issues) for general privacy questions. Use [GitHub Security Advisories](https://github.com/NiccTM/rigpilot/security/advisories/new) for a potential disclosure vulnerability.
