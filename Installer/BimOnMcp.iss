; ============================================================
;  BimOnMcp (Public) — Inno Setup Script
;  Revit 2025-2027 / Navisworks 2025-2027 / AutoCAD 2025-2027 + Dynamo
;  Control Autodesk BIM hosts from Claude via MCP. MIT License.
; ============================================================

#define MyAppName      "BimOn MCP"
#define MyAppVersion   "1.0.0"
#define MyAppPublisher "JungGeun Park (General Soju)"
#define MyAppURL       "https://www.youtube.com/@GeneralSoju"
#define MyAppSupport   "https://github.com/General-Soju/BimOnMcp"

; 소스 경로는 .iss(Installer\) 기준 상대경로 — 저장소를 클론한 누구나 빌드 가능
#define SrcRevit   "..\BimOnRevitPlugin\bin\Release\net8.0-windows"
#define SrcNavis   "..\BimOnNavisPlugin\bin\Release\net48"
#define SrcNavis26 "..\BimOnNavisPlugin\bin\Release\net48-2026"
#define SrcNavis27 "..\BimOnNavisPlugin\bin\Release\net48-2027"
#define SrcAcad    "..\BimOnAcadPlugin\bin\Release\net8.0-windows"
#define SrcBridge  "BridgeOutput"

; 이전 설치 방식(전체 사용자용)의 옛 AutoCAD 번들 잔재 제거 → 새 %AppData% 번들이 항상 로드
[InstallDelete]
Type: filesandordirs; Name: "{commonappdata}\Autodesk\ApplicationPlugins\BimOnAcadPlugin.bundle"; Components: autocad
Type: filesandordirs; Name: "{commonpf}\Autodesk\ApplicationPlugins\BimOnAcadPlugin.bundle"; Components: autocad

