using Microsoft.UI.Xaml.Controls;
using MiBudsController.ViewModels;

namespace MiBudsController.Views;

/// <summary>音效页：承载低延迟模式等音效设置。</summary>
public sealed partial class SoundEffectsPage : Page
{
    /// <summary>初始化页面组件。</summary>
    public SoundEffectsPage()
    {
        InitializeComponent();
    }

    /// <summary>音效视图模型。</summary>
    public SoundEffectsViewModel Vm { get; } = AppState.Sound;
}
