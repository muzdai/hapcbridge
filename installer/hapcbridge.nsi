; HA PC Bridge installer (NSIS 3, Unicode). Built by build.sh:
;   makensis -DVERSION=x.y.z -DPAYLOAD=<publish dir> -DFILELIST=<generated .nsh> -DOUTFILE=<exe> hapcbridge.nsi
;
; Silent use:  setup.exe /S [/HOST=broker /PORT=1883 /USER=name /PASS=secret] [/WOL=1] [/PAWNIO=1] [/FORCE]
;   installed already  -> updates in place (refuses to downgrade without /FORCE, exit code 3)
;   uninstall.exe /S [/PURGE]   (/PURGE also removes settings and logs)

Unicode true
ManifestDPIAware true
RequestExecutionLevel admin
SetCompressor /SOLID lzma
SetCompressorDictSize 64

!include "MUI2.nsh"
!include "nsDialogs.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
!include "WinVer.nsh"
!include "WordFunc.nsh"
!include "FileFunc.nsh"
!include "StrFunc.nsh"
${StrStr}
${UnStrStr}

!define APP        "HA PC Bridge"
!define SVC        "HaPcBridge"
!define UNINST_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\HaPcBridge"
!define PAWNIO_URL "https://github.com/namazso/PawnIO.Setup/releases"

Name "${APP}"
OutFile "${OUTFILE}"
InstallDir "$PROGRAMFILES64\HA PC Bridge"
BrandingText "${APP} ${VERSION}"
ShowInstDetails show
ShowUninstDetails show

VIProductVersion "${VERSION}.0"
VIAddVersionKey "ProductName" "${APP}"
VIAddVersionKey "FileDescription" "${APP} Setup"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "ProductVersion" "${VERSION}"
VIAddVersionKey "CompanyName" "muzdai"
VIAddVersionKey "LegalCopyright" "muzdai"

Var Mode              ; install | update | repair
Var InstalledVersion
Var InstalledDir
Var VersionCmp        ; 0 same, 1 this setup is newer, 2 installed is newer
Var HasConfig
Var HasPawnIO
Var MqttHost
Var MqttPort
Var MqttUser
Var MqttPass
Var OptWol
Var OptPawn
Var OptPurge
Var Force
Var hUpdate
Var hRepair
Var hRemove
Var hHost
Var hPort
Var hUser
Var hPass
Var hWol
Var hPawn
Var hPurge

!define MUI_ICON   "..\assets\app.ico"
!define MUI_UNICON "..\assets\app.ico"
!define MUI_ABORTWARNING

Page custom MaintPage MaintLeave
!define MUI_PAGE_CUSTOMFUNCTION_PRE DirPre
!define MUI_PAGE_CUSTOMFUNCTION_SHOW DirShow
!define MUI_PAGE_CUSTOMFUNCTION_LEAVE DirLeave
!insertmacro MUI_PAGE_DIRECTORY
Page custom MqttPage MqttLeave
Page custom OptionsPage OptionsLeave
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_TEXT "$(FinishText)"
!define MUI_FINISHPAGE_RUN
!define MUI_FINISHPAGE_RUN_NOTCHECKED
!define MUI_FINISHPAGE_RUN_TEXT "$(FinishRun)"
!define MUI_FINISHPAGE_RUN_FUNCTION OpenSettings
!insertmacro MUI_PAGE_FINISH

UninstPage custom un.OptionsPage un.OptionsLeave
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "Russian"
!insertmacro MUI_LANGUAGE "English"

; ------------------------------------------------------------------ texts
!macro T id ru en
  LangString ${id} ${LANG_RUSSIAN} "${ru}"
  LangString ${id} ${LANG_ENGLISH} "${en}"
!macroend

