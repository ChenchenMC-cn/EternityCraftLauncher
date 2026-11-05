// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
// rtcheck.exe — runtime gate for Eternity Craft Launcher.
//
// The launcher is built against .NET 10 (WPF). Without the Desktop Runtime Windows
// shows its own *English* "framework not found" dialog, which tells a Chinese user
// nothing useful. This program runs first, checks for the runtime itself, and — when
// it is missing — shows a bilingual prompt with a direct download link.
//
// Build:
//   cl /std:c++17 /EHsc /O2 /utf-8 rtcheck.cpp /link user32.lib advapi32.lib shell32.lib
//
// Behaviour:
//   runtime present -> start ECL.exe in the same folder, exit
//   runtime missing -> dialog (中文 / English), then download page or exit
//
// Every step is appended to rtcheck.log next to the exe, because this runs before
// the launcher exists and a silent failure would look like "nothing happens".

#ifndef UNICODE
#define UNICODE
#endif
#ifndef _UNICODE
#define _UNICODE
#endif

#include <windows.h>
#include <shellapi.h>
#include <shlwapi.h>
#include <string>
#include <vector>

#pragma comment(lib, "user32.lib")
#pragma comment(lib, "advapi32.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "gdi32.lib")   // CreateFontW / DeleteObject
#pragma comment(lib, "shlwapi.lib")

namespace {

constexpr wchar_t kLauncherExe[] = L"ECL.exe";
constexpr wchar_t kLogFile[] = L"rtcheck.log";
constexpr wchar_t kDownloadUrl[] = L"https://dotnet.microsoft.com/download/dotnet/10.0";
constexpr wchar_t kRequiredRuntime[] = L".NET 10 Desktop Runtime";

// ---------------------------------------------------------------- diagnostics ---

std::wstring ExeDir()
{
    wchar_t buf[MAX_PATH]{};
    DWORD n = GetModuleFileNameW(nullptr, buf, MAX_PATH);
    if (n == 0 || n >= MAX_PATH) return L".";

    std::wstring path(buf, n);
    size_t slash = path.find_last_of(L"\\/");
    return slash == std::wstring::npos ? L"." : path.substr(0, slash);
}

void Log(const wchar_t* fmt, ...)
{
    // Append-only, best effort: a failure here must never block startup.
    std::wstring line;
    va_list args;
    va_start(args, fmt);
    wchar_t body[1024]{};
    _vsnwprintf_s(body, _TRUNCATE, fmt, args);
    va_end(args);

    SYSTEMTIME st{};
    GetLocalTime(&st);
    wchar_t stamp[64]{};
    swprintf_s(stamp, L"[%02d:%02d:%02d.%03d] ", st.wHour, st.wMinute, st.wSecond, st.wMilliseconds);

    line = stamp;
    line += body;
    line += L"\r\n";

    std::wstring path = ExeDir() + L"\\" + kLogFile;
    HANDLE h = CreateFileW(path.c_str(), FILE_APPEND_DATA, FILE_SHARE_READ,
                           nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return;

    DWORD written = 0;
    // UTF-16 with a BOM on creation so Notepad reads it correctly.
    if (GetFileSize(h, nullptr) == 0)
    {
        const wchar_t bom = 0xFEFF;
        WriteFile(h, &bom, sizeof(bom), &written, nullptr);
    }
    WriteFile(h, line.c_str(), (DWORD)(line.size() * sizeof(wchar_t)), &written, nullptr);
    CloseHandle(h);
}

// ------------------------------------------------------------ runtime probing ---

// The Desktop Runtime ships Microsoft.WindowsDesktop.App; a console-only install has
// only Microsoft.NETCore.App, which is not enough for WPF. Check both the folder and
// the registry so a relocated dotnet root is still recognised.
bool HasDotNet10Desktop(std::wstring* detail)
{
    // 1) folder scan: <ProgramFiles>\dotnet\shared\Microsoft.WindowsDesktop.App\10.*
    wchar_t programFiles[MAX_PATH]{};
    if (GetEnvironmentVariableW(L"ProgramFiles", programFiles, MAX_PATH) > 0)
    {
        std::wstring pattern = std::wstring(programFiles) +
                               L"\\dotnet\\shared\\Microsoft.WindowsDesktop.App\\10.*";

        WIN32_FIND_DATAW fd{};
        HANDLE h = FindFirstFileW(pattern.c_str(), &fd);
        if (h != INVALID_HANDLE_VALUE)
        {
            FindClose(h);
            if (detail) *detail = std::wstring(L"folder: ") + fd.cFileName;
            return true;
        }
    }

    // 2) registry: HKLM\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App
    //    This is a 64-bit key; a 32-bit build must ask for the 64-bit view explicitly.
    HKEY key = nullptr;
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE,
                      L"SOFTWARE\\dotnet\\Setup\\InstalledVersions\\x64\\sharedfx\\Microsoft.WindowsDesktop.App",
                      0, KEY_READ | KEY_WOW64_64KEY, &key) == ERROR_SUCCESS)
    {
        wchar_t name[256]{};
        DWORD nameLen = 256, type = 0, dataLen = 0;
        for (DWORD i = 0;; ++i)
        {
            nameLen = 256;
            LONG rc = RegEnumValueW(key, i, name, &nameLen, nullptr, &type, nullptr, &dataLen);
            if (rc == ERROR_NO_MORE_ITEMS) break;
            if (rc != ERROR_SUCCESS) break;

            // value names look like "10.0.0"
            if (wcsncmp(name, L"10.", 3) == 0)
            {
                RegCloseKey(key);
                if (detail) *detail = std::wstring(L"registry: ") + name;
                return true;
            }
        }
        RegCloseKey(key);
    }

