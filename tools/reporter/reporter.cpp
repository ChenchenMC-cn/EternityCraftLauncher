// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
// eclcr.exe / eclnfer.exe — error viewers for Eternity Craft Launcher.
//
// Two separate executables built from this one file:
//
//   eclcr.exe    crash report   启动器自己崩了（未处理异常）
//   eclnfer.exe  non-fatal      不影响运行、但值得看一眼的问题
//
// They are native and standalone on purpose. A crash reporter cannot depend on the
// crashed process' runtime still being healthy, so no .NET, no WPF, no shared state —
// it reads a text file and renders it.
//
// Usage:  eclcr.exe   --dir "<ECL folder>"      newest file under <dir>\crash\
//         eclnfer.exe --dir "<ECL folder>"      <dir>\errors.jsonl
//
// Build: see build.bat (compiles twice with different /D).

#ifndef UNICODE
#define UNICODE
#endif
#ifndef _UNICODE
#define _UNICODE
#endif

#include <windows.h>
#include <commctrl.h>
#include <shlwapi.h>
#include <string>
#include <vector>
#include <cstdio>

#pragma comment(lib, "user32.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "shlwapi.lib")
#pragma comment(lib, "comctl32.lib")
#pragma comment(lib, "gdi32.lib")   // CreateFontW / DeleteObject

