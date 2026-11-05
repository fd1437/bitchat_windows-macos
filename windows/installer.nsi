Unicode True
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
!include "WinVer.nsh"
!include "nsDialogs.nsh"
!ifndef PAYLOAD
  !error "Define PAYLOAD with the publish folder containing win-x64 and win-arm64"
!endif
!ifndef OUTPUT
  !define OUTPUT "BitChat-Setup-unsigned.exe"
!endif
!ifndef VERSION
  !define VERSION "0.1.6"
!endif
Var ExistingInstall
Var ExistingAction
Var InstallLatest
Var ActionRepair
Var ActionLatest
Var ActionUninstall
Name "BitChat for Windows ${VERSION}"
OutFile "${OUTPUT}"
InstallDir "$LOCALAPPDATA\Programs\BitChat"
InstallDirRegKey HKCU "Software\BitChat" "InstallDir"
RequestExecutionLevel user
SetCompressor /SOLID lzma
SetCompressorDictSize 64
BrandingText "BitChat · Windows · unsigned"
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\BitChat.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Launch BitChat"
!insertmacro MUI_PAGE_WELCOME
Page custom ExistingInstallPage ExistingInstallLeave
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"
!insertmacro MUI_LANGUAGE "French"
Function .onInit
  StrCpy $ExistingInstall "0"
  StrCpy $ExistingAction "repair"
  StrCpy $InstallLatest "0"
  IfFileExists "$INSTDIR\BitChat.exe" 0 +2
    StrCpy $ExistingInstall "1"
  ${IfNot} ${AtLeastWin10}
    MessageBox MB_ICONSTOP "BitChat requires Windows 10 version 2004 or later (Windows 11 on ARM)."
    Abort
  ${EndIf}
  ${IfNot} ${IsNativeARM64}
    ${IfNot} ${IsNativeAMD64}
      MessageBox MB_ICONSTOP "BitChat requires 64-bit Windows (x64 or ARM64)."
      Abort
    ${EndIf}
  ${EndIf}
FunctionEnd
Function ExistingInstallPage
  ${If} $ExistingInstall != "1"
    Abort
  ${EndIf}
  nsDialogs::Create 1018
  Pop $0
  ${If} $0 == error
    Abort
  ${EndIf}
  ${NSD_CreateLabel} 0 0 100% 26u "An existing BitChat installation was detected. Choose what to do."
  Pop $0
  ${NSD_CreateRadioButton} 0 34u 100% 24u "Update / repair this installation (Mettre à jour / réparer)"
  Pop $ActionRepair
  ${NSD_Check} $ActionRepair
  ${NSD_CreateRadioButton} 0 66u 100% 24u "Install the latest version from GitHub (Installer la dernière version)"
  Pop $ActionLatest
  ${NSD_CreateRadioButton} 0 98u 100% 24u "Uninstall BitChat (Désinstaller)"
  Pop $ActionUninstall
  nsDialogs::Show
FunctionEnd
Function ExistingInstallLeave
  ${NSD_GetState} $ActionLatest $0
  ${If} $0 == ${BST_CHECKED}
    StrCpy $ExistingAction "latest"
    StrCpy $InstallLatest "1"
  ${Else}
    ${NSD_GetState} $ActionUninstall $0
    ${If} $0 == ${BST_CHECKED}
      StrCpy $ExistingAction "uninstall"
      ExecShell "open" "$INSTDIR\Uninstall.exe"
      Abort
    ${Else}
      StrCpy $ExistingAction "repair"
    ${EndIf}
  ${EndIf}
  System::Call 'kernel32::OpenMutexW(i 0x00100000, i 0, w "Local\BitChat.Windows.Instance") p .r0'
  ${If} $0 != 0
    System::Call 'kernel32::CloseHandle(p r0)'
    MessageBox MB_ICONEXCLAMATION|MB_OK "BitChat is running. Quit it from the notification-area menu, then run this installer again."
    Abort
  ${EndIf}
FunctionEnd
Function .onInstSuccess
  ${If} $InstallLatest == "1"
    ExecShell "open" "$INSTDIR\BitChat.exe" "--install-latest"
  ${EndIf}
FunctionEnd
Section "BitChat" SecMain
  SetOutPath "$INSTDIR"
  ${If} ${IsNativeARM64}
    File /oname=BitChat.exe "${PAYLOAD}\win-arm64\BitChat.exe"
    SetOutPath "$INSTDIR\native\win-arm64"
    File "${PAYLOAD}\win-arm64\native\win-arm64\bitchat_zlib.dll"
  ${Else}
    File /oname=BitChat.exe "${PAYLOAD}\win-x64\BitChat.exe"
    SetOutPath "$INSTDIR\native\win-x64"
    File "${PAYLOAD}\win-x64\native\win-x64\bitchat_zlib.dll"
  ${EndIf}
  SetOutPath "$INSTDIR"
  File /oname=LICENSE.txt "..\LICENSE"
  File "README-WINDOWS.md"
  File "THIRD-PARTY-NOTICES.txt"
  SetOutPath "$INSTDIR\licenses"
  File /r "licenses\*"
  SetOutPath "$INSTDIR\tor"
  File /r "vendor\tor\*"
  SetOutPath "$INSTDIR\media"
  File /r "vendor\media\*"
  SetOutPath "$INSTDIR"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateDirectory "$SMPROGRAMS\BitChat"
  CreateShortCut "$SMPROGRAMS\BitChat\BitChat.lnk" "$INSTDIR\BitChat.exe"
  CreateShortCut "$DESKTOP\BitChat.lnk" "$INSTDIR\BitChat.exe"
  WriteRegStr HKCU "Software\BitChat" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\BitChat" "DisplayName" "BitChat for Windows"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\BitChat" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\BitChat" "Publisher" "BitChat community port"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\BitChat" "DisplayIcon" "$INSTDIR\BitChat.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\BitChat" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\BitChat" "NoModify" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\BitChat" "NoRepair" 1
SectionEnd
Section "Uninstall"
  DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "BitChat"
  RMDir /r "$LOCALAPPDATA\BitChat\updates"
  RMDir /r "$INSTDIR\native"
  RMDir /r "$INSTDIR\licenses"
  RMDir /r "$INSTDIR\tor"
  RMDir /r "$INSTDIR\media"
  Delete "$INSTDIR\BitChat.exe"
  Delete "$INSTDIR\LICENSE.txt"
  Delete "$INSTDIR\README-WINDOWS.md"
  Delete "$INSTDIR\THIRD-PARTY-NOTICES.txt"
  Delete "$INSTDIR\Uninstall.exe"
  Delete "$DESKTOP\BitChat.lnk"
  Delete "$SMPROGRAMS\BitChat\BitChat.lnk"
  RMDir "$SMPROGRAMS\BitChat"
  RMDir "$INSTDIR"
  DeleteRegKey HKCU "Software\BitChat"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\BitChat"
SectionEnd
