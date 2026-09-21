using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using MiBudsController.Core.Services;
using Windows.ApplicationModel.DataTransfer;

namespace MiBudsController.ViewModels;

/// <summary>
/// 协议日志页面视图模型：展示内存日志条目，支持清空、复制文本、
/// 复制日志路径以及手动开启/停止磁盘记录。
/// </summary>
public partial class LogViewModel : ObservableObject
{
    private readonly DispatcherQueue _dispatcher;
    private readonly LogService _logs;

    /// <summary>订阅日志服务新增条目事件，并在 UI 线程追加到集合。</summary>
    public LogViewModel(LogService logs)
    {
        _logs = logs;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        logs.EntryAdded += entry => _dispatcher.TryEnqueue(() =>
        {
            Entries.Add(entry);
            EntryCount = Entries.Count;
            IsEmpty = Entries.Count == 0;
        });
        IsEmpty = true;
        LogPath = logs.LogDirectory;
        SyncCaptureState();
    }

    /// <summary>当前会话的日志条目集合。</summary>
    public ObservableCollection<LogEntry> Entries { get; } = new();

    /// <summary>日志文件的磁盘目录。</summary>
    public string LogPath { get; }

    /// <summary>日志条目总数。</summary>
    [ObservableProperty]
    private int _entryCount;

    /// <summary>日志是否为空。</summary>
    [ObservableProperty]
    private bool _isEmpty = true;

    /// <summary>是否已开启内存捕获（调试模式）。</summary>
    [ObservableProperty]
    private bool _isCaptureEnabled;

    /// <summary>是否正在写入磁盘。</summary>
    [ObservableProperty]
    private bool _isDiskLogEnabled;

    /// <summary>磁盘记录按钮文字。</summary>
    public string DiskLogButtonText => IsDiskLogEnabled ? "停止写入磁盘" : "开始记录";

    /// <summary>日志状态提示文案。</summary>
    public string CaptureHint =>
        IsCaptureEnabled
            ? IsDiskLogEnabled
                ? "调试模式 · 记录中（内存 + 磁盘）"
                : "调试模式 · 记录中（仅内存，未写盘）"
            : "调试模式未开启 · 未记录协议日志";

    /// <summary>从日志服务同步捕获/磁盘状态，并刷新派生绑定。</summary>
    public void SyncCaptureState()
    {
        IsCaptureEnabled = _logs.IsCaptureEnabled;
        IsDiskLogEnabled = _logs.IsDiskLogEnabled;
        OnPropertyChanged(nameof(DiskLogButtonText));
        OnPropertyChanged(nameof(CaptureHint));
        OnPropertyChanged(nameof(CanStartDiskLog));
    }

    /// <summary>未开启调试模式前不允许开始磁盘记录。</summary>
    public bool CanStartDiskLog => IsCaptureEnabled;

    /// <summary>清空界面条目和日志服务的内存缓冲。</summary>
    [RelayCommand]
    private void Clear()
    {
        Entries.Clear();
        EntryCount = 0;
        IsEmpty = true;
        _logs.ClearSessionBuffer();
    }

    /// <summary>把当前日志拼成文本并复制到剪贴板。</summary>
    [RelayCommand]
    private void Copy()
    {
        string text = string.Join(
            Environment.NewLine,
            Entries.Select(e => $"[{e.Time}] {e.DirectionText} {e.Hex}  {e.Summary}"));
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    /// <summary>复制日志目录路径到剪贴板。</summary>
    [RelayCommand]
    private void CopyLogPath()
    {
        var package = new DataPackage();
        package.SetText(_logs.LogDirectory);
        Clipboard.SetContent(package);
    }

    /// <summary>切换磁盘记录；调试模式未开启时仅刷新状态。</summary>
    [RelayCommand]
    private void ToggleDiskLog()
    {
        if (!_logs.IsCaptureEnabled)
        {
            SyncCaptureState();
            return;
        }

        _logs.IsDiskLogEnabled = !_logs.IsDiskLogEnabled;
        SyncCaptureState();
    }
}