!insertmacro T Need64        "Нужна 64-разрядная Windows." "64-bit Windows is required."
!insertmacro T NeedWin10     "Нужна Windows 10 версии 1809 или новее." "Windows 10 version 1809 or later is required."
!insertmacro T MaintTitle    "Программа уже установлена" "Already installed"
!insertmacro T MaintSub      "Выберите, что сделать." "Choose what to do."
!insertmacro T MaintInfo     "Установлена версия $InstalledVersion$\r$\n$InstalledDir$\r$\n$\r$\nЭтот установщик: версия ${VERSION}" "Installed: version $InstalledVersion$\r$\n$InstalledDir$\r$\n$\r$\nThis setup: version ${VERSION}"
!insertmacro T OptUpdate     "Обновить до версии ${VERSION}" "Update to version ${VERSION}"
!insertmacro T OptDowngrade  "Вернуть более старую версию ${VERSION}" "Go back to the older version ${VERSION}"
!insertmacro T OptSame       "Обновить (эта версия уже установлена)" "Update (this version is already installed)"
!insertmacro T OptRepair     "Восстановить: переписать файлы и пересоздать службу" "Repair: rewrite the files and recreate the service"
!insertmacro T OptRemove     "Удалить" "Uninstall"
!insertmacro T MaintNote     "Обновление и восстановление сохраняют настройки. Служба останавливается и запускается автоматически." "Update and repair keep your settings. The service is stopped and started automatically."
!insertmacro T ConfirmDown   "Установлена более новая версия ($InstalledVersion). Заменить её версией ${VERSION}?" "A newer version ($InstalledVersion) is installed. Replace it with ${VERSION}?"
!insertmacro T NoUninstaller "Файл удаления не найден. Выберите «Восстановить», затем повторите удаление." "The uninstaller is missing. Choose Repair first, then uninstall again."
!insertmacro T BrokerFailed  "Не удалось сохранить настройки MQTT-брокера. Укажите их после установки: значок в трее, пункт «Настройки». Подробности: C:\ProgramData\HaPcBridge\logs\setup.log" "Could not save the MQTT broker settings. Enter them after the installation: tray icon, Settings. Details: C:\ProgramData\HaPcBridge\logs\setup.log"
!insertmacro T DirNotEmpty   "В этой папке уже есть файлы. Выберите пустую или новую папку: при обновлении и удалении папка программы очищается целиком." "This folder already contains files. Choose an empty or a new folder: the program folder is cleared completely on update and uninstall."
!insertmacro T MqttTitle     "Подключение к MQTT" "MQTT connection"
!insertmacro T MqttSub       "Брокер Mosquitto, к которому подключён Home Assistant." "The Mosquitto broker Home Assistant uses."
!insertmacro T MqttHostL     "Адрес брокера" "Broker address"
!insertmacro T MqttPortL     "Порт" "Port"
!insertmacro T MqttUserL     "Пользователь" "User name"
!insertmacro T MqttPassL     "Пароль" "Password"
!insertmacro T MqttNote      "Всё это можно изменить позже: значок в трее, пункт «Настройки»." "All of this can be changed later from the tray icon, Settings."
!insertmacro T MqttNeedHost  "Укажите адрес брокера." "Enter the broker address."
!insertmacro T MqttBadPort   "Порт должен быть числом от 1 до 65535." "The port must be a number from 1 to 65535."
!insertmacro T OptsTitle     "Дополнительно" "Options"
!insertmacro T OptsSub       "Необязательные шаги настройки Windows." "Optional Windows setup steps."
!insertmacro T OptsWol       "Настроить Windows для включения по сети (Wake-on-LAN)" "Set Windows up for Wake-on-LAN"
!insertmacro T OptsWolNote   "Отключает «Быстрый запуск» и разрешает сетевой карте будить компьютер. Сеть может пропасть на пару секунд. Wake-on-LAN в BIOS нужно включить самостоятельно." "Turns Fast Startup off and lets the network adapter wake the PC. The network may drop for a couple of seconds. Wake-on-LAN in the BIOS has to be enabled by hand."
!insertmacro T OptsPawn      "Установить драйвер PawnIO" "Install the PawnIO driver"
!insertmacro T OptsPawnNote  "Нужен для температуры и мощности процессора. Скачивается через winget, требуется интернет." "Needed for CPU temperature and power. Downloaded with winget, needs internet access."
!insertmacro T OptsPawnHave  "Драйвер PawnIO уже установлен." "The PawnIO driver is already installed."
!insertmacro T FinishText    "${APP} ${VERSION} работает как служба Windows.$\r$\n$\r$\nВ Home Assistant компьютер появится в разделе «Настройки» > «Устройства и службы» > «Обнаружено»." "${APP} ${VERSION} is running as a Windows service.$\r$\n$\r$\nIn Home Assistant the PC appears under Settings > Devices && services > Discovered."
!insertmacro T FinishRun     "Открыть настройки" "Open settings"
!insertmacro T ShortcutName  "Настройки HA PC Bridge" "HA PC Bridge settings"
!insertmacro T StStop        "Останавливаю службу и агент..." "Stopping the service and the agent..."
!insertmacro T StFiles       "Копирую файлы..." "Copying files..."
!insertmacro T StBroker      "Сохраняю настройки брокера..." "Saving the broker settings..."
!insertmacro T StWol         "Настраиваю Wake-on-LAN..." "Setting up Wake-on-LAN..."
!insertmacro T StPawn        "Устанавливаю PawnIO через winget..." "Installing PawnIO with winget..."
!insertmacro T StPawnFail    "PawnIO не установился автоматически. Открываю страницу загрузки." "PawnIO could not be installed automatically. Opening the download page."
!insertmacro T StService     "Регистрирую и запускаю службу..." "Registering and starting the service..."
!insertmacro T StServiceFail "Служба не запустилась (код $0). Подробности: %ProgramData%\HaPcBridge\logs" "The service did not start (code $0). See %ProgramData%\HaPcBridge\logs"
!insertmacro T StOldCopy     "Найдена прежняя копия службы: $R1. Её файлы можно удалить вручную." "An earlier copy of the service was found: $R1. Its files can be deleted by hand."
!insertmacro T UnTitle       "Удаление ${APP}" "Uninstall ${APP}"
!insertmacro T UnSub         "Служба будет остановлена и удалена." "The service will be stopped and removed."
!insertmacro T UnInfo        "Компьютер будет убран с MQTT-брокера, чтобы не оставаться в Home Assistant." "The PC is removed from the MQTT broker so it doesn't linger in Home Assistant."
!insertmacro T UnPurge       "Также удалить настройки и журналы" "Also delete settings and logs"
!insertmacro T StBrokerClean "Убираю компьютер с брокера..." "Removing the PC from the broker..."

