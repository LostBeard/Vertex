; Vertex installer (Inno Setup 6.1+). Build with build-installer.bat (publishes a self-contained win-x64 Vertex into stage\ first).
;
; Per-user install, no administrator needed: Vertex's settings, people and sign-in are per user anyway (%APPDATA%\Vertex, DPAPI).
; The installer installs Vertex and nothing else. Its models are NOT installed here, the way Ollama's installer does not install models: Vertex fetches the language model
; itself the first time it needs it (the dashboard's Install button, or `vertex install-models`), from models.spawndev.com and then registry.ollama.ai, into the Ollama
; model cache; the speech, voice and face models download by themselves the first time they are used.

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

[Setup]
AppId={{B7C1E5A2-4D3F-4E8A-9C61-5A0F2D7E8B14}
AppName=Vertex
AppVersion={#AppVersion}
AppPublisher=Todd Tanner (LostBeard)
AppPublisherURL=https://github.com/LostBeard/Vertex
AppSupportURL=https://github.com/LostBeard/Vertex/issues
DefaultDirName={autopf}\Vertex
DefaultGroupName=Vertex
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
LicenseFile=..\LICENSE
OutputDir=Output
OutputBaseFilename=VertexSetup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=Vertex
UninstallDisplayIcon={app}\Vertex.exe
; Vertex.exe holds this mutex while it runs (Program.cs): setup asks to close it instead of failing on a locked file.
AppMutex=Vertex.Tray
CloseApplications=yes
UsedUserAreasWarning=no

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "autostart"; Description: "Start Vertex when Windows starts (it waits quietly in the notification area)"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
Source: "stage\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion
; The Llama 3.1 Community License (section 1.b.iii) wants the attribution notice in a Notice text file distributed with the model's copies. The model itself is installed by Vertex,
; and its license layer (the agreement) is written next to it there; this is the notice for the product.
Source: "..\NOTICE"; DestDir: "{app}"; DestName: "NOTICE.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Vertex"; Filename: "{app}\Vertex.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Vertex"; Filename: "{app}\Vertex.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
; The same Run-key value the tray menu's "Start with Windows" writes (VertexTray.cs), so the two agree.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Vertex"; ValueData: """{app}\Vertex.exe"" --background"; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\Vertex.exe"; Description: "Start Vertex"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[Code]
function InitializeSetup: Boolean;
var
  Exe: String;
  Code: Integer;
begin
  Result := True;
  // An update while Vertex is running: ask it to quit first (`Vertex.exe quit` stops it properly: the robot is parked, the game left). Without this a silent setup stops at
  // "Setup has detected that Vertex is currently running" (exit code 1, nothing installed - seen 2026-11-01). Needs a Vertex that has the quit command; for an older one, close it first.
  Exe := ExpandConstant('{localappdata}\Programs\Vertex\Vertex.exe');
  if FileExists(Exe) then Exec(Exe, 'quit', '', SW_HIDE, ewWaitUntilTerminated, Code);
  // Vertex runs its models through CUDA. Without an NVIDIA driver it falls back to OpenCL/CPU, which is far too slow to talk to.
  if not (FileExists(ExpandConstant('{sys}\nvcuda.dll')) or FileExists(ExpandConstant('{win}\sysnative\nvcuda.dll'))) then
    Result := SuppressibleMsgBox('No NVIDIA graphics driver was found on this PC.' + #13#10#13#10 +
      'Vertex runs its language model, speech and voices on an NVIDIA graphics card. Without one it is far too slow to use.' + #13#10#13#10 +
      'Install it anyway?', mbConfirmation, MB_YESNO, IDNO) = IDYES;
end;

// Uninstall keeps the models (they are in the Ollama model cache, shared with Ollama) and asks about the personal data Vertex keeps: who it knows, their voices and faces.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Data: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    Data := ExpandConstant('{userappdata}\Vertex');
    if DirExists(Data) and (not UninstallSilent) then
      if MsgBox('Also delete the things Vertex remembers on this PC: its settings, the people it knows and their saved voices and faces, and the characters?' + #13#10#13#10 +
        Data + #13#10#13#10 + 'The language model is kept (it is shared with Ollama).', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(Data, True, True, True);
  end;
end;
