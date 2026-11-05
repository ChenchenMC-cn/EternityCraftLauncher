// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using ECL.ViewModels;

namespace ECL.Models;

/// <summary>A row in the install page's Minecraft version list.</summary>
public sealed class VersionItem : ObservableObject
{
    private bool _isInstalled;

    public VersionItem(ManifestEntry entry)
    {
        Id = entry.Id;
        Type = entry.Type;
        Url = entry.Url;
        ReleaseTime = entry.ReleaseTime;
    }

    public string Id { get; }
    public string Type { get; }
    public string Url { get; }
    public DateTimeOffset ReleaseTime { get; }

    public bool IsInstalled
    {
        get => _isInstalled;
        set
        {
            if (!SetProperty(ref _isInstalled, value)) return;
            OnPropertyChanged(nameof(StateText));
        }
    }

    public string StateText => IsInstalled ? Services.Localization.T("已安装") : "";

    public string TypeLabel => Type switch
    {
        "release" => Services.Localization.T("正式版"),
        "snapshot" => Services.Localization.T("快照"),
        "old_beta" => "Beta",
        "old_alpha" => "Alpha",
        _ => Type
    };

    public bool IsRelease => Type == "release";

    public string ReleaseText => ReleaseTime.LocalDateTime.ToString("yyyy-MM-dd");
}
