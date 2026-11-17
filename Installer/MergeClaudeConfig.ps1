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

    # Refuse malformed existing data; never turn a parse failure into a new config.
    $config = $null
    if (Test-Path $ConfigPath) {
        try {
            $raw = [System.IO.File]::ReadAllText($ConfigPath, [System.Text.Encoding]::UTF8)
            if (-not $raw.Trim() -or -not $raw.TrimStart().StartsWith('{')) {
                throw 'Expected a non-empty JSON object.'
            }
            $config = $raw | ConvertFrom-Json -ErrorAction Stop
            if ($config -isnot [PSCustomObject]) { throw 'Expected a JSON object.' }
        } catch {
            throw "CONFIG_INVALID: Existing file was preserved: $ConfigPath. $($_.Exception.Message)"
        }
    }
    if ($null -eq $config) { $config = [PSCustomObject]@{} }

    if ($null -ne $config.mcpServers -and $config.mcpServers -isnot [PSCustomObject]) {
        throw "CONFIG_INVALID: mcpServers must be an object; existing file was preserved: $ConfigPath"
    }
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

    $json = $config | ConvertTo-Json -Depth 100 -WarningAction Stop
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    # Write completely before replacing the destination. Preserve unrelated .bak files.
    $tempPath = $ConfigPath + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    try {
        [System.IO.File]::WriteAllText($tempPath, $json, $utf8)
        if ([System.IO.File]::Exists($ConfigPath)) {
            [System.IO.File]::Replace($tempPath, $ConfigPath, [System.Management.Automation.Language.NullString]::Value)
        } else {
            [System.IO.File]::Move($tempPath, $ConfigPath)
        }
    } finally {
        if ([System.IO.File]::Exists($tempPath)) { [System.IO.File]::Delete($tempPath) }
    }
    return $true
}

# 1) Claude Desktop — always create/merge
$desktopCfg = Join-Path $env:APPDATA 'Claude\claude_desktop_config.json'
if (Merge-BimOnServers -ConfigPath $desktopCfg -BridgePath $BridgePath -CreateIfMissing) {
    Write-Host "OK: Claude Desktop config merged ($desktopCfg)"
}

# New packaged Claude Desktop profiles use LocalAppData instead of Roaming.
# Touch only existing profiles/configs; never create speculative package profiles.
$desktopData = Join-Path $env:LOCALAPPDATA 'Claude-Data'
if (Test-Path -LiteralPath $desktopData) {
    foreach ($profile in @(Get-ChildItem -LiteralPath $desktopData -Directory -Filter 'claude_*')) {
        $profileConfig = Join-Path $profile.FullName 'claude_desktop_config.json'
        if (Test-Path -LiteralPath $profileConfig) {
            if (Merge-BimOnServers -ConfigPath $profileConfig -BridgePath $BridgePath) {
                Write-Host "OK: Claude Desktop profile config merged ($profileConfig)"
            }
        }
    }
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
