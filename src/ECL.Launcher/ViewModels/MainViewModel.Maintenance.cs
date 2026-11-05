// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using System.Collections.ObjectModel;
using System.Windows.Input;
using ECL.Services;

namespace ECL.ViewModels;

/// <summary>
/// Per-version maintenance: verify every file against the version json, then repair
/// whatever is missing or corrupt. Both run as cancellable tasks so they show up in
/// the task menu.
/// </summary>
public sealed partial class MainViewModel
{
    public ObservableCollection<VerifyIssue> VerifyIssues { get; } = new();

    private string _verifySummary = "";
    public string VerifySummary
    {
        get => _verifySummary;
        private set => SetProperty(ref _verifySummary, value);
    }

    private bool _isVerifying;
    public bool IsVerifying
    {
        get => _isVerifying;
        private set => SetProperty(ref _isVerifying, value);
    }

    public bool HasVerifyResult => VerifySummary.Length > 0;
    public bool HasVerifyIssues => VerifyIssues.Count > 0;
    public bool CanMaintainVersion => _selectedInstalled != null && _api != null && !IsBusy && !IsVerifying;

    public ICommand VerifyVersionCommand { get; private set; } = null!;
    public ICommand RepairVersionCommand { get; private set; } = null!;
    public ICommand OpenVersionFolderCommand { get; private set; } = null!;

    private void InitVersionMaintenance()
    {
        VerifyVersionCommand = new AsyncRelayCommand(() => VerifyVersionAsync(), () => CanMaintainVersion);
        RepairVersionCommand = new AsyncRelayCommand(() => RepairVersionAsync(), () => CanMaintainVersion);
        OpenVersionFolderCommand = new RelayCommand(OpenVersionFolder, () => _selectedInstalled != null);
    }

    private void OpenVersionFolder()
    {
        if (_selectedInstalled is not { } v) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _paths.GameDir(v.Id),
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ErrorText = "打开目录失败：" + ex.Message;
        }
    }

    private async Task VerifyVersionAsync()
    {
        if (_selectedInstalled is not { } version || _api == null) return;

        ErrorText = "";
        VerifyIssues.Clear();
        VerifySummary = "";
        OnPropertyChanged(nameof(HasVerifyResult));
        OnPropertyChanged(nameof(HasVerifyIssues));

        IsVerifying = true;
        var task = BeginTask($"校验 {version.DisplayName}");
        _cts = new CancellationTokenSource();
        IsBusy = true;

        try
        {
            var verifier = new VersionVerifier(_paths, _settings, _api);
            var report = await verifier.VerifyAsync(version.Id,
                new Progress<string>(Log),
                new Progress<double>(p => ProgressValue = p),
                _cts.Token).ConfigureAwait(true);

            foreach (var issue in report.Issues) VerifyIssues.Add(issue);

            VerifySummary = report.Summary;
            StatusText = $"校验完成：{report.Summary}";
            OnPropertyChanged(nameof(HasVerifyResult));
            OnPropertyChanged(nameof(HasVerifyIssues));

            EndTask(task, report.Healthy ? TaskState.Done : TaskState.Failed, report.Summary);
        }
        catch (OperationCanceledException)
        {
            StatusText = "校验已取消";
            VerifySummary = "已取消";
            OnPropertyChanged(nameof(HasVerifyResult));
            EndTask(task, TaskState.Cancelled, "已取消");
        }
        catch (Exception ex)
        {
            ErrorText = "校验失败：" + ex.Message;
            VerifySummary = "校验失败：" + ex.Message;
            OnPropertyChanged(nameof(HasVerifyResult));
            Log("校验失败：" + ex);
            EndTask(task, TaskState.Failed, ex.Message);
        }
        finally
        {
            IsVerifying = false;
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
            ProgressValue = 0;
            RaiseMaintenanceState();
        }
    }

    private async Task RepairVersionAsync()
    {
        if (_selectedInstalled is not { } version || _api == null) return;

        ErrorText = "";
        IsVerifying = true;
        var task = BeginTask($"补全 {version.DisplayName} 的文件");
        _cts = new CancellationTokenSource();
        IsBusy = true;

        try
        {
            var verifier = new VersionVerifier(_paths, _settings, _api);
            var progress = SafeProgress.Create<InstallProgress>(p =>
            {
                StageText = p.Stage;
                ProgressValue = p.Download.Fraction;
                SpeedText = FormatSpeed(p.Download.BytesPerSecond);
                StatusText = $"{p.Download.FilesDone} / {p.Download.FilesTotal} 个文件";
            }, ex => Log("进度更新失败（不影响补全）：" + ex.Message));

            int total = await verifier.RepairAsync(version.Id, progress,
                new Progress<string>(Log), _cts.Token).ConfigureAwait(true);

            StageText = "";
            SpeedText = "";
            ProgressValue = 0;
            StatusText = $"补全完成：核对了 {total} 个文件";
            VerifySummary = $"补全完成（核对 {total} 个文件）";
            OnPropertyChanged(nameof(HasVerifyResult));

            EndTask(task, TaskState.Done, "补全完成");
        }
        catch (OperationCanceledException)
        {
            StatusText = "补全已取消";
            EndTask(task, TaskState.Cancelled, "已取消");
        }
        catch (Exception ex)
        {
            ErrorText = "补全失败：" + ex.Message;
            Log("补全失败：" + ex);
            EndTask(task, TaskState.Failed, ex.Message);
        }
        finally
        {
            IsVerifying = false;
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
            StageText = "";
            SpeedText = "";
            ProgressValue = 0;
            RaiseMaintenanceState();
        }
    }

    private void RaiseMaintenanceState()
    {
        OnPropertyChanged(nameof(CanMaintainVersion));
        (VerifyVersionCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RepairVersionCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (OpenVersionFolderCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }
}
