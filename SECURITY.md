# Security Policy

Report vulnerabilities privately through the repository security-advisory feature. Do not open public issues for vulnerabilities that could enable arbitrary privileged commands, named-pipe impersonation, unsafe hardware writes, or report-data disclosure.

PC Helper follows these rules:

- The privileged service accepts only typed, versioned commands from authorised local users.
- Profiles cannot contain scripts or executable paths.
- The service contains no network client.
- Unknown adapters and runtime plugins are not loaded.
- Missing or blocked privileged hardware access degrades to read-only operation.
- The application does not recommend disabling HVCI, vulnerable-driver blocking, Secure Boot, or antivirus protection.
