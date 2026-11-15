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
// Visual design: dark card layout with an accent stripe, drawn with plain GDI
// (RoundRect / FillRect / DrawText). GDI+ would allow softer shadows, but it is another
// DLL to load at exactly the moment the process is least healthy.
//
// Usage:  eclcr.exe   --dir "<ECL folder>"      newest file under <dir>\crash\
//         eclnfer.exe --dir "<ECL folder>"      <dir>\errors.jsonl
//         both: --lang en|zh  --dump-layout
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
#include <shellapi.h>
#include <shlwapi.h>
#include <dwmapi.h>
#include <uxtheme.h>
#include <string>
#include <vector>
#include <cstdio>

#pragma comment(lib, "user32.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "shlwapi.lib")
#pragma comment(lib, "comctl32.lib")
#pragma comment(lib, "gdi32.lib")   // CreateFontW / DeleteObject
#pragma comment(lib, "dwmapi.lib")  // dark caption bar, best effort
#pragma comment(lib, "uxtheme.lib")

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

    // Added for the card layout.
    const wchar_t* sectionSummary;
    const wchar_t* sectionReport;
    const wchar_t* noSummary;
    const wchar_t* errorKind;
};

#ifdef CRASH_REPORTER
constexpr wchar_t kSubDir[] = L"crash";
constexpr wchar_t kSubFile[] = L"";

const Strings kZh = {
    L"ECL 崩溃报告",
    L"启动器遇到了未处理的异常并已退出。完整报告在下面，复制出来通常就够定位问题。",
    L"复制详情", L"打开所在文件夹", L"关闭",
    L"没有找到崩溃记录。\r\n\r\n"
    L"如果启动器仍然打不开，可以看同目录下的 startup.log，"
    L"或者删除 settings.json 让它回到默认设置再试。",
    L"",
    L"已复制到剪贴板。",
    L"剪贴板被其它程序占用，复制失败。",
    L"还没有生成报告文件。",
    L"（崩溃文件是空的）",
    L"发生了什么", L"完整报告", L"（报告里没有异常摘要）", L""
};

const Strings kEn = {
    L"ECL Crash Report",
    L"The launcher hit an unhandled exception and exited. The full report is below.",
    L"Copy details", L"Open folder", L"Close",
    L"No crash record found.\r\n\r\n"
    L"If the launcher still will not open, check startup.log next to it, "
    L"or delete settings.json to fall back to defaults.",
    L"",
    L"Copied to clipboard.",
    L"Clipboard is busy — could not copy.",
    L"No report file has been written yet.",
    L"(the crash file is empty)",
    L"What happened", L"Full report", L"(no exception summary in the file)", L""
};
#else
constexpr wchar_t kSubDir[] = L"";
constexpr wchar_t kSubFile[] = L"errors.jsonl";

const Strings kZh = {
    L"ECL 非致命错误报告",
    L"这些问题没有让启动器退出，但可能影响功能（下载失败、模组解析失败等）。",
    L"复制详情", L"打开所在文件夹", L"关闭",
    L"",
    L"没有记录到非致命错误。\r\n\r\n"
    L"这个文件只会记下不影响运行的异常——下载失败、模组读不出来、"
    L"某个操作被拒绝之类。空着说明一切正常。",
    L"已复制到剪贴板。",
    L"剪贴板被其它程序占用，复制失败。",
    L"还没有生成报告文件。",
    L"（空）",
    L"概览", L"全部记录", L"（没有可归纳的内容）", L"非致命"
};

const Strings kEn = {
    L"ECL Non-fatal Errors",
    L"These did not stop the launcher but may affect it (failed downloads, unreadable mods).",
    L"Copy details", L"Open folder", L"Close",
    L"",
    L"Nothing recorded.\r\n\r\n"
    L"This file only collects problems that do not stop the launcher. "
    L"Empty means everything went fine.",
    L"Copied to clipboard.",
    L"Clipboard is busy — could not copy.",
    L"No report file has been written yet.",
    L"(empty)",
    L"Overview", L"All entries", L"(nothing to summarise)", L"Non-fatal"
};
#endif

const Strings* gText = &kZh;   // replaced in wWinMain once the language is known

