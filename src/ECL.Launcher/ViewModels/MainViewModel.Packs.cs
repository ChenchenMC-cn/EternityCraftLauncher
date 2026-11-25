// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using System.Collections.ObjectModel;
using System.IO;
using ECL.Services;
using Microsoft.Win32;

namespace ECL.ViewModels;

/// <summary>
/// Resource packs, shader packs and data packs. Same page as mods, switched with a
/// segmented control — adding a seventh page would reshuffle every navigation index for
/// three lists that behave identically.
/// </summary>
public sealed partial class MainViewModel
{
    private PackFolderService? _packsSvc;

    private PackFolderService PacksSvc => _packsSvc ??= new PackFolderService(_paths);

    /// <summary>0 = mods, 1 = resource packs, 2 = shader packs, 3 = data packs.</summary>
    private int _packTab;

    public int PackTab
    {
        get => _packTab;
        private set
        {
            if (!SetProperty(ref _packTab, value)) return;

            OnPropertyChanged(nameof(IsModsTab));
            OnPropertyChanged(nameof(ShowModsList));
            OnPropertyChanged(nameof(IsPacksTab));
            OnPropertyChanged(nameof(ShowNoMods));
            OnPropertyChanged(nameof(PacksPageTitle));
            OnPropertyChanged(nameof(PacksPageSubtitle));
            OnPropertyChanged(nameof(IsResourceTab));
            OnPropertyChanged(nameof(IsShaderTab));
            OnPropertyChanged(nameof(IsDataTab));
            OnPropertyChanged(nameof(PackHint));
            RefreshPacks();
        }
    }

    public bool IsModsTab => _packTab == 0;

    /// <summary>Mods list shows only on the mods tab, and only when the profile has jars.</summary>
    public bool ShowModsList => IsModsTab && HasMods;

    /// <summary>Any of the three content-pack tabs.</summary>
    public bool IsPacksTab => _packTab != 0;

    /// <summary>Empty-state card for mods: mods tab only, and only when the folder is empty.</summary>
    public bool ShowNoMods => IsModsTab && HasNoMods;
    public bool IsResourceTab => _packTab == 1;
    public bool IsShaderTab => _packTab == 2;
    public bool IsDataTab => _packTab == 3;

    public ObservableCollection<PackEntry> Packs { get; } = new();

    public System.Windows.Input.ICommand SelectPackTabCommand { get; private set; } = null!;
    public System.Windows.Input.ICommand ImportPackCommand { get; private set; } = null!;
    public System.Windows.Input.ICommand TogglePackCommand { get; private set; } = null!;
    public System.Windows.Input.ICommand DeletePackCommand { get; private set; } = null!;
    public System.Windows.Input.ICommand OpenPackFolderCommand { get; private set; } = null!;
    public System.Windows.Input.ICommand ImportModpackCommand { get; private set; } = null!;

    private PackKind CurrentPackKind => _packTab switch
    {
        1 => PackKind.ResourcePack,
        2 => PackKind.ShaderPack,
        _ => PackKind.DataPack
    };

    /// <summary>Tells the user where the files landed and, for data packs, how to enable them.</summary>
    /// <summary>The page heading follows the active tab, so "Mods" is not printed above a
    /// list of shader packs.</summary>
    public string PacksPageTitle => _packTab switch
    {
        1 => Services.Localization.T("资源包"),
        2 => Services.Localization.T("光影"),
        3 => Services.Localization.T("数据包"),
        _ => Services.Localization.T("模组")
    };

    public string PacksPageSubtitle => _packTab switch
    {
        1 => Services.Localization.T("每个版本有独立的 resourcepacks 目录；禁用只改文件名"),
        2 => Services.Localization.T("每个版本有独立的 shaderpacks 目录；需要装 Iris 或 OptiFine 才能生效"),
        3 => Services.Localization.T("数据包放在 game/datapacks；新世界会自动加载"),
        _ => Services.Localization.T("每个版本有独立的 mods 目录；启用与禁用只改文件名，移除会先放进 .ecl-trash")
    };

