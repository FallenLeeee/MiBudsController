using CommunityToolkit.Mvvm.ComponentModel;
using MiBudsController.Core.Services;

namespace MiBudsController.ViewModels;

/// <summary>
/// 设置页视图模型：读取/保存开机自启、最小化启动、最小化到托盘、
/// 托盘图标与调试模式等布尔设置，并联动应用状态。
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private const string AutoStartKey = "AutoStart";
    private const string StartMinimizedKey = "StartMinimized";
    private const string MinimizeToTrayKey = "MinimizeToTray";
    private const string ShowTrayIconKey = "ShowTrayIcon";
    private const string DebugModeKey = "DebugMode";

    private readonly AppSettings _settings;

    /// <summary>从设置存储读取各项开关；首次读取用默认值回退。</summary>
    public SettingsViewModel(AppSettings settings)
    {
        _settings = settings;
        _autoStart = ReadBool(AutoStartKey, () => Services.AutoStartService.IsEnabled());
        _startMinimized = ReadBool(StartMinimizedKey, () => false);
        _minimizeToTray = ReadBool(MinimizeToTrayKey, () => true);
        _showTrayIcon = ReadBool(ShowTrayIconKey, () => true);
        _debugMode = ReadBool(DebugModeKey, () => false);
        AppState.ApplyDebugMode(_debugMode);
    }

    /// <summary>是否开机自启。</summary>
    [ObservableProperty]
    private bool _autoStart;

    /// <summary>是否最小化启动（配合自启使用）。</summary>
    [ObservableProperty]
    private bool _startMinimized;

    /// <summary>关闭主窗口时是否最小化到托盘。</summary>
    [ObservableProperty]
    private bool _minimizeToTray;

    /// <summary>是否显示托盘图标。</summary>
    [ObservableProperty]
    private bool _showTrayIcon;

    /// <summary>是否启用调试模式（显示日志页并开启协议捕获）。</summary>
    [ObservableProperty]
    private bool _debugMode;

    /// <summary>自启对应的可执行文件路径。</summary>
    public string AutoStartPath => Services.AutoStartService.GetExecutablePath();

    /// <summary>设置页标题版本文字。</summary>
    public string VersionText
    {
        get
        {
            var asm = typeof(SettingsViewModel).Assembly.GetName();
            string v = asm.Version is null ? "1.0.0" : asm.Version.ToString(3);
            return $"MiBudsController {v}";
        }
    }

    /// <summary>版本号文字，用于设置页其他位置。</summary>
    public string AppVersion
    {
        get
        {
            var asm = typeof(SettingsViewModel).Assembly.GetName();
            return asm.Version is null ? "1.0.0" : asm.Version.ToString(3);
        }
    }

    /// <summary>自启开关变化：写入注册表并保存设置。</summary>
    partial void OnAutoStartChanged(bool value)
    {
        Services.AutoStartService.SetEnabled(value);
        _settings.Set(AutoStartKey, value ? "1" : "0");
        OnPropertyChanged(nameof(AutoStartPath));
    }

    /// <summary>最小化启动开关变化：保存设置。</summary>
    partial void OnStartMinimizedChanged(bool value) =>
        _settings.Set(StartMinimizedKey, value ? "1" : "0");

    /// <summary>关闭到托盘开关变化：保存设置。</summary>
    partial void OnMinimizeToTrayChanged(bool value) =>
        _settings.Set(MinimizeToTrayKey, value ? "1" : "0");

    /// <summary>托盘图标开关变化：保存设置并立即显示/销毁托盘。</summary>
    partial void OnShowTrayIconChanged(bool value)
    {
        _settings.Set(ShowTrayIconKey, value ? "1" : "0");
        AppState.ApplyTrayVisibility(value);
    }

    /// <summary>调试模式开关变化：保存设置并同步日志捕获与界面。</summary>
    partial void OnDebugModeChanged(bool value)
    {
        _settings.Set(DebugModeKey, value ? "1" : "0");
        AppState.ApplyDebugMode(value);
    }

    /// <summary>把设置字符串解析为布尔值，非法值使用传入的默认回调。</summary>
    private bool ReadBool(string key, Func<bool> fallback)
    {
        string? raw = _settings.Get(key);
        return raw switch
        {
            "1" or "true" or "True" => true,
            "0" or "false" or "False" => false,
            _ => fallback(),
        };
    }
}
