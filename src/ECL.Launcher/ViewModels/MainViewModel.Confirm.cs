// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using ECL.Services;

namespace ECL.ViewModels;

/// <summary>
/// Pre-install confirmation, drawn as an overlay inside the main window.
///
/// <para>A separate Window was tried first and rendered blank: it is its own visual root,
/// so theme lookups and the initial layout behaved differently from the rest of the app.
/// An overlay lives in the same tree, which removes that whole class of failure and makes
/// it screenshot-verifiable.</para>
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>One row of the confirmation list.</summary>
    public sealed record ConfirmRow(string Name, string Detail, string SizeText);

    private TaskCompletionSource<bool>? _confirmTcs;

    private bool _isConfirmVisible;
    private string _confirmHeading = "";
    private string _confirmSub = "";
    private string _confirmWarning = "";
    private bool _hasConfirmWarning;

    public ObservableCollection<ConfirmRow> ConfirmRows { get; } = new();

    public bool IsConfirmVisible
    {
        get => _isConfirmVisible;
        private set => SetProperty(ref _isConfirmVisible, value);
    }

    public string ConfirmHeading
    {
        get => _confirmHeading;
        private set => SetProperty(ref _confirmHeading, value);
    }

    public string ConfirmSub
    {
        get => _confirmSub;
        private set => SetProperty(ref _confirmSub, value);
    }

    public string ConfirmWarning
    {
        get => _confirmWarning;
        private set
        {
            if (SetProperty(ref _confirmWarning, value))
                HasConfirmWarning = !string.IsNullOrWhiteSpace(value);
        }
    }

    public bool HasConfirmWarning
    {
        get => _hasConfirmWarning;
        private set => SetProperty(ref _hasConfirmWarning, value);
    }

    public System.Windows.Input.ICommand ConfirmInstallCommand { get; private set; } = null!;
    public System.Windows.Input.ICommand CancelConfirmCommand { get; private set; } = null!;

    /// <summary>
    /// Shows the overlay and waits for the user. Returns false when they cancel, in which
    /// case the caller must not download anything.
    /// </summary>
    public async Task<bool> ConfirmAsync(string title, DependencyPlan plan, IReadOnlyCollection<string> alreadyPresent,
        string? extraWarning = null)
    {
        ConfirmRows.Clear();

        long total = 0;
        foreach (var v in plan.Order)
        {
            string file = v.File?.FileName ?? "";
            if (alreadyPresent.Contains(file)) continue;

            total += v.File?.Size ?? 0;
            ConfirmRows.Add(new ConfirmRow(
                v.Name,
                string.IsNullOrWhiteSpace(v.VersionNumber) ? file : v.VersionNumber,
                v.File == null ? "-" : v.SizeText));
        }

        // Everything is already on disk: no point asking — unless there is a warning to
        // show (e.g. "the profile has to be downloaded first").
        if (ConfirmRows.Count == 0 && string.IsNullOrWhiteSpace(extraWarning))
            return true;

        int deps = ConfirmRows.Count - 1;
        ConfirmHeading = ConfirmRows.Count == 0
            ? string.Format(Services.Localization.T("将安装 {0}"), title)
            : deps > 0
            ? string.Format(Services.Localization.T("将安装 {0}：1 个模组 + {1} 个前置"), title, deps)
            : string.Format(Services.Localization.T("将安装 {0}"), title);

        ConfirmSub = string.Format(
            Services.Localization.T("共 {0} 个文件，约 {1} MB。已存在的不会重复下载。"),
            ConfirmRows.Count, (total / 1024.0 / 1024.0).ToString("F1"));

        var warnings = new List<string>();
        if (!string.IsNullOrWhiteSpace(extraWarning)) warnings.Add(extraWarning);
        if (plan.Missing.Count > 0)
            warnings.Add(string.Format(
                Services.Localization.T("有 {0} 项前置找不到兼容版本，安装后可能无法启动："), plan.Missing.Count)
                + "\n" + string.Join("\n", plan.Missing));

        ConfirmWarning = string.Join("\n", warnings);

        _confirmTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        IsConfirmVisible = true;

        try
        {
            return await _confirmTcs.Task.ConfigureAwait(true);
        }
        finally
        {
            IsConfirmVisible = false;
            ConfirmRows.Clear();
            _confirmTcs = null;
        }
    }

    /// <summary>Fills the overlay with sample rows (used by --shot-confirm).</summary>
    public void ShowConfirmPreview()
    {
        ConfirmRows.Clear();
        ConfirmRows.Add(new ConfirmRow("Sodium", "0.5.13", "949 KB"));
        ConfirmRows.Add(new ConfirmRow("Indium", "1.0.30", "210 KB"));
        ConfirmRows.Add(new ConfirmRow("Iris", "1.7.6", "528 KB"));

        ConfirmHeading = string.Format(Services.Localization.T("将安装 {0}：1 个模组 + {1} 个前置"), "Iris", 2);
        ConfirmSub = string.Format(
            Services.Localization.T("共 {0} 个文件，约 {1} MB。已存在的不会重复下载。"), 3, "1.7");
        ConfirmWarning = string.Format(Services.Localization.T(
            "还没有 {0} 的 {1} 档案：将先下载游戏本体与资源（约 {2}），再装整合包内容。"),
            "1.20.1", "fabric", "700 MB");
        IsConfirmVisible = true;
    }

    /// <summary>
    /// Confirms something that has no file list — "update this pack" with counts, say.
    /// Same overlay so the look and the cancel semantics stay identical.
    /// </summary>
    public async Task<bool> ConfirmMessageAsync(string heading, string detail)
    {
        ConfirmRows.Clear();
        ConfirmHeading = heading;
        ConfirmSub = detail;
        ConfirmWarning = "";

        _confirmTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        IsConfirmVisible = true;

        try
        {
            return await _confirmTcs.Task.ConfigureAwait(true);
        }
        finally
        {
            IsConfirmVisible = false;
            ConfirmRows.Clear();
            _confirmTcs = null;
        }
    }

    private void ConfirmInstall(object? _) => _confirmTcs?.TrySetResult(true);

    private void CancelConfirm(object? _) => _confirmTcs?.TrySetResult(false);
}
