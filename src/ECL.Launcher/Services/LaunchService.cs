// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using ECL.Models;

namespace ECL.Services;

/// <summary>
/// Builds the java command line for a (possibly modded) version and starts the
/// game as a child process. Argument substitution follows the vanilla launcher
/// rules, and accounts map onto either <c>legacy</c> (offline) or <c>msa</c>
/// (Microsoft) user types.
/// </summary>
public sealed class LaunchService
{
    private readonly LauncherSettings _settings;
    private readonly GamePaths _paths;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public LaunchService(LauncherSettings settings, GamePaths paths)
    {
        _settings = settings;
        _paths = paths;
    }

    /// <param name="SkinAgent">
    /// Optional <c>-javaagent:authlib-injector.jar=...</c> produced when the user has
    /// picked a custom skin. Null means "no skin override", which is the common case.
    /// </param>
    public sealed record LaunchRequest(string VersionId, ResolvedVersion Version, Account Account,
        JavaRuntime Java, string? SkinAgent = null);

    private string? _runtimeJar;
    private bool _runtimeJarChecked;

    /// <summary>
    /// Path to <c>ECL.Runtime.jar</c>, or null when the bootstrap is unavailable.
    /// A normal build keeps the jar next to the exe; a single-file publish embeds
    /// it, so the copy is extracted to <c>cache/</c> once and reused afterwards.
    /// </summary>
    private string? EnsureRuntimeJar()
    {
        if (_runtimeJarChecked) return _runtimeJar;
        _runtimeJarChecked = true;

        string beside = Path.Combine(AppContext.BaseDirectory, "ECL.Runtime.jar");
        if (File.Exists(beside))
        {
            _runtimeJar = beside;
            return _runtimeJar;
        }

        string cached = Path.Combine(_paths.Root, "cache", "ECL.Runtime.jar");
        try
        {
            using var res = typeof(LaunchService).Assembly
                .GetManifestResourceStream("ECL.Assets.ECL.Runtime.jar");

            if (res == null)
            {
                // No embedded copy (jar was missing at build time); use a stale one
                // only if it happens to be complete.
                _runtimeJar = File.Exists(cached) && new FileInfo(cached).Length > 1024 ? cached : null;
                return _runtimeJar;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(cached)!);

            // Size match means a previous extraction finished; otherwise overwrite.
            if (!File.Exists(cached) || new FileInfo(cached).Length != res.Length)
            {
                using var fs = File.Create(cached);
                res.CopyTo(fs);
            }

            _runtimeJar = cached;
            return _runtimeJar;
        }
        catch
        {
            _runtimeJar = File.Exists(cached) && new FileInfo(cached).Length > 1024 ? cached : null;
            return _runtimeJar;
        }
    }

    /// <summary>
    /// Assembles the full java argument vector without starting anything. Kept
    /// separate from <see cref="Launch"/> so the smoke test can diff the command
    /// line against the vanilla launcher output.
    /// </summary>
    public List<string> BuildArguments(LaunchRequest req)
    {
        var version = req.Version;
        var detail = version.Detail;
        var account = req.Account;
        string versionId = req.VersionId;
        string nativesDir = _paths.VersionNatives(versionId);
        string classpath = BuildClasspath(version);

        bool msa = account.IsMicrosoft && !string.IsNullOrEmpty(account.AccessToken);

        var vars = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["auth_player_name"] = account.Name,
            ["version_name"] = versionId,
            ["game_directory"] = _paths.GameDir(versionId),
            ["assets_root"] = _paths.AssetsDir,
            ["game_assets"] = Path.Combine(_paths.AssetsDir, "virtual", "legacy"),
            // Legacy Forge profiles (1.7.x) write assets:"1.7.10" while the real
            // index lives under assetIndex.id ("legacy"); the index id always wins.
            ["assets_index_name"] = detail.AssetIndex?.Id ?? detail.Assets ?? "legacy",
            ["auth_uuid"] = account.Uuid,
            ["auth_access_token"] = msa ? account.AccessToken! : "0",
            ["auth_session"] = msa ? "token:" + account.AccessToken + ":" + account.Uuid : "-",
            ["clientid"] = "",
            ["auth_xuid"] = account.Xuid ?? "",
            ["user_type"] = msa ? "msa" : "legacy",
            ["user_properties"] = "{}",
            ["version_type"] = detail.Type ?? "release",
            ["natives_directory"] = nativesDir,
            ["launcher_name"] = "ECL",
            ["launcher_version"] = AppInfo.VersionNumber,
            ["classpath"] = classpath,
            ["classpath_separator"] = ";",
            ["library_directory"] = _paths.LibrariesDir,
            ["resolution_width"] = "1280",
            ["resolution_height"] = "720",
            ["quickPlayPath"] = "",
            ["quickPlaySingleplayer"] = "",
            ["quickPlayMultiplayer"] = "",
            ["quickPlayRealms"] = ""
        };

