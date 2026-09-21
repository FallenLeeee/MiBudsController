using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using MiBudsController.Core.Protocol;
using MiBudsController.Core.Services;

namespace MiBudsController.ViewModels;

/// <summary>
/// 音效设置视图模型：目前承载“低延迟模式”开关，
/// 负责把界面状态写入耳机并把耳机回读值同步回界面。
/// </summary>
public partial class SoundEffectsViewModel : ObservableObject
{
    private readonly EarbudsClient _client;
    private readonly DispatcherQueue _dispatcher;
    private bool _syncing;

    /// <summary>订阅客户端配置变化事件，并确保回调切回 UI 线程。</summary>
    public SoundEffectsViewModel(EarbudsClient client)
    {
        _client = client;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _client.ConfigChanged += (id, values) => _dispatcher.TryEnqueue(() => ApplyConfig(id, values));
    }

    /// <summary>低延迟模式开关。</summary>
    [ObservableProperty]
    private bool _lowLatency;

    /// <summary>开关变化后，若已连接耳机则写入 Config 0x2F。</summary>
    partial void OnLowLatencyChanged(bool value)
    {
        if (!_syncing && _client.IsConnected)
        {
            _client.SetConfig(RcspConfigIds.LowLatency, value ? (byte)1 : (byte)0);
        }
    }

    /// <summary>处理耳机回读的低延迟配置，加 _syncing 防止回写触发再次发送。</summary>
    private void ApplyConfig(byte id, byte[] values)
    {
        if (id == RcspConfigIds.LowLatency && values.Length >= 1)
        {
            _syncing = true;
            LowLatency = values[0] != 0;
            _syncing = false;
        }
    }
}
