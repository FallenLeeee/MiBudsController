using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using MiBudsController.ViewModels;

namespace MiBudsController.Views;

/// <summary>空间音频页：控制沉浸声、音频模式、场景渲染和头部跟踪。</summary>
public sealed partial class SpatialPage : Page
{
    /// <summary>初始化页面组件。</summary>
    public SpatialPage()
    {
        InitializeComponent();
    }

    /// <summary>空间音频视图模型。</summary>
    public SpatialViewModel Vm { get; } = AppState.Spatial;

    /// <summary>进入页面时刷新绑定，保证动态文案与连接状态即时生效。</summary>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Bindings.Update();
    }
}
