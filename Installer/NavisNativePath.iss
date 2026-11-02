{ Older native-folder registrations may take precedence over the v24 bundle. }
{ Update only an already registered BimOn folder at a verified Navisworks install. }
function Navis2027NativeDir(Param: String): String;
var Roots: array[0..1] of String; Names: TArrayOfString;
    R, I: Integer; Name, Location, Candidate: String;
begin
  Result := ExpandConstant('{commonpf}\Autodesk\Navisworks Manage 2027\Plugins\BimOnNavisPlugin');
  Roots[0] := 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall';
  Roots[1] := 'SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall';
  for R := 0 to 1 do
    if RegGetSubkeyNames(HKLM, Roots[R], Names) then
      for I := 0 to GetArrayLength(Names)-1 do
        if RegQueryStringValue(HKLM, Roots[R]+'\'+Names[I], 'DisplayName', Name) and
           (Name = 'Autodesk Navisworks Manage 2027') then
          if RegQueryStringValue(HKLM, Roots[R]+'\'+Names[I], 'InstallLocation', Location) then begin
            Candidate := AddBackslash(Location);
            if FileExists(Candidate+'Roamer.exe') then begin
              Result := Candidate+'Plugins\BimOnNavisPlugin'; Exit;
            end;
          end;
end;

function HasNavis2027Native(): Boolean;
begin
  Result := FileExists(AddBackslash(Navis2027NativeDir(''))+'BimOnNavisPlugin.dll');
end;