; ------------------------------------------------------------------ shared helpers
!macro StopEverythingBody UN
  DetailPrint "$(StStop)"
  nsExec::Exec 'sc.exe stop ${SVC}'
  Pop $0
  StrCpy $1 0
  ${Do}
    nsExec::ExecToStack 'sc.exe query ${SVC}'
    Pop $0
    Pop $2
    ${If} $0 != 0          ; no such service
      ${ExitDo}
    ${EndIf}
    !if "${UN}" == "un"
      ${UnStrStr} $3 $2 "STOPPED"
    !else
      ${StrStr} $3 $2 "STOPPED"
    !endif
    ${If} $3 != ""
      ${ExitDo}
    ${EndIf}
    Sleep 500
    IntOp $1 $1 + 1
  ${LoopUntil} $1 >= 40
  nsExec::Exec 'taskkill.exe /F /IM HaPcBridge.Agent.exe'
  Pop $0
  nsExec::Exec 'taskkill.exe /F /IM HaPcBridge.Service.exe'
  Pop $0
  Sleep 700
!macroend

Function StopEverything
  !insertmacro StopEverythingBody ""
FunctionEnd

Function un.StopEverything
  !insertmacro StopEverythingBody "un"
FunctionEnd

; ------------------------------------------------------------------ init
Function .onInit
  SetRegView 64
  SetShellVarContext all

  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "$(Need64)" /SD IDOK
    Abort
  ${EndIf}
  ${IfNot} ${AtLeastWin10}
    MessageBox MB_ICONSTOP "$(NeedWin10)" /SD IDOK
    Abort
  ${EndIf}
  ${IfNot} ${AtLeastBuild} 17763
    MessageBox MB_ICONSTOP "$(NeedWin10)" /SD IDOK
    Abort
  ${EndIf}

  StrCpy $Mode "install"
  StrCpy $MqttPort "1883"
  StrCpy $OptWol 0
  StrCpy $OptPawn 0
  StrCpy $Force 0

  ReadRegStr $InstalledDir HKLM "${UNINST_KEY}" "InstallLocation"
  ReadRegStr $InstalledVersion HKLM "${UNINST_KEY}" "DisplayVersion"
  ${If} $InstalledDir != ""
  ${AndIf} ${FileExists} "$InstalledDir\HaPcBridge.Service.exe"
    StrCpy $Mode "update"
    StrCpy $INSTDIR "$InstalledDir"
    ${VersionCompare} "${VERSION}" "$InstalledVersion" $VersionCmp
  ${EndIf}

  StrCpy $HasConfig 0
  ${If} ${FileExists} "$APPDATA\HaPcBridge\config.json"
    StrCpy $HasConfig 1
  ${EndIf}

  StrCpy $HasPawnIO 0
  ReadRegStr $0 HKLM "SYSTEM\CurrentControlSet\Services\PawnIO" "ImagePath"
  ${If} $0 != ""
    StrCpy $HasPawnIO 1
  ${EndIf}

  ; command-line options (mainly for silent installs)
  ${GetParameters} $R0
  ClearErrors
  ${GetOptions} $R0 "/HOST=" $R1
  ${IfNot} ${Errors}
    StrCpy $MqttHost $R1
  ${EndIf}
  ClearErrors
  ${GetOptions} $R0 "/PORT=" $R1
  ${IfNot} ${Errors}
    StrCpy $MqttPort $R1
  ${EndIf}
  ClearErrors
  ${GetOptions} $R0 "/USER=" $R1
  ${IfNot} ${Errors}
    StrCpy $MqttUser $R1
  ${EndIf}
  ClearErrors
  ${GetOptions} $R0 "/PASS=" $R1
  ${IfNot} ${Errors}
    StrCpy $MqttPass $R1
  ${EndIf}
  ClearErrors
  ${GetOptions} $R0 "/WOL=" $R1
  ${IfNot} ${Errors}
    StrCpy $OptWol $R1
  ${EndIf}
  ClearErrors
  ${GetOptions} $R0 "/PAWNIO=" $R1
  ${IfNot} ${Errors}
    StrCpy $OptPawn $R1
  ${EndIf}
  ClearErrors
  ${GetOptions} $R0 "/FORCE" $R1
  ${IfNot} ${Errors}
    StrCpy $Force 1
  ${EndIf}
  ClearErrors

  ${If} ${Silent}
  ${AndIf} $Mode == "update"
  ${AndIf} $VersionCmp == 2
  ${AndIf} $Force != 1
    SetErrorLevel 3          ; refusing to downgrade
    Quit
  ${EndIf}

  ${IfNot} ${Silent}
    ; interactive defaults for a first install
    StrCpy $OptWol 1
    ${If} $HasPawnIO == 0
      StrCpy $OptPawn 1
    ${EndIf}
  ${EndIf}
