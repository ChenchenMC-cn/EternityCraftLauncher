// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace ECL.Models;

/// <summary>
/// One skin the user can wear. <see cref="SkinPath"/> is always a local PNG — the
/// game never sees a remote URL, everything funnels through the local skin server.
/// </summary>
/// <param name="Source">mojang / littleskin / local</param>
/// <param name="Label">Shown in the picker, e.g. "正版 · ChenchenMC"</param>
/// <param name="Slim">4px arms (a.k.a. Alex model) instead of the classic 4px-wide body</param>
public sealed record SkinInfo(
    string Source,
    string Label,
    bool Slim,
    string? SkinPath,
    string? CapePath);

/// <summary>
/// Fetches skins from the three places they can come from and normalises them into
/// local PNG files under <c>&lt;dataRoot&gt;\skins\</c>.
///
/// <list type="bullet">
/// <item><b>Mojang</b> — the account's own skin and cape, straight from the profile
/// endpoint. Requires a valid Minecraft access token.</item>
/// <item><b>LittleSkin</b> — a third-party skin site with a Yggdrasil-compatible API.
/// Looked up by user name so no LittleSkin account is needed.</item>
/// <item><b>Local</b> — a PNG the user picked. Validated for size because Minecraft
/// rejects anything that is not 64x64 / 64x32.</item>
/// </list>
/// </summary>
public sealed class SkinService : IDisposable
{
    private const string MojangProfile = "https://api.minecraftservices.com/minecraft/profile";
    private const string LittleSkinRoot = "https://littleskin.cn/api/yggdrasil";

    private readonly HttpClient _http;
    private readonly string _dir;

    public SkinService(string dataRoot)
    {
        _dir = Path.Combine(dataRoot, "skins");
        Directory.CreateDirectory(_dir);

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("EternityCraftLauncher/1.0");
    }

    /// <summary>Folder holding every cached skin PNG. Named SkinDir because a
    /// property called Directory shadows System.IO.Directory inside this class.</summary>
    public string SkinDir => _dir;

    /// <summary>
    /// The account's current skin + cape. Returns null when the profile has no skin
    /// (possible: a brand new account that never uploaded one).
    /// </summary>
    public async Task<SkinInfo?> FromMojangAsync(string accessToken, string accountName,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(accessToken)) return null;

