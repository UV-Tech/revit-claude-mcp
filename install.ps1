param(
    [switch]$SkipNodeInstall,
    [switch]$SkipAddinBuild
)

$ErrorActionPreference = "Stop"

function Assert-Command {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [Parameter(Mandatory = $true)]
        [string]$InstallHint
    )

    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "$Name was not found. $InstallHint"
    }
}

Write-Host "=== RevitMCP Installer ===" -ForegroundColor Cyan
Write-Host "Project root: $PSScriptRoot" -ForegroundColor DarkGray

Assert-Command -Name "node" -InstallHint "Install Node.js 20 or newer from https://nodejs.org"
Assert-Command -Name "npm" -InstallHint "Install Node.js 20 or newer from https://nodejs.org"
Assert-Command -Name "dotnet" -InstallHint "Install the .NET 8 SDK from https://dotnet.microsoft.com"

$nodeMajor = [int]((node --version).TrimStart("v").Split(".")[0])
if ($nodeMajor -lt 20) {
    throw "Node.js 20 or newer is required. Current version: $(node --version)"
}

if (-not $SkipNodeInstall) {
    Write-Host "`n[1/3] Installing Node.js dependencies..." -ForegroundColor Yellow
    Push-Location "$PSScriptRoot\mcp-server"
    try {
        if (Test-Path "package-lock.json") {
            npm ci
        }
        else {
            npm install
        }

        npm test
        Write-Host "MCP server built and tested OK" -ForegroundColor Green
    }
    finally {
        Pop-Location
    }
}
else {
    Write-Host "`n[1/3] Skipping Node.js dependency install and build." -ForegroundColor Yellow
}

if (-not $SkipAddinBuild) {
    Write-Host "`n[2/3] Building Revit addin..." -ForegroundColor Yellow
    Push-Location "$PSScriptRoot\revit-addin\RevitMCP"
    try {
        dotnet build -c Release -nologo
        Write-Host "Revit addin built and copied to the Revit Addins folder" -ForegroundColor Green
    }
    finally {
        Pop-Location
    }
}
else {
    Write-Host "`n[2/3] Skipping Revit addin build." -ForegroundColor Yellow
}

Write-Host "`n[3/3] MCP client config:" -ForegroundColor Yellow
$serverPath = "$PSScriptRoot\mcp-server\dist\index.js"
$escapedServerPath = $serverPath.Replace("\", "/")
$config = @"

Add this to your MCP client settings:

{
  "mcpServers": {
    "revit": {
      "command": "node",
      "args": ["$escapedServerPath"],
      "env": {
        "REVIT_HOST": "http://localhost:6543",
        "REVIT_TIMEOUT_MS": "120000"
      }
    }
  }
}

"@

Write-Host $config -ForegroundColor White
Write-Host "=== Done ===" -ForegroundColor Cyan
Write-Host "1. Open Revit 2026." -ForegroundColor White
Write-Host "2. Open your .rvt project." -ForegroundColor White
Write-Host "3. Confirm the RevitMCP startup dialog appears." -ForegroundColor White
Write-Host "4. Start your MCP client with the config above." -ForegroundColor White
