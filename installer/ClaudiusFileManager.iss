; Inno Setup script for Claudius File Manager (Windows 10/11 x64)
; Easiest: run build-installer.bat, which builds the app and then this script.
; Version can be overridden:  iscc /DMyAppVersion=1.2.0 installer\ClaudiusFileManager.iss

#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

#define MyAppName      "Claudius File Manager"
#define MyAppExeName   "ClaudiusFileManager.exe"
#define MyAppPublisher "Michal Czapla & Claude AI"

; Paths. build-installer.bat passes these in; the defaults work when the script
; sits in <repo>\installer\ and the app was published to <repo>\publish\.
#ifndef MyRoot
  #define MyRoot AddBackslash(SourcePath) + ".."
#endif
#ifndef MyAppSource
  #define MyAppSource MyRoot + "\publish\" + MyAppExeName
#endif
#ifndef MyAppIcon
  #define MyAppIcon MyRoot + "\src\ClaudiusFileManager\Assets\app.ico"
#endif
#define MyLicenseDir AddBackslash(SourcePath) + "license"

; The app must be built before the installer. Stop with a clear message if it is not.
#if !FileExists(MyAppSource)
  #error ClaudiusFileManager.exe has not been built yet. Close Inno Setup and double-click build-installer.bat in the main Claudius-File-Manager folder; it builds the app and then this installer. (Needs the .NET 8 SDK: winget install Microsoft.DotNet.SDK.8)
#endif

