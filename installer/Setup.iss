#if Ver < EncodeVer(7, 1, 0)
  #error Inno Setup 7.1 or later is required. Run Release.cmd to obtain it automatically.
#endif

[Setup]
AppId=Syno.TinyTorrent
AppName=TinyTorrent
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}
VersionInfoProductVersion={#AppVersion}
VersionInfoOriginalFileName={#InstallerName}.exe
AppPublisherURL=https://github.com/SynoSoftware/TinyTorrent2
AppUpdatesURL=https://github.com/SynoSoftware/TinyTorrent2/releases/latest
DefaultDirName={localappdata}\Programs\TinyTorrent
PrivilegesRequired=lowest
SetupArchitecture=x86
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion={#MinWindows}
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableWelcomePage=yes
; It only repeats the choices on the pages before it.
DisableReadyPage=yes
ShowLanguageDialog=no
; The plugin tree shows only for the custom type. Every run opens on the
; first type, because the tree's starting state comes from the app, not from
; an earlier Setup.
AlwaysShowComponentsList=no
UsePreviousSetupType=no
WizardStyle=modern dynamic
SetupIconFile=..\resources\TinyTorrent.ico
UninstallDisplayIcon={app}\{#EngineName}.exe
OutputDir={#OutputDir}
OutputBaseFilename={#InstallerName}
Compression=lzma2/max
SolidCompression=yes
CloseApplications=yes
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
english.Typical=Typical installation
spanish.Typical=Instalación típica
english.Custom=Custom installation
spanish.Custom=Instalación personalizada
english.Update=Update TinyTorrent and keep the current features
spanish.Update=Actualizar TinyTorrent y mantener las funciones actuales
english.UpdateCustom=Update TinyTorrent and change features
spanish.UpdateCustom=Actualizar TinyTorrent y cambiar las funciones
english.UpdateReady=Click Install when you are ready to continue.
spanish.UpdateReady=Haga clic en Instalar cuando esté listo para continuar.
english.ChangePlugins=To turn a feature on, select its check box. To turn a feature off and remove its data, clear its check box. Click Install when you are ready to continue.
spanish.ChangePlugins=Para activar una función, marque su casilla. Para desactivarla y eliminar sus datos, desmarque su casilla. Haga clic en Instalar cuando esté listo para continuar.
english.Behavior=Behavior
spanish.Behavior=Comportamiento
english.Features=Features
spanish.Features=Funciones
english.Gozler=Gozler (installed)
spanish.Gozler=Gozler (instalado)
english.GozlerDownload=Gozler (needed to discover media; downloads about %1 MB)
spanish.GozlerDownload=Gozler (necesario para detectar multimedia; descarga unos %1 MB)
english.Discovery=Discover media on your whole computer
spanish.Discovery=Detectar multimedia en todo el equipo
english.Imdb=IMDb ratings
spanish.Imdb=Valoraciones de IMDb
english.ImdbInstalled=IMDb ratings (installed)
spanish.ImdbInstalled=Valoraciones de IMDb (instaladas)
english.ImdbDownload=IMDb ratings (downloads about %1 MB; setup takes a few minutes)
spanish.ImdbDownload=Valoraciones de IMDb (descarga unos %1 MB; la preparación tarda unos minutos)
english.Cities=City data for the peer map
spanish.Cities=Datos de ciudades para el mapa de pares
english.CitiesInstalled=City data for the peer map (installed)
spanish.CitiesInstalled=Datos de ciudades para el mapa de pares (instalados)
english.CitiesDownload=City data for the peer map (downloads about %1 MB)
spanish.CitiesDownload=Datos de ciudades para el mapa de pares (descarga unos %1 MB)
english.Subtitles=Download subtitles automatically
spanish.Subtitles=Descargar subtítulos automáticamente
english.Video=Download video information automatically
spanish.Video=Descargar información de vídeo automáticamente
english.Launch=Open TinyTorrent
spanish.Launch=Abrir TinyTorrent
english.WhatsNew=What's new
spanish.WhatsNew=Novedades
english.CloseNotes=Close
spanish.CloseNotes=Cerrar
english.DownloadTitle=Preparing TinyTorrent
spanish.DownloadTitle=Preparando TinyTorrent
english.DownloadDescription=Downloading required Microsoft components...
spanish.DownloadDescription=Descargando los componentes de Microsoft necesarios...
english.PrepareDescription=This takes a few seconds.
spanish.PrepareDescription=Esto tarda unos segundos.
english.Checking=Checking required components...
spanish.Checking=Comprobando los componentes necesarios...
english.Closing=Closing TinyTorrent...
spanish.Closing=Cerrando TinyTorrent...
english.InstallRuntime=Installing %1...
spanish.InstallRuntime=Instalando %1...
english.DotNet=Microsoft .NET Runtime
spanish.DotNet=Microsoft .NET Runtime
english.DotNetInstalled=Microsoft .NET Runtime (installed)
spanish.DotNetInstalled=Microsoft .NET Runtime (instalado)
english.DotNetDownload=Microsoft .NET Runtime (downloads about %1 MB)
spanish.DotNetDownload=Microsoft .NET Runtime (descarga unos %1 MB)
english.Windows=Windows App Runtime
spanish.Windows=Windows App Runtime
english.RuntimeFailed=%1 could not be installed. Please try again. If Windows asks for administrator approval, allow the Microsoft installer. Error: %2
spanish.RuntimeFailed=No se pudo instalar %1. Inténtalo de nuevo. Si Windows solicita permisos de administrador, permite el instalador de Microsoft. Error: %2
english.RuntimeRestart=Restart Windows to finish installing %1, then run TinyTorrent Setup again.
spanish.RuntimeRestart=Reinicia Windows para terminar de instalar %1 y vuelve a ejecutar el instalador de TinyTorrent.
english.CheckFailed=The required components could not be checked. Please try again or contact support.
spanish.CheckFailed=No se pudieron comprobar los componentes necesarios. Inténtalo de nuevo o contacta con soporte.
english.CloseFailed=TinyTorrent is still running. Finish any open prompt or operation, or choose Exit and stop transfers, then try again.
spanish.CloseFailed=TinyTorrent sigue en ejecución. Completa cualquier aviso u operación pendiente, o elige Salir y detener transferencias, y vuelve a intentarlo.
english.RegistrationFailed=Windows integration could not be updated. You can retry these choices in TinyTorrent Settings.
spanish.RegistrationFailed=No se pudo actualizar la integración con Windows. Puedes volver a intentarlo en los ajustes de TinyTorrent.

[Types]
Name: "typical"; Description: "{cm:Typical}"
Name: "custom"; Description: "{cm:Custom}"; Flags: iscustom

; The plugins install nothing themselves: the app applies the person's changes
; at its next start (SaveChoices), so a deselected plugin is turned off by the
; app, not left behind by Setup.
[Components]
Name: "app"; Description: "TinyTorrent"; Types: typical custom; Flags: fixed
; TinyTorrent needs it, so it is never cleared; InstallRuntime downloads it.
Name: "app\dotnet"; Description: "{cm:DotNet}"; Types: typical custom; Flags: fixed
; Behavior holds the settings, Features what downloads. Subtitles download
; only when a torrent finishes, so a typical installation turns them on;
; video information would ask about every movie already on the disks, so it
; starts off. Media discovery needs gozler, which can also be installed alone;
; ComponentsClickCheck keeps the two consistent.
Name: "behavior"; Description: "{cm:Behavior}"; Types: typical; Flags: disablenouninstallwarning
Name: "behavior\subtitles"; Description: "{cm:Subtitles}"; Types: typical; Flags: disablenouninstallwarning
Name: "behavior\video"; Description: "{cm:Video}"; Flags: disablenouninstallwarning
Name: "behavior\discovery"; Description: "{cm:Discovery}"; Flags: disablenouninstallwarning
Name: "features"; Description: "{cm:Features}"; Flags: disablenouninstallwarning
Name: "features\gozler"; Description: "Gozler"; Flags: disablenouninstallwarning
Name: "features\imdb"; Description: "{cm:Imdb}"; Flags: disablenouninstallwarning
Name: "features\cities"; Description: "{cm:Cities}"; Flags: disablenouninstallwarning

[Tasks]
Name: "handlers"; Description: "{cm:Handlers}"; Check: FirstInstall
Name: "startup"; Description: "{cm:Startup}"; Flags: unchecked; Check: FirstInstall

[Files]
; [Code] extracts these to {tmp}; listed first, they come first in the solid
; archive, so Setup does not unpack the whole app to reach them.
Source: "Requirements.ps1"; Flags: dontcopy
Source: "{#RuntimeDir}\requirements.json"; Flags: dontcopy
Source: "{#RuntimeDir}\WhatsNew.rtf"; Flags: dontcopy noencryption
Source: "{#SourceDir}\{#EngineName}.exe"; Flags: dontcopy
Source: "{#SourceDir}\*"; DestDir: "{app}"; Components: app; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,*.xml"

[Icons]
Name: "{userprograms}\TinyTorrent"; Filename: "{app}\{#EngineName}.exe"; AppUserModelID: "Syno.TinyTorrent"

[UninstallDelete]
Type: files; Name: "{app}\setup-choices.txt"

[Run]
Filename: "{app}\{#EngineName}.exe"; Description: "{cm:Launch}"; Flags: nowait postinstall skipifsilent; Check: OfferLaunch

[Code]
var
  Downloads: TDownloadWizardPage;
  Status: TOutputMarqueeProgressWizardPage;
  InstalledPlugins: String;
  InnoClickCheck: TNotifyEvent;
  InnoTypesChange: TNotifyEvent;
  DotNetMissing: Boolean;
  EarlyCheck: Boolean;
  WasRunning: Boolean;
  RebootNeeded: Boolean;
  NewInstall: Boolean;
  WhatsNewButton: TNewButton;

const
  SQLITE_OK = 0;
  SQLITE_ROW = 100;
  SQLITE_OPEN_READONLY = 1;

// The SQLite that Windows includes; its 32-bit build uses stdcall.
function sqlite3_open_v2(Filename: AnsiString; var Db: INT_PTR; Flags: Integer; Vfs: INT_PTR): Integer;
  external 'sqlite3_open_v2@winsqlite3.dll stdcall delayload setuponly';
function sqlite3_busy_timeout(Db: INT_PTR; Milliseconds: Integer): Integer;
  external 'sqlite3_busy_timeout@winsqlite3.dll stdcall delayload setuponly';
function sqlite3_prepare16_v2(Db: INT_PTR; Sql: String; Bytes: Integer; var Statement: INT_PTR; Tail: INT_PTR): Integer;
  external 'sqlite3_prepare16_v2@winsqlite3.dll stdcall delayload setuponly';
function sqlite3_step(Statement: INT_PTR): Integer;
  external 'sqlite3_step@winsqlite3.dll stdcall delayload setuponly';
function sqlite3_column_int(Statement: INT_PTR; Column: Integer): Integer;
  external 'sqlite3_column_int@winsqlite3.dll stdcall delayload setuponly';
function sqlite3_finalize(Statement: INT_PTR): Integer;
  external 'sqlite3_finalize@winsqlite3.dll stdcall delayload setuponly';
function sqlite3_close(Db: INT_PTR): Integer;
  external 'sqlite3_close@winsqlite3.dll stdcall delayload setuponly';

function FirstInstall: Boolean;
begin
  Result := NewInstall;
end;

procedure ShowWhatsNew(Sender: TObject);
var
  Dialog: TSetupForm;
  Notes: TRichEditViewer;
  CloseButton: TNewButton;
  NotesFile: String;
  Content: AnsiString;
begin
  NotesFile := ExpandConstant('{tmp}\WhatsNew.rtf');
  if not FileExists(NotesFile) then
    ExtractTemporaryFile('WhatsNew.rtf');
  if not LoadStringFromFile(NotesFile, Content) then exit;
  Dialog := CreateCustomForm(720, 500, True, True);
  try
    Dialog.Caption := CustomMessage('WhatsNew');
    Dialog.BorderStyle := bsSizeable;
    Dialog.BorderIcons := [biSystemMenu, biMaximize];
    Dialog.Constraints.MinWidth := ScaleX(360);
    Dialog.Constraints.MinHeight := ScaleY(240);
    Dialog.Position := poMainFormCenter;
    CloseButton := TNewButton.Create(Dialog);
    CloseButton.Parent := Dialog;
    CloseButton.SetBounds(Dialog.ClientWidth - ScaleX(87), Dialog.ClientHeight - ScaleY(35),
      ScaleX(75), ScaleY(23));
    CloseButton.Anchors := [akRight, akBottom];
    CloseButton.Caption := CustomMessage('CloseNotes');
    CloseButton.ModalResult := mrOk;
    CloseButton.Default := True;
    CloseButton.Cancel := True;
    Notes := TRichEditViewer.Create(Dialog);
    Notes.UseRichEdit := True;
    Notes.Parent := Dialog;
    Notes.SetBounds(ScaleX(12), ScaleY(12), Dialog.ClientWidth - ScaleX(24),
      CloseButton.Top - ScaleY(24));
    Notes.Anchors := [akLeft, akTop, akRight, akBottom];
    Notes.ReadOnly := True;
    Notes.Font.Size := 11;
    Notes.ScrollBars := ssVertical;
    Notes.RTFText := Content;
    Dialog.ActiveControl := Notes;
    Dialog.ShowModal;
  finally
    Dialog.Free;
  end;
end;

// Whether the app's downloaded data is on this computer: the file names of
// the app's Imdb.Dataset and Geolocation, in the engine's data folder.
function DataInstalled(const Name: String): Boolean;
begin
  Result := FileExists(ExpandConstant('{localappdata}\TinyTorrent\') + Name);
end;

// Reads one of the installed app's on/off settings. A setting that cannot be
// read keeps the value given, so the other settings are still read.
procedure ReadSetting(Db: INT_PTR; const Sql: String; var Value: Boolean);
var
  Statement: INT_PTR;
begin
  Statement := 0;
  if sqlite3_prepare16_v2(Db, Sql, -1, Statement, 0) <> SQLITE_OK then
  begin
    Log('A plugin setting was not read: ' + Sql);
    exit;
  end;
  if sqlite3_step(Statement) = SQLITE_ROW then
    Value := sqlite3_column_int(Statement, 0) <> 0
  else
    Log('A plugin setting was not read: ' + Sql);
  sqlite3_finalize(Statement);
end;

// Reads media discovery, automatic subtitles and automatic video information
// from the Library database, where the app's Settings saves them
// (Shared\Storage\Database\Schema.cs). Without a Library database the app has
// its defaults: all off, as is a setting that cannot be read. An earlier
// version saved IMDb as a provider with automatic downloads; the app now
// starts that choice off (Library\VideoLookup.cs), so it reads as off.
procedure ReadPluginSettings(var Discovery, Subtitles, Video: Boolean);
var
  Path: String;
  Db: INT_PTR;
begin
  Path := ExpandConstant('{localappdata}\TinyTorrent\library.db');
  if not FileExists(Path) then
  begin
    Discovery := False;
    Subtitles := False;
    Video := False;
    exit;
  end;
  Db := 0;
  try
    if sqlite3_open_v2(Utf8Encode(Path), Db, SQLITE_OPEN_READONLY, 0) = SQLITE_OK then
    begin
      // The running app can hold a write for a moment.
      sqlite3_busy_timeout(Db, 2000);
      ReadSetting(Db, 'SELECT discovery FROM library_settings;', Discovery);
      ReadSetting(Db, 'SELECT enabled FROM subtitle_settings;', Subtitles);
      ReadSetting(Db, 'SELECT enabled AND source<>''imdb'' FROM library_settings;', Video);
    end
    else
      Log('The Library database was not opened.');
    // A failed open also returns a handle to close.
    sqlite3_close(Db);
  except
    Log('The Library database was not read: ' + GetExceptionMessage);
  end;
end;

function PluginInstalled(const Name: String): Boolean;
begin
  Result := Pos(',' + Name + ',', InstalledPlugins) > 0;
end;

// What the installed app has: its settings and the data it downloaded.
procedure ReadInstalledPlugins;
var
  Discovery, Subtitles, Video: Boolean;
begin
  Discovery := False;
  Subtitles := False;
  Video := False;
  ReadPluginSettings(Discovery, Subtitles, Video);
  InstalledPlugins := ',';
  if Discovery then
    InstalledPlugins := InstalledPlugins + 'discovery,';
  if DataInstalled('imdb-2.db') then
    InstalledPlugins := InstalledPlugins + 'imdb,';
  if DataInstalled('dbip-city-lite.mmdb') then
    InstalledPlugins := InstalledPlugins + 'cities,';
  if Subtitles then
    InstalledPlugins := InstalledPlugins + 'subtitles,';
  if Video then
    InstalledPlugins := InstalledPlugins + 'video,';
end;

const
  // The rows of the tree, in [Components] order.
  DotNetRow = 1;
  FeaturesRow = 6;
  GozlerRow = 7;
  ImdbRow = 8;
  CitiesRow = 9;
  // Microsoft.NETCore.App 10.0.12, measured in November 2026.
  DotNetBytes = 80006873;

function ComponentOf(const Name: String): String;
begin
  if (Name = 'subtitles') or (Name = 'video') or (Name = 'discovery') then
    Result := 'behavior\' + Name
  else
    Result := 'features\' + Name;
end;

// The app looks for gozler in the same service key.
function GozlerInstalled: Boolean;
begin
  Result := RegKeyExists(HKLM64, 'SYSTEM\CurrentControlSet\Services\gozler');
end;

// gozler is a program with its own uninstaller, so TinyTorrent's Setup never
// removes it: an installed gozler stays checked.
procedure KeepGozler;
begin
  if GozlerInstalled then
    WizardSelectComponents('features\gozler');
end;

function PluginSelection(const Name: String): String;
begin
  if PluginInstalled(Name) then
    Result := ComponentOf(Name)
  else
    Result := '!' + ComponentOf(Name);
end;

// The tree opens with what the installed app has, so a box the person leaves
// alone changes nothing. Discovery that is on checks a missing gozler, which
// then installs.
procedure SelectInstalledPlugins;
begin
  WizardSelectComponents(PluginSelection('discovery') + ',' + PluginSelection('imdb') + ','
    + PluginSelection('cities') + ',' + PluginSelection('subtitles') + ',' + PluginSelection('video'));
  if GozlerInstalled or PluginInstalled('discovery') then
    WizardSelectComponents('features\gozler')
  else
    WizardSelectComponents('!features\gozler');
end;

// Whether gozler or the data is on this computer already.
function PluginPresent(const Name: String): Boolean;
begin
  if Name = 'gozler' then
    Result := GozlerInstalled
  else
    Result := PluginInstalled(Name);
end;

// The disk space gozler and the data take once set up; their downloads are
// deleted after setup. Measured in November 2026.
function PluginBytes(const Name: String): Int64;
begin
  Result := 0;
  if Name = 'gozler' then
    Result := 22004300
  else if Name = 'imdb' then
    Result := 573722624
  else if Name = 'cities' then
    Result := 126998165;
end;

// What is on this computer says so; what still has to download says how
// much, rounded, because the downloads change each month.
procedure ShowPluginNames;
begin
  if DotNetMissing then
    WizardForm.ComponentsList.ItemCaption[DotNetRow] := FmtMessage(CustomMessage('DotNetDownload'), [IntToStr(({#DotNetDownloadBytes} + 524288) div 1048576)])
  else
    WizardForm.ComponentsList.ItemCaption[DotNetRow] := CustomMessage('DotNetInstalled');
  if GozlerInstalled then
    WizardForm.ComponentsList.ItemCaption[GozlerRow] := CustomMessage('Gozler')
  else
    WizardForm.ComponentsList.ItemCaption[GozlerRow] := FmtMessage(CustomMessage('GozlerDownload'), ['7']);
  // Grayed out, as Inno shows a component the person cannot change.
  WizardForm.ComponentsList.ItemEnabled[GozlerRow] := not GozlerInstalled;
  if PluginInstalled('imdb') then
    WizardForm.ComponentsList.ItemCaption[ImdbRow] := CustomMessage('ImdbInstalled')
  else
    WizardForm.ComponentsList.ItemCaption[ImdbRow] := FmtMessage(CustomMessage('ImdbDownload'), ['280']);
  if PluginInstalled('cities') then
    WizardForm.ComponentsList.ItemCaption[CitiesRow] := CustomMessage('CitiesInstalled')
  else
    WizardForm.ComponentsList.ItemCaption[CitiesRow] := FmtMessage(CustomMessage('CitiesDownload'), ['57']);
end;

// Rounds up, as Inno's own sizes do.
function MBText(Bytes: Int64): String;
begin
  Result := Format('%.1n', [((Bytes * 10 + 1048575) div 1048576) / 10.0]);
end;

function GBText(Bytes: Int64): String;
begin
  Result := Format('%.2n', [((Bytes * 100 + 1073741823) div 1073741824) / 100.0]);
end;

procedure ShowPluginSize(Row: Integer; const Name: String; var Plugins, Selected: Int64);
var
  Bytes: Int64;
begin
  Bytes := PluginBytes(Name);
  if Bytes = 0 then
    WizardForm.ComponentsList.ItemSubItem[Row] := ''
  else
    WizardForm.ComponentsList.ItemSubItem[Row] := FmtMessage(SetupMessage(msgComponentSize2), [MBText(Bytes)]);
  Plugins := Plugins + Bytes;
  if not PluginPresent(Name) and WizardIsComponentSelected(ComponentOf(Name)) then
    Selected := Selected + Bytes;
end;

// Each row shows the space its part takes. Inno's total counts that space
// whether or not the part is on this computer, so Setup's total counts only
// what is missing: TinyTorrent's files on a first installation (Build.ps1
// measures them), and the plugins that still have to download.
procedure ShowPluginSizes;
var
  Plugins, Selected: Int64;
  Text: String;
begin
  Plugins := 0;
  Selected := 0;
  if FirstInstall then
    Selected := {#AppBytes};
  WizardForm.ComponentsList.ItemSubItem[DotNetRow] := FmtMessage(SetupMessage(msgComponentSize2), [MBText(DotNetBytes)]);
  if DotNetMissing then
    Selected := Selected + DotNetBytes;
  ShowPluginSize(GozlerRow, 'gozler', Plugins, Selected);
  ShowPluginSize(ImdbRow, 'imdb', Plugins, Selected);
  ShowPluginSize(CitiesRow, 'cities', Plugins, Selected);
  if Plugins = 0 then
    WizardForm.ComponentsList.ItemSubItem[FeaturesRow] := ''
  else
    WizardForm.ComponentsList.ItemSubItem[FeaturesRow] := FmtMessage(SetupMessage(msgComponentSize2), [MBText(Plugins)]);
  // Inno switches to GB where the MB text would reach 1,000.0.
  if Selected > 1048471142 then
  begin
    Text := SetupMessage(msgComponentsDiskSpaceGBLabel);
    StringChangeEx(Text, '[gb]', GBText(Selected), True);
  end
  else
  begin
    Text := SetupMessage(msgComponentsDiskSpaceMBLabel);
    StringChangeEx(Text, '[mb]', MBText(Selected), True);
  end;
  WizardForm.ComponentsDiskSpaceLabel.Caption := Text;
end;

// Media discovery needs gozler: checking discovery checks gozler, and
// clearing gozler clears discovery. Inno writes its own sizes after each
// click, so Setup corrects them after it.
procedure ComponentsClickCheck(Sender: TObject);
begin
  InnoClickCheck(Sender);
  if WizardIsComponentSelected('behavior\discovery') and not WizardIsComponentSelected('features\gozler') then
    if WizardForm.ComponentsList.ItemIndex = GozlerRow then
      WizardSelectComponents('!behavior\discovery')
    else
      WizardSelectComponents('features\gozler');
  KeepGozler;
  ShowPluginSizes;
end;

procedure ShowComponentsLabel;
begin
  if FirstInstall then
    exit;
  if WizardSetupType(False) = 'custom' then
    WizardForm.SelectComponentsLabel.Caption := CustomMessage('ChangePlugins')
  else
    WizardForm.SelectComponentsLabel.Caption := CustomMessage('UpdateReady');
end;

// A typical installation also turns media discovery on when gozler is
// installed already; only the custom type can start installing gozler.
procedure SelectTypicalPlugins;
begin
  if FirstInstall and (WizardSetupType(False) = 'typical') and GozlerInstalled then
    WizardSelectComponents('behavior\discovery,features\gozler');
end;

// Inno's custom type keeps the typical type's selection, so the tree is set
// to what the app has each time the person chooses it.
procedure TypesChange(Sender: TObject);
begin
  InnoTypesChange(Sender);
  if WizardSetupType(False) = 'custom' then
    SelectInstalledPlugins
  else
    SelectTypicalPlugins;
  ShowComponentsLabel;
  ShowPluginSizes;
end;

procedure SaveChange(var Choices: String; const Name: String; TurnsOff: Boolean);
var
  Selected: Boolean;
begin
  Selected := WizardIsComponentSelected(ComponentOf(Name));
  if Selected <> PluginInstalled(Name) then
    if Selected then
      Choices := Choices + '+' + Name + #13#10
    else if TurnsOff then
      Choices := Choices + '-' + Name + #13#10;
end;

// Only the plugins whose box differs from the app are written, so a plugin
// the person did not touch keeps its setting. The custom type turns plugins
// on and off. A first installation's typical type only turns its plugins on,
// so a reinstallation keeps a plugin the person turned on before. An
// upgrade's first type changes nothing. The app applies the changes at its
// next start, as Settings would, and deletes the file:
// Shell\SetupChoices.cs.
procedure SaveChoices;
var
  Choices: String;
  Custom: Boolean;
begin
  Custom := WizardSetupType(False) = 'custom';
  if not Custom and not FirstInstall then
    exit;
  Choices := '';
  SaveChange(Choices, 'discovery', Custom);
  SaveChange(Choices, 'imdb', Custom);
  SaveChange(Choices, 'cities', Custom);
  SaveChange(Choices, 'subtitles', Custom);
  SaveChange(Choices, 'video', Custom);
  // Turning discovery on installs a missing gozler already. Discovery that
  // was on checked gozler, so clearing discovery leaves that box checked
  // without the person's choice.
  if not GozlerInstalled and WizardIsComponentSelected('features\gozler')
    and (Pos('+discovery', Choices) = 0) and (Pos('-discovery', Choices) = 0) then
    Choices := Choices + '+gozler' + #13#10;
  if (Choices <> '') and not SaveStringToFile(ExpandConstant('{app}\setup-choices.txt'), Choices, False) then
    Log('The plugin choices were not saved.');
end;

function InitializeSetup: Boolean;
begin
  NewInstall := not RegKeyExists(HKCU64,
    'Software\Microsoft\Windows\CurrentVersion\Uninstall\Syno.TinyTorrent_is1');
  Result := True;
end;

// TinyTorrent's Help > Update passes /OPEN=1, because the person was using the
// window and expects it back after the update.
function OpensWindow: Boolean;
begin
  Result := ExpandConstant('{param:OPEN|0}') = '1';
end;

// An update restarts a running TinyTorrent in the tray, and this checkbox
// opens its window. Help > Update has restarted the window already.
function OfferLaunch: Boolean;
begin
  Result := not RebootNeeded and not (WasRunning and OpensWindow);
end;

// A .NET runtime of the release's major and minor version, at least the
// version the release was built with, where the .NET host looks for it. This
// runs in Setup itself, because starting PowerShell takes seconds.
function DotNetReady: Boolean;
var
  Location: String;
  Search: TFindRec;
  Minimum, Version: Int64;
  MinimumMajor, MinimumMinor, Major, Minor, Revision, Build: Word;
begin
  Result := False;
  if not StrToVersion('{#DotNetVersion}', Minimum) then
    RaiseException(CustomMessage('CheckFailed'));
  UnpackVersionComponents(Minimum, MinimumMajor, MinimumMinor, Revision, Build);
  if not RegQueryStringValue(HKLM32, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64',
    'InstallLocation', Location) then
    Location := ExpandConstant('{commonpf64}\dotnet');
  if FindFirst(AddBackslash(Location) + 'shared\Microsoft.NETCore.App\*', Search) then
  try
    repeat
      if ((Search.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) and
        StrToVersion(Search.Name, Version) then
      begin
        UnpackVersionComponents(Version, Major, Minor, Revision, Build);
        Result := (Major = MinimumMajor) and (Minor = MinimumMinor) and
          (ComparePackedVersion(Version, Minimum) >= 0);
      end;
    until Result or not FindNext(Search);
  finally
    FindClose(Search);
  end;
end;

// Windows lists app packages only through PowerShell's Get-AppxPackage,
// which takes seconds.
function CheckCommand: String;
begin
  Result := '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' +
    ExpandConstant('{tmp}\Requirements.ps1') + '"';
end;

function WindowsReady: Boolean;
var
  Code: Integer;
begin
  if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    CheckCommand, '', SW_HIDE, ewWaitUntilTerminated, Code) then
    RaiseException(CustomMessage('CheckFailed'));
  if (Code <> 0) and (Code <> 1) then
    RaiseException(CustomMessage('CheckFailed'));
  Result := Code = 0;
end;

// The check starts as soon as Setup opens, so it runs while the person reads
// the first page, and writes its exit code to a file when it ends.
procedure StartWindowsCheck;
var
  Code: Integer;
begin
  EarlyCheck := Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    CheckCommand + ' -Result "' + ExpandConstant('{tmp}\windows-ready.txt') + '"',
    '', SW_HIDE, ewNoWait, Code);
end;

// The early check's answer, waiting for it to end; the check runs again in
// full when it did not start, did not end in time, or could not tell.
function WindowsReadyAtStart: Boolean;
var
  Path: String;
  Text: AnsiString;
  Waited: Integer;
begin
  Path := ExpandConstant('{tmp}\windows-ready.txt');
  Waited := 0;
  while EarlyCheck and not FileExists(Path) and (Waited < 60000) do
  begin
    // Sleep alone stops the window from painting; Animate lets it paint.
    Status.Animate;
    Sleep(100);
    Waited := Waited + 100;
  end;
  if EarlyCheck and LoadStringFromFile(Path, Text) and (Trim(String(Text)) = '0') then
    Result := True
  else if EarlyCheck and LoadStringFromFile(Path, Text) and (Trim(String(Text)) = '1') then
    Result := False
  else
    Result := WindowsReady;
  // The answer is from before Setup installed anything, so a retry asks again.
  EarlyCheck := False;
end;

function RuntimeReady(const Kind: String): Boolean;
begin
  if Kind = 'DotNet' then
    Result := DotNetReady
  else
    Result := WindowsReady;
end;

// Every engine of this sign-in session has this window, and FindWindow sees
// only the windows of Setup's own session: engine\src\Desktop\Application.cpp.
function EngineRunning: Boolean;
begin
  Result := FindWindowByClassName('TinyTorrent.Broadcast') <> 0;
end;

procedure InitializeWizard;
begin
  if not WizardSilent and not FirstInstall then
  begin
    WhatsNewButton := TNewButton.Create(WizardForm);
    WhatsNewButton.Parent := WizardForm;
    WhatsNewButton.SetBounds(ScaleX(12), WizardForm.NextButton.Top,
      ScaleX(92), WizardForm.NextButton.Height);
    WhatsNewButton.Anchors := [akLeft, akBottom];
    WhatsNewButton.Caption := CustomMessage('WhatsNew');
    WhatsNewButton.OnClick := @ShowWhatsNew;
    WhatsNewButton.Visible := False;
  end;
  ExtractTemporaryFile('Requirements.ps1');
  ExtractTemporaryFile('requirements.json');
  StartWindowsCheck;
  Downloads := CreateDownloadPage(CustomMessage('DownloadTitle'),
    CustomMessage('DownloadDescription'), nil);
  Status := CreateOutputMarqueeProgressPage(CustomMessage('DownloadTitle'),
    CustomMessage('PrepareDescription'));
  ReadInstalledPlugins;
  DotNetMissing := not DotNetReady;
  ShowPluginNames;
  KeepGozler;
  if not FirstInstall then
  begin
    WizardForm.TypesCombo.Items.Strings[0] := CustomMessage('Update');
    WizardForm.TypesCombo.Items.Strings[1] := CustomMessage('UpdateCustom');
    WizardForm.TypesCombo.ItemIndex := 0;
  end;
  SelectTypicalPlugins;
  ShowPluginSizes;
  InnoClickCheck := WizardForm.ComponentsList.OnClickCheck;
  WizardForm.ComponentsList.OnClickCheck := @ComponentsClickCheck;
  InnoTypesChange := WizardForm.TypesCombo.OnChange;
  WizardForm.TypesCombo.OnChange := @TypesChange;
end;

procedure ShowStatus(const Text: String);
begin
  Status.SetText(Text, '');
  Status.Animate;
end;

// An upgrade shows only the components page.
function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = wpSelectTasks) and not FirstInstall;
end;

// Without the Ready page, the last page before the installation starts it,
// and Inno leaves that caption to the script.
procedure CurPageChanged(CurPageID: Integer);
begin
  if WhatsNewButton <> nil then
    WhatsNewButton.Visible := (CurPageID = wpSelectComponents) or (CurPageID = wpFinished);
  if (CurPageID = wpSelectComponents) and not FirstInstall then
  begin
    ShowComponentsLabel;
    WizardForm.NextButton.Caption := SetupMessage(msgButtonInstall);
  end
  else if CurPageID = wpSelectTasks then
    WizardForm.NextButton.Caption := SetupMessage(msgButtonInstall);
end;

procedure InstallRuntime(const Kind, Parameters: String; Elevated: Boolean;
  var NeedsRestart: Boolean);
var
  Code: Integer;
  Started: Boolean;
begin
  ShowStatus(FmtMessage(CustomMessage('InstallRuntime'), [CustomMessage(Kind)]));
  if Elevated then
    Started := ShellExec('runas', ExpandConstant('{tmp}\' + Kind + '.exe'),
      Parameters, '', SW_HIDE, ewWaitUntilTerminated, Code)
  else
    Started := Exec(ExpandConstant('{tmp}\' + Kind + '.exe'),
      Parameters, '', SW_HIDE, ewWaitUntilTerminated, Code);
  Log(Format('%s installer: started=%d, code=%d', [Kind, Ord(Started), Code]));
  RebootNeeded := RebootNeeded or (Started and (Code = 3010));
  if RuntimeReady(Kind) then exit;
  if Started and (Code = 3010) then
  begin
    NeedsRestart := True;
    RaiseException(FmtMessage(CustomMessage('RuntimeRestart'), [CustomMessage(Kind)]));
  end;
  RaiseException(FmtMessage(
    CustomMessage('RuntimeFailed'), [CustomMessage(Kind), IntToStr(Code)]));
end;

function StopEngine(const Filename: String): Boolean;
var
  Code: Integer;
begin
  // With no installed engine and none running, nothing needs to exit. An
  // engine started from another folder still gets the bundled engine's Exit.
  if not FileExists(Filename) and not EngineRunning then
  begin
    Result := True;
    exit;
  end;
  if Exec(Filename, '--headless --exit', '', SW_HIDE,
    ewWaitUntilTerminated, Code) then
  begin
    Result := Code = 0;
    exit;
  end;
  if IsUninstaller then
  begin
    Result := not FileExists(Filename);
    exit;
  end;
  Log(Format('Installed engine could not start (%d); using the bundled engine.', [Code]));
  ExtractTemporaryFile('{#EngineName}.exe');
  Result := Exec(ExpandConstant('{tmp}\{#EngineName}.exe'), '--headless --exit', '',
    SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  DotNet, Windows: Boolean;
begin
  Result := '';
  Status.Show;
  try
    try
      ShowStatus(CustomMessage('Checking'));
      DotNet := not DotNetReady;
      Windows := not WindowsReadyAtStart;
      Downloads.Clear;
      if DotNet then
        Downloads.Add('{#DotNetUrl}', 'DotNet.exe', '{#DotNetHash}');
      if Windows then
        Downloads.Add('{#WindowsUrl}', 'Windows.exe', '{#WindowsHash}');
      if DotNet or Windows then
      begin
        Status.Hide;
        Downloads.Show;
        try
          Downloads.Download;
        finally
          Downloads.Hide;
          Status.Show;
        end;
        if DotNet then InstallRuntime('DotNet', '/install /quiet /norestart', True, NeedsRestart);
        if Windows then InstallRuntime('Windows', '--quiet', False, NeedsRestart);
      end;
      WasRunning := WasRunning or EngineRunning;
      if WasRunning then
        ShowStatus(CustomMessage('Closing'));
      if not StopEngine(ExpandConstant('{app}\{#EngineName}.exe')) then
        Result := CustomMessage('CloseFailed');
    except
      Result := GetExceptionMessage;
    end;
  finally
    Status.Hide;
  end;
end;

function Registration(const Operation: String): Boolean;
var
  Code: Integer;
begin
  Result := Exec(ExpandConstant('{app}\{#EngineName}.exe'),
    '--headless --registration ' + Operation, '', SW_HIDE,
    ewWaitUntilTerminated, Code) and (Code = 0);
  Log(Format('Registration %s: code=%d', [Operation, Code]));
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
  Registered: Boolean;
  Parameters: String;
begin
  if CurStep = ssPostInstall then
  begin
    if FirstInstall then
    begin
      if WizardIsTaskSelected('handlers') then
        Registered := Registration('register_handlers')
      else
        Registered := Registration('unregister_handlers');
      if WizardIsTaskSelected('startup') then
        Registered := Registration('enable_startup') and Registered
      else
        Registered := Registration('disable_startup') and Registered;
      if not Registered then
        SuppressibleMsgBox(CustomMessage('RegistrationFailed'), mbInformation, MB_OK, IDOK);
    end;
    SaveChoices;
    if OpensWindow then
      Parameters := ''
    else
      Parameters := '--background';
    if WasRunning and not RebootNeeded then
      if not Exec(ExpandConstant('{app}\{#EngineName}.exe'), Parameters, '',
        SW_SHOWNORMAL, ewNoWait, Code) then
      begin
        Log(Format('Background restart failed (%d); offering launch on Finish.', [Code]));
        WasRunning := False;
      end;
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
  Registration('unregister_handlers');
  Registration('disable_startup');
end;
