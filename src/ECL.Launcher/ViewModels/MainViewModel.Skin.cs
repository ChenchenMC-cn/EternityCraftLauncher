// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using ECL.Models;

namespace ECL.ViewModels;

/// <summary>
/// Account-page skin and cape section. The services already knew how to fetch from
/// Mojang / LittleSkin / a local PNG; this is the UI state and the wiring into the
/// local skin server, which used to be reachable only through <c>--skin-set</c>.
/// </summary>
public sealed partial class MainViewModel
{
    private ImageSource? _skinPreview;
    private ImageSource? _capePreview;
    private string _littleSkinName = "";

    public System.Windows.Input.ICommand UseMojangSkinCommand { get; private set; } = null!;
    public System.Windows.Input.ICommand UseLittleSkinCommand { get; private set; } = null!;
    public System.Windows.Input.ICommand ImportSkinFileCommand { get; private set; } = null!;
    public System.Windows.Input.ICommand ImportCapeFileCommand { get; private set; } = null!;
    public System.Windows.Input.ICommand RemoveSkinCommand { get; private set; } = null!;

    public ImageSource? SkinPreview
    {
        get => _skinPreview;
        private set
        {
            if (!SetProperty(ref _skinPreview, value)) return;
            OnPropertyChanged(nameof(HasSkinPreview));
        }
    }

    public bool HasSkinPreview => _skinPreview != null;

    public ImageSource? CapePreview
    {
        get => _capePreview;
        private set
        {
            if (!SetProperty(ref _capePreview, value)) return;
            OnPropertyChanged(nameof(HasCapePreview));
        }
    }

    public bool HasCapePreview => _capePreview != null;

    public string SkinLabel => _currentSkin?.Label ?? Services.Localization.T("未设置（游戏里显示默认皮肤）");

    public string SkinModelHint => _currentSkin == null
        ? ""
        : (_currentSkin.Slim ? Services.Localization.T("纤细模型（Alex）") : Services.Localization.T("经典模型（Steve）"));

    public string LittleSkinName
    {
        get => _littleSkinName;
        set => SetProperty(ref _littleSkinName, value);
    }

    internal void InitSkinCommands()
    {
        UseMojangSkinCommand = new AsyncRelayCommand(UseMojangSkinAsync, () => !IsBusy && CanUseMojangSkin);
        UseLittleSkinCommand = new AsyncRelayCommand(UseLittleSkinAsync, () => !IsBusy);
        ImportSkinFileCommand = new RelayCommand(ImportSkinFile);
        ImportCapeFileCommand = new RelayCommand(ImportCapeFile);
        RemoveSkinCommand = new RelayCommand(() => _ = ApplySkinAsync(null));
    }

    private bool CanUseMojangSkin =>
        _currentAccount?.IsMicrosoft == true && _currentAccount.AccessToken is { Length: > 0 };

    /// <summary>Recomputes previews and labels after the skin changes.</summary>
    internal void RefreshSkinState()
    {
        SkinPreview = Models.SkinPreview.Front(_currentSkin?.SkinPath);
        CapePreview = Models.SkinPreview.Cape(_currentSkin?.CapePath);

        OnPropertyChanged(nameof(SkinLabel));
        OnPropertyChanged(nameof(SkinModelHint));
        (UseMojangSkinCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private async Task UseMojangSkinAsync()
    {
        if (_skinService == null) return;

        var account = _currentAccount;
        if (account?.AccessToken is not { Length: > 0 } token)
        {
            ErrorText = "请先用微软账户登录，再拉取正版皮肤。";
            return;
        }

        ErrorText = "";
        IsBusy = true;
        try
        {
            StatusText = "正在读取正版皮肤…";
            var info = await _skinService.FromMojangAsync(token, account.Name, CancellationToken.None)
                                          .ConfigureAwait(true);
            if (info == null)
            {
                ErrorText = "这个账号还没有设置皮肤（Mojang 返回了空列表）。";
                StatusText = "没有找到皮肤";
                return;
            }

            await ApplySkinAsync(info).ConfigureAwait(true);
            StatusText = "已应用正版皮肤";
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            Log("读取正版皮肤失败：" + ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task UseLittleSkinAsync()
    {
        if (_skinService == null) return;

        ErrorText = "";
        IsBusy = true;
        try
        {
            StatusText = "正在查询 LittleSkin…";
            var info = await _skinService
                .FromLittleSkinAsync(LittleSkinName, CancellationToken.None)
                .ConfigureAwait(true);

            if (info == null)
            {
                ErrorText = "这个用户在 LittleSkin 上没有公开皮肤。";
                return;
            }

            await ApplySkinAsync(info).ConfigureAwait(true);
            StatusText = $"已应用 {info.Label}";
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            Log("LittleSkin 查询失败：" + ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ImportSkinFile()
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择皮肤 PNG（64×64 或 64×32）",
                Filter = "PNG 图片 (*.png)|*.png",
                CheckFileExists = true
            };

            if (dialog.ShowDialog() != true) return;

            // The model has to match the skin; ask once so nobody wonders why the arms
            // look wrong.
            bool slim = _currentSkin?.Slim ?? false;
            var answer = MessageBox.Show(
                "这是纤细（Alex，手臂 3 像素）模型吗？\n\n选「是」= 纤细模型，选「否」= 经典模型（Steve）。",
                "选择皮肤模型", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (answer == MessageBoxResult.Yes) slim = true;
            else if (answer == MessageBoxResult.No) slim = false;

            var info = _skinService!.FromFile(dialog.FileName, slim);
            _ = ApplySkinAsync(info);
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
    }

    private void ImportCapeFile()
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择披风 PNG（64×32）",
                Filter = "PNG 图片 (*.png)|*.png",
                CheckFileExists = true
            };

            if (dialog.ShowDialog() != true) return;

            var info = _skinService!.FromCapeFile(dialog.FileName, _currentSkin);
            _ = ApplySkinAsync(info);
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
    }

    /// <summary>
    /// Persists the choice, refreshes the previews and pushes the textures to the local
    /// Yggdrasil server the game talks to at launch.
    /// </summary>
    private async Task ApplySkinAsync(SkinInfo? info)
    {
        _currentSkin = info;
        _skinService?.SaveCurrent(info);
        RefreshSkinState();

        if (info?.SkinPath == null)
        {
            StatusText = "已清除自定义皮肤";
            Log("皮肤：已清除");
            return;
        }

        try
        {
            byte[] png = await File.ReadAllBytesAsync(info.SkinPath).ConfigureAwait(true);
            byte[]? cape = info.CapePath != null && File.Exists(info.CapePath)
                ? await File.ReadAllBytesAsync(info.CapePath).ConfigureAwait(true)
                : null;

            if (_skinServer == null)
            {
                _skinServer = new Services.LocalSkinServer(Path.Combine(_paths.Root, "skin-server.pem"));
                _skinServer.Start();
            }

            _skinServer.SetSkin("", _currentAccount?.Name ?? "Player", png, info.Slim, cape);
            Log($"皮肤已应用：{info.Label}（{_skinServer.ApiRoot}）");
            StatusText = "皮肤已应用，进入游戏生效";
        }
        catch (Exception ex)
        {
            ErrorText = "皮肤已保存，但本机皮肤服务启动失败：" + ex.Message;
            Log("皮肤服务失败：" + ex.Message);
        }
    }
}