FunctionEnd

; ------------------------------------------------------------------ page: already installed
Function MaintPage
  ${If} $Mode == "install"
    Abort
  ${EndIf}
  !insertmacro MUI_HEADER_TEXT "$(MaintTitle)" "$(MaintSub)"
  nsDialogs::Create 1018
  Pop $0

  ${NSD_CreateLabel} 0 0 100% 42u "$(MaintInfo)"
  Pop $0

  ${If} $VersionCmp == 1
    ${NSD_CreateRadioButton} 0 50u 100% 12u "$(OptUpdate)"
  ${ElseIf} $VersionCmp == 2
    ${NSD_CreateRadioButton} 0 50u 100% 12u "$(OptDowngrade)"
  ${Else}
    ${NSD_CreateRadioButton} 0 50u 100% 12u "$(OptSame)"
  ${EndIf}
  Pop $hUpdate
  ${NSD_AddStyle} $hUpdate ${WS_GROUP}
  ${NSD_CreateRadioButton} 0 66u 100% 12u "$(OptRepair)"
  Pop $hRepair
  ${NSD_CreateRadioButton} 0 82u 100% 12u "$(OptRemove)"
  Pop $hRemove

  ${If} $VersionCmp == 0
    EnableWindow $hUpdate 0
    ${NSD_Check} $hRepair
  ${ElseIf} $VersionCmp == 2
    ${NSD_Check} $hRepair
  ${Else}
    ${NSD_Check} $hUpdate
  ${EndIf}

  ${NSD_CreateLabel} 0 104u 100% 24u "$(MaintNote)"
  Pop $0
  nsDialogs::Show
FunctionEnd

Function MaintLeave
  ${NSD_GetState} $hRemove $0
  ${If} $0 == ${BST_CHECKED}
    ${IfNot} ${FileExists} "$INSTDIR\uninstall.exe"
      MessageBox MB_ICONEXCLAMATION "$(NoUninstaller)"
      Abort
    ${EndIf}
    Exec '"$INSTDIR\uninstall.exe"'
    Quit
  ${EndIf}

  ${NSD_GetState} $hRepair $0
  ${If} $0 == ${BST_CHECKED}
    StrCpy $Mode "repair"
  ${Else}
    StrCpy $Mode "update"
    ${If} $VersionCmp == 2
      MessageBox MB_ICONQUESTION|MB_YESNO|MB_DEFBUTTON2 "$(ConfirmDown)" IDYES +2
      Abort
    ${EndIf}
  ${EndIf}
