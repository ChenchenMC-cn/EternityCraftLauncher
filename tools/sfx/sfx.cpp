// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
// ECL-Setup.exe — self-extracting shell for Eternity Craft Launcher.
//
// The launcher is 6 files plus a runtime gate; shipping a zip asks the user to pick
// a folder and then find the right exe. This produces one file that:
//
//   1. creates  <its own folder>\ECL\
//   2. writes the 6 launcher files + rtcheck.exe from embedded resources
//   3. starts ECL\rtcheck.exe, which checks .NET 10 and then launches the UI
//
// Design notes:
//   - Silent on success. Extraction takes well under a second; a progress dialog
//     would flash and vanish. Only failures show a message box.
//   - Never deletes anything in ECL\. That folder also holds settings.json,
//     accounts.json, versions\ and mods\ — user data. Re-running the setup is an
//     update: program files are overwritten, everything else is left alone.
//   - A locked ECL.exe (launcher still running) fails the write; the message says so
//     instead of leaving a half-updated install.
//
// Build: see build.bat (needs rc.exe from the Windows SDK, cl.exe from MSVC).

#ifndef UNICODE
#define UNICODE
#endif
#ifndef _UNICODE
#define _UNICODE
#endif

#include <windows.h>
#include <shlwapi.h>
#include <string>

#pragma comment(lib, "user32.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "shlwapi.lib")