// Palette: dark, so the window does not glare in a dim room or over a game.
const COLORREF kBgColor     = RGB(0x14, 0x14, 0x1C);
const COLORREF kCardColor   = RGB(0x1B, 0x1B, 0x26);
const COLORREF kCardHiColor = RGB(0x22, 0x22, 0x30);
const COLORREF kBorderColor = RGB(0x2C, 0x2C, 0x3E);
const COLORREF kTextColor   = RGB(0xF2, 0xF0, 0xFF);
const COLORREF kMutedColor  = RGB(0x9A, 0x94, 0xB8);
const COLORREF kFaintColor  = RGB(0x77, 0x72, 0x93);

#ifdef CRASH_REPORTER
const COLORREF kAccentColor = RGB(0xFF, 0x6B, 0x6B);   // red: something died
const COLORREF kAccentPress = RGB(0xD9, 0x50, 0x50);
#else
const COLORREF kAccentColor = RGB(0xF5, 0xB9, 0x42);   // amber: still running
const COLORREF kAccentPress = RGB(0xD2, 0x9B, 0x2E);
#endif

// Created in wWinMain, handed back to controls from WM_CTLCOLOR*.
HBRUSH gBgBrush = nullptr;
HBRUSH gCardBrush = nullptr;

constexpr int kIdHeading = 2002;
constexpr int kIdIntro = 2003;
constexpr int kIdEdit = 2001;
constexpr int kIdSumType = 2004;
constexpr int kIdSumMsg = 2005;
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

    // A runaway log must not eat the reporter's memory: 4 MB is far more than a human
    // will read and still loads instantly.
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
        if (best.empty() || CompareFileTime(&fd.ftLastWriteTime, &bestTime) > 0)
        {
            best = dir + L"\\" + fd.cFileName;
            bestTime = fd.ftLastWriteTime;
        }
    } while (FindNextFileW(h, &fd));

    FindClose(h);
    return best;
}

// EDIT only breaks lines on CRLF. Reports written on Linux tooling — and anything the
// user pasted together by hand — often use bare LF, which used to render the whole
// crash log as a single unreadable line.
std::wstring NormalizeNewlines(const std::wstring& text)
{
    std::wstring out;
    out.reserve(text.size() + text.size() / 16);

    for (size_t i = 0; i < text.size(); ++i)
    {
        wchar_t c = text[i];
        if (c == L'\r')
        {
            out += L"\r\n";
            if (i + 1 < text.size() && text[i + 1] == L'\n') ++i;
        }
        else if (c == L'\n')
        {
            out += L"\r\n";
        }
        else
        {
            out.push_back(c);
        }
    }

    return out;
}


#ifndef CRASH_REPORTER
std::wstring PrettyErrors(const std::wstring& jsonl);   // defined after Trim/SplitLines
#endif

std::wstring LoadContent(const std::wstring& root, std::wstring* where, std::wstring* raw)
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
    *raw = NormalizeNewlines(text);
    return text.empty() ? gText->emptyBody : *raw;
#else
    std::wstring path = root + L"\\" + kSubFile;
    *where = path;

    std::wstring text = ReadUtf8(path);
    if (text.empty()) return gText->emptyErrors;

    // Kept raw for the summary (it counts JSON entries); the pretty version is display only.
    *raw = NormalizeNewlines(text);
    return NormalizeNewlines(PrettyErrors(text));
#endif
}

// ---------------------------------------------------------------------------
//  Summary extraction
//
//  The report file is written by the launcher and its shape is stable, so a line scan
//  is enough — no parser, no dependency. Anything unexpected falls back to the
//  "no summary" label rather than printing garbage into the card.
// ---------------------------------------------------------------------------

std::wstring Trim(const std::wstring& s)
{
    size_t a = s.find_first_not_of(L" \t\r\n");
    if (a == std::wstring::npos) return L"";
    size_t b = s.find_last_not_of(L" \t\r\n");
    return s.substr(a, b - a + 1);
}

std::vector<std::wstring> SplitLines(const std::wstring& text)
{
    std::vector<std::wstring> lines;
    std::wstring current;
    for (wchar_t c : text)
    {
        if (c == L'\n') { lines.push_back(current); current.clear(); }
        else if (c != L'\r') current.push_back(c);
    }
    if (!current.empty()) lines.push_back(current);
    return lines;
}