        // Log4j configuration shipped as an asset (1.12+). Optional: the game
        // still runs with the default console appender when it is missing.
        if (detail.Logging?.Client is { File: { } lf } lc && !string.IsNullOrEmpty(lc.Argument))
        {
            string logConfig = Path.Combine(_paths.AssetsDir, "log_configs", lf.Id ?? "client");
            if (File.Exists(logConfig))
                vars["path"] = logConfig;
        }

        var args = new List<string>(96);
        args.Add($"-Xmx{_settings.MemoryMb}M");
        args.Add($"-Xms{Math.Min(_settings.MemoryMb, 1024)}M");
        args.Add("-Dfile.encoding=UTF-8");
        args.Add("-Dstdout.encoding=UTF-8");
        args.Add("-Dstderr.encoding=UTF-8");
        args.Add("-Dminecraft.launcher.brand=ECL");
        // Numeric form: this is consumed by mods and crash reports, not by people.
        args.Add($"-Dminecraft.launcher.version={AppInfo.VersionNumber}");

        if (detail.Arguments?.Jvm is { Count: > 0 } jvmArgs)
        {
            args.AddRange(Evaluate(jvmArgs, vars, keepEmpty: false));
        }
        else
        {
            // Pre-1.13 profiles carry no jvm block; supply the essentials.
            // The skin agent is appended once, further down, for both branches.
            args.Add("-Djava.library.path=" + nativesDir);
            args.Add("-cp");
            args.Add(classpath);
        }

        // The log4j configuration lives outside arguments.jvm (logging.client),
        // so it has to be appended by hand or the game falls back to a default
        // appender and version-specific log filters stop working.
        if (detail.Logging?.Client is { Argument: { Length: > 0 } logArg } &&
            vars.TryGetValue("path", out var logPath))
        {
            _ = logPath;
            args.Add(Expand(logArg, vars));
        }

        // Skin override goes in as a JVM argument. Must be present before the main
        // class: authlib-injector is a java agent and is ignored if it comes after.
        if (!string.IsNullOrEmpty(req.SkinAgent)) args.Add(req.SkinAgent);

        if (!string.IsNullOrWhiteSpace(_settings.ExtraJvmArgs))
            args.AddRange(SplitArguments(_settings.ExtraJvmArgs));

        // Per-version arguments come last so they win over the global defaults
        // (later -XX flags override earlier ones in HotSpot).
        if (_settings.VersionJvmArgs.TryGetValue(versionId, out var perVersion) &&
            !string.IsNullOrWhiteSpace(perVersion))
        {
            args.AddRange(SplitArguments(perVersion));
        }

        string mainClass = detail.MainClass ?? "net.minecraft.client.main.Main";
        if (_settings.UseBootstrap && EnsureRuntimeJar() != null)
        {
            args.Add("ecl.runtime.Bootstrap");
            args.Add(mainClass);
        }
        else
        {
            args.Add(mainClass);
        }

        if (detail.Arguments?.Game is { Count: > 0 } gameArgs)
        {
            // Empty values are meaningful here: "--clientId \"\"" is legal and the
            // next flag must not be swallowed as its value.
            args.AddRange(Evaluate(gameArgs, vars, keepEmpty: true));
        }
        else if (!string.IsNullOrWhiteSpace(detail.MinecraftArguments))
        {
            foreach (var token in SplitArguments(detail.MinecraftArguments))
                args.Add(Expand(token, vars));
        }

