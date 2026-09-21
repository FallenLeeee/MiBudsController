using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using MiBudsController.ViewModels;

namespace MiBudsController.Controls;

/// <summary>
/// 分段选择控件：把一组 EnumOption 渲染成单选按钮，
/// 常用于降噪模式、空间音频模式等选项切换，并支持整体禁用。
/// </summary>
public sealed partial class SegmentedControl : UserControl
{
    private bool _updating;

    /// <summary>初始化并给控件加入轻微入场动画。</summary>
    public SegmentedControl()
    {
        InitializeComponent();
        Transitions = new TransitionCollection
        {
            new EntranceThemeTransition { IsStaggeringEnabled = false },
        };
    }

    /// <summary>选项数据源依赖属性。</summary>
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource),
        typeof(IEnumerable<EnumOption>),
        typeof(SegmentedControl),
        new PropertyMetadata(null, OnItemsSourceChanged));

    /// <summary>当前选中项依赖属性。</summary>
    public static readonly DependencyProperty SelectedOptionProperty = DependencyProperty.Register(
        nameof(SelectedOption),
        typeof(EnumOption),
        typeof(SegmentedControl),
        new PropertyMetadata(null, OnSelectedOptionChanged));

    /// <summary>是否允许用户点击选项依赖属性。</summary>
    public static readonly DependencyProperty IsItemsEnabledProperty = DependencyProperty.Register(
        nameof(IsItemsEnabled),
        typeof(bool),
        typeof(SegmentedControl),
        new PropertyMetadata(true, OnIsItemsEnabledChanged));

    /// <summary>选项数据源。</summary>
    public IEnumerable<EnumOption>? ItemsSource
    {
        get => (IEnumerable<EnumOption>?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public EnumOption? SelectedOption
    {
        get => (EnumOption?)GetValue(SelectedOptionProperty);
        set => SetValue(SelectedOptionProperty, value);
    }

    public bool IsItemsEnabled
    {
        get => (bool)GetValue(IsItemsEnabledProperty);
        set => SetValue(IsItemsEnabledProperty, value);
    }

    /// <summary>数据源变化时重建选项按钮。</summary>
    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((SegmentedControl)d).Rebuild();

    /// <summary>选中项变化时同步按钮勾选状态。</summary>
    private static void OnSelectedOptionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((SegmentedControl)d).SyncSelection();

    /// <summary>禁用状态变化时刷新所有按钮。</summary>
    private static void OnIsItemsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((SegmentedControl)d).ApplyEnabled();

    /// <summary>
    /// 按 ItemsSource 重建按钮：每一项占一列，使用相同的单选按钮样式，
    /// 并以 Tag 保存对应的 EnumOption 供点击时回传。
    /// </summary>
    private void Rebuild()
    {
        RootPanel.Children.Clear();
        RootPanel.ColumnDefinitions.Clear();
        if (ItemsSource is null)
        {
            return;
        }

        List<EnumOption> options = ItemsSource.ToList();
        for (int i = 0; i < options.Count; i++)
        {
            RootPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var button = new RadioButton
            {
                Style = (Style)Resources["SegmentedRadioButtonStyle"],
                Content = options[i].Label,
                Tag = options[i],
                GroupName = $"SegmentedGroup{GetHashCode()}",
                MinHeight = 36,
                MinWidth = 0,
                IsChecked = Equals(options[i], SelectedOption),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            Grid.SetColumn(button, i);
            button.Checked += OnButtonChecked;
            RootPanel.Children.Add(button);
        }

        ApplyEnabled();
    }

    /// <summary>把外部设置的 SelectedOption 同步到界面按钮，避免回调冲突。</summary>
    private void SyncSelection()
    {
        if (_updating)
        {
            return;
        }

        _updating = true;
        foreach (UIElement child in RootPanel.Children)
        {
            if (child is RadioButton button)
            {
                button.IsChecked = button.Tag is EnumOption option && Equals(option, SelectedOption);
            }
        }

        _updating = false;
    }

    /// <summary>用户选中某个按钮后更新 SelectedOption，并防止内部回写造成死循环。</summary>
    private void OnButtonChecked(object sender, RoutedEventArgs e)
    {
        if (_updating
            || sender is not RadioButton { Tag: EnumOption option } button
            || button.IsChecked != true)
        {
            return;
        }

        if (!Equals(SelectedOption, option))
        {
            _updating = true;
            SelectedOption = option;
            _updating = false;
        }
    }

    /// <summary>按 IsItemsEnabled 统一启用或禁用所有选项按钮。</summary>
    private void ApplyEnabled()
    {
        foreach (UIElement child in RootPanel.Children)
        {
            if (child is RadioButton button)
            {
                button.IsEnabled = IsItemsEnabled;
            }
        }
    }
}
