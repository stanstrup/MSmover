; MSmover installer
;
; Per-user by default: installs into %LOCALAPPDATA%\Programs\MSmover and writes only HKCU, so it
; needs no administrator rights. That matters on a locked-down instrument PC, and it matches how
; the application itself runs: inside the logged-in session, where mapped drives exist and where
; symlink privileges are the user's.
;
; Keep this file pure ASCII. Without a byte-order mark makensis reads the script in the machine's
; ANSI code page, so a stray UTF-8 character builds on one machine and fails on another.
; build\package.ps1 enforces it.
;
; Built by build\package.ps1, which supplies the paths and version via /D. Not meant to be
; compiled by hand.
;
;   makensis /DVERSION=1.2.3 /DNUMERIC_VERSION=1.2.3.0 /DPAYLOAD=... /DOUTFILE=... installer.nsi

Unicode true
ManifestDPIAware true

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"

!ifndef VERSION
  !error "VERSION must be defined (e.g. /DVERSION=1.2.3)"
!endif
!ifndef NUMERIC_VERSION
  !error "NUMERIC_VERSION must be defined (e.g. /DNUMERIC_VERSION=1.2.3.0)"
!endif
!ifndef PAYLOAD
  !error "PAYLOAD must be defined (path to the published MSmover.exe)"
!endif
!ifndef OUTFILE
  !error "OUTFILE must be defined (path of the installer to produce)"
!endif
!ifndef ROOT
  !define ROOT ".."
!endif

!define APP_NAME     "MSmover"
!define APP_PUBLISHER "MSmover contributors"
!define APP_URL      "https://github.com/stanstrup/MSmover"
!define APP_EXE      "MSmover.exe"
!define UNINST_KEY   "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}"
!define RUN_KEY      "Software\Microsoft\Windows\CurrentVersion\Run"

Name "${APP_NAME} ${VERSION}"
OutFile "${OUTFILE}"
InstallDir "$LOCALAPPDATA\Programs\${APP_NAME}"
InstallDirRegKey HKCU "Software\${APP_NAME}" "InstallDir"
RequestExecutionLevel user
SetCompressor /SOLID lzma

VIProductVersion "${NUMERIC_VERSION}"
VIAddVersionKey "ProductName"     "${APP_NAME}"
VIAddVersionKey "ProductVersion"  "${VERSION}"
VIAddVersionKey "FileVersion"     "${NUMERIC_VERSION}"
VIAddVersionKey "CompanyName"     "${APP_PUBLISHER}"
VIAddVersionKey "LegalCopyright"  "MIT licensed"
VIAddVersionKey "FileDescription" "${APP_NAME} ${VERSION} installer"

!define MUI_ICON   "${ROOT}\src\MSmover.App\msmover.ico"
!define MUI_UNICON "${ROOT}\src\MSmover.App\msmover.ico"
!define MUI_ABORTWARNING

!define MUI_FINISHPAGE_RUN "$INSTDIR\${APP_EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "Start ${APP_NAME} now"
!define MUI_FINISHPAGE_LINK "Documentation and releases"
!define MUI_FINISHPAGE_LINK_LOCATION "${APP_URL}"

!insertmacro MUI_PAGE_LICENSE "${ROOT}\LICENSE"
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "English"

; ---------------------------------------------------------------------------------------------
; A running instance holds its own executable open, so it has to go before we overwrite it.
; Cancelling is safe: MSmover discards an incomplete destination file and never touches the
; source until a copy has been verified.
; ---------------------------------------------------------------------------------------------
; Piping tasklist through find means the exit code alone answers "is it running?" -- 0 when a
; line matched, 1 when none did. That avoids a string helper, which matters because a function
; called from the uninstaller has to be a separate "un." copy of itself.
!macro CloseRunningInstance UN
Function ${UN}CloseRunningInstance
  nsExec::ExecToStack 'cmd /c tasklist /FI "IMAGENAME eq ${APP_EXE}" /NH | find /I "${APP_EXE}"'
  Pop $0   ; exit code, or "error" if the command could not be run at all
  Pop $1   ; output, unused

  ${If} $0 == 0
    ; An unattended install must not stop to ask. /SD is belt and braces for the same reason.
    IfSilent closeit
    MessageBox MB_OKCANCEL|MB_ICONEXCLAMATION \
      "${APP_NAME} is running and must be closed to continue.$\n$\n\
       Any transfer in progress will be cancelled. No data is lost: the incomplete copy at the \
       destination is discarded and the source file is left untouched.$\n$\n\
       Close it now?" /SD IDOK IDOK closeit
    Abort
    closeit:
    nsExec::ExecToLog 'taskkill /IM "${APP_EXE}" /F'
    Pop $0
    Sleep 1500
  ${EndIf}
