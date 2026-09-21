using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using MiBudsController.Core.Models;

namespace MiBudsController.ViewModels;

/// <summary>
/// 单只耳机/充电盒的电量卡片视图模型：
/// 负责电量百分比、充电状态和对应颜色画刷的呈现。
/// </summary>
public partial class BatteryCardViewModel : ObservableObject
{
    private static readonly Brush ChargingBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0x22, 0xC5, 0x5E));
    private static readonly Brush FallbackAccentBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0x00, 0x78, 0xD4));
    private static Brush? _unknownBrush;

    /// <summary>创建一个标题为“左耳/右耳/充电盒”的电量卡片。</summary>
    public BatteryCardViewModel(string title)
    {
        Title = title;
    }

    /// <summary>卡片标题。</summary>
    public string Title { get; }

    /// <summary>当前电量读数；未连接时为 null。</summary>
    [ObservableProperty]
    private BatteryReading? _reading;

    /// <summary>界面显示的电量文字。</summary>
    public string LevelText => Reading is null ? "--" : $"{Reading.Level}%";

    /// <summary>界面显示的状态文字。</summary>
    public string StateText => Reading is null ? "未连接" : Reading.IsCharging ? "充电中" : "正常";

    /// <summary>是否正在充电。</summary>
    public bool IsCharging => Reading?.IsCharging ?? false;

    /// <summary>进度条百分比值（0-100）。</summary>
    public double PercentValue => Reading?.Level ?? 0;

    /// <summary>是否已有电量读数。</summary>
    public bool HasReading => Reading is not null;

    /// <summary>电量颜色：充电中绿色，正常跟随 Windows 强调色，未连接用次要文字色。</summary>
    public Brush LevelBrush
    {
        get
        {
            if (Reading is null)
            {
                return UnknownBrush;
            }

            if (Reading.IsCharging)
            {
                return ChargingBrush;
            }

            return AccentBrush;
        }
    }

    /// <summary>未连接时的次要文字色，只解析一次并缓存。</summary>
    private static Brush UnknownBrush
    {
        get
        {
            if (_unknownBrush is not null)
            {
                return _unknownBrush;
            }

            if (Application.Current?.Resources.TryGetValue("TextFillColorSecondaryBrush", out object? res) == true
                && res is Brush brush)
            {
                _unknownBrush = brush;
                return brush;
            }

            _unknownBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 0x8A, 0x8A, 0x8A));
            return _unknownBrush;
        }
    }

    /// <summary>正常时的强调色，取系统资源，取不到时用兜底蓝色。</summary>
    private static Brush AccentBrush
    {
        get
        {
            if (Application.Current?.Resources.TryGetValue("AccentTextFillColorPrimaryBrush", out object? res) == true
                && res is Brush brush)
            {
                return brush;
            }

            return FallbackAccentBrush;
        }
    }

    /// <summary>电量变化时联动刷新所有派生的绑定属性。</summary>
    partial void OnReadingChanged(BatteryReading? value)
    {
        OnPropertyChanged(nameof(LevelText));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(IsCharging));
        OnPropertyChanged(nameof(PercentValue));
        OnPropertyChanged(nameof(HasReading));
        OnPropertyChanged(nameof(LevelBrush));
    }

    /// <summary>主题或资源变化后强制刷新一次颜色画刷。</summary>
    public void RefreshBrush() => OnPropertyChanged(nameof(LevelBrush));
}