namespace {

// Strings are chosen at run time so the launcher can pass --lang; the compile-time
// /D only decides which *data source* this binary reads.
struct Strings
{
    const wchar_t* title;
    const wchar_t* intro;
    const wchar_t* copy;
    const wchar_t* folder;
    const wchar_t* close;
    const wchar_t* emptyCrash;
    const wchar_t* emptyErrors;
    const wchar_t* copied;
    const wchar_t* copyFailed;
    const wchar_t* noFile;
    const wchar_t* emptyBody;
};

#ifdef CRASH_REPORTER
constexpr wchar_t kSubDir[] = L"crash";
constexpr wchar_t kSubFile[] = L"";

const Strings kZh = {
    L"ECL 崩溃报告",
    L"启动器在这次运行中遇到了未处理的异常，已经退出。\r\n"
    L"下面的内容包含异常信息、运行环境和最近的启动日志。\r\n"
    L"点「复制全部」发出来，通常就能定位问题。",
    L"复制全部", L"打开所在文件夹", L"关闭",
    L"没有找到崩溃记录。\r\n\r\n"
    L"如果启动器仍然打不开，可以看同目录下的 startup.log，"
    L"或者删除 settings.json 让它回到默认设置再试。",
    L"",
    L"已复制到剪贴板。",
    L"剪贴板被其它程序占用，复制失败。",
    L"还没有生成报告文件。",
    L"（崩溃文件是空的）"
};

const Strings kEn = {
    L"ECL Crash Report",
    L"The launcher hit an unhandled exception and exited.\r\n"
    L"Below are the exception, the environment and the last start-up log.\r\n"
    L"Use \"Copy all\" and send it — that is usually enough to locate the fault.",
    L"Copy all", L"Open folder", L"Close",
    L"No crash record found.\r\n\r\n"
    L"If the launcher still will not open, check startup.log next to it, "
    L"or delete settings.json to fall back to defaults.",
    L"",
    L"Copied to clipboard.",
    L"Clipboard is busy — could not copy.",
    L"No report file has been written yet.",
    L"(the crash file is empty)"
};
#else
constexpr wchar_t kSubDir[] = L"";
constexpr wchar_t kSubFile[] = L"errors.jsonl";

const Strings kZh = {
    L"ECL 非致命错误报告",
    L"以下问题没有让启动器退出，但可能影响功能（下载失败、模组解析失败等）。\r\n"
    L"按时间从新到旧排列，每条都带着当时在做什么。",
    L"复制全部", L"打开所在文件夹", L"关闭",
    L"",
    L"没有记录到非致命错误。\r\n\r\n"
    L"这个文件只会记下不影响运行的异常——下载失败、模组读不出来、"
    L"某个操作被拒绝之类。空着说明一切正常。",
    L"已复制到剪贴板。",
    L"剪贴板被其它程序占用，复制失败。",
    L"还没有生成报告文件。",
    L"（空）"
};

const Strings kEn = {
    L"ECL Non-fatal Errors",
    L"These did not stop the launcher but may affect it (failed downloads, "
    L"unreadable mods, refused operations).\r\n"
    L"Newest first; each entry records what was being done at the time.",
    L"Copy all", L"Open folder", L"Close",
    L"",
    L"Nothing recorded.\r\n\r\n"
    L"This file only collects problems that do not stop the launcher. "
    L"Empty means everything went fine.",
    L"Copied to clipboard.",
    L"Clipboard is busy — could not copy.",
    L"No report file has been written yet.",
    L"(empty)"
};
#endif

const Strings* gText = &kZh;   // replaced in wWinMain once the language is known

// Palette, chosen to sit next to the launcher's light theme.
const COLORREF kBgColor     = RGB(0xF7, 0xF8, 0xFA);
const COLORREF kTextColor   = RGB(0x1B, 0x1D, 0x1F);
const COLORREF kMutedColor  = RGB(0x6B, 0x72, 0x7A);
const COLORREF kCardColor   = RGB(0xFF, 0xFF, 0xFF);
const COLORREF kBorderColor = RGB(0xE1, 0xE4, 0xE8);
const COLORREF kAccentColor = RGB(0x2E, 0x7D, 0x5B);
const COLORREF kAccentPress = RGB(0x24, 0x63, 0x48);
const COLORREF kHoverColor  = RGB(0xEC, 0xEF, 0xF3);

// Created in wWinMain, handed back to controls from WM_CTLCOLOR*.
HBRUSH gBgBrush = nullptr;
HBRUSH gCardBrush = nullptr;

constexpr int kIdHeading = 2002;
constexpr int kIdIntro = 2003;
constexpr int kIdEdit = 2001;
constexpr int kIdCopy = 1001;
constexpr int kIdFolder = 1002;
constexpr int kIdClose = 1003;

std::wstring ExeDir()
{
    wchar_t buf[MAX_PATH]{};
    DWORD n = GetModuleFileNameW(nullptr, buf, MAX_PATH);
    if (n == 0 || n >= MAX_PATH) return L".";

    std::wstring path(buf, n);
    size_t slash = path.find_last_of(L"\\/");
    return slash == std::wstring::npos ? L"." : path.substr(0, slash);
}

// The launcher passes --dir; falling back to the exe folder keeps the tool usable
// when someone double-clicks it directly.
std::wstring ResolveRoot()
{
    int argc = 0;
    LPWSTR* argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    std::wstring root;

    if (argv)
    {
        for (int i = 1; i + 1 < argc; ++i)
        {
            if (_wcsicmp(argv[i], L"--dir") == 0) { root = argv[i + 1]; break; }
        }
        LocalFree(argv);
    }

    return root.empty() ? ExeDir() : root;
}

// Reads a whole file as UTF-8 and converts to UTF-16 for the edit control.
// Returns an empty string when the file is missing — the caller shows a friendly
// "nothing here" instead of an empty window.
std::wstring ReadUtf8(const std::wstring& path)
{
    HANDLE h = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                           nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return L"";

    LARGE_INTEGER size{};
    if (!GetFileSizeEx(h, &size) || size.QuadPart <= 0) { CloseHandle(h); return L""; }

    // Guard against someone dropping a 2 GB log in here.
    const long long cap = 4 * 1024 * 1024;
    DWORD toRead = (DWORD)(size.QuadPart > cap ? cap : size.QuadPart);

    std::vector<char> bytes(toRead);
    DWORD read = 0;
    BOOL ok = ReadFile(h, bytes.data(), toRead, &read, nullptr);
    CloseHandle(h);
    if (!ok || read == 0) return L"";

    bytes.resize(read);

    // Strip a UTF-8 BOM; the launcher writes one on some files.
    size_t start = 0;
    if (read >= 3 && (unsigned char)bytes[0] == 0xEF &&
        (unsigned char)bytes[1] == 0xBB && (unsigned char)bytes[2] == 0xBF)
        start = 3;

    int wide = MultiByteToWideChar(CP_UTF8, 0, bytes.data() + start, (int)(read - start), nullptr, 0);
    if (wide <= 0) return L"";

    std::wstring result(wide, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, bytes.data() + start, (int)(read - start), result.data(), wide);
    return result;
}

// Newest *.txt under <root>\crash\ — one file per crash, so the latest is the one
// the user just hit.
std::wstring FindLatestCrash(const std::wstring& root)
{
    std::wstring dir = root + L"\\" + kSubDir;
    std::wstring pattern = dir + L"\\*.txt";

    WIN32_FIND_DATAW fd{};
    HANDLE h = FindFirstFileW(pattern.c_str(), &fd);
    if (h == INVALID_HANDLE_VALUE) return L"";

    std::wstring best;
    FILETIME bestTime{};

    do
    {
        if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) continue;
        if (best.empty() ||
            CompareFileTime(&fd.ftLastWriteTime, &bestTime) > 0)
        {
            best = dir + L"\\" + fd.cFileName;
            bestTime = fd.ftLastWriteTime;
        }
    } while (FindNextFileW(h, &fd));