[Setup]
; Keep this AppId the same forever, so upgrades replace the old version.
AppId={{1DC18710-7A18-4D1F-BB4B-282BB07ADEF4}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppCopyright=Freeware - Created by Michal Czapla & Claude AI
AppComments=Freeware dual-pane file manager. Created by Michal Czapla & Claude AI.
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} Setup (Freeware)
VersionInfoProductName={#MyAppName}

; Guideline: install folder C:\Program Files (x86)\Claudius File Manager
DefaultDirName={commonpf32}\{#MyAppName}
; Guideline: a folder in the Start menu
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
AllowNoIcons=no
PrivilegesRequired=admin
UsedUserAreasWarning=no

; Guideline: uninstaller (also listed in Settings > Apps)
Uninstallable=yes
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}

OutputDir={#MyRoot}\installer-output
OutputBaseFilename=ClaudiusFileManager-Setup-{#MyAppVersion}
#if FileExists(MyAppIcon)
SetupIconFile={#MyAppIcon}
#endif
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
; Claudius branding on the wizard pages
WizardImageFile={#AddBackslash(SourcePath)}wizard-large.bmp
WizardSmallImageFile={#AddBackslash(SourcePath)}wizard-small.bmp
ShowLanguageDialog=yes
; 64-bit only, Windows 10 1809+ / Windows 11
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
; Close a running copy before upgrading
CloseApplications=yes
RestartApplications=no
ChangesAssociations=yes

; Guideline: translations. The language picked here also becomes the app's language.
[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl";           LicenseFile: "{#MyLicenseDir}\License-en.txt"
Name: "pl"; MessagesFile: "compiler:Languages\Polish.isl";  LicenseFile: "{#MyLicenseDir}\License-pl.txt"
Name: "de"; MessagesFile: "compiler:Languages\German.isl";  LicenseFile: "{#MyLicenseDir}\License-de.txt"
Name: "fr"; MessagesFile: "compiler:Languages\French.isl";  LicenseFile: "{#MyLicenseDir}\License-fr.txt"
Name: "it"; MessagesFile: "compiler:Languages\Italian.isl"; LicenseFile: "{#MyLicenseDir}\License-it.txt"
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"; LicenseFile: "{#MyLicenseDir}\License-es.txt"

[CustomMessages]
en.ExplorerGroup=Explorer integration:
en.ContextMenuTask=Add "Open in Claudius File Manager" to the folder right-click menu
en.OpenIn=Open in Claudius File Manager
en.LicenseShortcut=License (Freeware)
en.WelcomeFreeware=Claudius File Manager is freeware: free to use, copy and share, at home and at work.%n%nCreated by Michal Czapla & Claude AI.

pl.ExplorerGroup=Integracja z Eksploratorem:
pl.ContextMenuTask=Dodaj „Otwórz w Claudius File Manager” do menu kontekstowego folderów
pl.OpenIn=Otwórz w Claudius File Manager
pl.LicenseShortcut=Licencja (Freeware)
pl.WelcomeFreeware=Claudius File Manager jest programem darmowym: możesz go używać, kopiować i udostępniać — w domu i w pracy.%n%nAutorzy: Michal Czapla & Claude AI.

de.ExplorerGroup=Explorer-Integration:
de.ContextMenuTask=„In Claudius File Manager öffnen“ zum Kontextmenü von Ordnern hinzufügen
de.OpenIn=In Claudius File Manager öffnen
de.LicenseShortcut=Lizenz (Freeware)
de.WelcomeFreeware=Claudius File Manager ist Freeware: kostenlos nutzen, kopieren und weitergeben – privat und beruflich.%n%nErstellt von Michal Czapla & Claude AI.

fr.ExplorerGroup=Intégration à l'Explorateur :
fr.ContextMenuTask=Ajouter « Ouvrir dans Claudius File Manager » au menu contextuel des dossiers
fr.OpenIn=Ouvrir dans Claudius File Manager
fr.LicenseShortcut=Licence (gratuit)
fr.WelcomeFreeware=Claudius File Manager est un logiciel gratuit : utilisez-le, copiez-le et partagez-le librement, à la maison comme au travail.%n%nCréé par Michal Czapla & Claude AI.

it.ExplorerGroup=Integrazione con Esplora file:
it.ContextMenuTask=Aggiungi «Apri in Claudius File Manager» al menu contestuale delle cartelle
it.OpenIn=Apri in Claudius File Manager
it.LicenseShortcut=Licenza (Freeware)
it.WelcomeFreeware=Claudius File Manager è freeware: gratuito da usare, copiare e condividere, a casa e al lavoro.%n%nCreato da Michal Czapla & Claude AI.

es.ExplorerGroup=Integración con el Explorador:
es.ContextMenuTask=Añadir «Abrir en Claudius File Manager» al menú contextual de las carpetas
es.OpenIn=Abrir en Claudius File Manager
es.LicenseShortcut=Licencia (Freeware)
es.WelcomeFreeware=Claudius File Manager es freeware: gratis para usar, copiar y compartir, en casa y en el trabajo.%n%nCreado por Michal Czapla & Claude AI.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "contextmenu"; Description: "{cm:ContextMenuTask}"; GroupDescription: "{cm:ExplorerGroup}"; Flags: unchecked

[Files]
Source: "{#MyAppSource}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#MyLicenseDir}\License-en.txt"; DestDir: "{app}"; DestName: "License.txt"; Languages: en; Flags: ignoreversion
Source: "{#MyLicenseDir}\License-pl.txt"; DestDir: "{app}"; DestName: "License.txt"; Languages: pl; Flags: ignoreversion
Source: "{#MyLicenseDir}\License-de.txt"; DestDir: "{app}"; DestName: "License.txt"; Languages: de; Flags: ignoreversion
Source: "{#MyLicenseDir}\License-fr.txt"; DestDir: "{app}"; DestName: "License.txt"; Languages: fr; Flags: ignoreversion
Source: "{#MyLicenseDir}\License-it.txt"; DestDir: "{app}"; DestName: "License.txt"; Languages: it; Flags: ignoreversion
Source: "{#MyLicenseDir}\License-es.txt"; DestDir: "{app}"; DestName: "License.txt"; Languages: es; Flags: ignoreversion

[Icons]
; Start menu folder: the app, the licence, and the uninstaller
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:LicenseShortcut}"; Filename: "{app}\License.txt"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Language chosen in setup -> used by the app on first start
Root: HKCU; Subkey: "Software\ClaudiusFileManager"; ValueType: string; ValueName: "Language"; ValueData: "{language}"; Flags: uninsdeletekey
; Right-click on a folder
Root: HKA; Subkey: "Software\Classes\Directory\shell\ClaudiusFileManager"; ValueType: string; ValueName: ""; ValueData: "{cm:OpenIn}"; Tasks: contextmenu; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Directory\shell\ClaudiusFileManager"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#MyAppExeName}"",0"; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\Directory\shell\ClaudiusFileManager\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: contextmenu
; Right-click on empty space inside a folder
Root: HKA; Subkey: "Software\Classes\Directory\Background\shell\ClaudiusFileManager"; ValueType: string; ValueName: ""; ValueData: "{cm:OpenIn}"; Tasks: contextmenu; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Directory\Background\shell\ClaudiusFileManager"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#MyAppExeName}"",0"; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\Directory\Background\shell\ClaudiusFileManager\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%V"""; Tasks: contextmenu
; Right-click on a drive
Root: HKA; Subkey: "Software\Classes\Drive\shell\ClaudiusFileManager"; ValueType: string; ValueName: ""; ValueData: "{cm:OpenIn}"; Tasks: contextmenu; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Drive\shell\ClaudiusFileManager"; ValueType: string; ValueName: "Icon"; ValueData: """{app}\{#MyAppExeName}"",0"; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\Drive\shell\ClaudiusFileManager\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: contextmenu

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Saved panel paths, theme, language and window size
Type: filesandordirs; Name: "{userappdata}\ClaudiusFileManager"

[Code]
// Add the freeware note under the standard text on the Welcome page.
procedure InitializeWizard();
var
  S: String;
begin
  S := CustomMessage('WelcomeFreeware');
  StringChangeEx(S, '%n', #13#10, True);
  WizardForm.WelcomeLabel2.Caption := WizardForm.WelcomeLabel2.Caption + #13#10#13#10 + S;
end;
