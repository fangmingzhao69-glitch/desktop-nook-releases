Var RuntimeFound
Var RuntimeExit
Var RuntimeBootstrapped
!ifndef RUNTIME_BOOTSTRAP_SCRIPT
!define RUNTIME_BOOTSTRAP_SCRIPT "RuntimeBootstrap.ps1"
!endif
Function DetectDesktopRuntime
 StrCpy $RuntimeFound 0
 !ifdef VERIFY_MISSING_RUNTIME
 StrCmp $RuntimeBootstrapped 1 0 detection_done
 !ifdef VERIFY_RUNTIME_AFTER_BOOTSTRAP
 StrCpy $RuntimeFound 1
 Goto detection_done
 !endif
 !endif
 SetRegView 32
 StrCpy $3 32
 StrCpy $0 0
 runtime_loop:
 EnumRegValue $1 HKLM "SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App" $0
 StrCmp $1 "" runtime_next_view
 StrCpy $2 $1 5
 StrCmp $2 "10.0." detection_found
 IntOp $0 $0 + 1
 Goto runtime_loop
 runtime_next_view:
 StrCmp $3 64 detection_done
 SetRegView 64
 StrCpy $3 64
 StrCpy $0 0
 Goto runtime_loop
 detection_found:
 StrCpy $RuntimeFound 1
 detection_done:
 SetRegView 32
FunctionEnd
Function .onInit
 Call DetectDesktopRuntime
 StrCmp $RuntimeFound 1 runtime_ready
 IfSilent runtime_missing_silent
 MessageBox MB_OKCANCEL|MB_ICONINFORMATION "轻匣需要 .NET 10 桌面运行时（x64）。点击确定将从微软官方下载并安装必要组件，完成后继续安装轻匣。Windows 可能要求管理员授权。" /SD IDCANCEL IDCANCEL runtime_cancelled
 InitPluginsDir
 File /oname=$PLUGINSDIR\RuntimeBootstrap.ps1 "${RUNTIME_BOOTSTRAP_SCRIPT}"
 File /oname=$PLUGINSDIR\DotNetRuntime.json "DotNetRuntime.json"
 ExecWait '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File "$PLUGINSDIR\RuntimeBootstrap.ps1" -ManifestPath "$PLUGINSDIR\DotNetRuntime.json"' $RuntimeExit
 StrCmp $RuntimeExit 1602 runtime_cancelled
 StrCmp $RuntimeExit 0 runtime_recheck
 StrCmp $RuntimeExit 3010 0 runtime_failed
 MessageBox MB_OK "必要组件已安装，Windows 建议稍后重启。轻匣将继续安装，不会自动重启电脑。" /SD IDOK
 runtime_recheck:
 StrCpy $RuntimeBootstrapped 1
 Call DetectDesktopRuntime
 StrCmp $RuntimeFound 1 runtime_ready
 MessageBox MB_OK "尚未检测到所需运行时。如组件安装提示需要重启，请重启电脑后重新运行安装包。现有程序和数据保留。" /SD IDOK
 SetErrorLevel 25
 Abort
 runtime_failed:
 MessageBox MB_OK "必要组件安装未完成，请检查网络后重试。现有程序和数据保留。" /SD IDOK
 SetErrorLevel 24
 Abort
 runtime_missing_silent:
 SetErrorLevel 23
 Abort
 runtime_cancelled:
 SetErrorLevel 1602
 Abort
 runtime_ready:
FunctionEnd