    FindClose(h);
    return best;
}

std::wstring LoadContent(const std::wstring& root, std::wstring* where)
{
#ifdef CRASH_REPORTER
    std::wstring path = FindLatestCrash(root);
    if (path.empty())
    {
        *where = L"";
        return gText->emptyCrash;
    }

    *where = path;
    std::wstring text = ReadUtf8(path);
    return text.empty() ? gText->emptyBody : text;
#else
    std::wstring path = root + L"\\" + kSubFile;
    *where = path;

    std::wstring text = ReadUtf8(path);
    if (text.empty()) return gText->emptyErrors;

    return text;
#endif
}

struct State
{
    std::wstring content;
    std::wstring sourcePath;
};

// Single source of truth for positions.
//
// CreateWindowEx takes the *window* size, which includes the title bar and borders;
// laying controls out against that number pushed the buttons past the bottom of the
// client area (they showed up cut in half) and let the edit box ride up over the intro
// text. Everything here is computed from GetClientRect, and re-run on WM_SIZE so
// resizing and DPI changes stay consistent.
void Layout(HWND hwnd)
{
    RECT rc{};
    GetClientRect(hwnd, &rc);
    const int w = rc.right - rc.left;
    const int h = rc.bottom - rc.top;

    const int margin = 24;
    const int inner = w - margin * 2;
    const int buttonH = 44;
    const int buttonY = h - margin - buttonH;

    // Boxes are deliberately taller than their text: a 31px font in a 30px box clips
    // the top and bottom of every glyph, which reads as "parts of the characters are
    // missing". Chinese glyphs use the full em box, so the margin matters.
    const int headingY = 12, headingH = 40;
    const int introY = 58, introH = 96;
    const int editY = 162;

    int editH = buttonY - 16 - editY;
    if (editH < 80) editH = 80;   // tiny window: keep the box usable instead of negative

    SetWindowPos(GetDlgItem(hwnd, kIdHeading), nullptr, margin, headingY, inner, headingH, SWP_NOZORDER);
    SetWindowPos(GetDlgItem(hwnd, kIdIntro), nullptr, margin, introY, inner, introH, SWP_NOZORDER);
    SetWindowPos(GetDlgItem(hwnd, kIdEdit), nullptr, margin, editY, inner, editH, SWP_NOZORDER);

    SetWindowPos(GetDlgItem(hwnd, kIdCopy), nullptr, margin, buttonY, 150, buttonH, SWP_NOZORDER);
    SetWindowPos(GetDlgItem(hwnd, kIdFolder), nullptr, margin + 162, buttonY, 170, buttonH, SWP_NOZORDER);
    SetWindowPos(GetDlgItem(hwnd, kIdClose), nullptr, w - margin - 120, buttonY, 120, buttonH, SWP_NOZORDER);
}

