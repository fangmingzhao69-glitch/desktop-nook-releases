; Check the x64 shared runtime before stopping/replacing an existing installation.
Function .onInit
 !ifdef VERIFY_MISSING_RUNTIME
 Goto runtime_missing
 !endif
 ; .NET x64 installers normally register in the 32-bit registry view.
 ; Also check the 64-bit view for manually registered installations.
 SetRegView 32
 StrCpy $3 32
 StrCpy $0 0
 runtime_loop:
 EnumRegValue $1 HKLM "SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App" $0
 StrCmp $1 "" runtime_next_view
 StrCpy $2 $1 5
 StrCmp $2 "10.0." runtime_ready
 IntOp $0 $0 + 1
 Goto runtime_loop
 runtime_next_view:
 StrCmp $3 64 runtime_missing
 SetRegView 64
 StrCpy $3 64
 StrCpy $0 0
 Goto runtime_loop
 runtime_missing:
 MessageBox MB_OKCANCEL|MB_ICONINFORMATION "轻量版需要 .NET 10 桌面运行时（Windows x64）。请先从微软官网下载并安装 Windows Desktop Runtime，再运行此安装包。现有程序和数据保持不变。点击确定打开下载页。" /SD IDCANCEL IDCANCEL runtime_abort
 ExecShell "open" "https://dotnet.microsoft.com/zh-cn/download/dotnet/10.0"
 runtime_abort:
 SetErrorLevel 23
 Abort
 runtime_ready:
 SetRegView 32
FunctionEnd