FunctionEnd

; ------------------------------------------------------------------ page: folder (first install only)
Function DirPre
  ${If} $Mode != "install"
    Abort
  ${EndIf}
FunctionEnd

; First visible page of a fresh install: nothing to go back to.
Function DirShow
  GetDlgItem $0 $HWNDPARENT 3
  EnableWindow $0 0
FunctionEnd

; The program folder is wiped on update and uninstall, so it must not be shared with anything else.
Function DirLeave
  ${If} ${FileExists} "$INSTDIR\HaPcBridge.Service.exe"
    Return
  ${EndIf}
  ${DirState} "$INSTDIR" $0
  ${If} $0 == 1
    MessageBox MB_ICONEXCLAMATION "$(DirNotEmpty)"
    Abort
  ${EndIf}
FunctionEnd

; ------------------------------------------------------------------ page: broker (first install without saved settings)
Function MqttPage
  ${If} $Mode != "install"
  ${OrIf} $HasConfig == 1
    Abort
  ${EndIf}
  !insertmacro MUI_HEADER_TEXT "$(MqttTitle)" "$(MqttSub)"
  nsDialogs::Create 1018
  Pop $0

  ${NSD_CreateLabel} 0 2u 28% 12u "$(MqttHostL)"
  Pop $0
  ${NSD_CreateText} 30% 0 50% 13u "$MqttHost"
  Pop $hHost
  ${NSD_CreateLabel} 0 20u 28% 12u "$(MqttPortL)"
  Pop $0
  ${NSD_CreateNumber} 30% 18u 18% 13u "$MqttPort"
  Pop $hPort
  ${NSD_CreateLabel} 0 38u 28% 12u "$(MqttUserL)"
  Pop $0
  ${NSD_CreateText} 30% 36u 50% 13u "$MqttUser"
  Pop $hUser
  ${NSD_CreateLabel} 0 56u 28% 12u "$(MqttPassL)"
  Pop $0
  ${NSD_CreatePassword} 30% 54u 50% 13u "$MqttPass"
  Pop $hPass
  ${NSD_CreateLabel} 0 80u 100% 20u "$(MqttNote)"
  Pop $0

  ${NSD_SetFocus} $hHost
  nsDialogs::Show
FunctionEnd

Function MqttLeave
  ${NSD_GetText} $hHost $MqttHost
  ${NSD_GetText} $hPort $MqttPort
  ${NSD_GetText} $hUser $MqttUser
  ${NSD_GetText} $hPass $MqttPass
  ${If} $MqttHost == ""
    MessageBox MB_ICONEXCLAMATION "$(MqttNeedHost)"
    Abort
  ${EndIf}
  ${If} $MqttPort < 1
  ${OrIf} $MqttPort > 65535
    MessageBox MB_ICONEXCLAMATION "$(MqttBadPort)"
    Abort
  ${EndIf}
FunctionEnd

; ------------------------------------------------------------------ page: options (first install only)
Function OptionsPage
  ${If} $Mode != "install"
    Abort
  ${EndIf}
  !insertmacro MUI_HEADER_TEXT "$(OptsTitle)" "$(OptsSub)"
  nsDialogs::Create 1018
  Pop $0

  ${NSD_CreateCheckbox} 0 0 100% 12u "$(OptsWol)"
  Pop $hWol
  ${NSD_CreateLabel} 12u 14u 96% 28u "$(OptsWolNote)"
  Pop $0
  ${If} $OptWol == 1
    ${NSD_Check} $hWol
  ${EndIf}

  ${NSD_CreateCheckbox} 0 52u 100% 12u "$(OptsPawn)"
  Pop $hPawn
  ${If} $HasPawnIO == 1
    ${NSD_CreateLabel} 12u 66u 96% 20u "$(OptsPawnHave)"
    Pop $0
    EnableWindow $hPawn 0
  ${Else}
    ${NSD_CreateLabel} 12u 66u 96% 20u "$(OptsPawnNote)"
    Pop $0
    ${If} $OptPawn == 1
      ${NSD_Check} $hPawn
    ${EndIf}
  ${EndIf}
  nsDialogs::Show
FunctionEnd

