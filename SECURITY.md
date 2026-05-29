# Security Policy

## Supported Versions

Security fixes target the latest `main` branch until versioned releases are established.

## Reporting a Vulnerability

Please do not open a public issue for vulnerabilities.

Send a private report to the project maintainer once a public repository owner is assigned, or use GitHub private vulnerability reporting if it is enabled on the repository.

Include:

- Revit Claude MCP version or commit.
- Operating system and Revit version.
- Steps to reproduce.
- Expected and actual impact.

## Localhost Boundary

Revit Claude MCP is designed as a local bridge:

- The Revit addin listens on `localhost:6543`.
- The MCP server accepts `REVIT_HOST` values only for `localhost`, `127.0.0.1`, or `::1` by default.
- Remote bridge URLs require the explicit opt-in `REVIT_ALLOW_REMOTE=1`; use that only for a trusted, isolated bridge.
- Do not include credentials in `REVIT_HOST`.
- Revit action paths are validated on both the Node MCP server and C# addin sides before dispatch.
- Do not expose this port to a network.
- Do not run untrusted MCP clients against a live Revit project.

Bulk model operations can modify active Revit documents. Use dry-run tools first when available and keep project backups.