        // The client has no --lang argument: passing one only produces
        // "Completely ignored arguments: [--lang, xx_xx]" in the log. The chosen
        // language is written into options.txt instead — see ApplyLanguage below.

        DedupeSystemProperties(args);
        return args;
    }

    /// <summary>
    /// Keeps only the last <c>-Dkey=value</c> per key. HotSpot lets the later one win,
    /// but the version json already expands <c>${launcher_version}</c> into its own
    /// <c>-Dminecraft.launcher.version</c>, so we ended up passing the flag twice with
    /// different values — and the stale second one (0.2.0) is what mods reported.
    /// </summary>
    private static void DedupeSystemProperties(List<string> args)
    {
        var lastIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < args.Count; i++)
        {
            string? key = SystemPropertyKey(args[i]);
            if (key != null) lastIndex[key] = i;
        }

        if (lastIndex.Count == 0) return;

        var drop = new HashSet<int>();
        for (int i = 0; i < args.Count; i++)
        {
            string? key = SystemPropertyKey(args[i]);
            if (key != null && lastIndex[key] != i) drop.Add(i);
        }

        if (drop.Count == 0) return;

        var kept = new List<string>(args.Count - drop.Count);
        for (int i = 0; i < args.Count; i++)
            if (!drop.Contains(i)) kept.Add(args[i]);

        args.Clear();
        args.AddRange(kept);
    }

    private static string? SystemPropertyKey(string arg)
    {
        if (!arg.StartsWith("-D", StringComparison.Ordinal)) return null;
        int eq = arg.IndexOf('=');
        return eq > 2 ? arg[..eq] : null;
    }

    /// <summary>
    /// Sets <c>lang:</c> in the version's options.txt.
    ///
    /// <para>This is the only way to choose the in-game language: the client reads it
    /// from options.txt and has no command-line equivalent (passing <c>--lang</c> just
    /// logs "Completely ignored arguments"). The file is edited line-by-line so every
    /// other option the player set is preserved.</para>
    /// </summary>
    private void ApplyLanguage(string versionId)
    {
        string gameDir = _paths.GameDir(versionId);
        string options = Path.Combine(gameDir, "options.txt");

        bool auto = string.IsNullOrWhiteSpace(_settings.GameLanguage) ||
                    _settings.GameLanguage.Equals(McLanguages.AutoCode, StringComparison.OrdinalIgnoreCase);

        try
        {
            if (auto)
            {
                // "auto" used to delete our lang: line and let the client decide — but
                // the client follows the *JVM* locale, not the Windows display language.
                // On a Chinese Windows with a JVM that defaults to English, the game then
                // came up in English, which is exactly the "自动没用" report.
                // So: write the detected language explicitly, and keep respecting a value
                // the player changed in-game themselves.
                string detected = McLanguages.DetectSystem();
                if (string.IsNullOrWhiteSpace(detected)) return;

                Directory.CreateDirectory(gameDir);

                var autoLines = File.Exists(options)
                    ? File.ReadAllLines(options).ToList()
                    : new List<string> { "version:3465" };

                string autoEntry = "lang:" + detected;
                int autoIndex = autoLines.FindIndex(l =>
                    l.StartsWith("lang:", StringComparison.OrdinalIgnoreCase));

                if (autoIndex >= 0)
                {
                    string existing = autoLines[autoIndex]["lang:".Length..].Trim();
                    bool ours = !string.IsNullOrEmpty(_settings.LastAppliedLanguage) &&
                                string.Equals(existing, _settings.LastAppliedLanguage,
                                              StringComparison.OrdinalIgnoreCase);

                    // Someone picked a language in-game; do not fight them.
                    if (!ours)
                    {
                        LastWarning = "游戏内语言已手动改过，自动模式不再覆盖（想恢复就把游戏内语言切回默认）";
                        return;
                    }

                    if (string.Equals(autoLines[autoIndex], autoEntry, StringComparison.Ordinal)) return;
                    autoLines[autoIndex] = autoEntry;
                }
                else
                {
                    autoLines.Add(autoEntry);
                }

                File.WriteAllLines(options, autoLines);
                _settings.LastAppliedLanguage = detected;
                try { _settings.Save(LauncherSettings.ResolveSettingsPath()); } catch { /* best effort */ }
                return;
            }

            string lang = McLanguages.Resolve(_settings.GameLanguage);
            if (string.IsNullOrWhiteSpace(lang)) return;

            Directory.CreateDirectory(gameDir);

            var lines = File.Exists(options)
                ? File.ReadAllLines(options).ToList()
                : new List<string> { "version:3465" };

            string entry = "lang:" + lang;
            int index = lines.FindIndex(l =>
                l.StartsWith("lang:", StringComparison.OrdinalIgnoreCase));

            if (index >= 0)
            {
                if (string.Equals(lines[index], entry, StringComparison.Ordinal) &&
                    string.Equals(_settings.LastAppliedLanguage, lang, StringComparison.OrdinalIgnoreCase))
                    return;
                lines[index] = entry;
            }
            else
            {
                lines.Add(entry);
            }

            File.WriteAllLines(options, lines);
            _settings.LastAppliedLanguage = lang;
            try { _settings.Save(LauncherSettings.ResolveSettingsPath()); } catch { /* best effort */ }
        }
        catch (Exception ex)
        {
            // A read-only game folder must not stop the launch; the game simply keeps
            // whatever language it had.
            LastWarning = "无法写入游戏语言到 options.txt：" + ex.Message;
        }
    }

    /// <summary>Non-fatal problem from the last launch attempt, surfaced by the caller.</summary>
    public string? LastWarning { get; private set; }

    /// <summary>
    /// True when an offline account is about to start 1.20.2 or newer.
    ///
    /// <para>Those clients call <c>createUserApiService</c> inside the Minecraft
    /// constructor without catching the failure, so a fake token gets an HTTP 401 and
    /// the game dies before the title screen. 1.20.1 and older catch it and keep going
    /// with <c>UserApiService.OFFLINE</c>. Nothing on our side can fix it: the token has
    /// to be real.</para>
    /// </summary>
    /// <summary>
    /// Warns when the configured heap exceeds what the machine can back with RAM.
    ///
    /// <para>Note: this is advisory only. <c>-Xmx</c> is an upper bound, not a
    /// reservation — <c>-Xmx60000M</c> happily starts on a 32 GB machine (verified).
    /// The risk is running out of memory later and letting Windows swap, so the launcher
    /// warns instead of blocking.</para>
    /// </summary>
    public static string? MemoryWarning(LauncherSettings settings)
    {
        long physicalMb;
        try
        {
            physicalMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);
        }
        catch
        {
            return null;
        }

        if (physicalMb <= 0) return null;

        // Leave room for metaspace, the JIT, direct buffers and the rest of the OS.
        if (settings.MemoryMb <= physicalMb * 0.75) return null;

        long suggestion = Math.Max(1024, (long)(physicalMb * 0.5));
        return $"内存分配 {settings.MemoryMb} MB 超过本机物理内存（{physicalMb} MB）的 75%。" +
               $"游戏仍能启动，但加载大型整合包时容易 OOM 或让系统频繁使用页面文件。" +
               $"建议改成 {suggestion} MB 左右。";
    }

    /// <summary>
    /// True when an offline account is about to start 1.20.2 or newer.
    ///
    /// <para>Those clients call <c>createUserApiService</c> inside the Minecraft
    /// constructor without catching the failure, so a fake token gets an HTTP 401 and
    /// the game dies before the title screen. 1.20.1 and older catch it and keep going
    /// with <c>UserApiService.OFFLINE</c>. Nothing on our side can fix it: the token has
    /// to be real.</para>
    /// </summary>
    public static bool OfflineBlocked(Account account, string baseVersion)
    {
        if (account.IsMicrosoft) return false;

        // "1.20.2" and up. Compared numerically: 1.9 vs 1.20 must not be a string
        // comparison, and "1.21" has no patch component but is still affected — an
        // earlier version of this check returned false for it.
        string[] parts = baseVersion.Split('.');
        if (parts.Length < 2) return false;
        if (!int.TryParse(parts[0], out int major) || !int.TryParse(parts[1], out int minor))
            return false;

        if (major != 1) return major > 1;
        if (minor != 20) return minor > 20;

        // 1.20: only 1.20.2 and later; bare "1.20" means 1.20.0.
        return parts.Length >= 3
            && int.TryParse(parts[2], out int patch)
            && patch >= 2;
    }

    public Process Launch(LaunchRequest req, Action<string> onOutput, Action<string> onError, out string commandLine)
    {
        var args = BuildArguments(req);
        string versionId = req.VersionId;
        string nativesDir = _paths.VersionNatives(versionId);

        Directory.CreateDirectory(_paths.LogsDir);
        Directory.CreateDirectory(_paths.Root);
        Directory.CreateDirectory(_paths.GameDir(versionId));
        Directory.CreateDirectory(_paths.ModsDir(versionId));
        Directory.CreateDirectory(nativesDir);

        ApplyLanguage(versionId);

        var psi = new ProcessStartInfo(req.Java.JavaExe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            // Run inside the game directory: relative paths in mod configs and
            // crash reports then land next to the version, not the data root.
            WorkingDirectory = _paths.GameDir(versionId),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        commandLine = req.Java.JavaExe + " " + string.Join(" ", args.Select(QuoteForDisplay));

        string logPath = _paths.VersionLog(versionId);
        var logWriter = new StreamWriter(logPath, append: false, Encoding.UTF8) { AutoFlush = true };
        logWriter.WriteLine($"# ECL {AppInfo.Version}  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        logWriter.WriteLine($"# account: {req.Account.Name} ({req.Account.Type})");
        logWriter.WriteLine("# " + Redact(commandLine));
        logWriter.WriteLine();

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            onOutput(e.Data);
            try { logWriter.WriteLine(e.Data); } catch { }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            onError(e.Data);
            try { logWriter.WriteLine(e.Data); } catch { }
        };
        process.Exited += async (_, _) =>
        {
            try { logWriter.Flush(); logWriter.Dispose(); } catch { }

            int exitCode;
            try { exitCode = process.ExitCode; }
            catch { return; }   // process already disposed

            // 0 means the player closed the game normally; nothing to explain.
            if (exitCode == 0) return;

            // The async readers can still be draining when Exited fires, so the tail
            // would be short of the lines that actually say what went wrong.
            await Task.Delay(300).ConfigureAwait(false);

            try
            {
                var tail = System.IO.File.ReadLines(logPath)
                    .TakeLast(200)
                    .ToList();

                var diagnosis = CrashAnalyzer.Analyze(exitCode, tail);

                onOutput($"[ECL] 游戏退出码 {exitCode}");
                onOutput($"[ECL] {diagnosis.Title}：{diagnosis.Summary}");
                if (diagnosis.Recognised) onOutput($"[ECL] 建议：{diagnosis.Advice}");

                // Persist it: the UI log is capped and lost when the window closes.
                System.IO.File.AppendAllText(logPath,
                    $"{Environment.NewLine}[ECL] 退出码 {exitCode}{Environment.NewLine}" +
                    $"[ECL] {diagnosis.Title}：{diagnosis.Summary}{Environment.NewLine}" +
                    (diagnosis.Recognised ? $"[ECL] 建议：{diagnosis.Advice}{Environment.NewLine}" : ""));
            }
            catch (Exception ex)
            {
                onOutput("[ECL] 崩溃原因分析失败：" + ex.Message);
            }
        };

        if (!process.Start())
            throw new InvalidOperationException(
                "Java 没能启动起来。到「设置 → Java」确认选中的那个还在，或者换一个。");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    // ------------------------------------------------------------- classpath ---

    public string BuildClasspath(ResolvedVersion version)
    {
        var detail = version.Detail;
        var parts = new List<string>(detail.Libraries.Count + 2);

        // The bootstrap jar must come first so ecl.runtime.Bootstrap can be found.
        if (_settings.UseBootstrap)
        {
            if (EnsureRuntimeJar() is { } runtimeJar) parts.Add(runtimeJar);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lib in detail.Libraries)
        {
            if (!InstallService.IsAllowed(lib.Rules)) continue;

            var artifact = lib.Downloads?.Artifact;
            string? path = artifact?.Path;
            if (path == null && artifact == null && lib.Name.Contains(':'))
            {
                var (p, _) = InstallService.MavenFromName(lib.Name, lib.Url ?? "https://libraries.minecraft.net/");
                path = p;
            }
            if (string.IsNullOrEmpty(path)) continue;
            if (!InstallService.NativesArchMatches(path)) continue;

            string full = _paths.LibraryFile(path);
            if (!File.Exists(full)) continue;
            if (seen.Add(full)) parts.Add(full);
        }

        // Modded profiles use their own jar copy (created at install time); fall
        // back to the vanilla parent when it is missing.
        string jar = _paths.VersionJar(version.Id);
        if (!File.Exists(jar)) jar = _paths.VersionJar(version.JarVersionId);
        if (File.Exists(jar) && seen.Add(jar)) parts.Add(jar);

        return string.Join(";", parts);
    }

    // ------------------------------------------------------------- arguments ---

    private static IEnumerable<string> Evaluate(List<JsonElement> nodes, Dictionary<string, string> vars, bool keepEmpty)
    {
        foreach (var node in nodes)
        {
            if (node.ValueKind == JsonValueKind.String)
            {
                string expanded = Expand(node.GetString() ?? "", vars);
                if (keepEmpty || !string.IsNullOrEmpty(expanded)) yield return expanded;
            }
            else if (node.ValueKind == JsonValueKind.Object)
            {
                var rules = node.TryGetProperty("rules", out var r) && r.ValueKind == JsonValueKind.Array
                    ? JsonSerializer.Deserialize<List<RuleNode>>(r.GetRawText(), JsonOpts)
                    : null;
                if (!InstallService.IsAllowed(rules)) continue;

                if (!node.TryGetProperty("value", out var value)) continue;
                if (value.ValueKind == JsonValueKind.String)
                {
                    string expanded = Expand(value.GetString() ?? "", vars);
                    if (keepEmpty || !string.IsNullOrEmpty(expanded)) yield return expanded;
                }
                else if (value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var v in value.EnumerateArray())
                        if (v.ValueKind == JsonValueKind.String)
                        {
                            string expanded = Expand(v.GetString() ?? "", vars);
                            if (keepEmpty || !string.IsNullOrEmpty(expanded)) yield return expanded;
                        }
                }
            }
        }
    }

    private static string Expand(string template, Dictionary<string, string> vars)
    {
        if (template.IndexOf("${", StringComparison.Ordinal) < 0) return template;
        var sb = new StringBuilder(template.Length + 16);
        int i = 0;
        while (i < template.Length)
        {
            int start = template.IndexOf("${", i, StringComparison.Ordinal);
            if (start < 0)
            {
                sb.Append(template, i, template.Length - i);
                break;
            }
            sb.Append(template, i, start - i);
            int end = template.IndexOf('}', start + 2);
            if (end < 0)
            {
                sb.Append(template, start, template.Length - start);
                break;
            }
            string key = template.Substring(start + 2, end - start - 2);
            sb.Append(vars.TryGetValue(key, out var v) ? v : "");
            i = end + 1;
        }
        return sb.ToString();
    }

    /// <summary>Splits a legacy argument string, honouring "quoted segments".</summary>
    private static IEnumerable<string> SplitArguments(string raw)
    {
        var sb = new StringBuilder();
        bool inQuotes = false;
        foreach (char c in raw)
        {
            if (c == '"') { inQuotes = !inQuotes; continue; }
            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    private static string QuoteForDisplay(string s) => s.Contains(' ') ? "\"" + s + "\"" : s;

    /// <summary>Removes access tokens from anything written to disk.</summary>
    private static string Redact(string commandLine)
    {
        var parts = commandLine.Split(' ');
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i] is "--accessToken" or "--auth_session" && i + 1 < parts.Length)
                parts[i + 1] = "<redacted>";
        }
        return string.Join(' ', parts);
    }
}
