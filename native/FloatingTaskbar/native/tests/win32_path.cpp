#include "win32_path.h"

#include "shell/jump_list.h"

#include <iostream>
#include <string>

using namespace atrium;

namespace {

bool Require(bool condition, const char* message)
{
    if (condition)
        return true;
    std::cerr << message << '\n';
    return false;
}

bool CheckFoldCase()
{
    return Require(FoldCase(L"").empty(), "empty string stays empty") &&
           Require(FoldCase(L"C:\\Program Files\\Atrium\\Atrium.EXE") == L"c:\\program files\\atrium\\atrium.exe", "ASCII path folds to lowercase") &&
           Require(FoldCase(L"ÄÖÜ") == L"äöü", "non-ASCII Latin letters fold to lowercase") &&
           Require(FoldCase(L"АБВГД") == L"абвгд", "Cyrillic letters fold to lowercase");
}

bool CheckPaths()
{
    const std::wstring self = ModulePath();
    return Require(!self.empty(), "ModulePath returns a non-empty path") &&
           Require(FoldCase(self).ends_with(L".exe"), "ModulePath of test executable ends in .exe") &&
           Require(FoldCase(ProcessImagePath(GetCurrentProcessId())) == FoldCase(self), "ProcessImagePath(pid) matches ModulePath()") &&
           Require(FoldCase(ProcessImagePath(GetCurrentProcess())) == FoldCase(self), "ProcessImagePath(HANDLE) matches ModulePath()") &&
           Require(ProcessImagePath(HANDLE{}).empty(), "ProcessImagePath(null) returns empty");
}

bool CheckIsRemotePath()
{
    wchar_t winDir[MAX_PATH]{};
    const UINT winDirLen = GetWindowsDirectoryW(winDir, MAX_PATH);
    if (!Require(winDirLen >= 2 && winDirLen < MAX_PATH && winDir[1] == L':', "GetWindowsDirectoryW returns a drive-rooted path"))
        return false;
    const std::wstring sysDir(winDir, winDirLen);
    const wchar_t sysDrive = sysDir[0];
    const wchar_t lowerDrive = FoldCase(std::wstring(1, sysDrive))[0];
    return Require(IsRemotePath(L"\\\\server\\share\\file.txt"), "UNC path is remote") &&
           Require(IsRemotePath(L"\\\\?\\UNC\\server\\share\\file.txt"), "extended uppercase UNC path is remote") &&
           Require(IsRemotePath(L"\\\\?\\unc\\server\\share"), "extended lowercase UNC path is remote") &&
           Require(!IsRemotePath(std::wstring(L"\\\\?\\") + sysDrive + L":\\Windows\\System32"), "extended fixed drive backslash path is local") &&
           Require(!IsRemotePath(std::wstring(L"\\\\?\\") + lowerDrive + L":/Users/test"), "extended fixed drive forward-slash path is local") &&
           Require(!IsRemotePath(std::wstring(L"\\\\.\\") + sysDrive + L":\\Windows\\System32"), "device-namespace fixed drive path is local") &&
           Require(!IsRemotePath(sysDir + L"\\System32"), "regular fixed drive path is local") &&
           Require(!IsRemotePath(L"\\\\.\\PhysicalDrive0"), "device namespace path is not remote") &&
           Require(!IsRemotePath(L"\\\\?\\Volume{00000000-0000-0000-0000-000000000000}\\file.txt"), "volume GUID path is not remote") &&
           Require(!IsRemotePath(L""), "empty path is not remote");
}

} // namespace

int main()
{
    if (!CheckFoldCase() || !CheckPaths() || !CheckIsRemotePath())
        return 1;
    std::cout << "win32 path: all checks passed\n";
    return 0;
}
