using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using MiBudsController.Core.Models;
using MiBudsController.Core.Services;
using Windows.Devices.Enumeration;

namespace MiBudsController.ViewModels;

/// <summary>
/// 主页视图模型：扫描 Windows 已连接的耳机、自动接入目标设备，
/// 维护连接状态、三块电量卡片及设备信息，并响应系统蓝牙连接广播。
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private const string LastDeviceKey = "LastDeviceId";

    /// <summary>连接广播后延迟扫描的间隔，避免系统尚未建连完成就扫到空列表。</summary>
    private const int BroadcastRescanDelayMs = 5000;

    /// <summary>打开界面时电量回读的等待上限；超时按连接监测策略清空显示。</summary>
    private const int BatteryRefreshTimeoutMs = 3000;

    private readonly EarbudsClient _client;
    private readonly BluetoothService _bluetooth = new();
    private readonly AppSettings _settings;
    private readonly LogService _logs;
    private readonly DispatcherQueue _dispatcher;
    private string? _lastDeviceId;
    private DeviceWatcher? _watcher;
    private bool _watcherStarted;
    private bool _initialized;
    private bool _verifying;
    private string? _attachedDeviceId;
    private CancellationTokenSource? _broadcastRescanCts;

    /// <summary>
    /// 构造时绑定客户端与蓝牙服务事件，并把所有回调切回 UI 线程；
    /// 同时读取上次成功连接的设备 ID 用于自动重连。
    /// </summary>
    public MainViewModel(EarbudsClient client, LogService logs, AppSettings settings)
    {
        _client = client;
        _logs = logs;
        _settings = settings;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _lastDeviceId = _settings.Get(LastDeviceKey);

        _client.SnapshotUpdated += snapshot => _dispatcher.TryEnqueue(() => ApplySnapshot(snapshot));
        _client.Disconnected += reason => _dispatcher.TryEnqueue(() => OnDisconnected(reason));
        _client.Warning += message => _dispatcher.TryEnqueue(() => ShowInfo(message, InfoBarSeverity.Warning));
        _bluetooth.ScanEventRaised += evt => _dispatcher.TryEnqueue(() => OnScanEvent(evt));
    }

    /// <summary>扫描到的已连接耳机集合。</summary>
    public ObservableCollection<PairedDeviceInfo> Devices { get; } = new();

    /// <summary>左耳电量卡片。</summary>
    public BatteryCardViewModel LeftBattery { get; } = new("左耳");

    /// <summary>右耳电量卡片。</summary>
    public BatteryCardViewModel RightBattery { get; } = new("右耳");

    /// <summary>充电盒电量卡片。</summary>
    public BatteryCardViewModel CaseBattery { get; } = new("充电盒");

    /// <summary>当前选中的设备。</summary>
    [ObservableProperty]
    private PairedDeviceInfo? _selectedDevice;

    /// <summary>是否正在扫描或接入，用于禁用刷新按钮。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRefresh))]
    private bool _isBusy;

    /// <summary>未忙时才能手动刷新。</summary>
    public bool CanRefresh => !IsBusy;

    /// <summary>是否已接入目标耳机。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDeviceStatus))]
    [NotifyPropertyChangedFor(nameof(ConnectedDeviceTitle))]
    [NotifyPropertyChangedFor(nameof(DeviceDisplayTitle))]
    private bool _isConnected;

    /// <summary>是否显示“已连接”状态区域。</summary>
    public bool ShowDeviceStatus => IsConnected;

    /// <summary>主页标题文字：已连接时显示耳机名，否则提示扫描。</summary>
    public string ConnectedDeviceTitle =>
        IsConnected && DeviceName is not null
            ? $"{DeviceName} · 已连接"
            : "扫描 Windows 当前已连接的耳机";

    /// <summary>托盘迷你面板标题的紧凑文字。</summary>
    public string DeviceDisplayTitle =>
        IsConnected && DeviceName is not null
            ? DeviceName
            : "小米耳机控制器";

    /// <summary>扫描/连接过程中的状态文字。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectedDeviceTitle))]
    private string _statusText = "未扫描";

    /// <summary>已连接耳机名称。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectedDeviceTitle))]
    [NotifyPropertyChangedFor(nameof(DeviceDisplayTitle))]
    private string? _deviceName;

    /// <summary>固件版本显示文字。</summary>
    [ObservableProperty]
    private string? _firmwareText = "固件：--";

    /// <summary>VID/PID 显示文字。</summary>
    [ObservableProperty]
    private string? _vidPidText = "VID/PID：--";

    /// <summary>界面上的扫描提示文字。</summary>
    [ObservableProperty]
    private string _scanHint = "启动时自动扫描；也可手动刷新。";

    /// <summary>断线后是否自动重连。</summary>
    [ObservableProperty]
    private bool _autoReconnect = true;

    /// <summary>InfoBar 是否可见。</summary>
    [ObservableProperty]
    private bool _isInfoOpen;

    /// <summary>InfoBar 消息内容。</summary>
    [ObservableProperty]
    private string? _infoMessage;

    /// <summary>InfoBar 严重级别。</summary>
    [ObservableProperty]
    private InfoBarSeverity _infoSeverity = InfoBarSeverity.Informational;

    /// <summary>启动连接监视并自动扫描；已初始化时重复调用无效。</summary>
    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        StartConnectionWatcher();
        await ScanDevicesCommand.ExecuteAsync(null);
    }

    /// <summary>
    /// 扫描当前已连接耳机：更新列表、优先保留原选择或上次设备，
    /// 再短暂捕获 BLE 广播，最后自动接入选中设备。
    /// </summary>
    [RelayCommand]
    private async Task ScanDevicesAsync()
    {
        IsBusy = true;
        StatusText = IsConnected ? "正在重新扫描…" : "正在扫描已连接设备…";
        ScanHint = "正在扫描 Windows 已连接的蓝牙设备…";
        try
        {
            IReadOnlyList<PairedDeviceInfo> devices = await _bluetooth.FindConnectedBudsAsync();

            PairedDeviceInfo? previous = SelectedDevice;
            Devices.Clear();
            foreach (PairedDeviceInfo device in devices)
            {
                Devices.Add(device);
            }

            SelectedDevice = Devices.FirstOrDefault(d => d.Id == previous?.Id)
                ?? Devices.FirstOrDefault(d => d.Id == _lastDeviceId)
                ?? Devices.FirstOrDefault();

            // 无线蓝牙可用时顺带捕获近距离 BLE 广播。
            await _bluetooth.CaptureAdvertisementsAsync(TimeSpan.FromSeconds(2));

            if (Devices.Count == 0)
            {
                StatusText = IsConnected ? "已连接" : "未找到已连接耳机";
                ScanHint = "当前没有系统已连接的 Redmi / Xiaomi 耳机。请先在 Windows 设置 → 蓝牙中连接耳机，再刷新。";
                if (!IsConnected)
                {
                    ShowInfo(ScanHint, InfoBarSeverity.Warning);
                }

                return;
            }

            ScanHint = $"扫描到 {Devices.Count} 台已连接耳机；正在自动接入…";
            if (SelectedDevice is not null)
            {
                await AttachDeviceAsync(SelectedDevice);
            }
        }
        catch (Exception ex)
        {
            ScanHint = $"扫描失败：{ex.Message}";
            ShowInfo($"扫描已连接设备失败：{ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>XAML 中刷新命令的别名，行为与扫描相同。</summary>
    [RelayCommand]
    private Task RefreshDevicesAsync() => ScanDevicesAsync();

    /// <summary>断开应用层通道；系统蓝牙连接保持不变，可再次扫描接入。</summary>
    [RelayCommand]
    private async Task DisconnectAsync()
    {
        IsBusy = true;
        await _client.DisconnectAsync();
        _attachedDeviceId = null;
        IsConnected = false;
        StatusText = "未连接";
        ScanHint = "已断开应用通道。耳机仍可保持系统蓝牙连接；点「重新扫描」可再次接入。";
        ClearBatteryReadings();
        IsBusy = false;
    }

    /// <summary>
    /// 打开主界面/迷你面板时校验耳机是否仍连接：
    /// 应用层或系统蓝牙任一失效则恢复“未连接”并清空电量（避免卡在“充电中”）；
    /// 仍连接时重新拉取设备信息，刷新电量与充电状态。
    /// </summary>
    public async Task VerifyConnectionAsync()
    {
        if (_verifying)
        {
            return;
        }

        _verifying = true;
        try
        {
            if (!_client.IsConnected)
            {
                if (IsConnected || HasAnyBatteryReading)
                {
                    ApplyDisconnectedState("耳机连接已失效，已恢复为未连接");
                }

                return;
            }

            string? deviceId = _attachedDeviceId ?? SelectedDevice?.Id ?? _lastDeviceId;
            if (deviceId is not null)
            {
                bool systemConnected = await _bluetooth.IsDeviceConnectedAsync(deviceId);
                if (!systemConnected)
                {
                    await _client.DisconnectAsync();
                    ApplyDisconnectedState("耳机已不在系统蓝牙连接中，已恢复为未连接");
                    return;
                }
            }

            if (!IsConnected)
            {
                IsConnected = true;
                StatusText = "已连接";
            }

            // 电量读取与连接状态监测同一入口：打开界面时强制回读。
            await RefreshBatteryReadingsAsync();
            _client.GetRunInfo();
        }
        catch (Exception ex)
        {
            _logs.AddSystem($"[校验] 打开界面时检查连接失败：{ex.Message}");
        }
        finally
        {
            _verifying = false;
        }
    }

    /// <summary>
    /// 电量读取策略与连接状态监测一致：
    /// 打开界面时请求回读设备信息；在超时内收到快照则更新；
    /// 未连接或回读失败/超时则清空电量，避免残留「充电中」。
    /// </summary>
    private async Task RefreshBatteryReadingsAsync()
    {
        if (!_client.IsConnected)
        {
            if (HasAnyBatteryReading)
            {
                ClearBatteryReadings();
                _logs.AddSystem("[电量] 未连接，已清空电量显示");
            }

            return;
        }

        var tcs = new TaskCompletionSource<DeviceSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnSnapshot(DeviceSnapshot snapshot) => tcs.TrySetResult(snapshot);

        _client.SnapshotUpdated += OnSnapshot;
        try
        {
            _client.GetDeviceInfo();
            Task completed = await Task.WhenAny(tcs.Task, Task.Delay(BatteryRefreshTimeoutMs));
            if (completed == tcs.Task)
            {
                DeviceSnapshot snapshot = await tcs.Task;
                _dispatcher.TryEnqueue(() => ApplySnapshot(snapshot));
                _logs.AddSystem("[电量] 打开界面已回读电量");
            }
            else
            {
                _dispatcher.TryEnqueue(ClearBatteryReadings);
                _logs.AddSystem("[电量] 打开界面回读超时，已清空电量（与连接状态监测一致）");
            }
        }
        finally
        {
            _client.SnapshotUpdated -= OnSnapshot;
        }
    }

    /// <summary>三块电量卡片是否仍持有读数。</summary>
    private bool HasAnyBatteryReading =>
        LeftBattery.HasReading || RightBattery.HasReading || CaseBattery.HasReading;

    /// <summary>清空电量读数，卡片回到“未连接 / --”。</summary>
    private void ClearBatteryReadings()
    {
        LeftBattery.Reading = null;
        RightBattery.Reading = null;
        CaseBattery.Reading = null;
    }

    /// <summary>统一把界面恢复为未连接，并清除过期电量/设备信息。</summary>
    private void ApplyDisconnectedState(string hint)
    {
        _attachedDeviceId = null;
        IsConnected = false;
        StatusText = "未连接";
        ScanHint = hint;
        FirmwareText = "固件：--";
        VidPidText = "VID/PID：--";
        ClearBatteryReadings();
        ShowInfo(hint, InfoBarSeverity.Warning);
        _logs.AddSystem($"[校验] {hint}");
    }

    /// <summary>用户切换选中设备时，自动尝试接入新设备。</summary>
    partial void OnSelectedDeviceChanged(PairedDeviceInfo? value)
    {
        if (value is null || IsBusy || _client.IsConnected && value.Id == _attachedDeviceId)
        {
            return;
        }

        _ = AttachDeviceAsync(value);
    }

    /// <summary>
    /// 接入指定设备：先确认系统蓝牙已连接，再建立 RFCOMM 通道，
    /// 成功后保存设备 ID 供下次启动或自动重连使用。
    /// </summary>
    private async Task AttachDeviceAsync(PairedDeviceInfo device)
    {
        if (_client.IsConnected && device.Id == _attachedDeviceId)
        {
            IsConnected = true;
            DeviceName = device.Name;
            StatusText = "已连接";
            return;
        }

        bool wasBusy = IsBusy;
        if (!wasBusy)
        {
            IsBusy = true;
        }

        StatusText = "正在接入已连接设备…";
        try
        {
            bool systemConnected = await _bluetooth.IsDeviceConnectedAsync(device.Id);
            if (!systemConnected)
            {
                throw new InvalidOperationException($"{device.Name} 未在系统蓝牙中连接。");
            }

            await _client.ConnectAsync(device.Id);
            DeviceName = device.Name;
            IsConnected = true;
            StatusText = "已连接";
            _attachedDeviceId = device.Id;
            _lastDeviceId = device.Id;
            _settings.Set(LastDeviceKey, _lastDeviceId);
            ScanHint = $"已自动接入系统已连接设备：{device.Name}";
            ShowInfo($"已接入 {device.Name}", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            if (device.Id == _attachedDeviceId)
            {
                _attachedDeviceId = null;
            }

            IsConnected = false;
            StatusText = "接入失败";
            ScanHint = ex.Message;
            ShowInfo(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            if (!wasBusy)
            {
                IsBusy = false;
            }
        }
    }

    /// <summary>启动系统蓝牙连接监视（新增/更新/移除/枚举完成事件）。</summary>
    private void StartConnectionWatcher()
    {
        if (_watcherStarted)
        {
            return;
        }

        try
        {
            _watcher = _bluetooth.CreateConnectedDeviceWatcher();
            _watcher.Added += OnWatcherAdded;
            _watcher.Updated += OnWatcherUpdated;
            _watcher.Removed += OnWatcherRemoved;
            _watcher.EnumerationCompleted += OnWatcherEnumerationCompleted;
            _watcher.Start();
            _watcherStarted = true;
            _logs.AddSystem("[扫描] 已启动 Windows 蓝牙连接监视");
        }
        catch (Exception ex)
        {
            _logs.AddSystem($"[扫描] 连接监视启动失败：{ex.Message}");
        }
    }

    /// <summary>设备出现广播处理。</summary>
    private void OnWatcherAdded(DeviceWatcher sender, DeviceInformation info) =>
        HandleWatcherDevice("出现", info.Id, info.Name);

    /// <summary>设备信息更新广播处理。</summary>
    private void OnWatcherUpdated(DeviceWatcher sender, DeviceInformationUpdate args) =>
        HandleWatcherDevice("更新", args.Id, Devices.FirstOrDefault(d => d.Id == args.Id)?.Name);

    /// <summary>设备移除广播处理。</summary>
    private void OnWatcherRemoved(DeviceWatcher sender, DeviceInformationUpdate args) =>
        HandleWatcherDevice("移除", args.Id, Devices.FirstOrDefault(d => d.Id == args.Id)?.Name);

    /// <summary>系统枚举完成时记录日志。</summary>
    private void OnWatcherEnumerationCompleted(DeviceWatcher sender, object args) =>
        _logs.AddSystem("[扫描] 系统已连接设备枚举完成");

    /// <summary>
    /// 统一处理连接广播：耳机关联设备出现时延迟重扫接入，
    /// 移除时若正是当前设备则断开并重扫。
    /// 出现/更新不立刻扫——系统还在建连时扫到的列表可能不完整，等 5 秒再接入。
    /// </summary>
    private void HandleWatcherDevice(string kind, string id, string? name)
    {
        bool buds = BluetoothService.IsEarbudsName(name) || Devices.Any(d => d.Id == id);
        _dispatcher.TryEnqueue(() =>
        {
            string label = string.IsNullOrWhiteSpace(name) ? id : name!;
            _logs.AddSystem($"[广播] 连接{kind}：{label}{(buds ? "（耳机）" : string.Empty)}");
            ScanHint = $"系统广播：{kind} {label}";

            if (!buds)
            {
                return;
            }

            if (kind == "移除")
            {
                if (!IsBusy && (SelectedDevice?.Id == id || _lastDeviceId == id))
                {
                    _ = RescanSafeAsync("耳机从系统断开");
                }

                return;
            }

            if (!IsConnected || SelectedDevice?.Id != id || _attachedDeviceId != id)
            {
                ScheduleBroadcastRescan($"检测到耳机连接广播：{label}");
            }
        });
    }

    /// <summary>
    /// 广播触发的延迟扫描：从广播出现起等 5 秒再扫，给系统蓝牙建连留时间；
    /// 期间若有新的广播则重置计时，避免连接中途扫空。
    /// </summary>
    private void ScheduleBroadcastRescan(string reason)
    {
        _broadcastRescanCts?.Cancel();
        _broadcastRescanCts?.Dispose();
        var cts = new CancellationTokenSource();
        _broadcastRescanCts = cts;

        int delaySeconds = BroadcastRescanDelayMs / 1000;
        _logs.AddSystem($"[扫描] {reason}；{delaySeconds} 秒后开始扫描，等待系统连接完成");
        ScanHint = $"{reason}；{delaySeconds} 秒后自动扫描…";

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(BroadcastRescanDelayMs, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            _dispatcher.TryEnqueue(() =>
            {
                if (cts.IsCancellationRequested)
                {
                    return;
                }

                if (IsBusy)
                {
                    _logs.AddSystem($"[扫描] {reason}；当前正忙，跳过本次延迟扫描");
                    return;
                }

                _ = RescanSafeAsync(reason);
            });
        });
    }

    /// <summary>记录扫描到的 BLE 广播摘要，耳机名称匹配时更新提示。</summary>
    private void OnScanEvent(BluetoothScanEvent evt)
    {
        string line = $"[广播/{evt.Kind}] {evt.Name} {evt.Id} {evt.Detail}".Trim();
        _logs.AddSystem(line);
        if (BluetoothService.IsEarbudsName(evt.Name))
        {
            ScanHint = $"捕获广播：{evt.Name}";
        }
    }

    /// <summary>带原因的重新扫描，内部错误统一交给扫描流程上报。</summary>
    private async Task RescanSafeAsync(string reason)
    {
        try
        {
            _logs.AddSystem($"[扫描] {reason}");
            await ScanDevicesAsync();
        }
        catch
        {
            // ScanDevicesAsync already reports errors to the UI.
        }
    }

    /// <summary>把设备快照的固件、VID/PID 与三块电量同步到界面。</summary>
    private void ApplySnapshot(DeviceSnapshot snapshot)
    {
        LeftBattery.Reading = snapshot.LeftBattery;
        RightBattery.Reading = snapshot.RightBattery;
        CaseBattery.Reading = snapshot.CaseBattery;
        FirmwareText = snapshot.FirmwareVersion1 is null
            ? "固件：--"
            : $"固件：{snapshot.FirmwareVersion1}（{snapshot.FirmwareVersion2}）";
        VidPidText = snapshot.VidPid ?? "VID/PID：--";
    }

    /// <summary>
    /// 处理断线：重置连接状态并提示原因；
    /// 若开启自动重连且有上次设备，则启动重连任务。
    /// </summary>
    private void OnDisconnected(string reason)
    {
        _attachedDeviceId = null;
        IsConnected = false;
        StatusText = "未连接";
        ClearBatteryReadings();
        ShowInfo(reason, InfoBarSeverity.Warning);
        if (AutoReconnect && _lastDeviceId is not null)
        {
            _ = TryAutoReconnectAsync();
        }
    }

    /// <summary>最多重试 3 次的自动重连：等待 3 秒后尝试接入上次设备。</summary>
    private async Task TryAutoReconnectAsync()
    {
        for (int attempt = 0; attempt < 3 && !IsConnected; attempt++)
        {
            await Task.Delay(3000);
            PairedDeviceInfo? target = Devices.FirstOrDefault(d => d.Id == _lastDeviceId) ?? SelectedDevice;
            if (target is null)
            {
                await ScanDevicesAsync();
                target = Devices.FirstOrDefault(d => d.Id == _lastDeviceId) ?? SelectedDevice;
                if (target is null)
                {
                    return;
                }
            }

            try
            {
                await AttachDeviceAsync(target);
                if (IsConnected)
                {
                    return;
                }
            }
            catch
            {
                // Keep retrying until the attempt budget is used up.
            }
        }
    }

    /// <summary>在 InfoBar 上显示一条消息。</summary>
    private void ShowInfo(string message, InfoBarSeverity severity)
    {
        InfoMessage = message;
        InfoSeverity = severity;
        IsInfoOpen = true;
    }
}
