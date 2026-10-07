#if Ver < EncodeVer(7, 1, 0)
  #error Inno Setup 7.1 or later is required. Run Release.cmd to obtain it automatically.
#endif

[Setup]
AppId=Syno.TinyTorrent
AppName=TinyTorrent
AppVersion={#AppVersion}
AppPublisher=SynoSoftware
AppPublisherURL=https://github.com/SynoSoftware/TinyTorrent2
AppUpdatesURL=https://github.com/SynoSoftware/TinyTorrent2/releases/latest
DefaultDirName={localappdata}\Programs\TinyTorrent
PrivilegesRequired=lowest
SetupArchitecture=x64
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion={#MinWindows}
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableWelcomePage=yes
WizardStyle=modern dynamic
SetupIconFile=..\resources\TinyTorrent.ico
UninstallDisplayIcon={app}\{#EngineName}.exe
OutputDir={#OutputDir}
OutputBaseFilename={#InstallerName}
Compression=lzma2
SolidCompression=yes
CloseApplications=no
RestartApplications=no
ChangesAssociations=yes
#ifdef Signed
SignTool=TinyTorrent
SignedUninstaller=yes
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[CustomMessages]
english.Handlers=Open torrent files and magnet links with TinyTorrent
spanish.Handlers=Abrir archivos torrent y enlaces magnet con TinyTorrent
english.Startup=Start TinyTorrent when I sign in
spanish.Startup=Iniciar TinyTorrent al iniciar sesión
english.Launch=Open TinyTorrent
spanish.Launch=Abrir TinyTorrent
english.DownloadTitle=Preparing TinyTorrent
spanish.DownloadTitle=Preparando TinyTorrent
english.DownloadDescription=Downloading required Microsoft components...
spanish.DownloadDescription=Descargando los componentes de Microsoft necesarios...
english.InstallRuntime=Installing %1...
spanish.InstallRuntime=Instalando %1...
english.DotNet=Microsoft .NET Runtime
spanish.DotNet=Microsoft .NET Runtime
english.Windows=Windows App Runtime
spanish.Windows=Windows App Runtime
english.VisualCpp=Microsoft Visual C++ Runtime
spanish.VisualCpp=Microsoft Visual C++ Runtime
english.RuntimeFailed=%1 could not be installed. Please try again. If Windows asks for administrator approval, allow the Microsoft installer. Error: %2
spanish.RuntimeFailed=No se pudo instalar %1. Inténtalo de nuevo. Si Windows solicita permisos de administrador, permite el instalador de Microsoft. Error: %2
english.CheckFailed=The required components could not be checked. Please try again or contact support.
spanish.CheckFailed=No se pudieron comprobar los componentes necesarios. Inténtalo de nuevo o contacta con soporte.
english.CloseFailed=TinyTorrent is still running. Finish any open prompt or operation, or choose Exit and stop transfers, then try again.
spanish.CloseFailed=TinyTorrent sigue en ejecución. Completa cualquier aviso u operación pendiente, o elige Salir y detener transferencias, y vuelve a intentarlo.
english.RegistrationFailed=Windows integration could not be updated. You can retry these choices in TinyTorrent Settings.
spanish.RegistrationFailed=No se pudo actualizar la integración con Windows. Puedes volver a intentarlo en los ajustes de TinyTorrent.

[Tasks]
Name: "handlers"; Description: "{cm:Handlers}"; Check: FirstInstall
Name: "startup"; Description: "{cm:Startup}"; Flags: unchecked; Check: FirstInstall

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,*.xml"
Source: "{#SourceDir}\{#EngineName}.exe"; Flags: dontcopy
Source: "Requirements.ps1"; Flags: dontcopy
Source: "{#RuntimeDir}\requirements.json"; Flags: dontcopy

[Icons]
Name: "{userprograms}\TinyTorrent"; Filename: "{app}\{#EngineName}.exe"; AppUserModelID: "Syno.TinyTorrent"

[Run]
Filename: "{app}\{#EngineName}.exe"; Description: "{cm:Launch}"; Flags: nowait postinstall skipifsilent; Check: OfferLaunch

[Code]
var
  Downloads: TDownloadWizardPage;
  WasRunning: Boolean;
  RebootNeeded: Boolean;
  NewInstall: Boolean;

function FirstInstall: Boolean;
begin
  Result := NewInstall;
end;

function InitializeSetup: Boolean;
begin
  NewInstall := not RegKeyExists(HKCU64,
    'Software\Microsoft\Windows\CurrentVersion\Uninstall\Syno.TinyTorrent_is1');
  Result := True;
end;

function OfferLaunch: Boolean;
begin
  Result := not WasRunning and not RebootNeeded;
end;

function Requirement(const Kind: String): Boolean;
var
  Code: Integer;
begin
  if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' +
    ExpandConstant('{tmp}\Requirements.ps1') + '" -Kind ' + Kind,
    '', SW_HIDE, ewWaitUntilTerminated, Code) then
    RaiseException(CustomMessage('CheckFailed'));
  if Code > 1 then
    RaiseException(CustomMessage('CheckFailed'));
  Result := Code = 0;
end;

procedure InitializeWizard;
begin
  ExtractTemporaryFile('Requirements.ps1');
  ExtractTemporaryFile('requirements.json');
  Downloads := CreateDownloadPage(CustomMessage('DownloadTitle'),
    CustomMessage('DownloadDescription'), nil);
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = wpSelectTasks) and not FirstInstall;
end;

procedure InstallRuntime(const Kind, Parameters: String; Elevated: Boolean);
var
  Code: Integer;
  Started: Boolean;
begin
  WizardForm.StatusLabel.Caption := FmtMessage(
    CustomMessage('InstallRuntime'), [CustomMessage(Kind)]);
  if Elevated then
    Started := ShellExec('runas', ExpandConstant('{tmp}\' + Kind + '.exe'),
      Parameters, '', SW_HIDE, ewWaitUntilTerminated, Code)
  else
    Started := Exec(ExpandConstant('{tmp}\' + Kind + '.exe'),
      Parameters, '', SW_HIDE, ewWaitUntilTerminated, Code);
  if not Started or ((Code <> 0) and (Code <> 3010)) then
    RaiseException(FmtMessage(
      CustomMessage('RuntimeFailed'), [CustomMessage(Kind), IntToStr(Code)]));
  RebootNeeded := RebootNeeded or (Code = 3010);
end;

function StopEngine(const Filename: String): Boolean;
var
  Code: Integer;
begin
  Result := Exec(Filename, '--headless --exit', '', SW_HIDE,
    ewWaitUntilTerminated, Code) and (Code = 0);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  DotNet, Windows, VisualCpp: Boolean;
  Engine: String;
begin
  Result := '';
  try
    DotNet := not Requirement('DotNet');
    Windows := not Requirement('Windows');
    VisualCpp := not Requirement('VisualCpp');
    Downloads.Clear;
    if DotNet then
      Downloads.Add('{#DotNetUrl}', 'DotNet.exe', '{#DotNetHash}');
    if Windows then
      Downloads.Add('{#WindowsUrl}', 'Windows.exe', '{#WindowsHash}');
    if VisualCpp then
      Downloads.Add('{#VisualCppUrl}', 'VisualCpp.exe', '{#VisualCppHash}');
    if DotNet or Windows or VisualCpp then
    begin
      Downloads.Show;
      try
        Downloads.Download;
      finally
        Downloads.Hide;
      end;
      if VisualCpp then InstallRuntime('VisualCpp', '/install /quiet /norestart', True);
      if DotNet then InstallRuntime('DotNet', '/install /quiet /norestart', True);
      if Windows then InstallRuntime('Windows', '--quiet', False);
      if not Requirement('DotNet') or not Requirement('Windows') or
        not Requirement('VisualCpp') then
      begin
        NeedsRestart := RebootNeeded;
        RaiseException(CustomMessage('CheckFailed'));
      end;
    end;
    WasRunning := WasRunning or Requirement('Running');
    Engine := ExpandConstant('{app}\{#EngineName}.exe');
    if not FileExists(Engine) then
    begin
      ExtractTemporaryFile('{#EngineName}.exe');
      Engine := ExpandConstant('{tmp}\{#EngineName}.exe');
    end;
    if not StopEngine(Engine) then
      Result := CustomMessage('CloseFailed');
  except
    Result := GetExceptionMessage;
  end;
end;

function Registration(const Operation: String): Boolean;
var
  Code: Integer;
begin
  Result := Exec(ExpandConstant('{app}\{#EngineName}.exe'),
    '--headless --registration ' + Operation, '', SW_HIDE,
    ewWaitUntilTerminated, Code) and (Code = 0);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
  Registered: Boolean;
begin
  if CurStep = ssPostInstall then
  begin
    if FirstInstall then
    begin
      if WizardIsTaskSelected('handlers') then
      begin
        if WizardSilent then
          Registered := Registration('register_handlers')
        else
          Registered := Registration('open_defaults');
        if not Registered then
          RaiseException(CustomMessage('RegistrationFailed'));
      end
      else if not Registration('unregister_handlers') then
        RaiseException(CustomMessage('RegistrationFailed'));
      if WizardIsTaskSelected('startup') then
      begin
        if not Registration('enable_startup') then
          RaiseException(CustomMessage('RegistrationFailed'));
      end
      else if not Registration('disable_startup') then
        RaiseException(CustomMessage('RegistrationFailed'));
    end;
    if WasRunning and not RebootNeeded then
      if not Exec(ExpandConstant('{app}\{#EngineName}.exe'), '--background', '',
        SW_SHOWNORMAL, ewNoWait, Code) then
        RaiseException(CustomMessage('CloseFailed'));
  end;
end;

function NeedRestart: Boolean;
begin
  Result := RebootNeeded;
end;

function InitializeUninstall: Boolean;
begin
  Result := StopEngine(ExpandConstant('{app}\{#EngineName}.exe'));
  if not Result then
  begin
    SuppressibleMsgBox(CustomMessage('CloseFailed'), mbError, MB_OK, IDOK);
    exit;
  end;
  Result := Registration('unregister_handlers') and Registration('disable_startup');
  if not Result then
    SuppressibleMsgBox(CustomMessage('RegistrationFailed'), mbError, MB_OK, IDOK);
end;
