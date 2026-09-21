using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using MiBudsController.ViewModels;

namespace MiBudsController.Views;

/// <summary>
/// 主页：展示设备列表、连接状态、电量卡片、降噪快捷设置等，
/// 并负责电量进度条颜色的动态刷新。
/// </summary>
public sealed partial class HomePage : Page
{
    /// <summary>构造时订阅三块电量卡片的属性变化，电量更新后刷新颜色。</summary>
    public HomePage()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyBatteryBrushes();
        Vm.LeftBattery.PropertyChanged += (_, _) => DispatcherQueue.TryEnqueue(ApplyBatteryBrushes);
        Vm.RightBattery.PropertyChanged += (_, _) => DispatcherQueue.TryEnqueue(ApplyBatteryBrushes);
        Vm.CaseBattery.PropertyChanged += (_, _) => DispatcherQueue.TryEnqueue(ApplyBatteryBrushes);
    }

    /// <summary>主页视图模型。</summary>
    public MainViewModel Vm { get; } = AppState.Main;

    /// <summary>降噪快捷设置视图模型。</summary>
    public NoiseViewModel Noise { get; } = AppState.Noise;

    /// <summary>页面进入时初始化扫描，并立即应用一次电量颜色。</summary>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _ = Vm.InitializeAsync();
        ApplyBatteryBrushes();
    }

    /// <summary>
    /// 只有进度条使用充电/强调色；数字文字保持主题默认色。
    /// 未连接或资源未就绪时静默跳过。
    /// </summary>
    private void ApplyBatteryBrushes()
    {
        try
        {
            LeftBatteryBar.Foreground = Vm.LeftBattery.LevelBrush;
            RightBatteryBar.Foreground = Vm.RightBattery.LevelBrush;
            CaseBatteryBar.Foreground = Vm.CaseBattery.LevelBrush;
        }
        catch
        {
            // 元素可能尚未创建完成。
        }
    }
}