        using var req = new HttpRequestMessage(HttpMethod.Get, MojangProfile);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"读取正版皮肤失败（HTTP {(int)resp.StatusCode}），登录可能已过期。");

        string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;

        if (!root.TryGetProperty("skins", out var skins) || skins.GetArrayLength() == 0)
            return null;

        var skin = skins[0];
        string url = skin.GetProperty("url").GetString() ?? "";
        bool slim = string.Equals(
            skin.TryGetProperty("variant", out var v) ? v.GetString() : null,
            "SLIM", StringComparison.OrdinalIgnoreCase);

        string? capeUrl = null;
        if (root.TryGetProperty("capes", out var capes) && capes.GetArrayLength() > 0)
            capeUrl = capes[0].GetProperty("url").GetString();

        string? skinPath = await DownloadAsync(url, "mojang-skin.png", ct).ConfigureAwait(false);
        string? capePath = capeUrl == null
            ? null
            : await DownloadAsync(capeUrl, "mojang-cape.png", ct).ConfigureAwait(false);

        return new SkinInfo("mojang", $"正版 · {accountName}", slim, skinPath, capePath);
    }

    /// <summary>
    /// Looks a LittleSkin user up by name. Two hops: name → uuid, uuid → signed
    /// textures. No LittleSkin account required, this is all public data.
    /// </summary>
    public async Task<SkinInfo?> FromLittleSkinAsync(string userName, CancellationToken ct)
    {
        string name = (userName ?? "").Trim();
        if (name.Length == 0)
            throw new InvalidOperationException("请输入 LittleSkin 的用户名。");

        // 1) name -> { id, name }
        string lookupUrl = $"{LittleSkinRoot}/api/users/profiles/minecraft/{Uri.EscapeDataString(name)}";
        using var lookupResp = await _http.GetAsync(lookupUrl, ct).ConfigureAwait(false);
        if (lookupResp.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException($"LittleSkin 上没有叫「{name}」的用户。");
        if (!lookupResp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"查询 LittleSkin 用户失败（HTTP {(int)lookupResp.StatusCode}）。");

        string lookupText = await lookupResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        string uuid;
        using (var lookupDoc = JsonDocument.Parse(lookupText))
        {
            uuid = lookupDoc.RootElement.GetProperty("id").GetString()
                   ?? throw new InvalidOperationException("LittleSkin 返回的用户信息里没有 id。");
        }

        // 2) uuid -> profile with base64-encoded textures property
        string profileUrl = $"{LittleSkinRoot}/sessionserver/session/minecraft/profile/{uuid}";
        using var profileResp = await _http.GetAsync(profileUrl, ct).ConfigureAwait(false);
        if (!profileResp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"读取 LittleSkin 皮肤失败（HTTP {(int)profileResp.StatusCode}）。");

        string profileText = await profileResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return await ParseTexturesPropertyAsync(profileText, "littleskin", $"LittleSkin · {name}", ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Validates and copies a PNG the user picked. Rejects anything Minecraft would
    /// refuse to render, with the actual dimensions in the message.
    /// </summary>
    public SkinInfo FromFile(string pngPath, bool slim)
    {
        if (!File.Exists(pngPath))
            throw new FileNotFoundException("找不到这个图片。", pngPath);

        int width, height;
        try
        {
            using var stream = File.OpenRead(pngPath);
            var decoder = BitmapDecoder.Create(stream,
                BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            width = decoder.Frames[0].PixelWidth;
            height = decoder.Frames[0].PixelHeight;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"「{Path.GetFileName(pngPath)}」不是有效的 PNG：" + ex.Message);
        }

        // Minecraft only accepts these two layouts. 64x64 is the modern format (with
        // the second layer); 64x32 is the pre-1.8 one and still works.
        bool ok = (width == 64 && height == 64) || (width == 64 && height == 32);
        if (!ok)
            throw new InvalidOperationException(
                $"皮肤必须是 64×64 或 64×32 像素，这张是 {width}×{height}。");

        string dest = Path.Combine(_dir, "local-skin.png");
        File.Copy(pngPath, dest, overwrite: true);

        return new SkinInfo("local", $"本地 · {Path.GetFileName(pngPath)}", slim, dest, null);
    }

    /// <summary>
    /// Validates a cape PNG the user picked and attaches it to the current skin.
    /// Modern capes are 64x32; the pre-1.9 ones are 22x17 and still render.
    /// </summary>
    public SkinInfo? FromCapeFile(string pngPath, SkinInfo? current)
    {
        if (!File.Exists(pngPath))
            throw new FileNotFoundException("找不到这个图片。", pngPath);

        int width, height;
        try
        {
            using var stream = File.OpenRead(pngPath);
            var decoder = BitmapDecoder.Create(stream,
                BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            width = decoder.Frames[0].PixelWidth;
            height = decoder.Frames[0].PixelHeight;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"「{Path.GetFileName(pngPath)}」不是有效的 PNG：" + ex.Message);
        }

        bool ok = (width == 64 && height == 32) || (width == 22 && height == 17) || (width == 64 && height == 64);
        if (!ok)
            throw new InvalidOperationException(
                $"披风必须是 64×32（或 22×17 老格式）像素，这张是 {width}×{height}。");

        string dest = Path.Combine(_dir, "local-cape.png");
        File.Copy(pngPath, dest, overwrite: true);

        // A cape needs a skin to hang on; keep whatever is currently worn.
        if (current?.SkinPath != null)
        {
            return current with
            {
                CapePath = dest,
                Label = current.Label.Contains("+ 披风", StringComparison.Ordinal)
                    ? current.Label
                    : current.Label + " + 披风"
            };
        }

        return new SkinInfo("local", $"披风 · {Path.GetFileName(pngPath)}", false, null, dest);
    }

    /// <summary>Loads the skin saved from a previous session, if any.</summary>
    public SkinInfo? LoadSaved()
    {
        string meta = Path.Combine(_dir, "current.json");
        if (!File.Exists(meta)) return null;

        try
        {
            var info = JsonSerializer.Deserialize<SkinInfo>(File.ReadAllText(meta));
            if (info?.SkinPath != null && File.Exists(info.SkinPath)) return info;
        }
        catch
        {
            // Corrupt metadata is not worth failing start-up over.
        }

        return null;
    }

    /// <summary>Remembers the choice so the next launch injects the same skin.</summary>
    public void SaveCurrent(SkinInfo? info)
    {
        try
        {
            string meta = Path.Combine(_dir, "current.json");
            if (info == null)
            {
                if (File.Exists(meta)) File.Delete(meta);
                return;
            }

            File.WriteAllText(meta, JsonSerializer.Serialize(info,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best effort: worst case the picker starts empty next time.
        }
    }

    // --- helpers -----------------------------------------------------------

    private async Task<SkinInfo?> ParseTexturesPropertyAsync(string profileJson, string source,
        string label, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(profileJson);
        if (!doc.RootElement.TryGetProperty("properties", out var props)) return null;

        foreach (var prop in props.EnumerateArray())
        {
            if (!string.Equals(prop.GetProperty("name").GetString(), "textures",
                    StringComparison.OrdinalIgnoreCase))
                continue;

            string b64 = prop.GetProperty("value").GetString() ?? "";
            string decoded;
            try
            {
                decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64));
            }
            catch
            {
                return null;
            }

            using var texDoc = JsonDocument.Parse(decoded);
            if (!texDoc.RootElement.TryGetProperty("textures", out var textures)) return null;

            string? skinUrl = null, capeUrl = null;
            bool slim = false;

            if (textures.TryGetProperty("SKIN", out var skin))
            {
                skinUrl = skin.GetProperty("url").GetString();
                slim = skin.TryGetProperty("metadata", out var md) &&
                       md.TryGetProperty("model", out var model) &&
                       string.Equals(model.GetString(), "slim", StringComparison.OrdinalIgnoreCase);
            }

            if (textures.TryGetProperty("CAPE", out var cape))
                capeUrl = cape.GetProperty("url").GetString();

            if (skinUrl == null) return null;

            string? skinPath = await DownloadAsync(skinUrl, $"{source}-skin.png", ct)
                .ConfigureAwait(false);
            string? capePath = capeUrl == null
                ? null
                : await DownloadAsync(capeUrl, $"{source}-cape.png", ct).ConfigureAwait(false);

            return new SkinInfo(source, label, slim, skinPath, capePath);
        }

        return null;
    }

    private async Task<string?> DownloadAsync(string url, string fileName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        try
        {
            byte[] bytes = await _http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
            if (bytes.Length < 64) return null;   // not a real PNG

            string dest = Path.Combine(_dir, fileName);
            await File.WriteAllBytesAsync(dest, bytes, ct).ConfigureAwait(false);
            return dest;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
