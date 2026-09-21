using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using MiBudsController.Core.Protocol;
using MiBudsController.Core.Services;

namespace MiBudsController.ViewModels;

/// <summary>
/// 降噪/通透设置视图模型：承载三种模式、降噪强度、通透预设和智能降噪，
/// 负责把界面选择写入耳机，并把耳机回读状态同步回界面。
/// </summary>
public partial class NoiseViewModel : ObservableObject
{
    private readonly EarbudsClient _client;
    private readonly DispatcherQueue _dispatcher;
    private CancellationTokenSource? _levelDebounce;
    private bool _syncing;

    /// <summary>订阅模式与配置变化事件，回调统一切回 UI 线程。</summary>
    public NoiseViewModel(EarbudsClient client)
    {
        _client = client;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _client.AncModeChanged += mode => _dispatcher.TryEnqueue(() => ApplyAncMode(mode));
        _client.ConfigChanged += (id, values) => _dispatcher.TryEnqueue(() => ApplyConfig(id, values));
    }

    /// <summary>降噪模式选项：通透、降噪、关闭。</summary>
    public IReadOnlyList<EnumOption> NoiseModes { get; } = new[]
    {
        new EnumOption(0, "通透"),
        new EnumOption(1, "降噪"),
        new EnumOption(2, "关闭"),
    };

    // 显示顺序与手机端一致；协议值显式给出（0 标准、2 环境增强、1 人声增强）。
    public IReadOnlyList<EnumOption> TransparencyPresets { get; } = new[]
    {
        new EnumOption(0, "标准"),
        new EnumOption(2, "环境增强"),
        new EnumOption(1, "人声增强"),
    };

    /// <summary>当前选中的降噪模式。</summary>
    [ObservableProperty]
    private EnumOption _selectedNoiseMode = new(2, "关闭");

    /// <summary>手动降噪强度（0-19，界面显示 1-20）。</summary>
    [ObservableProperty]
    private int _ancLevel = 10;

    /// <summary>降噪强度显示文字。</summary>
    public string AncLevelText => $"{AncLevel + 1} / 20";

    /// <summary>降噪强度的提示文案（智能降噪开启时不可手动调节）。</summary>
    public string AncLevelHint =>
        SmartNoiseCancelling
            ? "已由智能降噪接管，手动强度不可用"
            : "拖动调节降噪强度（0–19，界面显示 1–20）";

    /// <summary>当前选中的通透预设。</summary>
    [ObservableProperty]
    private EnumOption _selectedTransparencyPreset = new(0, "标准");

    /// <summary>智能降噪开关（对应协议 Config 0x66）。</summary>
    [ObservableProperty]
    private bool _smartNoiseCancelling;

    // UI 值为 0=通透、1=降噪、2=关闭（与 NoiseModes 一致，勿与协议枚举混淆）。
    public bool IsAncMode => SelectedNoiseMode.Value == 1;

    public bool IsTransparencyMode => SelectedNoiseMode.Value == 0;

    public bool IsOffMode => SelectedNoiseMode.Value == 2;

    /// <summary>智能降噪与手动强度互斥，仅降噪模式且未开智能时可调。</summary>
    public bool IsAncLevelEnabled => IsAncMode && !SmartNoiseCancelling;

    public bool IsSmartNoiseEnabled => IsAncMode;

    public bool IsTransparencyPresetsEnabled => IsTransparencyMode;

    public bool ShowAncBranch => IsAncMode;

    public bool ShowTransparencyBranch => IsTransparencyMode;

    public bool ShowOffHint => IsOffMode;

    /// <summary>降噪模式变化时刷新分支可见性，并写入协议 SetAnc。</summary>
    partial void OnSelectedNoiseModeChanged(EnumOption value)
    {
        NotifyBranches();
        if (_syncing || !_client.IsConnected)
        {
            return;
        }

        int mode = value.Value switch
        {
            0 => RcspAncModes.Transparency,
            1 => RcspAncModes.NoiseCancelling,
            _ => RcspAncModes.Off,
        };
        _client.SetAncMode(mode);
    }

    /// <summary>强度变化后刷新文字，并在 250ms 防抖后发送，避免拖动连发。</summary>
    partial void OnAncLevelChanged(int value)
    {
        OnPropertyChanged(nameof(AncLevelText));
        if (!_syncing && _client.IsConnected && IsAncLevelEnabled)
        {
            ScheduleAncLevel();
        }
    }

