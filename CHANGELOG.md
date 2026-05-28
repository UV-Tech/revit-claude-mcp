# Changelog

## 2.0.0

- Renamed the public project identity to Revit Claude MCP.
- Added a broader Revit MCP tool surface for model queries, exports, takeoffs, clashes, revisions, room finishes, family audits, and bulk operations.
- Hardened the Revit HTTP bridge with request serialization, clear timeouts, request size limits, method checks, JSON validation, and `/healthz`.
- Added `REVIT_TIMEOUT_MS` on the MCP server.
- Reworked documentation for public use.