std::wstring ValueAfterColon(const std::wstring& line)
{
    size_t colon = line.find(L':');
    if (colon == std::wstring::npos) return L"";
    return Trim(line.substr(colon + 1));
}

#ifndef CRASH_REPORTER
// Extracts a JSON string value without a parser: the launcher writes flat objects with
// fixed field names, and a hand-rolled scan keeps this tool dependency-free.
std::wstring JsonField(const std::wstring& line, const wchar_t* key)
{
    std::wstring needle = std::wstring(L"\"") + key + L"\"";
    size_t k = line.find(needle);
    if (k == std::wstring::npos) return L"";

    size_t quote = line.find(L'"', line.find(L':', k));
    if (quote == std::wstring::npos) return L"";

    std::wstring value;
    for (size_t i = quote + 1; i < line.size(); ++i)
    {
        wchar_t c = line[i];
        if (c == L'"') break;
        if (c == L'\\' && i + 1 < line.size())
        {
            wchar_t n = line[++i];
            switch (n)
            {
            case L'n': value.push_back(L'\n'); break;
            case L't': value.push_back(L'\t'); break;
            case L'r': value.push_back(L'\r'); break;
            default:  value.push_back(n); break;
            }
            continue;
        }
        value.push_back(c);
    }
    return value;
}

// JSONL is the storage format, not the reading format: one entry per line is hard to
// scan on screen. Render each entry as a small block instead, and fall back to the raw
// line when it does not look like an entry (hand-edited files, truncated writes).
std::wstring PrettyErrors(const std::wstring& jsonl)
{
    std::wstring out;

    for (const auto& raw : SplitLines(jsonl))
    {
        std::wstring line = Trim(raw);
        if (line.empty()) continue;

        if (line[0] != L'{')
        {
            out += line + L"\r\n\r\n";
            continue;
        }

        std::wstring time = JsonField(line, L"time");
        std::wstring kind = JsonField(line, L"kind");
        std::wstring message = JsonField(line, L"message");

        if (message.empty() && kind.empty())
        {
            out += line + L"\r\n\r\n";
            continue;
        }

        // "2026-11-15T20:12:03.221" -> "2026-11-15 20:12:03"
        if (time.size() >= 19)
        {
            time[10] = L' ';
            time = time.substr(0, 19);
        }

        out += L"[" + (time.empty() ? std::wstring(L"?") : time) + L"]";
        if (!kind.empty()) out += L"  " + kind;
        out += L"\r\n";
        out += L"    " + message + L"\r\n\r\n";
    }

    return out.empty() ? jsonl : out;
}
#endif

struct Summary
{
    std::wstring type;      // exception type, or the most useful first line
    std::wstring message;   // exception message / count line
    std::wstring meta;      // version · time · context
};

Summary ParseSummary(const std::wstring& content)
{
    Summary sum;
    auto lines = SplitLines(content);

    std::wstring version, time, context;

    for (const auto& raw : lines)
    {
        std::wstring line = Trim(raw);
        if (line.empty()) continue;

        if (sum.type.empty() && line.find(L"[异常类型]") == 0)
        {
            sum.type = Trim(line.substr(wcslen(L"[异常类型]")));
            continue;
        }
        if (sum.message.empty() && line.find(L"[异常消息]") == 0)
        {
            sum.message = Trim(line.substr(wcslen(L"[异常消息]")));
            continue;
        }
        if (version.empty() && line.find(L"版本") == 0) { version = ValueAfterColon(line); continue; }
        if (time.empty() && line.find(L"时间") == 0) { time = ValueAfterColon(line); continue; }
        if (context.empty() && line.find(L"上下文") == 0) { context = ValueAfterColon(line); continue; }
    }

#ifndef CRASH_REPORTER
    // Non-fatal files are JSONL: one entry per line, newest first. A count plus the
    // newest kind beats echoing the first JSON object.
    if (sum.type.empty())
    {
        int entries = 0;
        std::wstring newestKind;

        for (const auto& raw : lines)
        {
            std::wstring line = Trim(raw);
            if (line.size() < 2 || line[0] != L'{') continue;
            entries++;

            if (newestKind.empty())
            {
                size_t k = line.find(L"\"kind\"");
                if (k != std::wstring::npos)
                {
                    size_t q1 = line.find(L'"', line.find(L':', k));
                    if (q1 != std::wstring::npos)
                    {
                        size_t q2 = line.find(L'"', q1 + 1);
                        if (q2 != std::wstring::npos) newestKind = line.substr(q1 + 1, q2 - q1 - 1);
                    }
                }
            }
        }

        wchar_t buf[256]{};
        if (entries > 0) swprintf_s(buf, L"%d 条记录 · 最近：%s", entries, newestKind.c_str());
        else             swprintf_s(buf, L"%s", gText->noSummary);
        sum.message = buf;
        sum.type = gText->errorKind;
    }
#endif

    if (sum.type.empty()) sum.type = gText->noSummary;

    if (!version.empty()) sum.meta = version;
    if (!time.empty()) sum.meta += (sum.meta.empty() ? L"" : L"  ·  ") + time;
    if (!context.empty()) sum.meta += (sum.meta.empty() ? L"" : L"  ·  ") + context;

    return sum;
}