    public string PackHint
    {
        get
        {
            if (_selectedInstalled == null)
                return Services.Localization.T("先选择一个版本");

            string dir = PacksSvc.Dir(_selectedInstalled.Id, CurrentPackKind);
            return _packTab switch
            {
                1 => dir,
                2 => dir,
                _ => dir + Services.Localization.T("（全局数据包；新世界自动加载，旧世界用 /datapack enable 启用）")
            };
        }
    }

    /// <summary>Used by the screenshot path; the UI switches through the command.</summary>
    public void ShowPackTab(int index) => PackTab = index;

    /// <summary>
    /// Installs a <c>.mrpack</c> into the selected profile.
    ///
    /// <para>The profile must already have the right loader and game version — building it
    /// is the install page's job. Mixing the two flows here would mean a second copy of the
    /// loader pipeline, which is exactly the kind of duplication that drifts.</para>
    /// </summary>
    private async Task ImportModpackAsync()
    {
        if (_selectedInstalled == null)
        {
            ErrorText = Services.Localization.T("先选择一个版本");
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = Services.Localization.T("安装整合包"),
            Filter = Services.Localization.T("Modrinth 整合包 (*.mrpack)|*.mrpack|压缩包 (*.zip)|*.zip"),
            CheckFileExists = true
        };

        if (dialog.ShowDialog() != true) return;

        var index = ModpackService.ReadIndex(dialog.FileName, out string error);
        if (index == null)
        {
            ErrorText = Services.Localization.T("导入失败：") + error;
            Log("读取整合包失败：" + error);
            return;
        }

        // A missing profile means downloading Minecraft itself; say so before starting.
        string? profileWarning = FindModpackProfile(index) == null
            ? string.Format(Services.Localization.T(
                "还没有 {0} 的 {1} 档案：将先下载游戏本体与资源（约 {2}），再装整合包内容。"),
                index.Minecraft, index.Loader, "700 MB")
            : null;

        if (profileWarning != null && !await ConfirmAsync(index.Name,
                new DependencyPlan(Array.Empty<ModrinthVersion>(), Array.Empty<string>()),
                Array.Empty<string>(), profileWarning).ConfigureAwait(true))
        {
            Log("已取消");
            return;
        }

        IsBusy = true;
        ErrorText = "";
        try
        {
            // Build or reuse the profile the pack asks for. This can download Minecraft
            // itself, so it runs under the same busy flag and progress reporting.
            string profileId = await EnsureModpackProfileAsync(index, System.Threading.CancellationToken.None)
                .ConfigureAwait(true);

            // Already a managed pack? Upgrade it instead of laying it down again.
            var serviceForMarker = new ModpackService(_paths);
            var marker = serviceForMarker.ReadMarker(profileId);

            if (marker != null)
            {
                var diff = ModpackService.Diff(marker, index);
                if (diff.IsEmpty)
                {
                    Log($"整合包已是最新：{marker.Name} {marker.Version}");
                    StatusText = Services.Localization.T("已是最新");
                    return;
                }

                string detail = string.Format(
                    Services.Localization.T("{0} {1} → {2}：新增 {3}、更新 {4}、移除 {5} 个文件"),
                    marker.Name, marker.Version, index.Version,
                    diff.Added.Count, diff.Changed.Count, diff.Removed.Count);

                if (!await ConfirmMessageAsync(Services.Localization.T("更新整合包"), detail).ConfigureAwait(true))
                {
                    Log("已取消更新");
                    return;
                }

                var updateResult = await serviceForMarker.ApplyUpdateAsync(dialog.FileName, profileId,
                    new Progress<string>(Log),
                    SafeProgress.Create<(long Done, long Total)>(p =>
                    {
                        ProgressValue = p.Total > 0 ? (double)p.Done / p.Total : 0;
                        StatusText = $"{p.Done} / {p.Total} 个文件";
                    }, ex => Log("进度更新失败（不影响更新）：" + ex.Message))).ConfigureAwait(true);

                foreach (string problem in updateResult.Problems) Log("更新问题：" + problem);
                RefreshMods();
                RefreshPacks();
                Log($"整合包更新完成：{updateResult.Name} → {updateResult.Version}");
                return;
            }

            Log($"开始安装整合包 {index.Name} {index.Version}（{index.Files.Count} 个文件）→ {profileId}");

            var service = new ModpackService(_paths);
            var result = await service.InstallFilesAsync(dialog.FileName, profileId,
                new Progress<string>(Log),
                SafeProgress.Create<(long Done, long Total)>(p =>
                {
                    ProgressValue = p.Total > 0 ? (double)p.Done / p.Total : 0;
                    StageText = index.Name;
                    StatusText = $"{p.Done} / {p.Total} 个文件";
                }, ex => Log("进度更新失败（不影响安装）：" + ex.Message))).ConfigureAwait(true);

            foreach (string problem in result.Problems)
                Log("整合包问题：" + problem);

            RefreshMods();
            RefreshPacks();
            Log($"整合包安装完成：下载 {result.Downloaded}，跳过 {result.Skipped}，失败 {result.Failed}");
        }
        catch (Exception ex)
        {
            ErrorText = Services.Localization.T("安装失败：") + ex.Message;
            Log("整合包安装失败：" + ex.Message);
        }
        finally
        {
            IsBusy = false;
            StageText = "";
            ProgressValue = 0;
        }
    }

