# Code signing policy

Free code signing provided by SignPath.io, certificate by SignPath Foundation.

RigPilot signs only release artifacts built from this repository by the release workflow. Every signing request requires manual approval. The signing identity is never used for third-party binaries, local developer builds, or artifacts that cannot be traced to a reviewed commit.

## Roles

- Committer and reviewer: [NiccTM](https://github.com/NiccTM)
- Signing approver: [NiccTM](https://github.com/NiccTM)

Contributions from other authors require review before merge. Changes to build scripts, release workflows, privileged service code, hardware mutations, rollback, and recovery require the same review standard as application code.

## Release controls

- CI builds every component from source and enforces one product version across the app, service, hosts, CLI, MSI, and bundle.
- A signing request is tied to the source commit and generated artifacts. Release approval remains a separate manual action.
- Signed releases retain SHA-256 checksums, an SPDX SBOM, and GitHub artifact provenance attestations.
- Unsigned public previews are compiled with every privileged-service mutation locked. They are monitoring and compatibility-report builds, not hardware-control releases.
- A release is not described as hardware-qualified unless its exact device families and evidence state are recorded in `COMPATIBILITY.md` and the qualification ledger.

## Privacy

See [PRIVACY.md](PRIVACY.md). This program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it. The privileged Windows service contains no network client.

## Reporting signing concerns

Report a suspected signature, provenance, or release-policy violation through [GitHub Security Advisories](https://github.com/NiccTM/rigpilot/security/advisories/new). Reports concerning a SignPath Foundation certificate may also be sent to `support@signpath.io`.
