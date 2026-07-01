# MergeClaudeConfig.ps1  (BimOnMcp public — Revit / Navisworks / AutoCAD)
# Safely merges BimOn MCP servers into BOTH Claude clients' configs
# WITHOUT destroying existing settings:
#   1. Claude Desktop : %APPDATA%\Claude\claude_desktop_config.json (always)
#   2. Claude Code    : %USERPROFILE%\.claude.json (only if it exists)
param(
    [Parameter(Mandatory=$true)][string]$BridgePath
)

$ErrorActionPreference = 'Stop'

function Merge-BimOnServers {
    param([string]$ConfigPath, [string]$BridgePath, [switch]$CreateIfMissing)

    $dir = Split-Path $ConfigPath -Parent
    if (-not (Test-Path $ConfigPath)) {
        if (-not $CreateIfMissing) { return $false }
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
    }

    $config = $null
    if (Test-Path $ConfigPath) {
        try {
            $raw = [System.IO.File]::ReadAllText($ConfigPath, [System.Text.Encoding]::UTF8)
            if ($raw.Trim()) { $config = $raw | ConvertFrom-Json }
        } catch {
            Copy-Item $ConfigPath "$ConfigPath.bak" -Force
            $config = $null
        }
    }
    if ($null -eq $config) { $config = [PSCustomObject]@{} }

    if (-not ($config.PSObject.Properties.Name -contains 'mcpServers') -or $null -eq $config.mcpServers) {
        $config | Add-Member -NotePropertyName 'mcpServers' -NotePropertyValue ([PSCustomObject]@{}) -Force
    }

    foreach ($t in @('revit','navisworks','autocad')) {
        $name = switch ($t) {
            'revit'      { 'BimOn-Revit' }
            'navisworks' { 'BimOn-Navisworks' }
            'autocad'    { 'BimOn-AutoCAD' }
        }
        $entry = [PSCustomObject]@{
            command = $BridgePath
            args    = @('--target', $t)
        }
        if ($config.mcpServers.PSObject.Properties.Name -contains $name) {
            $config.mcpServers.$name = $entry
        } else {
            $config.mcpServers | Add-Member -NotePropertyName $name -NotePropertyValue $entry
        }
    }

    $json = $config | ConvertTo-Json -Depth 32
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($ConfigPath, $json, $utf8)
    return $true
}

# 1) Claude Desktop — always create/merge
$desktopCfg = Join-Path $env:APPDATA 'Claude\claude_desktop_config.json'
if (Merge-BimOnServers -ConfigPath $desktopCfg -BridgePath $BridgePath -CreateIfMissing) {
    Write-Host "OK: Claude Desktop config merged ($desktopCfg)"
}

# 2) Claude Code — merge only when Claude Code is present (do not create junk)
$codeCfg = Join-Path $env:USERPROFILE '.claude.json'
if (Test-Path $codeCfg) {
    if (Merge-BimOnServers -ConfigPath $codeCfg -BridgePath $BridgePath) {
        Write-Host "OK: Claude Code config merged ($codeCfg)"
    }
} else {
    Write-Host "SKIP: Claude Code not detected (no ~\.claude.json)"
}
