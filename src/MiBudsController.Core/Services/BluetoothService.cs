using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Networking.Sockets;

namespace MiBudsController.Core.Services;

/// <summary>系统扫描发现的蓝牙耳机条目。</summary>
public sealed record PairedDeviceInfo(string Id, string Name);

/// <summary>连接广播 / BLE 广播捕获回调的载荷。</summary>
public sealed record BluetoothScanEvent(
    string Kind,
    string Name,
    string Id,
    string Detail);

/// <summary>
/// 蓝牙服务：枚举系统已连接的耳机、监听连接变化、捕获 BLE 广播，
/// 并打开耳机 fd2d 控制服务的 RFCOMM 通道。
/// </summary>
public sealed class BluetoothService
{
    /// <summary>小米耳机控制服务 UUID（经典蓝牙 RFCOMM）。</summary>
    public static readonly Guid ControlServiceUuid = new("0000fd2d-0000-1000-8000-00805f9b34fb");

    /// <summary>DeviceWatcher / BLE 广播事件（Windows 连接广播）触发。</summary>
    public event Action<BluetoothScanEvent>? ScanEventRaised;

    /// <summary>
    /// 枚举当前已连接到 Windows、且名称像 Redmi/Xiaomi 耳机的蓝牙设备；
    /// 不做配对与连接弹窗。
    /// </summary>
    public async Task<IReadOnlyList<PairedDeviceInfo>> FindConnectedBudsAsync()
    {
        string pairedSelector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
        string connectedSelector = BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected);

        DeviceInformationCollection paired = await DeviceInformation.FindAllAsync(pairedSelector);
        DeviceInformationCollection connected = await DeviceInformation.FindAllAsync(connectedSelector);
        HashSet<string> connectedIds = connected
            .Select(d => d.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return paired
            .Where(d => connectedIds.Contains(d.Id) && IsEarbudsName(d.Name))
            .Select(d => new PairedDeviceInfo(d.Id, d.Name))
            .GroupBy(d => d.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    /// <summary>兼容旧调用的别名，现只扫描已连接设备。</summary>
    public Task<IReadOnlyList<PairedDeviceInfo>> FindPairedBudsAsync() => FindConnectedBudsAsync();

    /// <summary>创建“当前已连接蓝牙设备”的系统监视器。</summary>
    public DeviceWatcher CreateConnectedDeviceWatcher()
    {
        string selector = BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected);
        return DeviceInformation.CreateWatcher(selector);
    }

    /// <summary>
    /// 可选的 BLE 广播捕获：经典 RFCOMM 耳机连接后可能停止广播，
    /// 此方法在可用时记录附近小米/Redmi 耳机的广播信息。
    /// </summary>
    public async Task CaptureAdvertisementsAsync(TimeSpan? duration = null, CancellationToken cancellationToken = default)
    {
        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active,
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        TypedEventHandler<BluetoothLEAdvertisementWatcher, BluetoothLEAdvertisementReceivedEventArgs> onReceived = (_, args) =>
        {
            string name = args.Advertisement?.LocalName ?? string.Empty;
            string id = args.BluetoothAddress.ToString("X12");
            string key = $"{name}|{id}";
            if (!seen.Add(key) && !IsEarbudsName(name))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(name) && !IsBudsAddressHint(args.BluetoothAddress))
            {
                return;
            }

            short rssi = args.RawSignalStrengthInDBm;
            ScanEventRaised?.Invoke(new BluetoothScanEvent(
                "广播",
                string.IsNullOrWhiteSpace(name) ? "(匿名广播)" : name,
                id,
                $"RSSI={rssi}dBm"));
        };

        watcher.Received += onReceived;
        try
        {
            watcher.Start();
            await Task.Delay(duration ?? TimeSpan.FromSeconds(3), cancellationToken);
        }
        catch (Exception)
        {
            // Radio may be off or denied — scan path still works via DeviceWatcher.
        }
        finally
        {
            try
            {
                watcher.Stop();
            }
            catch
            {
                // ignored
            }

            watcher.Received -= onReceived;
        }
    }

    /// <summary>查询设备当前是否处于系统蓝牙连接状态。</summary>
    public async Task<bool> IsDeviceConnectedAsync(string deviceId)
    {
        try
        {
            BluetoothDevice? device = await BluetoothDevice.FromIdAsync(deviceId);
            return device is not null
                && device.ConnectionStatus == BluetoothConnectionStatus.Connected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 打开设备的 fd2d 控制服务并建立 StreamSocket；
    /// 设备未连接或缺少服务时抛出带中文说明的异常。
    /// </summary>
    public async Task<StreamSocket> OpenControlChannelAsync(string deviceId)
    {
        var device = await BluetoothDevice.FromIdAsync(deviceId);
        if (device is null)
        {
            throw new InvalidOperationException("无法打开蓝牙设备，请检查耳机是否已连接到 Windows。");
        }

        if (device.ConnectionStatus != BluetoothConnectionStatus.Connected)
        {
            throw new InvalidOperationException($"耳机未处于系统连接状态（{device.ConnectionStatus}），请先在 Windows 蓝牙设置中连接。");
        }

        var services = await device.GetRfcommServicesForIdAsync(
            RfcommServiceId.FromUuid(ControlServiceUuid),
            BluetoothCacheMode.Uncached);
        var service = services.Services.FirstOrDefault();
        if (service is null)
        {
            throw new InvalidOperationException($"设备上没有找到 fd2d 控制服务（{services.Error}）。");
        }

        var socket = new StreamSocket();
        await socket.ConnectAsync(
            service.ConnectionHostName,
            service.ConnectionServiceName,
            SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication);
        return socket;
    }

    /// <summary>按名称判断设备是否像小米/Redmi 耳机。</summary>
    public static bool IsEarbudsName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        string lowered = name.ToLowerInvariant();
        return lowered.Contains("buds", StringComparison.Ordinal)
            || lowered.Contains("earbuds", StringComparison.Ordinal)
            || lowered.Contains("redmi", StringComparison.Ordinal)
            || lowered.Contains("xiaomi", StringComparison.Ordinal)
            || lowered.Contains("小米", StringComparison.Ordinal);
    }

    /// <summary>按蓝牙地址 OUI 前缀判断是否可能为耳机（匿名广播兜底）。</summary>
    private static bool IsBudsAddressHint(ulong address)
    {
        // 常见小米耳机 OUI 前缀（BluetoothAddress 的低 24 位）。
        uint oui = (uint)((address >> 24) & 0xFFFFFF);
        return oui is 0x381C4A or 0x64B473 or 0x98F181 or 0x5C3C27 or 0x78D34F or 0xF46077 or 0x085B0E;
    }

    /// <summary>供外部主动触发一次扫描事件（调试/诊断用）。</summary>
    public void RaiseScanEvent(string kind, string name, string id, string detail) =>
        ScanEventRaised?.Invoke(new BluetoothScanEvent(kind, name, id, detail));
}
