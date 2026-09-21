using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MiBudsController.ViewModels;

namespace MiBudsController.Views;

/// <summary>
/// 设置页：开机自启、启动方式、托盘行为、调试模式等选项的宿主页面，
/// 并提供打开系统声音/蓝牙设置和迷你面板的入口。
/// </summary>
public sealed partial class SettingsPage : Page
{
    /// <summary>初始化页面组件。</summary>
    public SettingsPage()
    {
        InitializeComponent();
    }

    /// <summary>设置视图模型。</summary>
    public SettingsViewModel Vm { get; } = AppState.SettingsVm;

    /// <summary>主视图模型（页面其他绑定使用）。</summary>
    public MainViewModel Main { get; } = AppState.Main;

    /// <summary>打开迷你控制面板。</summary>
    private void OpenMini_Click(object sender, RoutedEventArgs e) =>
        AppState.ShowMiniPanel();

    /// <summary>弹出系统托盘使用提示对话框。</summary>
    private async void ShowTrayHint_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "系统托盘",
            Content = "若任务栏通知区域未显示图标，请点击「^」展开隐藏图标，或在 Windows 设置中将本应用设为显示。\n\n左键：打开 / 关闭迷你控制面板\n右键：打开主界面 / 退出",
            CloseButtonText = "知道了",
            DefaultButton = ContentDialogButton.Close,
        };
        await dialog.ShowAsync();
    }

    /// <summary>打开 Windows 声音设置。</summary>
    private void OpenWindowsSoundSettings_Click(object sender, RoutedEventArgs e) =>
        OpenUri("ms-settings:sound");

    /// <summary>打开 Windows 蓝牙设置。</summary>
    private void OpenBluetoothSettings_Click(object sender, RoutedEventArgs e) =>
        OpenUri("ms-settings:bluetooth");

    /// <summary>用系统默认应用打开 URI；受限环境下失败时静默忽略。</summary>
    private static void OpenUri(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch
        {
            // 受限环境下 Shell 启动可能失败，静默忽略。
        }
    }
}