FunctionEnd
!macroend

!insertmacro CloseRunningInstance ""
!insertmacro CloseRunningInstance "un."

; ---------------------------------------------------------------------------------------------

Section "${APP_NAME} (required)" SEC_APP
  SectionIn RO
  SetShellVarContext current

  Call CloseRunningInstance

  SetOutPath "$INSTDIR"
  File /oname=${APP_EXE} "${PAYLOAD}"
  File /oname=LICENSE.txt "${ROOT}\LICENSE"

  WriteRegStr HKCU "Software\${APP_NAME}" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "Software\${APP_NAME}" "Version" "${VERSION}"

  CreateDirectory "$SMPROGRAMS\${APP_NAME}"
  CreateShortcut "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}"

  WriteUninstaller "$INSTDIR\Uninstall.exe"

  ; Add/Remove Programs, per user.
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  WriteRegStr   HKCU "${UNINST_KEY}" "DisplayName"     "${APP_NAME}"
  WriteRegStr   HKCU "${UNINST_KEY}" "DisplayVersion"  "${VERSION}"
  WriteRegStr   HKCU "${UNINST_KEY}" "DisplayIcon"     "$INSTDIR\${APP_EXE}"
  WriteRegStr   HKCU "${UNINST_KEY}" "Publisher"       "${APP_PUBLISHER}"
  WriteRegStr   HKCU "${UNINST_KEY}" "URLInfoAbout"    "${APP_URL}"
  WriteRegStr   HKCU "${UNINST_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr   HKCU "${UNINST_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr   HKCU "${UNINST_KEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoRepair" 1
  WriteRegDWORD HKCU "${UNINST_KEY}" "EstimatedSize" "$0"
SectionEnd

Section "Desktop shortcut" SEC_DESKTOP
  SetShellVarContext current
  CreateShortcut "$DESKTOP\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}"
SectionEnd

Section /o "Start automatically when I log in" SEC_AUTOSTART
  ; Same key and the same --tray argument the application writes from its own Settings tab, so
  ; the two agree about what "autostart" means.
  WriteRegStr HKCU "${RUN_KEY}" "${APP_NAME}" '"$INSTDIR\${APP_EXE}" --tray'
SectionEnd

!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_APP} \
    "The ${APP_NAME} application and a Start Menu shortcut."
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_DESKTOP} \
    "Put a shortcut on the desktop."
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_AUTOSTART} \
    "Start ${APP_NAME} minimised to the tray whenever you log in. Recommended on an instrument PC."
!insertmacro MUI_FUNCTION_DESCRIPTION_END

Function .onInit
  ; An upgrade over a previous per-user install just overwrites in place, but a leftover
  ; uninstaller entry pointing somewhere else would be confusing, so adopt the recorded location.
  ReadRegStr $0 HKCU "Software\${APP_NAME}" "InstallDir"
  ${If} $0 != ""
    StrCpy $INSTDIR $0
  ${EndIf}
FunctionEnd

; ---------------------------------------------------------------------------------------------

Section "Uninstall"
  SetShellVarContext current

  Call un.CloseRunningInstance

  Delete "$INSTDIR\${APP_EXE}"
  Delete "$INSTDIR\LICENSE.txt"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"

  Delete "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk"
  RMDir  "$SMPROGRAMS\${APP_NAME}"
  Delete "$DESKTOP\${APP_NAME}.lnk"

  DeleteRegValue HKCU "${RUN_KEY}" "${APP_NAME}"
  DeleteRegKey   HKCU "${UNINST_KEY}"
  DeleteRegKey   HKCU "Software\${APP_NAME}"

  ; Settings, logs and the transfer journal are deliberately kept unless asked for: the journal is
  ; the record of what was moved where, and defaulting to deleting it would be the wrong answer.
  IfSilent skipdata
  MessageBox MB_YESNO|MB_ICONQUESTION|MB_DEFBUTTON2 \
    "Also delete your rules, logs and transfer history?$\n$\n\
     $APPDATA\${APP_NAME}$\n$\n\
     This includes journal.jsonl, the record of every file ${APP_NAME} has moved and its \
     checksum. Choose No to keep it." IDNO skipdata
  RMDir /r "$APPDATA\${APP_NAME}"
  skipdata:
SectionEnd
