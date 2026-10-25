; Instalador do Whiteboard Simulator (Inno Setup 6).
; Instala o .NET 8 Desktop Runtime ANTES do app, só se ele ainda não existir no computador.
; Gere com: installer\build-installer.ps1

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish-fd"
#endif

#define AppName "Whiteboard Simulator"
#define AppExe  "WhiteboardSimulator.exe"

; Versão mínima do runtime exigida (o app é .NET 8 e aceita versões mais novas).
#define RuntimeMajor 8
; Sempre aponta para a versão de segurança mais recente do .NET 8 Desktop Runtime (x64).
#define RuntimeUrl "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe"

[Setup]
AppId={{6F0C2D4E-8B1A-4C57-9E3D-5A7B1F2C9D40}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppName}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#AppExe}
SetupIconFile=..\Assets\app.ico
OutputDir=output
OutputBaseFilename=WhiteboardSimulator-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Instalar o runtime exige administrador. Em linha de comando dá para usar /CURRENTUSER (só o app, sem runtime).
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=commandline
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "Criar um atalho na Área de &Trabalho"; GroupDescription: "Atalhos:"

[Files]
Source: "{#SourceDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Abrir o {#AppName}"; Flags: nowait postinstall skipifsilent

[Code]
var
  DownloadPage: TDownloadWizardPage;
  RuntimeNeeded: Boolean;

{ O .NET registra cada Desktop Runtime instalado em HKLM\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx.
  Vale qualquer versão com número principal >= 8. }
function IsDesktopRuntimeInstalled: Boolean;
var
  Names: TArrayOfString;
  I, Major: Integer;
begin
  Result := False;
  if RegGetValueNames(HKLM, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App', Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
    begin
      Major := StrToIntDef(Copy(Names[I], 1, Pos('.', Names[I]) - 1), 0);
      if Major >= {#RuntimeMajor} then
      begin
        Result := True;
        Exit;
      end;
    end;
end;

procedure InitializeWizard;
begin
  RuntimeNeeded := not IsDesktopRuntimeInstalled;
  DownloadPage := CreateDownloadPage('Baixando o .NET {#RuntimeMajor}', 'Baixando o .NET {#RuntimeMajor} Desktop Runtime (necessário para o {#AppName})...', nil);
end;

{ Antes de copiar o app: se faltar o runtime, baixa e instala (em silêncio). Se algo falhar, a instalação para aqui. }
function NextButtonClick(CurPageID: Integer): Boolean;
var
  RuntimeFile: String;
  ResultCode: Integer;
begin
  Result := True;
  if (CurPageID <> wpReady) or (not RuntimeNeeded) then Exit;

  RuntimeFile := ExpandConstant('{tmp}\windowsdesktop-runtime-win-x64.exe');
  DownloadPage.Clear;
  DownloadPage.Add('{#RuntimeUrl}', 'windowsdesktop-runtime-win-x64.exe', '');
  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
    except
      if SuppressibleMsgBox('Não foi possível baixar o .NET {#RuntimeMajor} Desktop Runtime:' + #13#10 + GetExceptionMessage + #13#10#13#10 +
         'Verifique a conexão com a internet e tente de novo, ou instale o runtime manualmente em https://dotnet.microsoft.com/download/dotnet/{#RuntimeMajor}.0',
         mbError, MB_OK, IDOK) = IDOK then;
      Result := False;
      Exit;
    end;
  finally
    DownloadPage.Hide;
  end;

  WizardForm.StatusLabel.Caption := 'Instalando o .NET {#RuntimeMajor} Desktop Runtime...';
  if not Exec(RuntimeFile, '/install /quiet /norestart', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    SuppressibleMsgBox('Não foi possível executar o instalador do .NET {#RuntimeMajor}.', mbError, MB_OK, IDOK);
    Result := False;
    Exit;
  end;

  { 0 = instalado; 3010 = instalado, pede reinício (o app já pode ser instalado). Outro código = falha. }
  if (ResultCode <> 0) and (ResultCode <> 3010) then
  begin
    SuppressibleMsgBox('A instalação do .NET {#RuntimeMajor} Desktop Runtime falhou (código ' + IntToStr(ResultCode) + ').' + #13#10 +
      'O {#AppName} não foi instalado.', mbError, MB_OK, IDOK);
    Result := False;
  end;
end;