Function OptionsLeave
  ${NSD_GetState} $hWol $OptWol
  ${NSD_GetState} $hPawn $OptPawn
  ${If} $HasPawnIO == 1
    StrCpy $OptPawn 0
  ${EndIf}
FunctionEnd

Function OpenSettings
  ; Through Explorer, so the settings window runs as the logged-on user, not elevated like this setup.
  Exec '"$WINDIR\explorer.exe" "$SMPROGRAMS\${APP}\$(ShortcutName).lnk"'
FunctionEnd

; ------------------------------------------------------------------ install / update / repair
Section "Install"
  SetRegView 64
  SetShellVarContext all

  ; A copy installed by hand from the zip: its service is replaced, its files stay where they were.
  ${If} $Mode == "install"
    ReadRegStr $R1 HKLM "SYSTEM\CurrentControlSet\Services\${SVC}" "ImagePath"
    ${If} $R1 != ""
      DetailPrint "$(StOldCopy)"
    ${EndIf}
  ${EndIf}

  ; Same rule as the folder page, for silent installs with /D=
  ${IfNot} ${FileExists} "$INSTDIR\HaPcBridge.Service.exe"
    ${DirState} "$INSTDIR" $0
    ${If} $0 == 1
      DetailPrint "$(DirNotEmpty)"
      SetErrorLevel 4
      Abort "$(DirNotEmpty)"
    ${EndIf}
  ${EndIf}

  Call StopEverything

  ; Update / repair: clear the old version first, so files a newer build no longer ships don't pile up.
  ${If} ${FileExists} "$INSTDIR\HaPcBridge.Service.exe"
    RMDir /r "$INSTDIR"
  ${EndIf}

  DetailPrint "$(StFiles)"
  SetOutPath "$INSTDIR"
  SetOverwrite on
  File /r "${PAYLOAD}\*.*"
  WriteUninstaller "$INSTDIR\uninstall.exe"

  ; Broker settings were entered (page) or given (/HOST=...): they win over whatever config exists.
  ${If} $Mode == "install"
  ${AndIf} $MqttHost != ""
    DetailPrint "$(StBroker)"
    ; Handed over in a file inside the installer's private temp folder (deleted by the service right after
    ; reading, and with the folder on exit): nothing on a command line, no quoting to go wrong.
    InitPluginsDir
    FileOpen $9 "$PLUGINSDIR\broker.txt" w
    FileWriteUTF16LE /BOM $9 "$MqttHost$\r$\n"
    FileWriteUTF16LE $9 "$MqttPort$\r$\n"
    FileWriteUTF16LE $9 "$MqttUser$\r$\n"
    FileWriteUTF16LE $9 "$MqttPass$\r$\n"
    FileClose $9
    nsExec::ExecToLog '"$INSTDIR\HaPcBridge.Service.exe" init --from "$PLUGINSDIR\broker.txt"'
    Pop $0
    Delete "$PLUGINSDIR\broker.txt"
    ${If} $0 != 0
      DetailPrint "$(BrokerFailed)"
      SetErrorLevel 5
      IfSilent +2
        MessageBox MB_ICONEXCLAMATION "$(BrokerFailed)"
    ${EndIf}
  ${EndIf}
  StrCpy $MqttPass ""

  ${If} $OptWol == 1
    DetailPrint "$(StWol)"
    WriteRegDWORD HKLM "SYSTEM\CurrentControlSet\Control\Session Manager\Power" "HiberbootEnabled" 0
    nsExec::ExecToLog `powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "Get-NetAdapter -Physical | Where-Object { $$_.Status -eq 'Up' } | ForEach-Object { Set-NetAdapterPowerManagement -Name $$_.Name -WakeOnMagicPacket Enabled -ErrorAction SilentlyContinue; Set-NetAdapterAdvancedProperty -Name $$_.Name -RegistryKeyword '*WakeOnMagicPacket' -RegistryValue 1 -ErrorAction SilentlyContinue; Write-Output ('Wake-on-LAN: ' + $$_.Name) }"`
    Pop $0
  ${EndIf}

  ${If} $OptPawn == 1
    DetailPrint "$(StPawn)"
    nsExec::ExecToLog 'winget.exe install --id namazso.PawnIO --exact --silent --accept-package-agreements --accept-source-agreements --disable-interactivity'
    Pop $0
    ${If} $0 != 0
      DetailPrint "$(StPawnFail)"
      ${IfNot} ${Silent}
        ExecShell "open" "${PAWNIO_URL}"
      ${EndIf}
    ${EndIf}
  ${EndIf}

  DetailPrint "$(StService)"
  nsExec::ExecToLog '"$INSTDIR\HaPcBridge.Service.exe" install'
  Pop $0
  ${If} $0 != 0
    DetailPrint "$(StServiceFail)"
    SetErrorLevel 2
  ${EndIf}

  CreateDirectory "$SMPROGRAMS\${APP}"
  CreateShortcut "$SMPROGRAMS\${APP}\$(ShortcutName).lnk" "$INSTDIR\HaPcBridge.Agent.exe" "--settings" "$INSTDIR\HaPcBridge.Agent.exe" 0

  WriteRegStr HKLM "${UNINST_KEY}" "DisplayName" "${APP}"
  WriteRegStr HKLM "${UNINST_KEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKLM "${UNINST_KEY}" "Publisher" "muzdai"
  WriteRegStr HKLM "${UNINST_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKLM "${UNINST_KEY}" "DisplayIcon" "$INSTDIR\HaPcBridge.Agent.exe"
  WriteRegStr HKLM "${UNINST_KEY}" "UninstallString" '"$INSTDIR\uninstall.exe"'
  WriteRegStr HKLM "${UNINST_KEY}" "QuietUninstallString" '"$INSTDIR\uninstall.exe" /S'
  WriteRegStr HKLM "${UNINST_KEY}" "URLInfoAbout" "https://github.com/muzdai/hapcbridge"
  WriteRegDWORD HKLM "${UNINST_KEY}" "NoModify" 1
  WriteRegDWORD HKLM "${UNINST_KEY}" "NoRepair" 1
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKLM "${UNINST_KEY}" "EstimatedSize" "$0"
SectionEnd