namespace {

constexpr wchar_t kInstallDir[] = L"ECL";
constexpr wchar_t kGateExe[] = L"rtcheck.exe";
constexpr int kPayloadTypeId = 1000;   // see payload.rc

struct Entry
{
    int id;
    const wchar_t* name;
};

// Ids must match payload.rc.
// The launcher is the framework-dependent single-file build: one 1.7 MB exe that
// already embeds ECL.dll and is obfuscated. ECL.Runtime.jar is the Java probe the
// launcher looks for next to itself; without it the launcher falls back to parsing
// `java -version`, so it is optional but cheap to include.
const Entry kFiles[] = {
    { 1, L"ECL.exe" },
    { 2, L"ECL.Runtime.jar" },
    { 3, L"rtcheck.exe" },

    // Error viewers. Native, so they still work when the launcher itself died.
    { 4, L"eclcr.exe" },
    { 5, L"eclnfer.exe" },
};

std::wstring ExeDir()
{
    wchar_t buf[MAX_PATH]{};
    DWORD n = GetModuleFileNameW(nullptr, buf, MAX_PATH);
    if (n == 0 || n >= MAX_PATH) return L".";

    std::wstring path(buf, n);
    size_t slash = path.find_last_of(L"\\/");
    return slash == std::wstring::npos ? L"." : path.substr(0, slash);
}

void Log(const std::wstring& dir, const wchar_t* fmt, ...)
{
    wchar_t body[1024]{};
    va_list args;
    va_start(args, fmt);
    _vsnwprintf_s(body, _TRUNCATE, fmt, args);
    va_end(args);

    SYSTEMTIME st{};
    GetLocalTime(&st);
    wchar_t line[1200]{};
    swprintf_s(line, L"[%02d:%02d:%02d.%03d] %s\r\n",
               st.wHour, st.wMinute, st.wSecond, st.wMilliseconds, body);

    std::wstring path = dir + L"\\setup.log";
    HANDLE h = CreateFileW(path.c_str(), FILE_APPEND_DATA, FILE_SHARE_READ,
                           nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return;

    DWORD written = 0;
    if (GetFileSize(h, nullptr) == 0)
    {
        const wchar_t bom = 0xFEFF;
        WriteFile(h, &bom, sizeof(bom), &written, nullptr);
    }
    WriteFile(h, line, (DWORD)(wcslen(line) * sizeof(wchar_t)), &written, nullptr);
    CloseHandle(h);
}

// Returns false and sets error when the destination cannot be written.
bool ExtractOne(int id, const std::wstring& dest, DWORD* error)
{
    HRSRC res = FindResourceW(nullptr, MAKEINTRESOURCEW(id), RT_RCDATA);
    if (!res) { *error = ERROR_RESOURCE_NAME_NOT_FOUND; return false; }

    DWORD size = SizeofResource(nullptr, res);
    HGLOBAL glob = LoadResource(nullptr, res);
    if (!glob) { *error = GetLastError(); return false; }

    const void* data = LockResource(glob);
    if (!data) { *error = GetLastError(); return false; }

    HANDLE h = CreateFileW(dest.c_str(), GENERIC_WRITE, 0, nullptr,
                           CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) { *error = GetLastError(); return false; }

    DWORD written = 0;
    BOOL ok = WriteFile(h, data, size, &written, nullptr);
    CloseHandle(h);

    if (!ok || written != size) { *error = ok ? ERROR_WRITE_FAULT : GetLastError(); return false; }
    return true;
}

// Writes <dir>\settings.json with dataRoot = "data" on first install.
//
// The launcher treats a settings.json next to its exe as "portable mode" and puts
// everything (accounts, versions, libraries, assets) under <dir>\data. That is what
// keeps the whole install inside one folder.
//
// Never overwrites: on update the user's own settings.json — with their language,
// memory, mirror and per-version JVM args — must survive.
bool WritePortableSettings(const std::wstring& dir, bool* created)
{
    std::wstring path = dir + L"\\settings.json";
    *created = false;

    if (PathFileExistsW(path.c_str())) return true;

    static const char kJson[] =
        "{\r\n"
        "  \"dataRoot\": \"data\"\r\n"
        "}\r\n";

    HANDLE h = CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr,
                           CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return false;

    DWORD written = 0;
    BOOL ok = WriteFile(h, kJson, (DWORD)(sizeof(kJson) - 1), &written, nullptr);
    CloseHandle(h);

    *created = ok;
    return ok;
}

// Larger, Chinese-first message box. Kept simple: setup only fails in a few ways and
// each one has a concrete fix the user can act on.
void Fail(const std::wstring& text, const std::wstring& hint)
{
    std::wstring msg = text + L"\r\n\r\n" + hint;
    MessageBoxW(nullptr, msg.c_str(), L"ECL 安装失败", MB_ICONERROR | MB_OK);
}

} // namespace

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int)
{
    std::wstring root = ExeDir();
    std::wstring dir = root + L"\\" + kInstallDir;

    if (!CreateDirectoryW(dir.c_str(), nullptr) && GetLastError() != ERROR_ALREADY_EXISTS)
    {
        Fail(L"无法创建文件夹：" + dir,
             L"可能是这个位置没有写入权限。把 ECL-Setup.exe 移到桌面或 D 盘再试一次。");
        return 1;
    }

    Log(dir, L"--- setup start ---");
    Log(dir, L"target: %s", dir.c_str());

    for (const auto& f : kFiles)
    {
        std::wstring dest = dir + L"\\" + f.name;
        DWORD err = 0;

        if (!ExtractOne(f.id, dest, &err))
        {
            Log(dir, L"FAILED %s: error %lu", f.name, err);

            std::wstring hint = L"错误代码 " + std::to_wstring(err) + L"。";
            if (err == ERROR_SHARING_VIOLATION || err == ERROR_LOCK_VIOLATION)
                hint += L"\r\n\r\nECL 好像正在运行。请先关闭启动器窗口，再重新运行 ECL-Setup.exe。";
            else
                hint += L"\r\n\r\n如果这个位置不可写，把 ECL-Setup.exe 换个目录再试。";

            Fail(std::wstring(L"解压 ") + f.name + L" 失败。", hint);
            return 2;
        }

        Log(dir, L"extracted %s", f.name);
    }

    // Portable marker. Without it the launcher would use %APPDATA%\EternityCraft and
    // the ECL folder would hold nothing but binaries.
    bool settingsCreated = false;
    if (!WritePortableSettings(dir, &settingsCreated))
    {
        Log(dir, L"WARNING: could not write settings.json");
    }
    else
    {
        Log(dir, settingsCreated ? L"portable settings.json written" : L"kept existing settings.json");
    }

    // Hand over to the runtime gate. It decides whether .NET 10 is present, shows
    // the bilingual prompt if not, and finally starts ECL.exe.
    std::wstring gate = dir + L"\\" + kGateExe;
    STARTUPINFOW si{};
    si.cb = sizeof(si);
    PROCESS_INFORMATION pi{};

    std::wstring cmd = L"\"" + gate + L"\"";

    if (!CreateProcessW(gate.c_str(), cmd.data(), nullptr, nullptr, FALSE,
                        0, nullptr, dir.c_str(), &si, &pi))
    {
        DWORD err = GetLastError();
        Log(dir, L"gate launch failed: %lu", err);
        Fail(L"启动 rtcheck.exe 失败。",
             L"错误代码 " + std::to_wstring(err) + L"。可以手动打开 ECL 文件夹运行 rtcheck.exe。");
        return 3;
    }

    Log(dir, L"gate launched (pid %lu)", pi.dwProcessId);
    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);
    return 0;
}