// ---------------------------------------------------------------------------
//  Window state
// ---------------------------------------------------------------------------

struct State
{
    std::wstring content;
    std::wstring sourcePath;
    Summary summary;

    HFONT heading = nullptr;
    HFONT subtitle = nullptr;
    HFONT section = nullptr;
    HFONT chip = nullptr;
    HFONT body = nullptr;
    HFONT mono = nullptr;
    HFONT button = nullptr;
};

// Card geometry, recomputed on every size change and reused by the painter.
struct LayoutRects
{
    RECT header{};
    RECT summaryCard{};
    RECT reportCard{};
    RECT edit{};
    RECT btnCopy{}, btnFolder{}, btnClose{};
    int margin = 28;
};

LayoutRects gLayout{};

void ComputeLayout(HWND hwnd)
{
    RECT rc{};
    GetClientRect(hwnd, &rc);
    const int w = rc.right - rc.left;
    const int h = rc.bottom - rc.top;

    const int margin = 28;
    const int buttonH = 44;
    const int summaryH = 92;

    gLayout = LayoutRects{};
    gLayout.margin = margin;
    gLayout.header = { margin, margin, w - margin, margin + 76 };

    const int summaryTop = gLayout.header.bottom + 16;
    gLayout.summaryCard = { margin, summaryTop, w - margin, summaryTop + summaryH };

    const int buttonY = h - margin - buttonH;
    const int reportTop = gLayout.summaryCard.bottom + 20;
    const int reportBottom = buttonY - 20;

    gLayout.reportCard = { margin, reportTop, w - margin, reportBottom };
    // 40px of the card is the section-label row.
    gLayout.edit = { margin + 16, reportTop + 42, w - margin - 16, reportBottom - 14 };

    gLayout.btnCopy   = { margin, buttonY, margin + 160, buttonY + buttonH };
    gLayout.btnFolder = { margin + 172, buttonY, margin + 172 + 180, buttonY + buttonH };
    gLayout.btnClose  = { w - margin - 120, buttonY, w - margin, buttonY + buttonH };

    auto place = [&](int id, const RECT& r)
    {
        if (HWND c = GetDlgItem(hwnd, id))
            SetWindowPos(c, nullptr, r.left, r.top, r.right - r.left, r.bottom - r.top,
                         SWP_NOZORDER | SWP_NOACTIVATE);
    };

    // Header text sits to the right of the accent stripe.
    place(kIdHeading, { margin + 18, gLayout.header.top + 2, w - margin, gLayout.header.top + 40 });
    place(kIdIntro,   { margin + 18, gLayout.header.top + 42, w - margin, gLayout.header.bottom });

    place(kIdSumType, { gLayout.summaryCard.left + 16, gLayout.summaryCard.top + 28,
                        gLayout.summaryCard.right - 16, gLayout.summaryCard.top + 52 });
    place(kIdSumMsg,  { gLayout.summaryCard.left + 16, gLayout.summaryCard.top + 54,
                        gLayout.summaryCard.right - 16, gLayout.summaryCard.bottom - 6 });
    place(kIdEdit,    gLayout.edit);

    place(kIdCopy,   gLayout.btnCopy);
    place(kIdFolder, gLayout.btnFolder);
    place(kIdClose,  gLayout.btnClose);
}