LRESULT CALLBACK WndProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam)
{
    auto* state = reinterpret_cast<State*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));

    switch (msg)
    {
    case WM_CREATE:
    {
        auto* cs = reinterpret_cast<CREATESTRUCTW*>(lParam);
        SetWindowLongPtrW(hwnd, GWLP_USERDATA, (LONG_PTR)cs->lpCreateParams);
        return 0;
    }

    case WM_ERASEBKGND:
    {
        RECT rc{};
        GetClientRect(hwnd, &rc);
        FillRect(reinterpret_cast<HDC>(wParam), &rc, gBgBrush ? gBgBrush : (HBRUSH)(COLOR_WINDOW + 1));
        return 1;
    }

    case WM_CTLCOLORSTATIC:
    {
        HDC dc = reinterpret_cast<HDC>(wParam);
        auto control = reinterpret_cast<HWND>(lParam);
        SetBkMode(dc, TRANSPARENT);
        // The intro paragraph is secondary information, so it stays grey.
        SetTextColor(dc, GetDlgCtrlID(control) == kIdIntro ? kMutedColor : kTextColor);
        return reinterpret_cast<LRESULT>(gBgBrush);
    }

    case WM_CTLCOLOREDIT:
    {
        HDC dc = reinterpret_cast<HDC>(wParam);
        SetTextColor(dc, kTextColor);
        SetBkColor(dc, kCardColor);
        return reinterpret_cast<LRESULT>(gCardBrush);
    }

    case WM_DRAWITEM:
    {
        auto* dis = reinterpret_cast<DRAWITEMSTRUCT*>(lParam);
        if (!dis) break;

        const bool primary = (dis->CtlID == kIdCopy);
        const bool pressed = (dis->itemState & ODS_SELECTED) != 0;

        // Round corners: a 1px round rect drawn by hand, because owner-draw buttons
        // come out square otherwise and the whole point is to look less like 1995.
        COLORREF fill = primary
            ? (pressed ? kAccentPress : kAccentColor)
            : (pressed ? kHoverColor : kCardColor);
        COLORREF edge = primary ? fill : kBorderColor;

        HBRUSH brush = CreateSolidBrush(fill);
        HPEN pen = CreatePen(PS_SOLID, 1, edge);
        HGDIOBJ oldBrush = SelectObject(dis->hDC, brush);
        HGDIOBJ oldPen = SelectObject(dis->hDC, pen);

        RoundRect(dis->hDC, dis->rcItem.left, dis->rcItem.top,
                  dis->rcItem.right, dis->rcItem.bottom, 10, 10);

        SelectObject(dis->hDC, oldBrush);
        SelectObject(dis->hDC, oldPen);
        DeleteObject(brush);
        DeleteObject(pen);

        wchar_t label[128]{};
        GetWindowTextW(dis->hwndItem, label, ARRAYSIZE(label));

        SetBkMode(dis->hDC, TRANSPARENT);
        SetTextColor(dis->hDC, primary ? RGB(0xFF, 0xFF, 0xFF) : kTextColor);

        HGDIOBJ oldFont = SelectObject(dis->hDC, GetStockObject(DEFAULT_GUI_FONT));
        HFONT btnFont = reinterpret_cast<HFONT>(SendMessageW(dis->hwndItem, WM_GETFONT, 0, 0));
        if (btnFont) SelectObject(dis->hDC, btnFont);

        RECT textRect = dis->rcItem;
        DrawTextW(dis->hDC, label, -1, &textRect,
                  DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);

        SelectObject(dis->hDC, oldFont);
        return TRUE;
    }

    case WM_COMMAND:
        switch (LOWORD(wParam))
        {
        case kIdCopy:
            if (state)
            {
                // Clipboard can be held open by another process; retry briefly.
                for (int attempt = 0; attempt < 5; ++attempt)
                {
                    if (OpenClipboard(hwnd))
                    {
                        EmptyClipboard();
                        size_t bytes = (state->content.size() + 1) * sizeof(wchar_t);
                        HGLOBAL mem = GlobalAlloc(GMEM_MOVEABLE, bytes);
                        if (mem)
                        {
                            void* dst = GlobalLock(mem);
                            if (dst)
                            {
                                memcpy(dst, state->content.c_str(), bytes);
                                GlobalUnlock(mem);
                                SetClipboardData(CF_UNICODETEXT, mem);
                            }
                        }
                        CloseClipboard();
                        MessageBoxW(hwnd, gText->copied, gText->title, MB_ICONINFORMATION | MB_OK);
                        return 0;
                    }
                    Sleep(50);
                }
                MessageBoxW(hwnd, gText->copyFailed, gText->title, MB_ICONWARNING | MB_OK);
            }
            return 0;

        case kIdFolder:
            if (state && !state->sourcePath.empty())
            {
                std::wstring args = L"/select,\"" + state->sourcePath + L"\"";
                ShellExecuteW(nullptr, L"open", L"explorer.exe", args.c_str(), nullptr, SW_SHOWNORMAL);
            }
            else
            {
                MessageBoxW(hwnd, gText->noFile, gText->title, MB_ICONINFORMATION | MB_OK);
            }
            return 0;

        case kIdClose:
            DestroyWindow(hwnd);
            return 0;

        default:
            break;
        }
        break;

    case WM_SIZE:
        Layout(hwnd);
        return 0;

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

// Writes the real control rectangles to <root>\layout.txt.
//
// The first version of this window looked correct in code and was visibly broken on
// screen (buttons cut in half, edit box overlapping the intro). A GUI has no console,
// so without this there is no way to check the layout other than asking a human to
// squint at a screenshot.
void DumpLayout(HWND hwnd, const std::wstring& root)
{
    RECT client{};
    GetClientRect(hwnd, &client);

    std::wstring path = root + L"\\layout.txt";
    FILE* f = nullptr;
    if (_wfopen_s(&f, path.c_str(), L"w, ccs=UTF-8") != 0 || !f) return;

    fwprintf(f, L"client %dx%d\n", client.right, client.bottom);

    struct { int id; const wchar_t* name; } items[] = {
        { kIdHeading, L"heading" },
        { kIdIntro,   L"intro"   },
        { kIdEdit,    L"edit"    },
        { kIdCopy,    L"copy"    },
        { kIdFolder,  L"folder"  },
        { kIdClose,   L"close"   },
    };

    for (const auto& it : items)
    {
        HWND c = GetDlgItem(hwnd, it.id);
        if (!c) { fwprintf(f, L"%-8s (missing)\n", it.name); continue; }

        RECT r{};
        GetWindowRect(c, &r);
        MapWindowPoints(nullptr, hwnd, reinterpret_cast<POINT*>(&r), 2);
        fwprintf(f, L"%-8s x=%4d y=%4d w=%4d h=%4d bottom=%4d visible=%d text=%d\n",
                 it.name, r.left, r.top, r.right - r.left, r.bottom - r.top, r.bottom,
                 IsWindowVisible(c) ? 1 : 0, GetWindowTextLengthW(c));
    }

    fclose(f);
}

} // namespace


