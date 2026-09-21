using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using MiBudsController.Core.Protocol;
using MiBudsController.Core.Services;

namespace MiBudsController.ViewModels;

/// <summary>
/// 空间音频设置视图模型：承载沉浸声开关、音频模式（小米/杜比）、
/// 场景模式和头部跟踪，并把设置写入协议 Config 0x1D/0x1E 等通道。
/// </summary>
public partial class SpatialViewModel : ObservableObject
{
    private readonly EarbudsClient _client;
    private readonly DispatcherQueue _dispatcher;
    private bool _syncing;
    private DateTimeOffset _ignoreSpatialReadbackUntil = DateTimeOffset.MinValue;

    /// <summary>订阅配置回读与连接状态事件，回调统一切回 UI 线程。</summary>
    public SpatialViewModel(EarbudsClient client)
    {
        _client = client;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _client.ConfigChanged += (id, values) => _dispatcher.TryEnqueue(() => ApplyConfig(id, values));
        _client.Disconnected += _ => _dispatcher.TryEnqueue(NotifyDerived);
        _client.Connected += () => _dispatcher.TryEnqueue(NotifyDerived);

        _selectedAudioMode = AudioModes[0];
        _selectedSceneMode = SceneModes[0];
    }

    /// <summary>是否已连接耳机。</summary>
    public bool IsConnected => _client.IsConnected;

    /// <summary>连接状态提示文字。</summary>
    public string ConnectionHint =>
        IsConnected
            ? "更改会写入耳机"
            : "未连接耳机 · 界面可切换，写入协议需连接后生效";

    /// <summary>空间音频引擎选项：小米空间音频 / 杜比空间音频。</summary>
    public IReadOnlyList<EnumOption> AudioModes { get; } = new[]
    {
        new EnumOption(0, "小米空间音频"),
        new EnumOption(1, "杜比空间音频"),
    };

    /// <summary>场景渲染选项：标准、音乐、视频、游戏、听书。</summary>
    public IReadOnlyList<EnumOption> SceneModes { get; } = new[]
    {
        new EnumOption(1, "标准"),
        new EnumOption(2, "音乐"),
        new EnumOption(3, "视频"),
        new EnumOption(4, "游戏"),
        new EnumOption(5, "听书"),
    };

    /// <summary>沉浸声总开关。</summary>
    [ObservableProperty]
    private bool _spatialAudioEnabled;

    /// <summary>当前选中的音频模式。</summary>
    [ObservableProperty]
    private EnumOption _selectedAudioMode;

    /// <summary>当前选中的场景模式。</summary>
    [ObservableProperty]
    private EnumOption _selectedSceneMode;

    /// <summary>头部跟踪开关。</summary>
    [ObservableProperty]
    private bool _headTracking;

    /// <summary>空间音频写入状态提示（未连接等场景说明）。</summary>
    [ObservableProperty]
    private string _spatialWriteStatus = string.Empty;

    /// <summary>音频模式在选项列表中的索引，供滑块绑定。</summary>
    public int SelectedAudioModeIndex
    {
        get
        {
            for (int i = 0; i < AudioModes.Count; i++)
            {
                if (AudioModes[i].Value == SelectedAudioMode.Value)
                {
                    return i;
                }
            }

            return -1;
        }
        set
        {
            if (value >= 0 && value < AudioModes.Count)
            {
                SelectedAudioMode = AudioModes[value];
            }
        }
    }

    /// <summary>场景模式在选项列表中的索引，供滑块绑定。</summary>
    public int SelectedSceneModeIndex
    {
        get
        {
            for (int i = 0; i < SceneModes.Count; i++)
            {
                if (SceneModes[i].Value == SelectedSceneMode.Value)
                {
                    return i;
                }
            }

            return 0;
        }
        set
        {
            if (value >= 0 && value < SceneModes.Count)
            {
                SelectedSceneMode = SceneModes[value];
            }
        }
    }

    /// <summary>音频模式可用性：沉浸声开启后可选。</summary>
    public bool IsAudioModeEnabled => SpatialAudioEnabled;

