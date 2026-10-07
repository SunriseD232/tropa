; Установщик Тропы (Inno Setup 6). Собирается tools/build-release.ps1:
;   ISCC.exe /DAppVersion=0.2.0 /DSourceDir=<publish> /DOutputDir=<out> installer\tropa.iss
;
; Что делает:
;   1. Перед установкой: аварийный откат прежней версии (прокси, служба), удаление прежней службы.
;   2. Копирует программу в Program Files\Tropa — писать туда может только администратор, поэтому
;      служба доверяет только Tropa.exe из этого каталога (ADR-016). Каталог выбрать нельзя.
;   3. Регистрирует и запускает службу (LocalSystem, отложенный автозапуск).
;   4. Автозапуск интерфейса — по галочке, записывает сама Тропа от имени пользователя.
; Удаление: откат всех изменений системы, удаление службы и файлов. Настройки и подписки
; пользователя удаляются только по его согласию.

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\out\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\out"
#endif

[Setup]
AppId={{6C8E2F1A-4B7D-4E3A-9C5B-2D1F0A8E7B63}
AppName=Тропа
AppVersion={#AppVersion}
AppVerName=Тропа {#AppVersion}
AppPublisher=Тропа
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\Tropa
DisableDirPage=yes
DefaultGroupName=Тропа
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=Tropa-{#AppVersion}-setup
SetupIconFile=..\src\Tropa.App\Assets\tropa.ico
UninstallDisplayIcon={app}\Tropa.exe
UninstallDisplayName=Тропа
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Файлы заменяем только после того, как сами остановили Тропу и службу (PrepareToInstall).
CloseApplications=no
RestartApplications=no

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "autostart"; Description: "Запускать Тропу вместе с Windows"; GroupDescription: "Дополнительно:"
Name: "desktopicon"; Description: "Значок на рабочем столе"; GroupDescription: "Дополнительно:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion createallsubdirs

[Icons]
Name: "{group}\Тропа"; Filename: "{app}\Tropa.exe"
Name: "{group}\Аварийно вернуть настройки сети"; Filename: "{app}\Tropa.exe"; Parameters: "--emergency-rollback"; Comment: "Отключить Тропу, вернуть прокси Windows и снять блокировку интернета"
Name: "{autodesktop}\Тропа"; Filename: "{app}\Tropa.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Tropa.Service.exe"; Parameters: "--install"; Flags: runhidden waituntilterminated; StatusMsg: "Устанавливаю службу Тропы…"
Filename: "{app}\Tropa.exe"; Parameters: "--set-autostart=on"; Flags: runasoriginaluser waituntilterminated; Tasks: autostart
Filename: "{app}\Tropa.exe"; Parameters: "--set-autostart=off"; Flags: runasoriginaluser waituntilterminated; Tasks: not autostart
Filename: "{app}\Tropa.exe"; Description: "Запустить Тропу"; Flags: postinstall nowait skipifsilent runasoriginaluser

[UninstallRun]
; Сначала откат от имени пользователя (прокси Windows, блокировка, служба), затем удаление службы.
Filename: "{app}\Tropa.exe"; Parameters: "--emergency-rollback"; Flags: runhidden waituntilterminated; RunOnceId: "TropaRollback"
Filename: "{app}\Tropa.exe"; Parameters: "--set-autostart=off"; Flags: runhidden waituntilterminated; RunOnceId: "TropaAutostartOff"
Filename: "{app}\Tropa.Service.exe"; Parameters: "--uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "TropaService"

[UninstallDelete]
; Только наши каталоги: рабочие файлы службы (журнал уже пуст после отката) и остатки в каталоге программы.
Type: filesandordirs; Name: "{commonappdata}\Tropa"
Type: filesandordirs; Name: "{app}"

[Code]
const
  AppExe = 'Tropa.exe';

function RunHidden(const FileName, Params: String): Integer;
var
  Code: Integer;
begin
  if not Exec(FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Code := -1;
  Result := Code;
end;

{ Закрывает только Тропу из каталога установки, а не процессы с таким же именем где-то ещё. }
procedure StopInstalledApp(const Dir: String);
begin
  RunHidden(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -Command "Get-Process -Name Tropa -ErrorAction SilentlyContinue | ' +
    'Where-Object { $_.Path -like ''' + Dir + '\*'' } | Stop-Process -Force"');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Dir: String;
begin
  Result := '';
  Dir := ExpandConstant('{app}');
  if FileExists(Dir + '\' + AppExe) then
  begin
    { Прежняя версия: вернуть прокси и сеть, закрыть интерфейс, убрать службу. }
    RunHidden(Dir + '\' + AppExe, '--emergency-rollback');
    StopInstalledApp(Dir);
  end;
  if FileExists(Dir + '\Tropa.Service.exe') then
    RunHidden(Dir + '\Tropa.Service.exe', '--uninstall');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    StopInstalledApp(ExpandConstant('{app}'));
  if CurUninstallStep = usPostUninstall then
  begin
    if (not UninstallSilent) and
       (MsgBox('Удалить также настройки, подписки и ключи серверов?' + #13#10 + #13#10 +
               'Выберите «Нет», если собираетесь установить Тропу снова — тогда ничего не придётся вводить заново.',
               mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) then
    begin
      DelTree(ExpandConstant('{userappdata}\Tropa'), True, True, True);
      DelTree(ExpandConstant('{localappdata}\Tropa'), True, True, True);
    end;
  end;
end;
