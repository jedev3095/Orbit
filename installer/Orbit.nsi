; Build through build.ps1: the payload and uninstall list are generated from a
; fresh self-contained publish. Personal data is never part of that directory.
Unicode true
RequestExecutionLevel user
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
!include "WinVer.nsh"

Name "Orbit"
OutFile "${OUTPUT_FILE}"
InstallDir "$LOCALAPPDATA\Programs\Orbit"
InstallDirRegKey HKCU "Software\Orbit\Installer" "InstallLocation"
BrandingText "Orbit"
SetCompressor lzma
SetDatablockOptimize on
ShowInstDetails show
ShowUninstDetails show
VIProductVersion "${APP_VERSION}.0"
VIAddVersionKey "ProductName" "Orbit Setup"
VIAddVersionKey "CompanyName" "Orbit contributors"
VIAddVersionKey "FileDescription" "Orbit offline installer"
VIAddVersionKey "FileVersion" "${APP_VERSION}"
VIAddVersionKey "LegalCopyright" "Orbit contributors"
!define MUI_ICON "..\assets\orbit.ico"
!define MUI_UNICON "..\assets\orbit.ico"
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TEXT "Install Orbit for your Windows account.$\r$\n$\r$\nThe .NET runtime and Microsoft WebView2 offline installer are included. No separate downloads are needed.$\r$\n$\r$\nYour saved accounts and passwords are separate from the program files and are kept when uninstalling."
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN "$INSTDIR\Orbit.exe"
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH
!insertmacro MUI_LANGUAGE "English"

Var WebViewPresent
Var SetupMutex
!define WEBVIEW_KEY "Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\Orbit"

!macro RequireOrbitClosed
  UserInfo::GetName
  Pop $0
  System::Call 'kernel32::OpenMutexW(i 0x00100000, i 0, w "Orbit.AccountManager.$0") p.r1'
  ${If} $1 <> 0
    System::Call 'kernel32::CloseHandle(p r1)'
    MessageBox MB_OK|MB_ICONEXCLAMATION "Close Orbit before installing or uninstalling it." /SD IDOK
    SetErrorLevel 2
    Quit
  ${EndIf}
!macroend

Function .onInit
  SetShellVarContext current
  SetRegView 64
  ${IfNot} ${RunningX64}
    MessageBox MB_OK|MB_ICONSTOP "This installer requires 64-bit Windows." /SD IDOK
    SetErrorLevel 1
    Quit
  ${EndIf}
  ${IfNot} ${AtLeastWin10}
    MessageBox MB_OK|MB_ICONSTOP "Orbit requires Windows 10 or later." /SD IDOK
    SetErrorLevel 1
    Quit
  ${EndIf}
  !insertmacro RequireOrbitClosed
  UserInfo::GetName
  Pop $0
  System::Call 'kernel32::CreateMutexW(p 0, i 0, w "Orbit.Setup.$0") p.r1 ?e'
  Pop $2
  StrCpy $SetupMutex $1
  ${If} $2 = 183
    MessageBox MB_OK|MB_ICONEXCLAMATION "Orbit setup is already running." /SD IDOK
    SetErrorLevel 2
    Quit
  ${EndIf}
FunctionEnd

Function CheckWebView
  StrCpy $WebViewPresent 0
  SetRegView 32
  ReadRegStr $0 HKCU "${WEBVIEW_KEY}" "pv"
  ${If} $0 != ""
  ${AndIf} $0 != "0.0.0.0"
    StrCpy $WebViewPresent 1
  ${EndIf}
  ReadRegStr $0 HKLM "${WEBVIEW_KEY}" "pv"
  ${If} $0 != ""
  ${AndIf} $0 != "0.0.0.0"
    StrCpy $WebViewPresent 1
  ${EndIf}
  SetRegView 64
  ReadRegStr $0 HKCU "${WEBVIEW_KEY}" "pv"
  ${If} $0 != ""
  ${AndIf} $0 != "0.0.0.0"
    StrCpy $WebViewPresent 1
  ${EndIf}
