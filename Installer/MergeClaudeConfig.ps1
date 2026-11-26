# Backward-compatible entry point; no installed CLI or Python required.
param(
    [string]$BridgePath = (Join-Path $PSScriptRoot 'BimOnMcpBridge.exe'),
    [string]$Clients = 'desktop,code,codex',
    [string]$ReportPath = (Join-Path $PSScriptRoot 'McpRegistration.json'),
    [switch]$Diagnose
)
$ErrorActionPreference = 'Stop'
if (!(Test-Path -LiteralPath $BridgePath -PathType Leaf)) { throw 'BRIDGE_NOT_FOUND: Reinstall BimOn MCP.' }
$invokeArgs = @('--register-clients', '--clients', $Clients, '--report', $ReportPath)
if ($Diagnose) { $invokeArgs += '--diagnose' }
& $BridgePath @invokeArgs
if ($LASTEXITCODE -ne 0) { throw "MCP registration needs attention. Read $ReportPath" }
