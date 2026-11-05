// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
// ECL.Native - Eternity Craft Launcher native core
// Flat C ABI consumed by the C# host through P/Invoke.
#pragma once

#include <windows.h>

#ifdef __cplusplus
extern "C" {
#endif

#define ECL_API __declspec(dllexport)

#define ECL_OK               0
#define ECL_ERR_ARG          1
#define ECL_ERR_IO           2
#define ECL_ERR_NET          3
#define ECL_ERR_HASH         4
#define ECL_ERR_ZIP          5
#define ECL_ERR_CANCEL       6

// Algo ids
#define ECL_ALGO_SHA1   1
#define ECL_ALGO_SHA256 2

// Progress callback. Counters are monotonically increasing, sampled at most
// ~10 times per second to keep the UI thread cheap.
//   filesDone / filesTotal : whole batch
//   bytesDone / bytesTotal : whole batch (may be 0 when size is unknown)
#define ECL_CB_PROGRESS 1

typedef void (__stdcall *ECL_PROGRESS_CB)(int filesDone, int filesTotal,
                                          long long bytesDone, long long bytesTotal,
                                          void* user);

ECL_API const char* __stdcall ECL_Version(void);

// Hashes a single file. outHex must hold algoLen*2+1 bytes (41 for SHA1).
ECL_API int __stdcall ECL_HashFile(const wchar_t* path, int algo, char* outHex, int outHexLen);

// Hashes many files on a worker pool. outHexes is a caller allocated flat buffer of
// count*(hexLen+1) chars; entry i starts at i*(hexLen+1). Unreadable files get "".
ECL_API int __stdcall ECL_HashFiles(const wchar_t* const* paths, int count, int algo,
                                    char* outHexes, int outHexLen,
                                    ECL_PROGRESS_CB cb, void* user, int threads);

// Extracts a .zip into destDir, creating sub directories. Entries whose name
// contains "..\\" or starts with '/' are rejected (zip-slip guard).
ECL_API int __stdcall ECL_Unzip(const wchar_t* zipPath, const wchar_t* destDir,
                                ECL_PROGRESS_CB cb, void* user);

// Downloads files in parallel. A destination that already matches expectedSize
// (>0) is skipped and reported as a hit. Files are written to "<dest>.part" and
// moved into place only after the transfer completed.
ECL_API int __stdcall ECL_DownloadBatch(const wchar_t* const* urls,
                                        const wchar_t* const* dests,
                                        const long long* expectedSizes,
                                        int count, int threads,
                                        ECL_PROGRESS_CB cb, void* user);

// Requests an in-flight batch to stop. Safe to call from another thread.
ECL_API void __stdcall ECL_Cancel(void);

// Returns how many files of the last batch were served from cache (no transfer).
ECL_API int __stdcall ECL_LastCacheHits(void);

// Fills a buffer with a short human readable error report of the last failure.
ECL_API int __stdcall ECL_LastError(wchar_t* buf, int bufLen);

#ifdef __cplusplus
}
#endif