    /// <summary>是否为小米空间音频（启用沉浸声且选择小米模式）。</summary>
    public bool IsXiaomiSpatialAudio => SpatialAudioEnabled && SelectedAudioMode.Value == 0;

    /// <summary>是否显示未连接提示。</summary>
    public bool ShowOfflineHint => !IsConnected;

    /// <summary>是否显示写入状态警示。</summary>
    public bool ShowSpatialWriteWarning => !string.IsNullOrEmpty(SpatialWriteStatus);

    /// <summary>音频模式区域的说明文字。</summary>
    public string AudioModeCaption =>
        IsAudioModeEnabled
            ? "小米空间音频 / 杜比空间音频 · 开启沉浸声后可切换"
            : "开启沉浸声后可选择音频模式";

    /// <summary>场景区域说明文字。</summary>
    public string SceneCaption =>
        IsXiaomiSpatialAudio
            ? "场景渲染 · Config 0x36"
            : "开启沉浸声并选择「小米空间音频」后可选场景";

    /// <summary>场景状态的动态文字。</summary>
    public string SceneStatus
    {
        get
        {
            if (!IsXiaomiSpatialAudio)
            {
                return "当前不可写：需 沉浸声开 + 音频模式=小米空间音频";
            }

            string mode = SelectedSceneMode.Label;
            return IsConnected
                ? $"当前场景：{mode}"
                : $"当前场景：{mode}（未连接，仅界面）";
        }
    }

    /// <summary>
    /// 官方 DeviceConfigSpatialAudio 位图：configId 0x1D。
    /// bit0=开关，bit1=官方默认低延迟偏好，bit3=头部跟踪。
    /// </summary>
    private byte BuildSpatialBitmap()
    {
        byte bits = 0x00;
        if (SpatialAudioEnabled)
        {
            // bit0 开启 + 官方默认低延迟偏好 bit1，合起来为 0x03。
            bits |= 0x01;
            bits |= 0x02;
        }

        if (HeadTracking)
        {
            bits |= 0x08;
        }

        return bits;
    }