    return false;
}

// ------------------------------------------------------------------ localised ---

struct Strings
{
    const wchar_t* title;
    const wchar_t* heading;
    const wchar_t* body;
    const wchar_t* install;
    const wchar_t* later;
    const wchar_t* language;
    const wchar_t* where;      // "下载页面："
    const wchar_t* launched;   // status line
};

const Strings kZh{
    L"ECL 需要 .NET 10 运行时",
    L"缺少 .NET 10 Desktop Runtime",
    L"Eternity Craft Launcher 是用 .NET 10（WPF）构建的。\n"
    L"这台电脑上还没有安装桌面运行时，所以启动器无法运行。\n\n"
    L"点「下载并安装」会打开微软官方下载页（约 55 MB，安装后即可启动）。",
    L"下载并安装",
    L"稍后再说",
    L"English",
    L"下载页面：",
    L"已打开下载页，安装完成后重新运行 ECL 即可。"
};

const Strings kEn{
    L"ECL needs the .NET 10 runtime",
    L".NET 10 Desktop Runtime is missing",
    L"Eternity Craft Launcher is built against .NET 10 (WPF).\n"
    L"The desktop runtime is not installed on this machine, so the launcher cannot start.\n\n"
    L"\"Download and install\" opens Microsoft's official page (~55 MB).",
    L"Download and install",
    L"Not now",
    L"中文",
    L"Download page: ",
    L"Download page opened. Run ECL again after installing."
};

// Which language to start in: the launcher's own setting if it exists, else the
// system UI language.
bool PreferChinese()
{
    // settings.json is written by the launcher; a tiny substring probe is enough
    // here because this runs before any JSON parser is available.
    std::wstring settings = ExeDir() + L"\\settings.json";
    HANDLE h = CreateFileW(settings.c_str(), GENERIC_READ, FILE_SHARE_READ,
                           nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h != INVALID_HANDLE_VALUE)
    {
        char buf[8192]{};
        DWORD read = 0;
        ReadFile(h, buf, sizeof(buf) - 1, &read, nullptr);
        CloseHandle(h);

        std::string text(buf, read);
        if (text.find("\"zh") != std::string::npos) return true;
        if (text.find("\"en") != std::string::npos) return false;
    }

    LANGID lang = GetUserDefaultUILanguage();
    return PRIMARYLANGID(lang) == LANG_CHINESE;
}

