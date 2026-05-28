# Contributing to RevitMCP

Thanks for helping make Revit automation more open and useful.

## Local Setup

Requirements:

- Revit 2026
- .NET 8 SDK
- Node.js 20 or newer

Build the MCP server:

```powershell
cd mcp-server
npm install
npm run build
```

Build the Revit addin:

```powershell
cd revit-addin\RevitMCP
dotnet build -c Release
```

## Development Workflow

1. Create an issue or comment on an existing one before large changes.
2. Keep Revit API behavior in `revit-addin/RevitMCP/RevitEventHandler.cs`.
3. Register matching MCP tools in `mcp-server/src/index.ts`.
4. Update `README.md` when user-facing behavior changes.
5. Run both builds before opening a pull request.

## Pull Request Checklist

- [ ] `npm run build` passes in `mcp-server`.
- [ ] `dotnet build -c Release` passes in `revit-addin/RevitMCP`.
- [ ] New tools include clear descriptions and input schemas.
- [ ] Risky write operations have safe defaults or dry-run support where practical.
- [ ] Documentation is updated.

## Design Principles

- Revit API calls must stay on Revit's UI thread through `ExternalEvent`.
- MCP tools should return structured JSON, not plain text summaries.
- Prefer dry-run support for bulk destructive operations.
- Keep local-only behavior explicit: RevitMCP listens on localhost by default.