    /// <summary>刷新所有依赖派生属性的绑定通知。</summary>
    private void NotifyDerived()
    {
        OnPropertyChanged(nameof(IsAudioModeEnabled));
        OnPropertyChanged(nameof(IsXiaomiSpatialAudio));
        OnPropertyChanged(nameof(SelectedAudioModeIndex));
        OnPropertyChanged(nameof(SelectedSceneModeIndex));
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(ConnectionHint));
        OnPropertyChanged(nameof(ShowOfflineHint));
        OnPropertyChanged(nameof(AudioModeCaption));
        OnPropertyChanged(nameof(SceneCaption));
        OnPropertyChanged(nameof(SceneStatus));
        OnPropertyChanged(nameof(ShowSpatialWriteWarning));
        OnPropertyChanged(nameof(SpatialWriteStatus));
    }

    /// <summary>写入空间音频主开关位图，并延迟回读校验。</summary>
    private void WriteSpatialPrimary()
    {
        byte bitmap = BuildSpatialBitmap();
        SpatialWriteStatus = string.Empty;
        _ignoreSpatialReadbackUntil = DateTimeOffset.UtcNow.AddSeconds(2.5);

        // 官方路径：SET Config 0x1D 写入完整位图（开启默认 0x03）。
        _client.SetConfigAndReadBack(RcspConfigIds.SpatialAudioSwitch, bitmap);
        _client.SetConfig(RcspConfigIds.SpatialAudioBitmap, bitmap);
    }

    /// <summary>沉浸声开关变化：关闭时连带关闭头部跟踪，并写入主位图。</summary>
    partial void OnSpatialAudioEnabledChanged(bool value)
    {
        if (!value)
        {
            HeadTracking = false;
        }

        NotifyDerived();
        if (_syncing || !_client.IsConnected)
        {
            return;
        }

        // 官方 setConfig 在每次切换沉浸声时不会重写音频模式。
        WriteSpatialPrimary();
    }

    /// <summary>音频模式变化：写 DolbyAudioSpatialMode，并写旧版 AudioMode 兼容旧固件。</summary>
    partial void OnSelectedAudioModeChanged(EnumOption value)
    {
        NotifyDerived();
        if (_syncing || !_client.IsConnected || !SpatialAudioEnabled)
        {
            return;
        }

        // 官方路径写入 0x68；同时写旧版 CONFIG_AUDIO_MODE 0x01 兼容仍在读取它的固件。
        _client.SetConfigAndReadBack(RcspConfigIds.DolbyAudioSpatialMode, (byte)value.Value);
        _client.SetConfig(RcspConfigIds.AudioMode, (byte)value.Value);
    }

    /// <summary>场景模式变化：写 Config 0x36，仅小米空间音频且已连接时生效。</summary>
    partial void OnSelectedSceneModeChanged(EnumOption value)
    {
        NotifyDerived();
        if (_syncing || !_client.IsConnected || !IsXiaomiSpatialAudio)
        {
            return;
        }

        _client.SetConfigAndReadBack(RcspConfigIds.SceneRendering, (byte)value.Value);
    }

    /// <summary>头部跟踪开关变化：沉浸声未开启时不允许打开。</summary>
    partial void OnHeadTrackingChanged(bool value)
    {
        if (value && !SpatialAudioEnabled)
        {
            HeadTracking = false;
            NotifyDerived();
            return;
        }

        NotifyDerived();
        if (_syncing || !_client.IsConnected || !SpatialAudioEnabled)
        {
            return;
        }

        WriteSpatialPrimary();
    }

    /// <summary>
    /// 处理耳机回读配置：校验空间音频位图是否真的生效，
    /// 同时同步音频模式与场景模式，避免写入被设备拒绝后 UI 失真。
    /// </summary>
    private void ApplyConfig(byte id, byte[] values)
    {
        if (values.Length < 1)
        {
            return;
        }

        bool ignoreSpatialSnap =
            (id is RcspConfigIds.SpatialAudioSwitch or RcspConfigIds.SpatialAudioBitmap)
            && DateTimeOffset.UtcNow < _ignoreSpatialReadbackUntil;

        _syncing = true;
        try
        {
            switch (id)
            {
                case RcspConfigIds.SpatialAudioSwitch:
                case RcspConfigIds.SpatialAudioBitmap:
                    byte primary = values[0];
                    bool spatialOn = (primary & 0x01) != 0;
                    bool headOn = (primary & 0x08) != 0;

                    if (spatialOn)
                    {
                        _ignoreSpatialReadbackUntil = DateTimeOffset.MinValue;
                        SpatialWriteStatus = string.Empty;
                        SpatialAudioEnabled = true;
                        HeadTracking = headOn;
                    }
                    else if (ignoreSpatialSnap)
                    {
                        // p76c 常对 SET 0x1D 回 ACK 但 GET 仍为 0，保留界面状态，不反复提示。
                        _client.GetConfig(RcspConfigIds.SpatialAudioBitmap);
                    }
                    else if (SpatialAudioEnabled)
                    {
                        SpatialAudioEnabled = false;
                        HeadTracking = false;
                    }

                    break;
                case RcspConfigIds.DolbyAudioSpatialMode:
                    EnumOption? dolby = AudioModes.FirstOrDefault(m => m.Value == values[0]);
                    if (dolby is not null)
                    {
                        SelectedAudioMode = dolby;
                    }

                    break;
                case RcspConfigIds.AudioMode:
                    EnumOption? mode = AudioModes.FirstOrDefault(m => m.Value == values[0]);
                    if (mode is not null)
                    {
                        SelectedAudioMode = mode;
                    }

                    break;
                case RcspConfigIds.SceneRendering:
                    byte sceneByte = values.Length >= 2 && values[1] != 0 ? values[1] : values[0];
                    if (sceneByte == 0)
                    {
                        sceneByte = 1;
                    }

                    EnumOption? scene = SceneModes.FirstOrDefault(s => s.Value == sceneByte);
                    if (scene is not null)
                    {
                        SelectedSceneMode = scene;
                    }

                    break;
            }
        }
        finally
        {
            _syncing = false;
            NotifyDerived();
        }
    }
}