; ------------------------------------------------------------------ uninstall
Function un.onInit
  SetRegView 64
  SetShellVarContext all
  StrCpy $OptPurge 0
  ${GetParameters} $R0
  ClearErrors
  ${GetOptions} $R0 "/PURGE" $R1
  ${IfNot} ${Errors}
    StrCpy $OptPurge 1
  ${EndIf}
  ClearErrors
FunctionEnd

Function un.OptionsPage
  !insertmacro MUI_HEADER_TEXT "$(UnTitle)" "$(UnSub)"
  nsDialogs::Create 1018
  Pop $0
  ${NSD_CreateLabel} 0 0 100% 24u "$(UnInfo)"
  Pop $0
  ${NSD_CreateCheckbox} 0 34u 100% 12u "$(UnPurge)"
  Pop $hPurge
  nsDialogs::Show
FunctionEnd

Function un.OptionsLeave
  ${NSD_GetState} $hPurge $OptPurge
FunctionEnd

Section "un.Install"
  SetRegView 64
  SetShellVarContext all

  Call un.StopEverything

  ${If} ${FileExists} "$INSTDIR\HaPcBridge.Service.exe"
    DetailPrint "$(StBrokerClean)"
    nsExec::ExecToLog '"$INSTDIR\HaPcBridge.Service.exe" purge'
    Pop $0
    nsExec::ExecToLog '"$INSTDIR\HaPcBridge.Service.exe" uninstall'
    Pop $0
  ${EndIf}
  nsExec::Exec 'sc.exe delete ${SVC}'
  Pop $0
  Call un.StopEverything

  ; The registered program folder is ours alone (the installer refuses non-empty folders) → remove it whole.
  ; An uninstaller started from anywhere else only deletes the files it knows by name.
  ReadRegStr $R2 HKLM "${UNINST_KEY}" "InstallLocation"
  SetOutPath "$TEMP"
  ${If} $R2 == $INSTDIR
    RMDir /r "$INSTDIR"
  ${Else}
    !include "${FILELIST}"
    Delete "$INSTDIR\uninstall.exe"
    RMDir "$INSTDIR"
  ${EndIf}

  Delete "$SMPROGRAMS\${APP}\Настройки HA PC Bridge.lnk"
  Delete "$SMPROGRAMS\${APP}\HA PC Bridge settings.lnk"
  RMDir "$SMPROGRAMS\${APP}"

  DeleteRegKey HKLM "${UNINST_KEY}"

  ${If} $OptPurge == 1
    RMDir /r "$APPDATA\HaPcBridge"
  ${EndIf}
SectionEnd
