; Vertex installer (Inno Setup 6.1+). Build with build-installer.bat (publishes a self-contained win-x64 Vertex into stage\ first).
;
; Per-user install, no administrator needed: Vertex's settings, people and sign-in are per user anyway (%APPDATA%\Vertex, DPAPI).
; The language model is NOT in the installer (4.9 GB). Setup downloads llama3.1:8b from the Ollama registry, checks its SHA-256 and writes it into the
; Ollama model cache the way `ollama pull` would (Vertex reads that cache directly; Ollama itself does not have to be installed). If the cache already
; has it, nothing is downloaded. The speech, voice and face models download by themselves the first time Vertex uses them.

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

#define ModelDigest "667b0c1932bc6ffc593ed1d03f895bf2dc8dc6df21db3042284a6f4416b06a29"
#define ModelSize   "4920738944"

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
; The small layers and the manifest of llama3.1:8b, written next to the downloaded model. dontcopy: extracted by the code below, only when the model is installed.
Source: "model\manifest-llama3.1-8b.json"; Flags: dontcopy
Source: "model\sha256-455f34728c9b5dd3376378bfb809ee166c145b0b4c1f1a6feca069055066ef9a"; Flags: dontcopy
Source: "model\sha256-948af2743fc78a328dcb3b0f5a31b3d75f415840fdb699e8b1235978392ecf85"; Flags: dontcopy
Source: "model\sha256-0ba8f0e314b4264dfd19df045cde9d4c394a52474bf92ed6a3de22a4ca31a177"; Flags: dontcopy
Source: "model\sha256-56bb8bd477a519ffa694fc449c2413c6f0e1d3b1c88fa7e3c9d88d3ae49d4dcb"; Flags: dontcopy

[Icons]
Name: "{autoprograms}\Vertex"; Filename: "{app}\Vertex.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Vertex"; Filename: "{app}\Vertex.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
; The same Run-key value the tray menu's "Start with Windows" writes (VertexTray.cs), so the two agree.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Vertex"; ValueData: """{app}\Vertex.exe"" --background"; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\Vertex.exe"; Description: "Start Vertex"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[Code]
const
  ModelDigest = '{#ModelDigest}';
  ModelUrl = 'https://registry.ollama.ai/v2/library/llama3.1/blobs/sha256:{#ModelDigest}';
  // 4.9 GB model, plus room for the rest (the speech, voice and face models land in %LOCALAPPDATA%\Vertex later).
  NeedFreeBytesMB = 6000;

var
  ModelPage: TDownloadWizardPage;
  ModelSize: Int64;
  ModelDownloaded: Boolean;   // the blob is in {tmp}, checked, and must be moved into the cache
  ModelDeclined: Boolean;     // needed, but the download failed and was given up

function OllamaRoot: String;
begin
  Result := GetEnv('OLLAMA_MODELS');
  if Result = '' then Result := ExpandConstant('{%USERPROFILE}\.ollama\models');
end;

function BlobPath(const Digest: String): String;
begin
  Result := OllamaRoot + '\blobs\sha256-' + Digest;
end;

function ManifestPath: String;
begin
  Result := OllamaRoot + '\manifests\registry.ollama.ai\library\llama3.1\8b';
end;

function ModelPresent: Boolean;
var
  Size: Int64;
begin
  Result := FileExists(ManifestPath) and FileSize64(BlobPath(ModelDigest), Size) and (Size = ModelSize);
end;

function InitializeSetup: Boolean;
begin
  ModelSize := StrToInt64('{#ModelSize}');
  Result := True;
  // Vertex runs its models through CUDA. Without an NVIDIA driver it falls back to OpenCL/CPU, which is far too slow to talk to.
  if not (FileExists(ExpandConstant('{sys}\nvcuda.dll')) or FileExists(ExpandConstant('{win}\sysnative\nvcuda.dll'))) then
    Result := SuppressibleMsgBox('No NVIDIA graphics driver was found on this PC.' + #13#10#13#10 +
      'Vertex runs its language model, speech and voices on an NVIDIA graphics card. Without one it is far too slow to use.' + #13#10#13#10 +
      'Install it anyway?', mbConfirmation, MB_YESNO, IDNO) = IDYES;
end;

function OnDownloadProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  if ProgressMax > 0 then
    ModelPage.SetText('Downloading the language model (llama3.1:8b)',
      Format('%d of %d MB', [Progress div 1048576, ProgressMax div 1048576]));
  Result := True;
end;

procedure InitializeWizard;
begin
  ModelPage := CreateDownloadPage('Downloading the language model',
    'Vertex thinks with llama3.1:8b, about 4.9 GB. It is downloaded once and kept; this can take a while.', @OnDownloadProgress);
end;

function FreeMB(const Dir: String): Cardinal;
var
  Free, Total: Cardinal;
