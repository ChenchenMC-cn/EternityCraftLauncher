// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
// shot.exe — screenshots a window belonging to a freshly launched process.
//
// Why this exists: the PowerShell path needs Add-Type (blocked by policy on this
// machine) and the launcher's own --shot only captures WPF windows. A ~120-line native
// helper keeps visual checks scriptable for the reporter tools.
//
// Usage: shot.exe <out.bmp> <delay-ms> <exe> [args...]
//
// Writes a 32-bit BMP (no GDI+: nothing here should depend on extra runtime DLLs).

#ifndef UNICODE
#define UNICODE
#endif
#ifndef _UNICODE
#define _UNICODE
#endif

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <string>
#include <vector>
#include <cstdio>

namespace {

std::wstring Quote(const std::wstring& s)
{
    if (s.find(L' ') == std::wstring::npos) return s;
    return L"\"" + s + L"\"";
}

bool SaveBmp(const std::wstring& path, int w, int h, const void* bits, int stride)
{
    BITMAPFILEHEADER fh{};
    BITMAPINFOHEADER ih{};
    ih.biSize = sizeof(ih);
    ih.biWidth = w;
    ih.biHeight = -h;          // top-down
    ih.biPlanes = 1;
    ih.biBitCount = 32;
    ih.biCompression = BI_RGB;
    ih.biSizeImage = stride * h;

    fh.bfType = 0x4D42;        // 'BM'
    fh.bfOffBits = sizeof(fh) + sizeof(ih);
    fh.bfSize = fh.bfOffBits + ih.biSizeImage;

    FILE* f = nullptr;
    if (_wfopen_s(&f, path.c_str(), L"wb") != 0 || !f) return false;

    fwrite(&fh, sizeof(fh), 1, f);
    fwrite(&ih, sizeof(ih), 1, f);
    fwrite(bits, ih.biSizeImage, 1, f);
    fclose(f);
    return true;
}

} // namespace

int wmain(int argc, wchar_t** argv)
{
    if (argc < 4)
    {
        wprintf(L"usage: shot.exe <out.bmp> <delay-ms> <exe> [args...]\n");
        return 2;
    }

    std::wstring out = argv[1];
    int delay = _wtoi(argv[2]);
    std::wstring exe = argv[3];

    std::wstring cmd = Quote(exe);
    for (int i = 4; i < argc; ++i) cmd += L" " + Quote(argv[i]);

    STARTUPINFOW si{};
    si.cb = sizeof(si);
    PROCESS_INFORMATION pi{};
    std::vector<wchar_t> mutableCmd(cmd.begin(), cmd.end());
    mutableCmd.push_back(L'\0');

    if (!CreateProcessW(nullptr, mutableCmd.data(), nullptr, nullptr, FALSE, 0,
                        nullptr, nullptr, &si, &pi))
    {
        wprintf(L"CreateProcess failed: %lu\n", GetLastError());
        return 1;
    }

    // Wait for a top-level window owned by that process (the launcher/reporter shows
    // exactly one), then let it finish painting before capturing.
    HWND target = nullptr;
    for (int i = 0; i < 100 && !target; ++i)
    {
        Sleep(100);
        struct Ctx { DWORD pid; HWND found; } ctx{ pi.dwProcessId, nullptr };
        EnumWindows([](HWND h, LPARAM p) -> BOOL
        {
            auto* c = reinterpret_cast<Ctx*>(p);
            DWORD pid = 0;
            GetWindowThreadProcessId(h, &pid);
            if (pid == c->pid && IsWindowVisible(h))
            {
                c->found = h;
                return FALSE;
            }
            return TRUE;
        }, reinterpret_cast<LPARAM>(&ctx));
        target = ctx.found;
    }

    if (!target)
    {
        wprintf(L"no window appeared\n");
        TerminateProcess(pi.hProcess, 1);
        return 1;
    }

    SetForegroundWindow(target);
    Sleep(delay);              // allow the first paint + layout to settle

    RECT rc{};
    GetWindowRect(target, &rc);
    const int w = rc.right - rc.left;
    const int h = rc.bottom - rc.top;
    if (w <= 0 || h <= 0)
    {
        wprintf(L"bad window rect\n");
        TerminateProcess(pi.hProcess, 1);
        return 1;
    }

    HDC screen = GetDC(nullptr);
    HDC mem = CreateCompatibleDC(screen);
    HBITMAP bmp = CreateCompatibleBitmap(screen, w, h);
    HGDIOBJ old = SelectObject(mem, bmp);

    BOOL ok = BitBlt(mem, 0, 0, w, h, screen, rc.left, rc.top, SRCCOPY);

    // Pull the pixels out as 32bpp top-down.
    BITMAPINFO bi{};
    bi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    bi.bmiHeader.biWidth = w;
    bi.bmiHeader.biHeight = -h;
    bi.bmiHeader.biPlanes = 1;
    bi.bmiHeader.biBitCount = 32;
    bi.bmiHeader.biCompression = BI_RGB;

    std::vector<unsigned char> pixels((size_t)w * h * 4);
    GetDIBits(mem, bmp, 0, (UINT)h, pixels.data(), &bi, DIB_RGB_COLORS);

    SelectObject(mem, old);
    DeleteObject(bmp);
    DeleteDC(mem);
    ReleaseDC(nullptr, screen);

    bool saved = ok && SaveBmp(out, w, h, pixels.data(), w * 4);
    wprintf(L"%s %dx%d -> %s\n", saved ? L"captured" : L"FAILED", w, h, out.c_str());

    TerminateProcess(pi.hProcess, 0);
    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);
    return saved ? 0 : 1;
}
