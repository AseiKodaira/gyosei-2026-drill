; 事業所記録アプリ インストーラー (Inno Setup 6 / 無償)
; ビルド: tools\build-windows.ps1 (dist\app と dist\server を作ってから ISCC を実行)
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

[Setup]
AppId={{6B0F7E52-4D1B-4C2E-9E0B-4B5E5B1A7C11}
AppName=事業所記録アプリ
AppVersion={#AppVersion}
AppPublisher=事業所内製
DefaultDirName={autopf}\KirokuApp
DefaultGroupName=事業所記録アプリ
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist\installer
OutputBaseFilename=KirokuApp-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
UninstallDisplayName=事業所記録アプリ

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"

[Dirs]
; 端末設定(暗号化済みの端末トークン)は一般ユーザーも読み書き。記録データ(data)は親機のサービスだけが触れる。
Name: "{commonappdata}\KirokuApp\device"; Permissions: users-modify

[Files]
Source: "..\dist\app\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion
Source: "..\dist\server\*"; DestDir: "{app}\server"; Flags: recursesubdirs ignoreversion; Check: IsParent

[Icons]
Name: "{commondesktop}\事業所記録アプリ"; Filename: "{app}\Kiroku.App.exe"
Name: "{commonstartup}\事業所記録アプリ"; Filename: "{app}\Kiroku.App.exe"

[Run]
; 親機: 共有サービスの登録・自動起動・ファイアウォール(同一サブネット限定)・データフォルダ権限を自動設定
Filename: "{app}\server\Kiroku.Server.exe"; Parameters: "install-service"; Flags: runhidden waituntilterminated; Check: IsParent; StatusMsg: "共有サービスを設定しています…"
Filename: "{app}\Kiroku.App.exe"; Description: "事業所記録アプリを起動する"; Flags: postinstall nowait skipifsilent

[UninstallRun]
Filename: "{app}\server\Kiroku.Server.exe"; Parameters: "uninstall-service"; Flags: runhidden waituntilterminated; RunOnceId: "KirokuSvc"; Check: FileExists(ExpandConstant('{app}\server\Kiroku.Server.exe'))

[Code]
var
  RolePage: TInputOptionWizardPage;

procedure InitializeWizard;
begin
  RolePage := CreateInputOptionPage(wpWelcome, 'このパソコンの役割', '親機は事業所に1台だけです。',
    'このパソコンを、記録を保管する「親機」にしますか? それとも「子機」にしますか?', True, False);
  RolePage.Add('このパソコンを親機にする(記録を保管・共有。業務時間中は電源を入れておく1台)');
  RolePage.Add('このパソコンを子機にする(親機につないで記録を入力)');
  RolePage.SelectedValueIndex := 0;
end;

function IsParent: Boolean;
begin
  Result := RolePage.SelectedValueIndex = 0;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Mode: String;
begin
  if CurStep = ssPostInstall then
  begin
    ForceDirectories(ExpandConstant('{commonappdata}\KirokuApp'));
    if IsParent then Mode := 'parent' else Mode := 'child';
    SaveStringToFile(ExpandConstant('{commonappdata}\KirokuApp\mode.txt'), Mode, False);
  end;
end;
