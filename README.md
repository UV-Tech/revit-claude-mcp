<div align="center">

# Revit Claude MCP

### Control Autodesk Revit from Claude with a local, open-source MCP bridge.

[![MCP Server](https://img.shields.io/badge/MCP-server-111827)](https://modelcontextprotocol.io/)
[![Revit](https://img.shields.io/badge/Revit-2026-186BFF)](https://www.autodesk.com/products/revit/)
[![Node](https://img.shields.io/badge/Node.js-20%2B-339933)](https://nodejs.org/)
[![.NET](https://img.shields.io/badge/.NET-8-512BD4)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

**Revit Claude MCP lets Claude read, modify, export, audit, and coordinate Revit models through 40 local tools.**

[Quick Start](#quick-start) - [Features](#features) - [Example Prompts](#example-prompts) - [Architecture](#architecture) - [Contributing](CONTRIBUTING.md)

</div>

---

## Why This Exists

Revit automation is powerful, but usually locked behind custom scripts, addins, and repetitive UI work. Revit Claude MCP turns Revit into a local MCP tool surface so Claude can help with real BIM workflows:

- Query model elements, rooms, views, sheets, families, and parameters.
- Create and modify Revit elements with controlled write tools.
- Export PDFs, DWGs, IFC, schedules, clash reports, and material takeoffs.
- Automate sheet setup, revisions, room finishes, view templates, and bulk parameters.
- Keep everything local: Claude talks to a Node MCP server, which talks to a localhost Revit addin.

## Quick Start

### 1. Clone and Install

```powershell
git clone https://github.com/UV-Tech/revit-claude-mcp.git
cd revit-claude-mcp
.\install.ps1
```

The installer builds the Node MCP server, builds the Revit addin, and copies the addin files to:

```text
%APPDATA%\Autodesk\Revit\Addins\2026\
```

### 2. Open Revit

1. Open Revit 2026.
2. Open your `.rvt` project.
3. Check the health endpoint below, or call `revit_health_check` from your MCP client.

Health check:

```text
http://localhost:6543/healthz
```

The health response includes service version, startup time, uptime, queue timeout, Revit API timeout, and request-size limit so MCP clients can diagnose bridge state before running model operations.

The addin no longer shows a startup dialog by default. If you want a visible startup confirmation, set `REVIT_MCP_SHOW_STARTUP_DIALOG=1` before launching Revit.

### 3. Connect Claude

Add this to your Claude Desktop or Claude Code MCP config, adjusting the path:

```json
{
  "mcpServers": {
    "revit": {
      "command": "node",
      "args": ["C:/path/to/revit-claude-mcp/mcp-server/dist/index.js"],
      "env": {
        "REVIT_HOST": "http://localhost:6543",
        "REVIT_TIMEOUT_MS": "120000"
      }
    }
  }
}
```

Optional safety override: `REVIT_HOST` is intentionally restricted to `localhost` / `127.0.0.1` by default. If you intentionally proxy a trusted remote Revit bridge, set `REVIT_ALLOW_REMOTE=1` explicitly.

Before running model tools, call `revit_health_check` to confirm Claude can reach the loaded Revit addin.

## Features

| Area | What Claude Can Do |
| --- | --- |
| Model intelligence | Read project info, levels, rooms, elements, families, views, sheets, and parameters |
| Element operations | Create walls, place family instances, set parameters, move elements, delete elements |
| Sheets and views | Create sheets, place views, activate views, apply templates, rename in bulk, duplicate views |
| Exports | Export PDF, DWG/DXF, IFC, and schedules |
| Takeoffs | Generate material quantity tables, export CSV, create live material schedules |
| Coordination | Detect clashes and export HTML/CSV clash reports |
| Revisions | Add revisions, stamp sheets, set issue dates, export issue PDFs |
| Room data | Read and bulk-set floor, wall, ceiling, base finishes, and comments |
| Model health | Audit families, find unused content candidates, bulk import CSV parameter data |
| Commands | Run Revit postable commands and launch Dynamo |

## Example Prompts

```text
List every door on Level 2, group them by type, and show missing fire-rating parameters.
```

```text
Generate a material takeoff for all walls and floors on Level 1 and export it to C:\Reports\takeoff.csv.
```

```text
Check for clashes between structural columns and MEP ducts, then export an HTML report to C:\Reports\clashes.html.
```

```text
Create sheets for every floor plan view whose name starts with "FP -", use prefix A-1, and skip existing sheets.
```

```text
Issue all sheets for construction with revision "Issued for Construction", date 2026-05-28, then export PDFs.
```

## Architecture

```text
Claude / MCP client
  |
  | stdio
  v
Node.js MCP server
  |
  | HTTP JSON on localhost:6543
  v
C# Revit addin
  |
  | ExternalEvent on Revit UI thread
  v
Autodesk Revit 2026 API
```

The Revit API requires UI-thread access. The addin receives local HTTP requests, serializes them safely, and dispatches work through `ExternalEvent`.

## Requirements

| Tool | Version |
| --- | --- |
| Autodesk Revit | 2026 |
| Windows | 10 or 11 |
| Node.js | 20, 22, 24, or 25 |
| .NET SDK | 8.x |
| Claude | Claude Desktop, Claude Code, or another MCP client |

## Repository Layout

```text
.
├─ mcp-server/                 # TypeScript MCP server
│  ├─ src/index.ts             # MCP tool definitions
│  └─ src/bridge.ts            # Local Revit HTTP bridge client and safety validation
├─ revit-addin/RevitMCP/       # C# Revit addin
│  ├─ App.cs                   # Addin entry point
│  ├─ RevitHttpServer.cs       # Localhost HTTP bridge
│  └─ RevitEventHandler.cs     # Revit API actions
├─ install.ps1                 # Windows installer/build helper
├─ ROADMAP.md
├─ CONTRIBUTING.md
└─ SECURITY.md
```

## Build Manually

Build/test the MCP server:

```powershell
cd mcp-server
npm install
npm run build
npm test
```

Build the Revit addin:

```powershell
cd revit-addin\RevitMCP
dotnet build -c Release
```

## Safety Model

Revit Claude MCP is a local bridge:

- The addin listens on `localhost:6543`.
- The MCP server blocks remote `REVIT_HOST` values unless `REVIT_ALLOW_REMOTE=1` is explicitly set for a trusted bridge.
- Both the MCP server and the C# addin validate Revit action paths before dispatching work.
- Do not expose the port to a network.
- Use dry-run tools first when available.
- Keep backups before bulk write operations.

## Troubleshooting

| Problem | Fix |
| --- | --- |
| Claude cannot reach Revit | Make sure Revit is open, a model is loaded, and `http://localhost:6543/healthz` responds |
| Need visible startup confirmation | Set `REVIT_MCP_SHOW_STARTUP_DIALOG=1` before launching Revit |
| Addin does not load | Check `%APPDATA%\Autodesk\Revit\Addins\2026\` for `RevitMCP.addin` and `RevitMCP.dll` |
| Build fails because RevitAPI.dll is missing | Update `HintPath` values in `revit-addin/RevitMCP/RevitMCP.csproj` |
| Long operation times out | Increase `REVIT_TIMEOUT_MS` in the MCP config |
| Clash detection is slow | Limit by level or use smaller category pairs |

## Roadmap

See [ROADMAP.md](ROADMAP.md). The big goals are better packaging, more Revit domains, safer dry-run flows, test doubles that work without Revit, and a friendlier installer for non-developers.

## Contributing

Contributions are welcome. Start with [CONTRIBUTING.md](CONTRIBUTING.md), open an issue for large changes, and run both builds before a pull request.

## License

MIT. See [LICENSE](LICENSE).