// -------------------------------------------------------------------- dialog ---

struct DialogState
{
    const Strings* text;
    bool installRequested = false;
    bool languageToggled = false;
};

LRESULT CALLBACK DialogProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam)
{
    auto* state = reinterpret_cast<DialogState*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));

    switch (msg)
    {
    case WM_CREATE:
    {
        auto* cs = reinterpret_cast<CREATESTRUCTW*>(lParam);
        SetWindowLongPtrW(hwnd, GWLP_USERDATA, (LONG_PTR)cs->lpCreateParams);
        return 0;
    }

    case WM_COMMAND:
    {
        if (!state) break;
        switch (LOWORD(wParam))
        {
        case 1001:  // install
            state->installRequested = true;
            DestroyWindow(hwnd);
            return 0;
        case 1002:  // later
            DestroyWindow(hwnd);
            return 0;
        case 1003:  // language
            state->languageToggled = true;
            state->text = (state->text == &kZh) ? &kEn : &kZh;
            {
                // Repaint the labels in place rather than recreating the window.
                SetWindowTextW(hwnd, state->text->title);
                SetDlgItemTextW(hwnd, 2001, state->text->heading);
                SetDlgItemTextW(hwnd, 2002, state->text->body);
                SetDlgItemTextW(hwnd, 1001, state->text->install);
                SetDlgItemTextW(hwnd, 1002, state->text->later);
                SetDlgItemTextW(hwnd, 1003, state->text->language);
            }
            return 0;
        default:
            break;
        }
        break;
    }

    case WM_CLOSE:
        DestroyWindow(hwnd);
        return 0;

    case WM_DESTROY:
        PostQuitMessage(0);
        return 0;

    default:
        break;
    }

    return DefWindowProcW(hwnd, msg, wParam, lParam);
}