    private void SelectPackTab(object? parameter)
    {
        if (parameter is string s && int.TryParse(s, out int index))
            PackTab = index;
        else if (parameter is int i)
            PackTab = i;
    }

    public void RefreshPacks()
    {
        Packs.Clear();
        if (_selectedInstalled == null || IsModsTab) return;

        try
        {
            foreach (var e in PacksSvc.List(_selectedInstalled.Id, CurrentPackKind))
                Packs.Add(e);
        }
        catch (Exception ex)
        {
            Log("读取内容包目录失败：" + ex.Message);
        }
    }

    private void ImportPack()
    {
        if (_selectedInstalled == null) return;

        var dialog = new OpenFileDialog
        {
            Title = Services.Localization.T("导入"),
            Filter = Services.Localization.T("压缩包 (*.zip)|*.zip"),
            CheckFileExists = true,
            Multiselect = true
        };

        if (dialog.ShowDialog() != true) return;

        int ok = 0;
        foreach (string file in dialog.FileNames)
        {
            try
            {
                PacksSvc.Import(_selectedInstalled.Id, CurrentPackKind, file);
                ok++;
            }
            catch (Exception ex)
            {
                Log($"导入 {Path.GetFileName(file)} 失败：{ex.Message}");
            }
        }

        RefreshPacks();
        Log($"已导入 {ok} 个文件到 {PackFolderService.FolderName(CurrentPackKind)}/");
    }

    private void TogglePack(object? parameter)
    {
        if (parameter is not PackEntry entry || _selectedInstalled == null) return;

        try
        {
            PacksSvc.SetEnabled(_selectedInstalled.Id, CurrentPackKind, entry, !entry.IsEnabled);
            RefreshPacks();
        }
        catch (Exception ex)
        {
            Log("切换状态失败：" + ex.Message);
        }
    }

    private void DeletePack(object? parameter)
    {
        if (parameter is not PackEntry entry || _selectedInstalled == null) return;

        try
        {
            PacksSvc.MoveToTrash(_selectedInstalled.Id, CurrentPackKind, entry);
            Log($"已移除 {entry.FileName}（在 {PackFolderService.FolderName(CurrentPackKind)}/.ecl-trash）");
        }
        catch (Exception ex)
        {
            Log("移除失败：" + ex.Message);
        }

        RefreshPacks();
    }

    private void OpenPackFolder()
    {
        if (_selectedInstalled == null) return;

        try
        {
            string dir = PacksSvc.Dir(_selectedInstalled.Id, CurrentPackKind);
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log("打开目录失败：" + ex.Message);
        }
    }
}
