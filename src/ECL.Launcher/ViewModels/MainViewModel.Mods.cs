// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using System.Collections.ObjectModel;
using System.IO;
using ECL.Services;
using Microsoft.Win32;

namespace ECL.ViewModels;

/// <summary>
/// Mods page: every jar inside the selected version's own <c>mods/</c> folder,
/// with metadata read straight from the archive.
/// </summary>
public sealed partial class MainViewModel
{
    private ModService? _mods;

    private ModService ModsSvc => _mods ??= new ModService(_paths);

    public ObservableCollection<ModEntry> Mods { get; } = new();

    public bool HasMods => Mods.Count > 0;
    public bool HasNoMods => Mods.Count == 0;

    public string ModsVersionTitle => _selectedInstalled?.DisplayName ?? "未选择版本";
    public string ModsVersionSub => _selectedInstalled == null
        ? "先在主页选择一个已安装的版本"
        : $"Minecraft {_selectedInstalled.BaseVersion}  ·  {_selectedInstalled.KindLabel}" +
          (_settings.IsolateByVersion ? "  ·  独立目录" : "  ·  共享目录");

    public string ModsPathText => _selectedInstalled == null
        ? ""
        : ModsSvc.ModsDir(_selectedInstalled.Id);

    public string ModsSummary
    {
        get
        {
            if (Mods.Count == 0) return "";
            int enabled = Mods.Count(m => m.IsEnabled);
            return $"{enabled} 个已启用  ·  共 {Mods.Count} 个";
        }
    }

    /// <summary>Recomputed in <see cref="RefreshMods"/>; empty when everything resolves.</summary>
    private List<string> _modsMissingCache = new();

    public string ModsWarning
    {
        get
        {
            if (_selectedInstalled == null) return "";
            var kind = _selectedInstalled.Kind;

            // The 47.4+ bug is silent until launch: OptiFine counts as a valid
            // Forge mod, so the generic check below would say it is fine.
            if (kind == LoaderKind.Forge && !ForgeProfileSupportsOptiFine &&
                Mods.Any(m => m.IsEnabled && m.Platform == ModPlatform.OptiFine))
                return "Forge 47.4+ 与 OptiFine 有已知冲突，启动会崩。" +
                       "请禁用 OptiFine，或换 Forge 47.3 及更早的构建。";

            var bad = Mods.Where(m => m.IsEnabled && !m.WorksOn(kind)).ToList();
            if (bad.Count == 0)
            {
                // Nothing structurally wrong; the remaining silent killer is a
                // missing dependency — the game starts, then dies while loading.
                var missing = _modsMissingCache;
                if (missing.Count == 0) return "";

                string list = string.Join("、", missing.Take(3));
                if (missing.Count > 3) list += $" 等 {missing.Count} 项";
                return $"缺少前置模组：{list}。补上之后游戏才能正常加载。";
            }

            if (kind == LoaderKind.Vanilla)
                return $"原版不能加载模组：{bad.Count} 个已启用的模组不会生效，请改用 Fabric / Quilt / Forge。";

            string names = string.Join("、", bad.Take(3).Select(m => m.Title));
            if (bad.Count > 3) names += $" 等 {bad.Count} 个";
            return $"{bad.Count} 个模组与 {_selectedInstalled.KindLabel} 不匹配：{names}";
        }
    }

    public bool HasModsWarning => ModsWarning.Length > 0;

    public bool IsForgeProfile => _selectedInstalled?.Kind == LoaderKind.Forge;

    /// <summary>True when this Forge build can actually carry OptiFine.</summary>
    public bool ForgeProfileSupportsOptiFine =>
        _selectedInstalled is { Kind: LoaderKind.Forge } v && ForgeInstaller.IsOptiFineCompatible(v.LoaderVersion);

    public bool CanInstallOptiFineMod =>
        ForgeProfileSupportsOptiFine &&
        !Mods.Any(m => m.Platform == ModPlatform.OptiFine);

    public string OptiFineModButtonText
    {
        get
        {
            if (_selectedInstalled is not { Kind: LoaderKind.Forge })
                return "仅 Forge 版本支持";
            if (!ForgeProfileSupportsOptiFine)
                return "Forge 47.4+ 不支持 OptiFine";
            if (Mods.Any(m => m.Platform == ModPlatform.OptiFine))
                return "OptiFine 已安装";
            return "用 OptiFine 优化此版本";
        }
    }

    // ------------------------------------------------------------------ state ---

    public void RefreshMods()
    {
        Mods.Clear();
        try
        {
            if (_selectedInstalled != null)
                foreach (var m in ModsSvc.List(_selectedInstalled.Id))
                    Mods.Add(m);
        }
        catch (Exception ex)
        {
            Log("读取模组目录失败：" + ex.Message);
        }

        RefreshModDependencies();
        RaiseModsState();
    }

    /// <summary>
    /// Recomputes missing prerequisites. Called after any change to the enabled set
    /// — a disabled dependency is just as missing as an absent file.
    /// </summary>
    private void RefreshModDependencies()
    {
        try
        {
            _modsMissingCache = ModService.FindMissingDependencies(Mods.ToList());
            if (_modsMissingCache.Count > 0)
                Log($"缺少前置模组：{string.Join("；", _modsMissingCache)}");
        }
        catch (Exception ex)
        {
            _modsMissingCache = new();
            Log("检查模组依赖失败：" + ex.Message);
        }
    }