FunctionEnd

Section "-WebView2 prerequisite"
  Call CheckWebView
  ${If} $WebViewPresent = 0
    DetailPrint "Installing the included Microsoft WebView2 Runtime..."
    InitPluginsDir
    SetOutPath "$PLUGINSDIR"
    SetCompress off
    File /oname=WebView2RuntimeSetup.exe "${WEBVIEW_INSTALLER}"
    SetCompress auto
    ClearErrors
    ExecWait '"$PLUGINSDIR\WebView2RuntimeSetup.exe" /silent /install' $0
    ${If} ${Errors}
      MessageBox MB_OK|MB_ICONSTOP "Microsoft WebView2 setup could not start. Orbit has not been installed." /SD IDOK
      SetErrorLevel 3
      Quit
    ${EndIf}
    ${If} $0 = 3010
      SetRebootFlag true
    ${EndIf}
    Call CheckWebView
    ${If} $WebViewPresent = 0
      MessageBox MB_OK|MB_ICONSTOP "Microsoft WebView2 could not be installed. Restart Windows if requested, then run Orbit setup again." /SD IDOK
      SetErrorLevel 3
      Quit
    ${EndIf}
  ${Else}
    DetailPrint "Microsoft WebView2 Runtime is already installed."
  ${EndIf}
SectionEnd

Section "Orbit (required)" Core
  SectionIn RO
  SetOutPath "$INSTDIR"
  SetOverwrite on
  ClearErrors
  File /r "${PAYLOAD_DIR}\*"
  ${If} ${Errors}
    MessageBox MB_OK|MB_ICONSTOP "Some program files could not be installed. Close Orbit, check the folder permissions and run setup again." /SD IDOK
    SetErrorLevel 4
    Quit
  ${EndIf}
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  WriteRegStr HKCU "Software\Orbit\Installer" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "Orbit"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "Publisher" "Orbit contributors"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\Orbit.exe"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
  WriteRegStr HKCU "${UNINSTALL_KEY}" "QuietUninstallString" '$\"$INSTDIR\Uninstall.exe$\" /S'
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "EstimatedSize" ${INSTALLED_KB}
  CreateDirectory "$SMPROGRAMS\Orbit"
  CreateShortcut "$SMPROGRAMS\Orbit\Orbit.lnk" "$INSTDIR\Orbit.exe"
  CreateShortcut "$SMPROGRAMS\Orbit\Uninstall Orbit.lnk" "$INSTDIR\Uninstall.exe"
SectionEnd

Section /o "Desktop shortcut" Desktop
  CreateShortcut "$DESKTOP\Orbit.lnk" "$INSTDIR\Orbit.exe"
  WriteRegDWORD HKCU "Software\Orbit\Installer" "DesktopShortcut" 1
SectionEnd

Function un.onInit
  SetShellVarContext current
  SetRegView 64
  !insertmacro RequireOrbitClosed
FunctionEnd

Section "Uninstall"
  ; This generated list deletes only files actually shipped in the payload.
  ; No recursive removal, browser profiles, vaults or backups are involved.
  !include "${UNINSTALL_INCLUDE}"
  Delete "$SMPROGRAMS\Orbit\Orbit.lnk"
  Delete "$SMPROGRAMS\Orbit\Uninstall Orbit.lnk"
  RMDir "$SMPROGRAMS\Orbit"
  ReadRegDWORD $0 HKCU "Software\Orbit\Installer" "DesktopShortcut"
  ${If} $0 = 1
    Delete "$DESKTOP\Orbit.lnk"
  ${EndIf}
  DeleteRegKey HKCU "${UNINSTALL_KEY}"
  DeleteRegKey HKCU "Software\Orbit\Installer"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
SectionEnd