int WINAPI wWinMain(HINSTANCE inst, HINSTANCE, PWSTR, int)
{
    SetProcessDPIAware();

    // --lang en|zh wins; otherwise follow the OS UI language.
    {
        const wchar_t* forced = nullptr;
        int argc = 0;
        if (LPWSTR* argv = CommandLineToArgvW(GetCommandLineW(), &argc))
        {
            for (int i = 1; i + 1 < argc; ++i)
                if (_wcsicmp(argv[i], L"--lang") == 0) { forced = argv[i + 1]; break; }
            // keep argv alive only until we copy what we need
            if (forced) { gText = (_wcsnicmp(forced, L"en", 2) == 0) ? &kEn : &kZh; }
            LocalFree(argv);
        }
        if (!forced)
        {
            LANGID lang = GetUserDefaultUILanguage();
            gText = (PRIMARYLANGID(lang) == LANG_CHINESE) ? &kZh : &kEn;
        }
    }

    State state;
    std::wstring root = ResolveRoot();

#ifdef CRASH_REPORTER
    std::wstring dir = root + L"\\" + kSubDir;
    CreateDirectoryW(dir.c_str(), nullptr);   // so "open folder" always works
#endif

    state.content = LoadContent(root, &state.sourcePath);

    const wchar_t* cls = L"ECLReporter";
    WNDCLASSEXW wc{};
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = WndProc;
    wc.hInstance = inst;
    wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    wc.hbrBackground = (HBRUSH)(COLOR_WINDOW + 1);
    wc.lpszClassName = cls;
    RegisterClassExW(&wc);

    gBgBrush = CreateSolidBrush(kBgColor);
    gCardBrush = CreateSolidBrush(kCardColor);

    const DWORD style = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_THICKFRAME;
    const DWORD exStyle = WS_EX_DLGMODALFRAME;

    // Ask for a client area, not a window size. Passing 900x700 straight to
    // CreateWindowEx would leave ~640px of usable height once the title bar and
    // borders are subtracted — the bottom buttons ended up half off-screen.
    const int clientW = 900, clientH = 700;
    RECT frameRect{ 0, 0, clientW, clientH };
    AdjustWindowRectEx(&frameRect, style, FALSE, exStyle);
    const int winW = frameRect.right - frameRect.left;
    const int winH = frameRect.bottom - frameRect.top;

    int x = (GetSystemMetrics(SM_CXSCREEN) - winW) / 2;
    int y = (GetSystemMetrics(SM_CYSCREEN) - winH) / 2;

    HWND hwnd = CreateWindowExW(exStyle, cls, gText->title, style,
                                x, y, winW, winH, nullptr, nullptr, inst, &state);
    if (!hwnd) return 1;

    HFONT headingFont = CreateFontW(-28, 0, 0, 0, FW_BOLD, FALSE, FALSE, FALSE,
                                    DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
                                    CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Microsoft YaHei");
    HFONT ui = CreateFontW(-19, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
                           OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                           DEFAULT_PITCH, L"Microsoft YaHei UI");
    HFONT mono = CreateFontW(-16, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
                             OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                             FIXED_PITCH, L"Consolas");
    HFONT btnFont = CreateFontW(-18, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
                                OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
                                DEFAULT_PITCH, L"Microsoft YaHei UI");

    // Created at 0x0: Layout() owns every position and runs right after.
    HWND heading = CreateWindowExW(0, L"STATIC", gText->title,
                                   WS_CHILD | WS_VISIBLE | SS_LEFT,
                                   0, 0, 0, 0, hwnd,
                                   (HMENU)(INT_PTR)kIdHeading, inst, nullptr);

    HWND intro = CreateWindowExW(0, L"STATIC", gText->intro,
                                 WS_CHILD | WS_VISIBLE | SS_LEFT,
                                 0, 0, 0, 0, hwnd,
                                 (HMENU)(INT_PTR)kIdIntro, inst, nullptr);

    HWND edit = CreateWindowExW(WS_EX_CLIENTEDGE, L"EDIT", state.content.c_str(),
                                WS_CHILD | WS_VISIBLE | WS_VSCROLL | WS_HSCROLL |
                                ES_MULTILINE | ES_READONLY | ES_AUTOVSCROLL,
                                0, 0, 0, 0,
                                hwnd, (HMENU)(INT_PTR)kIdEdit, inst, nullptr);

    SendMessageW(heading, WM_SETFONT, (WPARAM)headingFont, TRUE);
    SendMessageW(intro, WM_SETFONT, (WPARAM)ui, TRUE);
    SendMessageW(edit, WM_SETFONT, (WPARAM)mono, TRUE);

    struct { const wchar_t* text; int id; } buttons[] = {
        { gText->copy, kIdCopy },
        { gText->folder, kIdFolder },
        { gText->close, kIdClose },
    };

    for (const auto& b : buttons)
    {
        // BS_OWNERDRAW so WM_DRAWITEM can paint a rounded, colour-coded button.
        HWND btn = CreateWindowExW(0, L"BUTTON", b.text,
                                   WS_CHILD | WS_VISIBLE | WS_TABSTOP | BS_OWNERDRAW,
                                   0, 0, 0, 0, hwnd,
                                   (HMENU)(INT_PTR)b.id, inst, nullptr);
        SendMessageW(btn, WM_SETFONT, (WPARAM)btnFont, TRUE);
    }

    Layout(hwnd);

    // Show, then jump to the end: crash stacks put the useful line near the top but
    // error lists are newest-last, and either way scrolling up is easier than down.
    ShowWindow(hwnd, SW_SHOW);
    UpdateWindow(hwnd);
    SetFocus(edit);

    // Dump AFTER ShowWindow: IsWindowVisible reports false for every child while the
    // parent is still hidden, which made an earlier run look like the controls had
    // never been created.
    for (int i = 1; i < __argc; ++i)
    {
        if (_wcsicmp(__wargv[i], L"--dump-layout") == 0) { DumpLayout(hwnd, root); break; }
    }

    MSG msg{};
    while (GetMessageW(&msg, nullptr, 0, 0) > 0)
    {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }

    DeleteObject(headingFont);
    DeleteObject(ui);
    DeleteObject(mono);
    DeleteObject(btnFont);
    if (gBgBrush) DeleteObject(gBgBrush);
    if (gCardBrush) DeleteObject(gCardBrush);
    return 0;
}