void PaintWindow(HWND hwnd, HDC target)
{
    RECT client{};
    GetClientRect(hwnd, &client);

    // Double buffer: card borders plus child controls flicker badly without it.
    HDC mem = CreateCompatibleDC(target);
    HBITMAP bmp = CreateCompatibleBitmap(target, client.right, client.bottom);
    HGDIOBJ oldBitmap = SelectObject(mem, bmp);

    HBRUSH bg = gBgBrush ? gBgBrush : (HBRUSH)(COLOR_WINDOW + 1);
    FillRect(mem, &client, bg);

    auto drawCard = [&](const RECT& r)
    {
        HBRUSH fill = CreateSolidBrush(kCardColor);
        HPEN pen = CreatePen(PS_SOLID, 1, kBorderColor);
        HGDIOBJ ob = SelectObject(mem, fill);
        HGDIOBJ op = SelectObject(mem, pen);
        RoundRect(mem, r.left, r.top, r.right, r.bottom, 12, 12);
        SelectObject(mem, ob);
        SelectObject(mem, op);
        DeleteObject(fill);
        DeleteObject(pen);
    };

    // Accent stripe: the only saturated colour in the window, and the fastest way to
    // tell a crash report from a non-fatal one at a glance.
    RECT stripe{ gLayout.margin, gLayout.header.top + 4,
                 gLayout.margin + 4, gLayout.header.top + 40 };
    HBRUSH accent = CreateSolidBrush(kAccentColor);
    FillRect(mem, &stripe, accent);
    DeleteObject(accent);

    drawCard(gLayout.summaryCard);
    drawCard(gLayout.reportCard);

    auto* state = reinterpret_cast<State*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
    SetBkMode(mem, TRANSPARENT);

    if (state && state->section)
    {
        HGDIOBJ old = SelectObject(mem, state->section);
        SetTextColor(mem, kFaintColor);

        RECT sumLabel{ gLayout.summaryCard.left + 16, gLayout.summaryCard.top + 8,
                       gLayout.summaryCard.right - 16, gLayout.summaryCard.top + 26 };
        DrawTextW(mem, gText->sectionSummary, -1, &sumLabel, DT_LEFT | DT_SINGLELINE);

        RECT repLabel{ gLayout.reportCard.left + 16, gLayout.reportCard.top + 12,
                       gLayout.reportCard.right - 16, gLayout.reportCard.top + 32 };
        DrawTextW(mem, gText->sectionReport, -1, &repLabel, DT_LEFT | DT_SINGLELINE);

        if (!state->summary.meta.empty())
        {
            RECT meta = repLabel;
            DrawTextW(mem, state->summary.meta.c_str(), -1, &meta,
                      DT_RIGHT | DT_SINGLELINE | DT_END_ELLIPSIS);
        }

        SelectObject(mem, old);
    }

    BitBlt(target, 0, 0, client.right, client.bottom, mem, 0, 0, SRCCOPY);

    SelectObject(mem, oldBitmap);
    DeleteObject(bmp);
    DeleteDC(mem);
}

