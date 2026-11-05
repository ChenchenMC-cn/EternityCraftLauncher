// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ECL.Services;

/// <summary>
/// A tiny Yggdrasil-compatible skin server on 127.0.0.1, used to make a local PNG
/// show up in-game.
///
/// <para>Why this is necessary: Minecraft does not read skin files from disk. It asks
/// <c>sessionserver.mojang.com</c> for a <c>textures</c> property, and that property is
/// **signed** — the client verifies it against the signature public key the API
/// advertises. Offline accounts have no Mojang-side textures at all, so the only way to
/// give them a skin without a mod is to stand in for that server: advertise our own key,
/// sign our own textures, and point <c>authlib-injector</c> at us.</para>
///
/// <para>Endpoints served:</para>
/// <list type="bullet">
/// <item><c>GET /</c> — metadata with <c>signaturePublickey</c> and <c>skinDomains</c></item>
/// <item><c>GET /sessionserver/session/minecraft/profile/&lt;uuid&gt;</c> — signed textures</item>
/// <item><c>GET /textures/skin.png</c>, <c>/textures/cape.png</c> — the actual images</item>
/// </list>
///
/// <para>Written against <see cref="TcpListener"/> rather than HttpListener on purpose:
/// HttpListener needs a URL ACL reservation on some machines, which would mean asking
/// the user to run netsh as administrator just to see their skin.</para>
/// </summary>
public sealed class LocalSkinServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly RSA _rsa;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();

    private string _uuidHex = "";
    private string _playerName = "Player";
    private byte[] _skinPng = Array.Empty<byte>();
    private byte[]? _capePng;
    private bool _slim;

    public int Port { get; }

    /// <summary>Value to hand to authlib-injector as the API root.</summary>
    public string ApiRoot { get; }

    public LocalSkinServer(string keyPath)
    {
        _rsa = LoadOrCreateKey(keyPath);

        // Port 0 lets the OS pick a free one; the port is only ever read back, never
        // assumed, because two launchers or a restart could otherwise collide.
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        ApiRoot = $"http://127.0.0.1:{Port}";
    }

    /// <summary>
    /// Sets the skin to serve. Safe to call while requests are in flight: reads take
    /// the same lock before touching the buffers.
    /// </summary>
    public void SetSkin(string uuidHex, string playerName, byte[] png, bool slim, byte[]? cape)
    {
        lock (_gate)
        {
            _uuidHex = NormaliseUuid(uuidHex);
            _playerName = string.IsNullOrWhiteSpace(playerName) ? "Player" : playerName;
            _skinPng = png ?? Array.Empty<byte>();
            _slim = slim;
            _capePng = cape;
        }
    }

    public void Start()
    {
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                // Transient; keep serving.
                continue;
            }

            _ = Task.Run(() => HandleAsync(client, ct), ct);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 5000;
                client.SendTimeout = 5000;

                using var stream = client.GetStream();

                string? requestLine = await ReadRequestLineAsync(stream, ct).ConfigureAwait(false);
                if (requestLine == null) return;

                // "GET /path?query HTTP/1.1"
                string[] parts = requestLine.Split(' ');
                if (parts.Length < 2) return;

                string path = parts[1];
                int query = path.IndexOf('?');
                if (query >= 0) path = path[..query];

                var (status, contentType, body) = Route(path);
                await WriteResponseAsync(stream, status, contentType, body, ct).ConfigureAwait(false);
            }
            catch
            {
                // One bad request must not take the listener down.
            }
        }
    }

    private static async Task<string?> ReadRequestLineAsync(NetworkStream stream, CancellationToken ct)
    {
        // The whole header block must be consumed before replying. Returning as soon
        // as the request line is parsed leaves the client still writing its headers;
        // writing a response at that point makes the kernel reset the connection and
        // the caller sees "Error while copying content to a stream".
        var buffer = new byte[2048];
        var accumulated = new StringBuilder();

        while (accumulated.Length < 16384)
        {
            int read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0) break;

            accumulated.Append(Encoding.ASCII.GetString(buffer, 0, read));

            // Header block terminator: an empty line.
            if (accumulated.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal) >= 0)
                break;
        }

        string text = accumulated.ToString();
        int lineEnd = text.IndexOf("\r\n", StringComparison.Ordinal);
        if (lineEnd <= 0) return null;

        string requestLine = text[..lineEnd];
        return requestLine.StartsWith("GET ", StringComparison.Ordinal) ||
               requestLine.StartsWith("HEAD ", StringComparison.Ordinal)
            ? requestLine
            : null;
    }

    private (int Status, string ContentType, byte[] Body) Route(string path)
    {
        // 1) API metadata: the client reads the public key from here before it trusts
        //    anything else we say.
        if (path == "/" || path.Length == 0)
        {
            string json =
                "{\"signaturePublickey\":" + JsonStr(_rsa.ExportSubjectPublicKeyInfoPem()) + "," +
                "\"skinDomains\":[\"127.0.0.1\",\"localhost\"]," +
                "\"meta\":{\"serverName\":\"ECL Local Skins\",\"implementationName\":\"ECL\",\"implementationVersion\":\"1.0\"}}";
            return (200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
        }

        // 2) profile -> signed textures
        const string profilePrefix = "/sessionserver/session/minecraft/profile/";
        if (path.StartsWith(profilePrefix, StringComparison.OrdinalIgnoreCase))
        {
            string requested = NormaliseUuid(path[profilePrefix.Length..]);
            byte[] json = BuildProfileJson(requested);
            return (200, "application/json; charset=utf-8", json);
        }

        // 3) the images themselves
        if (path.Equals("/textures/skin.png", StringComparison.OrdinalIgnoreCase))
        {
            lock (_gate) return (200, "image/png", _skinPng);
        }

        if (path.Equals("/textures/cape.png", StringComparison.OrdinalIgnoreCase))
        {
            lock (_gate)
            {
                return _capePng == null
                    ? (404, "text/plain", Array.Empty<byte>())
                    : (200, "image/png", _capePng);
            }
        }

        return (404, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("not found"));
    }

    private byte[] BuildProfileJson(string requestedUuid)
    {
        string uuidHex;
        string name;
        byte[] skinBytes;
        bool slim;
        bool hasCape;

        lock (_gate)
        {
            uuidHex = string.IsNullOrEmpty(_uuidHex) ? requestedUuid : _uuidHex;
            name = _playerName;
            skinBytes = _skinPng;
            slim = _slim;
            hasCape = _capePng != null;
        }

        // A profile is useless without a skin; report 404 rather than serve empty.
        if (skinBytes.Length == 0)
            return Encoding.UTF8.GetBytes("{}");

        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var textures = new StringBuilder();
        textures.Append("\"SKIN\":{\"url\":\"");
        textures.Append(ApiRoot);
        textures.Append("/textures/skin.png\"");
        if (slim) textures.Append(",\"metadata\":{\"model\":\"slim\"}");
        textures.Append('}');
        if (hasCape)
        {
            textures.Append(",\"CAPE\":{\"url\":\"");
            textures.Append(ApiRoot);
            textures.Append("/textures/cape.png\"}");
        }

        string texturesJson =
            "{\"timestamp\":" + timestamp + "," +
            "\"profileId\":\"" + uuidHex + "\"," +
            "\"profileName\":" + JsonStr(name) + "," +
            "\"textures\":{" + textures + "}}";

        string valueBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(texturesJson));

        // The signature covers the *base64 text*, not the raw JSON — that is what the
        // client verifies, and getting it backwards yields a silently ignored skin.
        byte[] signature = _rsa.SignData(
            Encoding.ASCII.GetBytes(valueBase64),
            HashAlgorithmName.SHA1,
            RSASignaturePadding.Pkcs1);

        string json =
            "{\"id\":\"" + uuidHex + "\"," +
            "\"name\":" + JsonStr(name) + "," +
            "\"properties\":[{\"name\":\"textures\"," +
            "\"value\":\"" + valueBase64 + "\"," +
            "\"signature\":\"" + Convert.ToBase64String(signature) + "\"}]}";

        return Encoding.UTF8.GetBytes(json);
    }

    private static async Task WriteResponseAsync(NetworkStream stream, int status,
        string contentType, byte[] body, CancellationToken ct)
    {
        string reason = status switch
        {
            200 => "OK",
            404 => "Not Found",
            _ => "OK"
        };

        var head = new StringBuilder();
        head.Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n");
        head.Append("Content-Type: ").Append(contentType).Append("\r\n");
        head.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        head.Append("Cache-Control: no-store\r\n");
        head.Append("Connection: close\r\n");
        head.Append("\r\n");

        byte[] headBytes = Encoding.ASCII.GetBytes(head.ToString());
        await stream.WriteAsync(headBytes, ct).ConfigureAwait(false);
        if (body.Length > 0)
            await stream.WriteAsync(body, ct).ConfigureAwait(false);

        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    // --- helpers -----------------------------------------------------------

    /// <summary>Drops dashes and lowercases; the client sends the dashed form.</summary>
    private static string NormaliseUuid(string uuid)
    {
        if (string.IsNullOrWhiteSpace(uuid)) return "";
        return uuid.Replace("-", "").Trim().ToLowerInvariant();
    }

    private static string JsonStr(string value)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    /// <summary>
    /// Reuses the key across runs. Regenerating would invalidate any cached trust the
    /// client established, so the PEM is persisted next to the skins.
    /// </summary>
    private static RSA LoadOrCreateKey(string keyPath)
    {
        var rsa = RSA.Create(2048);

        try
        {
            if (File.Exists(keyPath))
            {
                rsa.ImportFromPem(File.ReadAllText(keyPath));
                return rsa;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
            File.WriteAllText(keyPath, rsa.ExportRSAPrivateKeyPem());
        }
        catch
        {
            // Unreadable or corrupt key file: fall through with the fresh key. The
            // worst case is that the client re-fetches our public key.
        }

        return rsa;
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
        _rsa.Dispose();
        _cts.Dispose();
    }
}