// Returns true when the user asked to install.
bool ShowMissingDialog(bool startChinese)
{
    DialogState state;
    state.text = startChinese ? &kZh : &kEn;

    HINSTANCE inst = GetModuleHandleW(nullptr);
    const wchar_t* cls = L"ECLRuntimeGate";

    WNDCLASSEXW wc{};
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = DialogProc;
    wc.hInstance = inst;
    wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    wc.hbrBackground = (HBRUSH)(COLOR_WINDOW + 1);
    wc.lpszClassName = cls;
    RegisterClassExW(&wc);

    // Larger than a normal message box: the text is three paragraphs in 16pt.
    const int w = 720, h = 460;
    int x = (GetSystemMetrics(SM_CXSCREEN) - w) / 2;
    int y = (GetSystemMetrics(SM_CYSCREEN) - h) / 2;

    HWND dlg = CreateWindowExW(WS_EX_DLGMODALFRAME, cls, state.text->title,
                               WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_VISIBLE,
                               x, y, w, h, nullptr, nullptr, inst, &state);
    if (!dlg)
    {
        // Last resort: a plain English message box still beats dying silently.
        MessageBoxW(nullptr, kEn.body, kEn.title, MB_ICONERROR | MB_OK);
        return false;
    }

    HFONT font = CreateFontW(-20, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE,
                             DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
                             CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Microsoft YaHei UI");
    HFONT bold = CreateFontW(-26, 0, 0, 0, FW_SEMIBOLD, FALSE, FALSE, FALSE,
                             DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
                             CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Microsoft YaHei UI");

    HWND heading = CreateWindowExW(0, L"STATIC", state.text->heading,
                                   WS_CHILD | WS_VISIBLE | SS_LEFT,
                                   28, 22, w - 56, 40, dlg, (HMENU)2001, inst, nullptr);
    HWND body = CreateWindowExW(0, L"STATIC", state.text->body,
                                WS_CHILD | WS_VISIBLE | SS_LEFT,
                                28, 72, w - 56, 240, dlg, (HMENU)2002, inst, nullptr);

    SendMessageW(heading, WM_SETFONT, (WPARAM)bold, TRUE);
    SendMessageW(body, WM_SETFONT, (WPARAM)font, TRUE);

    struct ButtonSpec { const wchar_t* text; int id; int x; int w; };
    ButtonSpec buttons[] = {
        { state.text->install,  1001,  28, 200 },
        { state.text->later,    1002, 244, 140 },
        { state.text->language, 1003, 400, 140 },
    };

    for (const auto& b : buttons)
    {
        HWND hb = CreateWindowExW(0, L"BUTTON", b.text,
                                  WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_PUSHBUTTON,
                                  b.x, h - 96, b.w, 52, dlg, (HMENU)(INT_PTR)b.id, inst, nullptr);
        SendMessageW(hb, WM_SETFONT, (WPARAM)font, TRUE);
    }

    // Focus the primary action so Enter installs.
    SetFocus(GetDlgItem(dlg, 1001));

    MSG msg{};
    while (GetMessageW(&msg, nullptr, 0, 0) > 0)
    {
        if (!IsDialogMessageW(dlg, &msg))
        {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }

    DeleteObject(font);
    DeleteObject(bold);
    return state.installRequested;
}

// -------------------------------------------------------------------- launch ---

bool LaunchLauncher()
{
    std::wstring exe = ExeDir() + L"\\" + kLauncherExe;

    if (!PathFileExistsW(exe.c_str()))
    {
        Log(L"launcher not found: %s", exe.c_str());
        MessageBoxW(nullptr,
                    L"ECL.exe 不在这个文件夹里。\r\n请把 rtcheck.exe 和启动器放在一起。",
                    L"文件缺失", MB_ICONERROR | MB_OK);
        return false;
    }

    STARTUPINFOW si{};
    si.cb = sizeof(si);
    PROCESS_INFORMATION pi{};

    std::wstring cmd = L"\"" + exe + L"\"";

    if (!CreateProcessW(exe.c_str(), cmd.data(), nullptr, nullptr, FALSE,
                        0, nullptr, ExeDir().c_str(), &si, &pi))
    {
        DWORD err = GetLastError();
        Log(L"CreateProcess failed: %lu", err);

        wchar_t msg[512]{};
        swprintf_s(msg,
                   L"启动 ECL.exe 失败（错误 %lu）。\r\n\r\n"
                   L".NET 10 Desktop Runtime 可能刚安装完还没生效，重启电脑再试一次。",
                   err);
        MessageBoxW(nullptr, msg, L"启动失败", MB_ICONERROR | MB_OK);
        return false;
    }

    Log(L"launched %s (pid %lu)", exe.c_str(), pi.dwProcessId);
    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);
    return true;
}

} // namespace

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int)
{
    Log(L"--- rtcheck start ---");

    // Two test switches, both harmless in normal use:
    //   --check-only    probe and exit (0 = present, 2 = missing) without any UI
    //   --force-missing pretend the runtime is absent, to exercise the dialog
    std::wstring cmdLine = GetCommandLineW();
    bool checkOnly = cmdLine.find(L"--check-only") != std::wstring::npos;
    bool forceMissing = cmdLine.find(L"--force-missing") != std::wstring::npos;

    std::wstring detail;
    bool ok = !forceMissing && HasDotNet10Desktop(&detail);
    if (forceMissing) detail = L"forced by --force-missing";

    Log(L"required: %s", kRequiredRuntime);
    Log(ok ? L"detected: %s" : L"NOT installed", detail.c_str());

    if (checkOnly)
    {
        Log(ok ? L"exit 0 (runtime present)" : L"exit 2 (runtime missing)");
        return ok ? 0 : 2;
    }

    if (ok)
    {
        LaunchLauncher();
        return 0;   // window is gone; the launcher owns the session now
    }

    bool wantsInstall = ShowMissingDialog(PreferChinese());

    if (wantsInstall)
    {
        Log(L"opening download page");
        ShellExecuteW(nullptr, L"open", kDownloadUrl, nullptr, nullptr, SW_SHOWNORMAL);
    }
    else
    {
        Log(L"user declined");
    }

    return 0;
}