// ---------------------------------------------------------------------------
//  Window procedure
// ---------------------------------------------------------------------------

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
        return 1;   // PaintWindow owns the whole client area

    case WM_PAINT:
    {
        PAINTSTRUCT ps{};
        HDC dc = BeginPaint(hwnd, &ps);
        PaintWindow(hwnd, dc);
        EndPaint(hwnd, &ps);
        return 0;
    }

    case WM_CTLCOLORSTATIC:
    {
        HDC dc = reinterpret_cast<HDC>(wParam);
        auto control = reinterpret_cast<HWND>(lParam);
        int id = GetDlgCtrlID(control);

        SetBkMode(dc, TRANSPARENT);

        if (id == kIdSumType)      SetTextColor(dc, kAccentColor);
        else if (id == kIdIntro)   SetTextColor(dc, kMutedColor);
        else                       SetTextColor(dc, kTextColor);

        // Every static here sits on a paint-drawn card, so it must not fill its own
        // background or the rounded border gets squared off.
        return reinterpret_cast<LRESULT>(GetStockObject(NULL_BRUSH));
    }

    case WM_CTLCOLOREDIT:
    {
        HDC dc = reinterpret_cast<HDC>(wParam);
        SetTextColor(dc, RGB(0xDC, 0xDA, 0xEE));
        SetBkColor(dc, kCardHiColor);
        return reinterpret_cast<LRESULT>(CreateSolidBrush(kCardHiColor));
    }

    case WM_DRAWITEM:
    {
        auto* dis = reinterpret_cast<DRAWITEMSTRUCT*>(lParam);
        if (!dis) break;

        const bool primary = (dis->CtlID == kIdCopy);
        const bool pressed = (dis->itemState & ODS_SELECTED) != 0;
        const bool hot = (dis->itemState & ODS_HOTLIGHT) != 0;

        // Round corners: round rect drawn by hand, because owner-draw buttons come out
        // square otherwise and the whole point is to look less like 1995.
        COLORREF fill = primary
            ? (pressed ? kAccentPress : kAccentColor)
            : (pressed || hot ? kCardHiColor : kCardColor);
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
        // Dark text on the red/amber primary button reads better than white.
        SetTextColor(dis->hDC, primary ? RGB(0x1A, 0x10, 0x10) : kTextColor);

        HFONT btnFont = reinterpret_cast<HFONT>(SendMessageW(dis->hwndItem, WM_GETFONT, 0, 0));
        HGDIOBJ oldFont = btnFont ? SelectObject(dis->hDC, btnFont) : nullptr;

        RECT textRect = dis->rcItem;
        DrawTextW(dis->hDC, label, -1, &textRect,
                  DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);

        if (oldFont) SelectObject(dis->hDC, oldFont);
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

    case WM_GETMINMAXINFO:
    {
        // Below this the two cards collide.
        auto* mmi = reinterpret_cast<MINMAXINFO*>(lParam);
        mmi->ptMinTrackSize.x = 720;
        mmi->ptMinTrackSize.y = 560;
        return 0;
    }

    case WM_SIZE:
        ComputeLayout(hwnd);
        InvalidateRect(hwnd, nullptr, TRUE);
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
        { kIdHeading, L"heading"  },
        { kIdIntro,   L"intro"    },
        { kIdSumType, L"sumType"  },
        { kIdSumMsg,  L"sumMsg"   },
        { kIdEdit,    L"edit"     },
        { kIdCopy,    L"copy"     },
        { kIdFolder,  L"folder"   },
        { kIdClose,   L"close"    },
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

    std::wstring rawContent;
    state.content = LoadContent(root, &state.sourcePath, &rawContent);
    state.summary = ParseSummary(rawContent.empty() ? state.content : rawContent);

    const wchar_t* cls = L"ECLReporter";
    WNDCLASSEXW wc{};
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = WndProc;
    wc.hInstance = inst;
    wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    wc.hbrBackground = nullptr;      // painted in WM_PAINT
    wc.lpszClassName = cls;
    RegisterClassExW(&wc);

    gBgBrush = CreateSolidBrush(kBgColor);
    gCardBrush = CreateSolidBrush(kCardColor);

    state.heading = CreateFontW(-26, 0, 0, 0, FW_BOLD, FALSE, FALSE, FALSE,
                                DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
                                CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Microsoft YaHei");
    state.subtitle = CreateFontW(-14, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE,
                                 DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
                                 CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Microsoft YaHei UI");
    state.section = CreateFontW(-13, 0, 0, 0, FW_SEMIBOLD, FALSE, FALSE, FALSE,
                                DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
                                CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Microsoft YaHei UI");
    state.chip = CreateFontW(-18, 0, 0, 0, FW_SEMIBOLD, FALSE, FALSE, FALSE,
                             DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
                             CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Microsoft YaHei UI");
    state.body = CreateFontW(-15, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE,
                             DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
                             CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Microsoft YaHei UI");
    state.mono = CreateFontW(-15, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE,
                             DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
                             CLEARTYPE_QUALITY, FIXED_PITCH, L"Consolas");
    state.button = CreateFontW(-16, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE,
                               DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
                               CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Microsoft YaHei UI");

    const DWORD style = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_THICKFRAME |
                        WS_MINIMIZEBOX | WS_MAXIMIZEBOX;
    const DWORD exStyle = 0;

    // Ask for a client area, not a window size: passing 980x760 to CreateWindowEx would
    // leave ~700px once the frame is subtracted and push the buttons off-screen.
    const int clientW = 980, clientH = 760;
    RECT frameRect{ 0, 0, clientW, clientH };
    AdjustWindowRectEx(&frameRect, style, FALSE, exStyle);
    const int winW = frameRect.right - frameRect.left;
    const int winH = frameRect.bottom - frameRect.top;

    int x = (GetSystemMetrics(SM_CXSCREEN) - winW) / 2;
    int y = (GetSystemMetrics(SM_CYSCREEN) - winH) / 2;

    HWND hwnd = CreateWindowExW(exStyle, cls, gText->title, style,
                                x, y, winW, winH, nullptr, nullptr, inst, &state);
    if (!hwnd) return 1;

    // Dark caption bar on Windows 10 1809+ / 11. Failure is fine: the frame stays light
    // and the client area — the part that matters — is already dark.
    BOOL darkTitle = TRUE;
    DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */,
                          &darkTitle, sizeof(darkTitle));

    // Created at 0x0: ComputeLayout owns every position and runs right after.
    HWND heading = CreateWindowExW(0, L"STATIC", gText->title,
                                   WS_CHILD | WS_VISIBLE | SS_LEFT,
                                   0, 0, 0, 0, hwnd, (HMENU)(INT_PTR)kIdHeading, inst, nullptr);

    HWND intro = CreateWindowExW(0, L"STATIC", gText->intro,
                                 WS_CHILD | WS_VISIBLE | SS_LEFT,
                                 0, 0, 0, 0, hwnd, (HMENU)(INT_PTR)kIdIntro, inst, nullptr);

    HWND sumType = CreateWindowExW(0, L"STATIC", state.summary.type.c_str(),
                                   WS_CHILD | WS_VISIBLE | SS_LEFT | SS_ENDELLIPSIS,
                                   0, 0, 0, 0, hwnd, (HMENU)(INT_PTR)kIdSumType, inst, nullptr);

    HWND sumMsg = CreateWindowExW(0, L"STATIC", state.summary.message.c_str(),
                                  WS_CHILD | WS_VISIBLE | SS_LEFT,
                                  0, 0, 0, 0, hwnd, (HMENU)(INT_PTR)kIdSumMsg, inst, nullptr);

    // No WS_EX_CLIENTEDGE: the light 3D border fights the dark card, so the edit sits
    // flush inside the rounded card and the card border does the framing.
    // No WS_HSCROLL: wrapped text reads better inside a card, and stack-trace
    // indentation survives wrapping because the font is monospace.
    HWND edit = CreateWindowExW(0, L"EDIT", state.content.c_str(),
                                WS_CHILD | WS_VISIBLE | WS_VSCROLL |
                                ES_MULTILINE | ES_READONLY | ES_AUTOVSCROLL | ES_NOHIDESEL,
                                0, 0, 0, 0, hwnd, (HMENU)(INT_PTR)kIdEdit, inst, nullptr);

    // Dark scrollbar on Windows 10 1809+. Harmless no-op elsewhere.
    SetWindowTheme(edit, L"DarkMode_Explorer", nullptr);

    SendMessageW(heading, WM_SETFONT, (WPARAM)state.heading, TRUE);
    SendMessageW(intro, WM_SETFONT, (WPARAM)state.subtitle, TRUE);
    SendMessageW(sumType, WM_SETFONT, (WPARAM)state.chip, TRUE);
    SendMessageW(sumMsg, WM_SETFONT, (WPARAM)state.body, TRUE);
    SendMessageW(edit, WM_SETFONT, (WPARAM)state.mono, TRUE);

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
                                   0, 0, 0, 0, hwnd, (HMENU)(INT_PTR)b.id, inst, nullptr);
        SendMessageW(btn, WM_SETFONT, (WPARAM)state.button, TRUE);
    }

    ComputeLayout(hwnd);

    ShowWindow(hwnd, SW_SHOW);
    UpdateWindow(hwnd);
    SetFocus(edit);

    // Dump AFTER ShowWindow: IsWindowVisible reports false for every child while the
    // parent is hidden, which made an earlier run look like nothing had been created.
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

    DeleteObject(state.heading);
    DeleteObject(state.subtitle);
    DeleteObject(state.section);
    DeleteObject(state.chip);
    DeleteObject(state.body);
    DeleteObject(state.mono);
    DeleteObject(state.button);
    if (gBgBrush) DeleteObject(gBgBrush);
    if (gCardBrush) DeleteObject(gCardBrush);
    return 0;
}