    private void RaiseModsState()
    {
        OnPropertyChanged(nameof(HasMods));
        OnPropertyChanged(nameof(HasNoMods));
        OnPropertyChanged(nameof(ModsVersionTitle));
        OnPropertyChanged(nameof(ModsVersionSub));
        OnPropertyChanged(nameof(ModsPathText));
        OnPropertyChanged(nameof(ModsSummary));
        OnPropertyChanged(nameof(ModsWarning));
        OnPropertyChanged(nameof(HasModsWarning));
        OnPropertyChanged(nameof(IsForgeProfile));
        OnPropertyChanged(nameof(ForgeProfileSupportsOptiFine));
        OnPropertyChanged(nameof(CanInstallOptiFineMod));
        OnPropertyChanged(nameof(OptiFineModButtonText));
        (ImportModCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (InstallOptiFineModCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        RaiseMarketState();
    }

    // ---------------------------------------------------------------- commands ---

    private void ToggleMod(ModEntry? entry)
    {
        if (entry == null || _selectedInstalled == null) return;
        try
        {
            ModsSvc.SetEnabled(_selectedInstalled.Id, entry, !entry.IsEnabled);
            StatusText = $"{entry.Title} 已{(entry.IsEnabled ? "启用" : "禁用")}";
            Log($"模组：{entry.FileName} → {(entry.IsEnabled ? "启用" : "禁用")}");
        }
        catch (Exception ex)
        {
            entry.IsEnabled = !entry.IsEnabled;   // undo the optimistic toggle
            ErrorText = "切换失败：" + ex.Message;
            Log("切换模组失败：" + ex.Message);
        }

        RefreshModDependencies();
        RaiseModsState();
    }

    private void DeleteMod(ModEntry? entry)
    {
        if (entry == null || _selectedInstalled == null) return;

        var answer = System.Windows.MessageBox.Show(
            $"把「{entry.Title}」移到 mods\\.ecl-trash？\n\n文件不会被直接删除，需要时可以手动挪回来。",
            "移除模组", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Question);
        if (answer != System.Windows.MessageBoxResult.OK) return;

        try
        {
            ModsSvc.MoveToTrash(_selectedInstalled.Id, entry);
            Mods.Remove(entry);
            StatusText = $"已移除 {entry.Title}";
            Log($"模组移入回收目录：{entry.FileName}");
        }
        catch (Exception ex)
        {
            ErrorText = "移除失败：" + ex.Message;
        }

        RaiseModsState();
    }

    private void ImportMod()
    {
        if (_selectedInstalled == null) return;

        var dialog = new OpenFileDialog
        {
            Title = "选择模组 jar（可多选）",
            Filter = "模组文件 (*.jar)|*.jar|所有文件 (*.*)|*.*",
            Multiselect = true,
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true) return;

        int ok = 0;
        var errors = new List<string>();
        foreach (var file in dialog.FileNames)
        {
            try
            {
                var entry = ModsSvc.Import(_selectedInstalled.Id, file);
                ok++;
                Log($"导入模组：{entry.FileName}（{entry.PlatformLabel}" +
                    (string.IsNullOrEmpty(entry.ModVersion) ? "" : " " + entry.ModVersion) + "）");

                if (!entry.WorksOn(_selectedInstalled.Kind))
                    Log($"  ⚠ 这是 {entry.PlatformLabel} 模组，当前版本是 {_selectedInstalled.KindLabel}，启动时不会加载");
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(file)}：{ex.Message}");
            }
        }

        RefreshMods();
        StatusText = ok > 0 ? $"已导入 {ok} 个模组" : "没有导入任何模组";
        if (errors.Count > 0) ErrorText = "部分文件导入失败：\n" + string.Join("\n", errors);
    }

    private void OpenModsFolder()
    {
        if (_selectedInstalled == null) return;
        try
        {
            string dir = ModsSvc.ModsDir(_selectedInstalled.Id);
            Directory.CreateDirectory(dir);
            OpenUrl(dir);
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
    }

    /// <summary>One-click "OptiFine as a Forge mod" for the selected profile.</summary>
    private async Task InstallOptiFineModAsync()
    {
        if (_selectedInstalled is not { Kind: LoaderKind.Forge } version)
        {
            ErrorText = "只有 Forge 版本才能把 OptiFine 当模组安装。";
            return;
        }

        if (!ForgeInstaller.IsOptiFineCompatible(version.LoaderVersion))
        {
            // Installing would "succeed" and then crash on launch; refuse instead.
            ErrorText = $"Forge {version.LoaderVersion} 与 OptiFine 有已知冲突（启动即崩）。" +
                        "请改装 Forge 47.3 或更早的构建。";
            StatusText = "Forge 版本与 OptiFine 不兼容";
            return;
        }

        ErrorText = "";
        IsBusy = true;
        _cts = new CancellationTokenSource();
        try
        {
            StatusText = "正在获取 OptiFine 构建列表…";
            var opti = new OptiFineInstaller(_paths);
            var builds = await opti.GetBuildsAsync(version.BaseVersion, _cts.Token).ConfigureAwait(true);
            if (builds.Count == 0)
                throw new InvalidOperationException($"OptiFine 没有为 {version.BaseVersion} 提供构建。");

            var build = builds[0];
            StageText = $"下载 OptiFine {build.Edition}";
            Log($"为 {version.Id} 安装 OptiFine {build.Edition}（作为 Forge 模组）");

            string dest = await opti.InstallAsForgeModAsync(version.BaseVersion, build.FileName, version.Id,
                new Progress<string>(Log), _cts.Token).ConfigureAwait(true);

            Log("OptiFine 模组已就位：" + dest);
            StatusText = $"OptiFine {build.Edition} 已加入 {version.DisplayName}";
            ProgressValue = 1;
            RefreshMods();
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消";
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            StatusText = "OptiFine 安装失败";
            Log("OptiFine 安装失败：" + ex);
        }
        finally
        {
            IsBusy = false;
            StageText = "";
            _cts?.Dispose();
            _cts = null;
        }
    }
}