begin
  if GetSpaceOnDisk(ExtractFileDrive(Dir) + '\', True, Free, Total) then Result := Free else Result := 999999;
end;

// Downloads the model blob into {tmp}; asks before trying again when it fails. True when the checked file is there.
function DownloadModel: Boolean;
var
  Again: Boolean;
begin
  Result := False;
  repeat
    Again := False;
    ModelPage.Clear;
    ModelPage.Add(ModelUrl, 'llama3.1-8b.gguf', ModelDigest);
    ModelPage.Show;
    try
      try
        ModelPage.Download;
        Result := True;
      except
        Log('Model download failed: ' + GetExceptionMessage);
        if not ModelPage.AbortedByUser then
          Again := SuppressibleMsgBox('The language model could not be downloaded:' + #13#10#13#10 + GetExceptionMessage + #13#10#13#10 +
            'Try again? (If not, Vertex is still installed; run this setup again later to finish.)', mbError, MB_YESNO, IDNO) = IDYES;
      end;
    finally
      ModelPage.Hide;
    end;
  until Result or not Again;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = wpReady) and not ModelPresent then
  begin
    if (FreeMB(ExpandConstant('{tmp}')) < NeedFreeBytesMB) or (FreeMB(OllamaRoot) < NeedFreeBytesMB) then
    begin
      ModelDeclined := True;
      SuppressibleMsgBox('There is not enough free disk space for the language model (about 6 GB are needed).' + #13#10#13#10 +
        'Vertex will be installed without it. Free some space and run this setup again to download it.', mbError, MB_OK, IDOK);
    end
    else if DownloadModel then
      ModelDownloaded := True
    else
      ModelDeclined := True;
  end;
end;

procedure PutSmall(const Name, Dest: String);
begin
  ExtractTemporaryFile(Name);
  if not FileCopy(ExpandConstant('{tmp}\' + Name), Dest, False) then
    RaiseException('Could not write ' + Dest);
end;

// Writes the model into the Ollama cache. The manifest goes LAST: Vertex lists a model only when its manifest and blob are both there.
procedure InstallModel;
var
  Blob: String;
begin
  ForceDirectories(OllamaRoot + '\blobs');
  ForceDirectories(ExtractFilePath(ManifestPath));
  PutSmall('sha256-455f34728c9b5dd3376378bfb809ee166c145b0b4c1f1a6feca069055066ef9a', BlobPath('455f34728c9b5dd3376378bfb809ee166c145b0b4c1f1a6feca069055066ef9a'));
  PutSmall('sha256-948af2743fc78a328dcb3b0f5a31b3d75f415840fdb699e8b1235978392ecf85', BlobPath('948af2743fc78a328dcb3b0f5a31b3d75f415840fdb699e8b1235978392ecf85'));
  PutSmall('sha256-0ba8f0e314b4264dfd19df045cde9d4c394a52474bf92ed6a3de22a4ca31a177', BlobPath('0ba8f0e314b4264dfd19df045cde9d4c394a52474bf92ed6a3de22a4ca31a177'));
  PutSmall('sha256-56bb8bd477a519ffa694fc449c2413c6f0e1d3b1c88fa7e3c9d88d3ae49d4dcb', BlobPath('56bb8bd477a519ffa694fc449c2413c6f0e1d3b1c88fa7e3c9d88d3ae49d4dcb'));
  Blob := BlobPath(ModelDigest);
  // A move on the same drive (instant, no second copy of 4.9 GB); a copy across drives.
  if not RenameFile(ExpandConstant('{tmp}\llama3.1-8b.gguf'), Blob) then
  begin
    if not FileCopy(ExpandConstant('{tmp}\llama3.1-8b.gguf'), Blob, False) then RaiseException('Could not write ' + Blob);
    DeleteFile(ExpandConstant('{tmp}\llama3.1-8b.gguf'));
  end;
  PutSmall('manifest-llama3.1-8b.json', ManifestPath);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and ModelDownloaded then
  begin
    try
      InstallModel;
    except
      ModelDeclined := True;
      SuppressibleMsgBox('Vertex is installed, but the language model could not be put in place:' + #13#10#13#10 + GetExceptionMessage + #13#10#13#10 +
        'Run this setup again to finish.', mbError, MB_OK, IDOK);
    end;
  end;
  if (CurStep = ssDone) and ModelDeclined then
    SuppressibleMsgBox('Vertex is installed, but it has no language model yet, so it cannot think or talk.' + #13#10#13#10 +
      'Run this setup again with the internet connected, or run "ollama pull llama3.1:8b" if you use Ollama.', mbInformation, MB_OK, IDOK);
end;

// Uninstall keeps the model (it is in the shared Ollama cache) and asks about the personal data Vertex keeps: who it knows, their voices and faces.
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
