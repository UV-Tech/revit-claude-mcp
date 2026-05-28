# Security Policy

## Supported Versions

Security fixes target the latest `main` branch until versioned releases are established.

## Reporting a Vulnerability

Please do not open a public issue for vulnerabilities.

Send a private report to the project maintainer once a public repository owner is assigned, or use GitHub private vulnerability reporting if it is enabled on the repository.

Include:

- RevitMCP version or commit.
- Operating system and Revit version.
- Steps to reproduce.
- Expected and actual impact.

## Localhost Boundary

RevitMCP is designed as a local bridge:

- The Revit addin listens on `localhost:6543`.
- Do not expose this port to a network.
- Do not run untrusted MCP clients against a live Revit project.

Bulk model operations can modify active Revit documents. Use dry-run tools first when available and keep project backups.
