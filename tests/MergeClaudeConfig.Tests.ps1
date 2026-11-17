param([string]$ScriptPath = (Join-Path $PSScriptRoot '../Installer/MergeClaudeConfig.ps1'))
$ErrorActionPreference = 'Stop'
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Resolve-Path $ScriptPath).Path,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Merger script parse error'}
$function=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Merge-BimOnServers'},$true)
if(!$function){throw 'Merger function missing'}
. ([scriptblock]::Create($function.Extent.Text))
$root=Join-Path ([IO.Path]::GetTempPath()) ('BimOnConfigTest-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root)|Out-Null
$utf8=New-Object Text.UTF8Encoding($false)
$script:passed=0
function Check($ok,$message){if(!$ok){throw $message}}
function Pass($message){$script:passed++; Write-Output ('PASS '+$message)}
$path=Join-Path $root 'new/config.json'
Check (!(Merge-BimOnServers -ConfigPath $path -BridgePath 'C:\Test Path\bridge.exe')) 'Created absent optional client'
Pass 'absent optional client is not created'
Check (Merge-BimOnServers -ConfigPath $path -BridgePath 'C:\Test Path\bridge.exe' -CreateIfMissing) 'Missing mandatory client not created'
$created=Get-Content $path -Raw -Encoding UTF8|ConvertFrom-Json
Check ($created.mcpServers.'BimOn-Revit'.command -eq 'C:\Test Path\bridge.exe') 'Command lost spaces'
Pass 'new config and path with spaces'
$json='{"preferences":{"language":"ko","nested":{"enabled":true}},"mcpServers":{"Other":{"command":"keep","env":{"TEST_VALUE":"not-a-secret"}}}}'
[IO.File]::WriteAllText($path,$json,$utf8)
$null=Merge-BimOnServers -ConfigPath $path -BridgePath 'C:\Test Path\bridge.exe'
$config=Get-Content $path -Raw -Encoding UTF8|ConvertFrom-Json
Check ($config.preferences.nested.enabled -and $config.preferences.language -eq 'ko' -and $config.mcpServers.Other.env.TEST_VALUE -eq 'not-a-secret') 'Existing settings lost'
Pass 'preferences and unrelated MCP config preserved'
$first=[IO.File]::ReadAllBytes($path)
$null=Merge-BimOnServers -ConfigPath $path -BridgePath 'C:\Test Path\bridge.exe'
Check ([Convert]::ToBase64String($first) -eq [Convert]::ToBase64String([IO.File]::ReadAllBytes($path))) 'Non-idempotent update'
Pass 'repeat merge is byte-idempotent'
foreach($invalid in @('{broken','[]','"text"','null',' ','{"mcpServers":[]}','{"mcpServers":"bad"}')){
    [IO.File]::WriteAllText($path,$invalid,$utf8)
    [IO.File]::WriteAllText(($path+'.bak'),'existing-backup',$utf8)
    $rejected=$false
    try{$null=Merge-BimOnServers -ConfigPath $path -BridgePath 'C:\Test Path\bridge.exe'}catch{$rejected=$true}
    Check $rejected ('Invalid config was accepted: '+$invalid)
    Check ([IO.File]::ReadAllText($path) -eq $invalid) 'Invalid config overwritten'
    Check ([IO.File]::ReadAllText($path+'.bak') -eq 'existing-backup') 'Existing backup overwritten'
    Pass ('invalid config is preserved: '+$invalid)
}
# Run the entire script with isolated environment roots to exercise client discovery.
$originalAppData=$env:APPDATA; $originalLocal=$env:LOCALAPPDATA; $originalProfile=$env:USERPROFILE
try {
    $env:APPDATA=Join-Path $root 'roaming'
    $env:LOCALAPPDATA=Join-Path $root 'local'
    $env:USERPROFILE=Join-Path $root 'user'
    $modern=Join-Path $env:LOCALAPPDATA 'Claude-Data/claude_test/claude_desktop_config.json'
    $absent=Join-Path $env:LOCALAPPDATA 'Claude-Data/claude_absent'
    [IO.Directory]::CreateDirectory((Split-Path $modern))|Out-Null
    [IO.Directory]::CreateDirectory($absent)|Out-Null
    [IO.Directory]::CreateDirectory($env:USERPROFILE)|Out-Null
    [IO.File]::WriteAllText($modern,$json,$utf8)
    & $ScriptPath -BridgePath 'C:\Test Path\bridge.exe'
    $merged=Get-Content $modern -Raw -Encoding UTF8|ConvertFrom-Json
    Check ($merged.mcpServers.'BimOn-Revit'.command -eq 'C:\Test Path\bridge.exe' -and $merged.preferences.language -eq 'ko') 'Modern Desktop profile was not preserved/merged'
    Check (!(Test-Path (Join-Path $absent 'claude_desktop_config.json'))) 'Created speculative profile config'
    Pass 'modern Desktop existing profiles discovered; absent profiles not created'
    [IO.File]::WriteAllText($modern,'{broken',$utf8)
    $rejected=$false
    try { & $ScriptPath -BridgePath 'C:\Test Path\bridge.exe' } catch { $rejected=$true }
    Check ($rejected -and [IO.File]::ReadAllText($modern) -eq '{broken') 'Malformed modern profile was overwritten'
    Pass 'modern Desktop malformed profile preserved and failure propagated'
} finally {
    $env:APPDATA=$originalAppData; $env:LOCALAPPDATA=$originalLocal; $env:USERPROFILE=$originalProfile
}
Write-Output "TOTAL $script:passed passed ($ScriptPath)"
