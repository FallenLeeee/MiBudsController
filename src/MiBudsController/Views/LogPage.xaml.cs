using Microsoft.UI.Xaml.Controls;
using MiBudsController.ViewModels;

namespace MiBudsController.Views;

/// <summary>协议日志页：展示调试模式下的内存协议日志及磁盘记录控制。</summary>
public sealed partial class LogPage : Page
{
    /// <summary>初始化页面组件。</summary>
    public LogPage()
    {
        InitializeComponent();
    }

    /// <summary>日志页视图模型。</summary>
    public LogViewModel Vm { get; } = AppState.Log;
}
