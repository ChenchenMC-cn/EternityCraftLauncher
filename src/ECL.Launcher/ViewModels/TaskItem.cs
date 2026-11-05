// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 ChenchenMC <https://github.com/ChenchenMC-cn/EternityCraftLauncher>
using System.Windows.Input;

namespace ECL.ViewModels;

public enum TaskState
{
    Running = 0,
    Done = 1,
    Failed = 2,
    Cancelled = 3
}

/// <summary>
/// One entry in the task menu: an install, a download, a launch — anything that
/// runs long enough to want a cancel button.
/// </summary>
public sealed class TaskItem : ObservableObject
{
    public TaskItem(string title, ICommand cancelCommand)
    {
        Title = title;
        CancelCommand = cancelCommand;
        _detail = "准备中…";
    }

    public string Title { get; }

    public DateTime StartedAt { get; } = DateTime.Now;

    /// <summary>Cancels this task; provided by the view model.</summary>
    public ICommand CancelCommand { get; }

    private string _detail;
    /// <summary>Last log line from the running operation — that is the progress.</summary>
    public string Detail
    {
        get => _detail;
        set => SetProperty(ref _detail, value);
    }

    private TaskState _state = TaskState.Running;
    public TaskState State
    {
        get => _state;
        set
        {
            if (!SetProperty(ref _state, value)) return;
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(IsFinished));
            OnPropertyChanged(nameof(StateLabel));
            OnPropertyChanged(nameof(StateBrush));
        }
    }

    public bool IsRunning => _state == TaskState.Running;
    public bool IsFinished => _state != TaskState.Running;

    public string StateLabel => _state switch
    {
        TaskState.Running => "进行中",
        TaskState.Done => "完成",
        TaskState.Failed => "失败",
        TaskState.Cancelled => "已取消",
        _ => ""
    };

    /// <summary>Traffic-light colour for the status dot.</summary>
    public string StateBrush => _state switch
    {
        TaskState.Running => "#3DDC97",
        TaskState.Done => "#7C8B9A",
        TaskState.Failed => "#FF6B6B",
        TaskState.Cancelled => "#F5B942",
        _ => "#7C8B9A"
    };

    public string TimeLabel => StartedAt.ToString("HH:mm:ss");
}