    /// <summary>通透预设变化时写入 EffectStrength 的 Target=Transparency。</summary>
    partial void OnSelectedTransparencyPresetChanged(EnumOption value)
    {
        if (!_syncing && _client.IsConnected && IsTransparencyMode)
        {
            _client.SetConfig(RcspConfigIds.EffectStrength, RcspStrengthTargets.Transparency, (byte)value.Value);
        }
    }

    /// <summary>智能降噪开关变化时写入 Config 0x66。</summary>
    partial void OnSmartNoiseCancellingChanged(bool value)
    {
        NotifyBranches();
        if (!_syncing && _client.IsConnected)
        {
            _client.SetConfig(RcspConfigIds.SmartNoise, value ? (byte)1 : (byte)0);
        }
    }

    /// <summary>通知界面各分支可见性和派生属性统一刷新。</summary>
    private void NotifyBranches()
    {
        OnPropertyChanged(nameof(IsAncMode));
        OnPropertyChanged(nameof(IsTransparencyMode));
        OnPropertyChanged(nameof(IsOffMode));
        OnPropertyChanged(nameof(IsAncLevelEnabled));
        OnPropertyChanged(nameof(IsSmartNoiseEnabled));
        OnPropertyChanged(nameof(IsTransparencyPresetsEnabled));
        OnPropertyChanged(nameof(ShowAncBranch));
        OnPropertyChanged(nameof(ShowTransparencyBranch));
        OnPropertyChanged(nameof(ShowOffHint));
        OnPropertyChanged(nameof(AncLevelHint));
        OnPropertyChanged(nameof(AncLevelText));
    }

    /// <summary>把协议模式值映射回界面选项（协议 0 关闭、1 降噪、2 通透）。</summary>
    private void ApplyAncMode(int mode)
    {
        // 协议 0/1/2 分别对应关闭/降噪/通透，映射到界面 Value 2/1/0。
        EnumOption? option = mode switch
        {
            RcspAncModes.NoiseCancelling => NoiseModes.FirstOrDefault(m => m.Value == 1),
            RcspAncModes.Transparency => NoiseModes.FirstOrDefault(m => m.Value == 0),
            _ => NoiseModes.FirstOrDefault(m => m.Value == 2),
        };
        if (option is null)
        {
            return;
        }

        _syncing = true;
        SelectedNoiseMode = option;
        _syncing = false;
        NotifyBranches();
    }

    /// <summary>处理耳机回读的强度与智能降噪配置，同步 UI 并抑制回写。</summary>
    private void ApplyConfig(byte id, byte[] values)
    {
        switch (id)
        {
            case RcspConfigIds.EffectStrength when values.Length >= 2:
                _syncing = true;
                if (values[0] == RcspStrengthTargets.Anc)
                {
                    AncLevel = Math.Clamp((int)values[1], 0, 19);
                }
                else
                {
                    EnumOption? preset = TransparencyPresets.FirstOrDefault(p => p.Value == values[1]);
                    if (preset is not null)
                    {
                        SelectedTransparencyPreset = preset;
                    }
                }

                _syncing = false;
                break;
            case RcspConfigIds.SmartNoise when values.Length >= 1:
                _syncing = true;
                SmartNoiseCancelling = values[0] != 0;
                _syncing = false;
                NotifyBranches();
                break;
        }
    }

    /// <summary>防抖调度发送降噪强度，新值会取消上一次未发送任务。</summary>
    private void ScheduleAncLevel()
    {
        _levelDebounce?.Cancel();
        var cts = new CancellationTokenSource();
        _levelDebounce = cts;
        int level = Math.Clamp(AncLevel, 0, 19);
        _ = DebouncedSendAsync(cts, level);
    }

    /// <summary>延迟 250ms 后发送强度；等待期间智能降噪或被新值取代则放弃。</summary>
    private async Task DebouncedSendAsync(CancellationTokenSource cts, int level)
    {
        try
        {
            await Task.Delay(250, cts.Token);
            // 互斥保证：智能降噪开启时绝不写入手动强度。
            if (!cts.IsCancellationRequested && _client.IsConnected && !SmartNoiseCancelling)
            {
                _client.SetConfig(RcspConfigIds.EffectStrength, RcspStrengthTargets.Anc, (byte)level);
            }
        }
        catch (OperationCanceledException)
        {
            // 已被更新的滑块值取代。
        }
    }
}
