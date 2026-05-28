# RevitMCP v2

Local MCP server for controlling Revit 2026 from Claude Code or any MCP client.

## Architecture

```text
MCP client
  -> stdio
Node.js MCP server: mcp-server
  -> HTTP JSON on http://localhost:6543
C# Revit addin: revit-addin/RevitMCP
  -> Revit API
Revit 2026
```

## Requirements

| Tool | Notes |
| --- | --- |
| Revit 2026 | Installed locally |
| .NET 8 SDK | Used to build the addin |
| Node.js 20+ | Used to run the MCP server |
| Claude Code or another MCP client | Connects to `dist/index.js` over stdio |

## Install

From this folder:

```powershell
.\install.ps1
```

The installer builds the Node MCP server, builds the C# addin, and copies the addin files to:

```text
%APPDATA%\Autodesk\Revit\Addins\2026\
```

## MCP Client Config

Add a server entry like this, adjusting the path to your local checkout:

```json
{
  "mcpServers": {
    "revit": {
      "command": "node",
      "args": ["C:/path/to/revit-mcp/mcp-server/dist/index.js"],
      "env": {
        "REVIT_HOST": "http://localhost:6543",
        "REVIT_TIMEOUT_MS": "120000"
      }
    }
  }
}
```

## Daily Use

1. Open Revit 2026.
2. Open the `.rvt` project you want to work on.
3. Confirm the RevitMCP startup dialog appears.
4. Start your MCP client.

The addin also exposes a small health endpoint:

```text
GET http://localhost:6543/healthz
```

## Tool Groups

RevitMCP currently registers 39 MCP tools.

| Group | Tools |
| --- | --- |
| Read/query | model info, elements, parameters, views, sheets, levels, families, rooms |
| Create/modify | walls, family instances, parameters, delete, move |
| Sheets/views | create sheets, place views, activate views, templates, renaming, bulk duplication, auto-population |
| Export | PDF, DWG/DXF, IFC, schedules |
| Takeoff | material takeoff, CSV export, material schedule creation |
| Coordination | clash detection and HTML/CSV clash reports |
| Issue/revisions | drawing issue automation and revision history |
| Rooms | finish schedules and bulk finish updates |
| Audit/data | family audit, CSV parameter import, batch parameter updates |
| Commands | Revit postable commands and Dynamo launch |

## Example Prompts

```text
Generate a material takeoff for all walls and floors on Level 1 and export it to C:\Reports\takeoff.csv
```

```text
Check for clashes between structural columns and MEP ducts, then export an HTML report to C:\Reports\clashes.html
```

```text
Issue all sheets for construction with revision "Issued for Construction", date 2026-05-28, then export PDF to C:\Issue\2026-05-28\
```

```text
Apply the "Working" view template to all floor plan views whose name contains "ARCH".
```

## Troubleshooting

| Problem | Fix |
| --- | --- |
| Cannot reach Revit addin | Make sure Revit is open and the addin startup dialog appeared. Then check `http://localhost:6543/healthz`. |
| Addin does not load | Check that `RevitMCP.addin` and `RevitMCP.dll` are in `%APPDATA%\Autodesk\Revit\Addins\2026\`. |
| Build fails because RevitAPI.dll is missing | Update the `HintPath` values in `revit-addin/RevitMCP/RevitMCP.csproj`. |
| Long export times out | Increase `REVIT_TIMEOUT_MS` in the MCP server environment. The addin default is 120 seconds. |
| Clash detection is slow | Narrow the level or category pair before running large model checks. |

## Extending

1. Add the Revit-side action in `revit-addin/RevitMCP/RevitEventHandler.cs`.
2. Register the matching MCP tool in `mcp-server/src/index.ts`.
3. Run `npm run build` in `mcp-server`.
4. Run `dotnet build -c Release` in `revit-addin/RevitMCP`.
5. Restart Revit so the updated addin DLL is loaded.