[Setup]
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppSupport}
AppCopyright=Copyright (C) 2026 JungGeun Park (General Soju). MIT License.
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName}
VersionInfoCopyright=Copyright (C) 2026 JungGeun Park (General Soju). MIT License.
LicenseFile=License.txt
DefaultDirName={autopf}\BimOnAI
DefaultGroupName=BimOn AI
DisableProgramGroupPage=yes
OutputDir=.\Output
OutputBaseFilename=BimOnMcp_Setup_v{#MyAppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
WizardSizePercent=120
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName={#MyAppName}
SetupIconFile=BimOn.ico
UninstallDisplayIcon={userappdata}\BimOnAI\BimOn.ico

[Languages]
Name: "en";     MessagesFile: "compiler:Default.isl"
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[Components]
Name: "bridge";  Description: "BimOn MCP Bridge  (required for Claude Desktop / Code)"; Types: full compact custom; Flags: fixed
Name: "revit";   Description: "Revit 2025 / 2026 / 2027 Plugin (+ Dynamo)";           Types: full compact custom
Name: "navis";   Description: "Navisworks 2025 / 2026 / 2027 Plugin";                 Types: full custom
Name: "autocad"; Description: "AutoCAD 2025 / 2026 / 2027 Plugin (+ Civil 3D etc.)";  Types: full custom

[Types]
Name: "full";    Description: "Full install  (Bridge + Revit + Navisworks + AutoCAD)"
Name: "compact"; Description: "Minimal install  (Bridge + Revit only)"
Name: "custom";  Description: "Custom install"; Flags: iscustom

; ============================================================
;  MCP Bridge + IronPython StdLib
; ============================================================
[Files]
Source: "{#SrcBridge}\BimOnMcpBridge.exe"; DestDir: "{userappdata}\BimOnAI"; Components: bridge; Flags: ignoreversion
Source: "MergeClaudeConfig.ps1";           DestDir: "{userappdata}\BimOnAI"; Components: bridge; Flags: ignoreversion
Source: "BimOn.ico";                        DestDir: "{userappdata}\BimOnAI"; Components: bridge; Flags: ignoreversion
Source: "{#SrcRevit}\Lib\*"; DestDir: "{userappdata}\BimOnAI\Lib"; Components: bridge; Flags: ignoreversion recursesubdirs createallsubdirs

; ============================================================
;  Revit — copies to every installed version folder
; ============================================================
; --- 2025 ---
Source: "{#SrcRevit}\BimOnRevit.addin";                DestDir: "{userappdata}\Autodesk\Revit\Addins\2025"; Components: revit; Check: IsRevit2025; Flags: ignoreversion
Source: "{#SrcRevit}\*.dll";                            DestDir: "{userappdata}\Autodesk\Revit\Addins\2025"; Components: revit; Check: IsRevit2025; Flags: ignoreversion
; --- 2026 ---
Source: "{#SrcRevit}\BimOnRevit.addin";                DestDir: "{userappdata}\Autodesk\Revit\Addins\2026"; Components: revit; Check: IsRevit2026; Flags: ignoreversion
Source: "{#SrcRevit}\*.dll";                            DestDir: "{userappdata}\Autodesk\Revit\Addins\2026"; Components: revit; Check: IsRevit2026; Flags: ignoreversion
; --- 2027 ---
Source: "{#SrcRevit}\BimOnRevit.addin";                DestDir: "{userappdata}\Autodesk\Revit\Addins\2027"; Components: revit; Check: IsRevit2027; Flags: ignoreversion
Source: "{#SrcRevit}\*.dll";                            DestDir: "{userappdata}\Autodesk\Revit\Addins\2027"; Components: revit; Check: IsRevit2027; Flags: ignoreversion

; ============================================================
;  Navisworks — version-specific builds (API v22/v23/v24)
; ============================================================
Source: "{#SrcNavis}\*";   DestDir: "{commonappdata}\Autodesk\Navisworks Manage 2025\Plugins\BimOnNavisPlugin"; Components: navis; Check: IsNavis2025; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcNavis26}\*"; DestDir: "{commonappdata}\Autodesk\Navisworks Manage 2026\Plugins\BimOnNavisPlugin"; Components: navis; Check: IsNavis2026; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SrcNavis27}\*"; DestDir: "{commonappdata}\Autodesk\Navisworks Manage 2027\Plugins\BimOnNavisPlugin"; Components: navis; Check: IsNavis2027; Flags: ignoreversion recursesubdirs createallsubdirs

; ============================================================
;  AutoCAD — ApplicationPlugins bundle (R25-R28, version-independent)
; ============================================================
Source: "{#SrcAcad}\PackageContents.xml"; DestDir: "{userappdata}\Autodesk\ApplicationPlugins\BimOnAcadPlugin.bundle"; Components: autocad; Flags: ignoreversion
Source: "{#SrcAcad}\*.dll";               DestDir: "{userappdata}\Autodesk\ApplicationPlugins\BimOnAcadPlugin.bundle\Contents"; Components: autocad; Flags: ignoreversion
Source: "{#SrcAcad}\BimOnAcadPlugin.deps.json"; DestDir: "{userappdata}\Autodesk\ApplicationPlugins\BimOnAcadPlugin.bundle\Contents"; Components: autocad; Flags: ignoreversion

[Dirs]
Name: "{userappdata}\BimOnAI\Scripts\Revit"
Name: "{userappdata}\BimOnAI\Scripts\Navisworks"
Name: "{userappdata}\BimOnAI\Scripts\AutoCAD"

[Code]
function IsRevit2025(): Boolean;
begin
  Result := RegKeyExists(HKLM, 'SOFTWARE\Autodesk\Revit\Autodesk Revit 2025') or
            RegKeyExists(HKLM, 'SOFTWARE\WOW6432Node\Autodesk\Revit\Autodesk Revit 2025');
end;
function IsRevit2026(): Boolean;
begin
  Result := RegKeyExists(HKLM, 'SOFTWARE\Autodesk\Revit\Autodesk Revit 2026') or
            RegKeyExists(HKLM, 'SOFTWARE\WOW6432Node\Autodesk\Revit\Autodesk Revit 2026');
end;
function IsRevit2027(): Boolean;
begin
  Result := RegKeyExists(HKLM, 'SOFTWARE\Autodesk\Revit\Autodesk Revit 2027') or
            RegKeyExists(HKLM, 'SOFTWARE\WOW6432Node\Autodesk\Revit\Autodesk Revit 2027');
end;
function IsNavis2025(): Boolean;
begin Result := DirExists(ExpandConstant('{commonappdata}\Autodesk\Navisworks Manage 2025')); end;
function IsNavis2026(): Boolean;
begin Result := DirExists(ExpandConstant('{commonappdata}\Autodesk\Navisworks Manage 2026')); end;
function IsNavis2027(): Boolean;
begin Result := DirExists(ExpandConstant('{commonappdata}\Autodesk\Navisworks Manage 2027')); end;

function InitializeSetup(): Boolean;
var Msg: String; ResultCode: Integer;
begin
  Result := True;
  Msg := 'BimOn MCP — Setup' + #13#10 + #13#10;
  Msg := Msg + 'Please close the following before continuing:' + #13#10;
  Msg := Msg + '  - Autodesk Revit  (2025 / 2026 / 2027)' + #13#10;
  Msg := Msg + '  - Autodesk Navisworks Manage  (2025 / 2026 / 2027)' + #13#10;
  Msg := Msg + '  - AutoCAD  (2025 / 2026 / 2027)' + #13#10;
  Msg := Msg + '  - Claude Desktop' + #13#10 + #13#10;
  Msg := Msg + 'Run as a NORMAL user (not administrator). Click OK to continue.';
  MsgBox(Msg, mbInformation, MB_OK);
  Exec('taskkill.exe', '/im BimOnMcpBridge.exe /f', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure ConfigureClaude();
var BridgePath, Ps1Path, Params: String; ResultCode: Integer;
begin
  BridgePath := ExpandConstant('{userappdata}\BimOnAI\BimOnMcpBridge.exe');
  Ps1Path    := ExpandConstant('{userappdata}\BimOnAI\MergeClaudeConfig.ps1');
  Params := '-NoProfile -ExecutionPolicy Bypass -File "' + Ps1Path + '" -BridgePath "' + BridgePath + '"';
  Exec('powershell.exe', Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var MetaFile, Msg: String;
begin
  if CurStep = ssPostInstall then
  begin
    MetaFile := ExpandConstant('{userappdata}\BimOnAI\scripts_meta.json');
    if not FileExists(MetaFile) then SaveStringToFile(MetaFile, '[]', False);
    ConfigureClaude();
    Msg := 'Installation complete!' + #13#10 + #13#10;
    Msg := Msg + 'Restart Claude Desktop / Claude Code to activate BimOn MCP.' + #13#10 + #13#10;
    Msg := Msg + 'Registered MCP servers: BimOn-Revit, BimOn-Navisworks, BimOn-AutoCAD' + #13#10;
    Msg := Msg + '(Dynamo tools are part of the Revit plugin.)' + #13#10 + #13#10;
    Msg := Msg + 'Author : JungGeun Park (General Soju)' + #13#10;
    Msg := Msg + 'GitHub : https://github.com/General-Soju/BimOnMcp' + #13#10;
    Msg := Msg + 'YouTube: https://www.youtube.com/@GeneralSoju';
    MsgBox(Msg, mbInformation, MB_OK);
  end;
end;
