param(
    [string]$BridgePath = (Join-Path $PSScriptRoot '../Installer/BridgeOutput/BimOnMcpBridge.exe'),
    [string]$ScriptPath = (Join-Path $PSScriptRoot '../Installer/MergeClaudeConfig.ps1')
)
$ErrorActionPreference = 'Stop'
$root=Join-Path ([IO.Path]::GetTempPath()) ('BimOnWrapperTest-'+[guid]::NewGuid().ToString('N'))
$previous=@{}
try {
    foreach($name in @('APPDATA','LOCALAPPDATA','USERPROFILE','CLAUDE_CONFIG_DIR','CODEX_HOME')) {
        $previous[$name]=[Environment]::GetEnvironmentVariable($name)
        [Environment]::SetEnvironmentVariable($name,(Join-Path $root $name))
    }
    & $ScriptPath -BridgePath (Resolve-Path $BridgePath).Path -ReportPath (Join-Path $root 'report.json') -Clients 'code,codex'
    $report=Get-Content (Join-Path $root 'report.json') -Raw|ConvertFrom-Json
    if($report.exitCode -ne 0 -or $report.results.Count -ne 2){throw 'Wrapper registration failed'}
    $null=& $ScriptPath -BridgePath (Resolve-Path $BridgePath).Path -ReportPath (Join-Path $root 'report.json') -Clients 'code,codex' -Diagnose
    if(!(Test-Path (Join-Path $env:CODEX_HOME 'config.toml'))){throw 'Codex missing'}
    Write-Output 'PASS Windows PowerShell 5.1 wrapper, report and diagnostic invocation'
} finally {
    foreach($name in $previous.Keys){[Environment]::SetEnvironmentVariable($name,$previous[$name])}
}
