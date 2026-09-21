using Windows.Networking.Sockets;
using Windows.Storage.Streams;
using MiBudsController.Core.Models;
using MiBudsController.Core.Protocol;

namespace MiBudsController.Core.Services;

/// <summary>
/// RFCOMM 会话状态机：负责连接、SAFER+ 鉴权握手、状态同步、
/// 指令分发和耳机通知 ACK。
/// </summary>
public sealed class EarbudsClient : IDisposable
{
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(5);

    private readonly BluetoothService _bluetooth = new();
    private readonly LogService _logs;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Dictionary<byte, PendingRequest> _pending = new();
    private readonly object _pendingLock = new();
    private readonly object _stateLock = new();
    private readonly DeviceSnapshot _snapshot = new();

    private StreamSocket? _socket;
    private DataWriter? _writer;
    private CancellationTokenSource? _cts;
    private Task? _readTask;
    private byte _seq;
    private bool _connected;
    private bool _disconnectRaised;

    private sealed record PendingRequest(
        byte Opcode,
        TaskCompletionSource<RcspMessage> Completion,
        bool RequireDeviceInitiated = false);

    /// <summary>创建客户端并绑定日志服务。</summary>
    public EarbudsClient(LogService logs)
    {
        _logs = logs;
    }

    /// <summary>当前是否已建立应用层连接。</summary>
    public bool IsConnected
    {
        get
        {
            lock (_stateLock)
            {
                return _connected;
            }
        }
    }

    /// <summary>连接建立成功事件。</summary>
    public event Action? Connected;

    /// <summary>连接断开事件，带原因文案。</summary>
    public event Action<string>? Disconnected;

    /// <summary>非致命警告事件。</summary>
    public event Action<string>? Warning;

    /// <summary>设备快照更新事件。</summary>
    public event Action<DeviceSnapshot>? SnapshotUpdated;

    /// <summary>降噪模式变化事件。</summary>
    public event Action<int>? AncModeChanged;

    /// <summary>配置回读/通知事件，带配置 ID 与数值。</summary>
    public event Action<byte, byte[]>? ConfigChanged;

    /// <summary>
    /// 连接设备：先断开旧会话，打开 RFCOMM 通道后启动读取循环，
    /// 再完成 SAFER+ 鉴权（失败继续尝试），最后同步设备状态。
    /// </summary>
    public async Task ConnectAsync(string deviceId)
    {
        await DisconnectAsync();

        var cts = new CancellationTokenSource();
        lock (_stateLock)
        {
            _cts = cts;
            _disconnectRaised = false;
        }

        try
        {
            var socket = await _bluetooth.OpenControlChannelAsync(deviceId);
            var writer = new DataWriter(socket.OutputStream);
            lock (_stateLock)
            {
                _socket = socket;
                _writer = writer;
            }

            _readTask = Task.Run(() => ReadLoopAsync(socket, cts));
            try
            {
                await AuthenticateAsync();
                _logs.AddSystem("[鉴权] SAFER+ 握手完成");
            }
            catch (Exception ex)
            {
                // Some models skip the SAFER+ handshake entirely; continue anyway.
                // 0xF4 等通知可能与请求 seq 撞车，勿把未鉴权当成连接失败。
                _logs.AddSystem($"[鉴权] 未完成，继续尝试连接：{ex.Message}");
                Warning?.Invoke($"鉴权未完成，继续尝试连接：{ex.Message}");
            }

            lock (_stateLock)
            {
                _connected = true;
            }

            Connected?.Invoke();
            SyncStateAsync();
        }
        catch (Exception ex)
        {
            await DisconnectAsync();
            throw new InvalidOperationException($"连接耳机失败：{ex.Message}", ex);
        }
    }

    /// <summary>断开当前连接并释放套接字、写入器与读取任务。</summary>
    public async Task DisconnectAsync()
    {
        CancellationTokenSource? cts;
        StreamSocket? socket;
        DataWriter? writer;
        Task? readTask;
        lock (_stateLock)
        {
            cts = _cts;
            _cts = null;
            socket = _socket;
            _socket = null;
            writer = _writer;
            _writer = null;
            readTask = _readTask;
            _connected = false;
            _disconnectRaised = true;
        }

        try
        {
            cts?.Cancel();
        }
        catch
        {
            // ignored
        }

        try
        {
            writer?.Dispose();
        }
        catch
        {
            // ignored
        }

        try
        {
            socket?.Dispose();
        }
        catch
        {
            // ignored
        }

        if (readTask is not null)
        {
            try
            {
                await readTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // ignored
            }
        }
    }

    /// <summary>设置降噪模式（不等待返回）。</summary>
    public void SetAncMode(int mode) => SendRequestNoWait(RcspOpcodes.SetAnc, PayloadBuilder.SetAncMode((byte)mode));

    /// <summary>写入一条配置（不等待返回）。</summary>
    public void SetConfig(byte configId, params byte[] values) =>
        SendRequestNoWait(RcspOpcodes.SetConfig, PayloadBuilder.SetConfig(configId, values));

    /// <summary>
    /// 先 SET_CONFIG，稍后延迟 GET_CONFIG 回读同一配置（空间音频校验路径）。
    /// </summary>
    public void SetConfigAndReadBack(byte configId, params byte[] values)
    {
        string valueHex = values.Length == 0 ? "(empty)" : Convert.ToHexString(values);
        _logs.AddSystem($"[空间] SET 0x{configId:X2}={valueHex}");
        SendRequestNoWaitInternal(RcspOpcodes.SetConfig, PayloadBuilder.SetConfig(configId, values));
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(100);
                if (!IsConnected)
                {
                    return;
                }

                _logs.AddSystem($"[空间] GET 0x{configId:X2}");
                SendRequestNoWaitInternal(RcspOpcodes.GetConfig, PayloadBuilder.GetConfig(configId));
            }
            catch (Exception ex)
            {
                _logs.AddSystem($"[空间] 回读失败 0x{configId:X2}：{ex.Message}");
            }
        });
    }

    /// <summary>读取一条配置。</summary>
    public void GetConfig(byte configId) => SendRequestNoWait(RcspOpcodes.GetConfig, PayloadBuilder.GetConfig(configId));

    /// <summary>请求设备信息。</summary>
    public void GetDeviceInfo() => SendRequestNoWait(RcspOpcodes.GetDeviceInfo, PayloadBuilder.GetDeviceInfo());

    /// <summary>请求运行信息。</summary>
    public void GetRunInfo() => SendRequestNoWait(RcspOpcodes.GetRunInfo, PayloadBuilder.GetRunInfo());

    /// <summary>释放内部发送锁。</summary>
    public void Dispose() => _sendLock.Dispose();

    /// <summary>
    /// 鉴权握手：发送随机挑战，校验 E21(random)；
    /// 部分固件改用自己的挑战，则回以 SAFER+ 响应，
    /// 最后发送鉴权确认并等待耳机主动发起的 0x51 完成。
    /// </summary>
    private async Task AuthenticateAsync()
    {
        // 官方客户端在 SPP 套接字打开后会短暂等待稳定。
        await Task.Delay(500);

        byte[] random = SaferPlus.GenerateChallenge();
        _logs.AddSystem("[鉴权] 发送挑战 0x50");
        RcspMessage challengeResponse = await SendRequestAsync(
            RcspOpcodes.AuthChallenge,
            PayloadBuilder.AuthChallenge(random));

        // 设备可能先回通知帧；仅接受 0x50 Response 作为鉴权应答。
        if (challengeResponse.Opcode != RcspOpcodes.AuthChallenge
            || challengeResponse.Type == RcspMessageType.EarbudsRequest
            || challengeResponse.Type == RcspMessageType.EarbudsNotify
            || challengeResponse.Payload.Length < 16)
        {
            throw new InvalidOperationException("鉴权响应格式不正确。");
        }

        byte[] answer = challengeResponse.Payload.Length >= 17
            ? challengeResponse.Payload[^16..]
            : challengeResponse.Payload;
        byte[] expected = new SaferPlus().ComputeChallengeResponse(random);
        if (!answer.AsSpan().SequenceEqual(expected))
        {
            // 部分固件返回自己的挑战而非 E21(random)，按 GB 方式回算。
            _logs.AddSystem("[鉴权] 设备返回自有挑战，回以 SAFER+ 响应");
            byte[] response = new SaferPlus().ComputeChallengeResponse(answer);
            await SendResponseAsync(
                RcspOpcodes.AuthChallenge,
                challengeResponse.Sequence,
                PayloadBuilder.AuthChallengeResponse(response));
        }
        else
        {
            _logs.AddSystem("[鉴权] 挑战校验通过");
        }

        // 告诉耳机配对校验通过：发送 AUTH_CONFIRM 请求 [01 00]。
        _logs.AddSystem("[鉴权] 发送确认 0x51");
        await SendRequestAsync(RcspOpcodes.AuthConfirm, PayloadBuilder.AuthConfirmRequest());

        // 之后耳机主动发起 0x50 挑战和 0x51 结果命令，
        // 读取循环会自动应答；这里等待 0x51 完成握手。
        _logs.AddSystem("[鉴权] 等待耳机发起的 0x51 确认");
        await WaitForInboundAsync(RcspOpcodes.AuthConfirm);
        _logs.AddSystem("[鉴权] 收到耳机 0x51");
    }

    /// <summary>连接后同步设备信息、运行信息与全部关注的配置项。</summary>
    private void SyncStateAsync()
    {
        SendRequestNoWaitInternal(RcspOpcodes.GetDeviceInfo, PayloadBuilder.GetDeviceInfo());
        SendRequestNoWaitInternal(RcspOpcodes.GetRunInfo, PayloadBuilder.GetRunInfo());

        byte[] configIds =
        {
            RcspConfigIds.AudioMode,
            RcspConfigIds.EffectStrength,
            RcspConfigIds.SpatialAudioSwitch,
            RcspConfigIds.SpatialAudioBitmap,
            RcspConfigIds.LowLatency,
            RcspConfigIds.SceneRendering,
            RcspConfigIds.SmartNoise,
            RcspConfigIds.DolbyAudioSpatialMode,
        };

        foreach (byte id in configIds)
        {
            SendRequestNoWaitInternal(RcspOpcodes.GetConfig, PayloadBuilder.GetConfig(id));
        }
    }

    /// <summary>
    /// 发送并等待响应的请求：按序列号登记待处理请求，
    /// 超时未响应则抛出 TimeoutException。
    /// </summary>
    private async Task<RcspMessage> SendRequestAsync(byte opcode, byte[] payload)
    {
        await _sendLock.WaitAsync();
        byte sequence = _seq++;
        var pending = new PendingRequest(
            opcode,
            new TaskCompletionSource<RcspMessage>(TaskCreationOptions.RunContinuationsAsynchronously),
            RequireDeviceInitiated: false);
        lock (_pendingLock)
        {
            _pending[sequence] = pending;
        }

        try
        {
            await WriteAsync(new RcspMessage(RcspMessageType.PhoneRequest, opcode, sequence, payload));
            Task completed = await Task.WhenAny(pending.Completion.Task, Task.Delay(ResponseTimeout));
            if (completed != pending.Completion.Task)
            {
                throw new TimeoutException($"等待耳机响应超时（opcode 0x{opcode:X2}）。");
            }

            return await pending.Completion.Task;
        }
        finally
        {
            lock (_pendingLock)
            {
                _pending.Remove(sequence);
            }

            _sendLock.Release();
        }
    }

    /// <summary>发送一条响应消息，失败时上报断开。</summary>
    private async Task SendResponseAsync(byte opcode, byte sequence, byte[] payload)
    {
        await _sendLock.WaitAsync();
        try
        {
            await WriteAsync(new RcspMessage(RcspMessageType.Response, opcode, sequence, payload));
        }
        catch (Exception ex)
        {
            RaiseDisconnected($"发送响应失败：{ex.Message}");
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>登记一个等待耳机主动发起消息的占位请求（用于等待 0x51 鉴权确认）。</summary>
    private async Task<RcspMessage> WaitForInboundAsync(byte opcode)
    {
        var tcs = new TaskCompletionSource<RcspMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        byte key;
        lock (_pendingLock)
        {
            do
            {
                key = _seq++;
            }
            while (_pending.ContainsKey(key));

            _pending[key] = new PendingRequest(opcode, tcs, RequireDeviceInitiated: true);
        }

        try
        {
            Task completed = await Task.WhenAny(tcs.Task, Task.Delay(ResponseTimeout));
            if (completed != tcs.Task)
            {
                throw new TimeoutException($"等待耳机发起的鉴权确认超时（opcode 0x{opcode:X2}）。");
            }

            return await tcs.Task;
        }
        finally
        {
            lock (_pendingLock)
            {
                _pending.Remove(key);
            }
        }
    }

    /// <summary>后台任务方式发送请求，仅在已连接时执行，避免阻塞调用线程。</summary>
    private void SendRequestNoWait(byte opcode, byte[] payload)
    {
        _ = Task.Run(async () =>
        {
            if (!IsConnected)
            {
                return;
            }

            SendRequestNoWaitInternal(opcode, payload);
        });
    }

    /// <summary>不等待响应的实际发送逻辑（带发送锁与序列号分配）。</summary>
    private void SendRequestNoWaitInternal(byte opcode, byte[] payload)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _sendLock.WaitAsync();
                try
                {
                    byte sequence = _seq++;
                    await WriteAsync(new RcspMessage(RcspMessageType.PhoneRequest, opcode, sequence, payload));
                }
                finally
                {
                    _sendLock.Release();
                }
            }
            catch (Exception ex)
            {
                RaiseDisconnected($"发送失败：{ex.Message}");
            }
        });
    }

    /// <summary>记录并写入一条发送消息。</summary>
    private async Task WriteAsync(RcspMessage message)
    {
        var writer = _writer;
        if (writer is null)
        {
            throw new InvalidOperationException("尚未连接。");
        }

        _logs.Add(LogDirection.Tx, message);
        writer.WriteBytes(message.Encode());
        await writer.StoreAsync();
    }

    /// <summary>
    /// 读取循环：持续加载蓝牙数据流，交给帧解析器切分，
    /// 对每条消息记录日志并分发处理；连接中断时上报断开。
    /// </summary>
    private async Task ReadLoopAsync(StreamSocket socket, CancellationTokenSource session)
    {
        try
        {
            var reader = new DataReader(socket.InputStream);
            reader.InputStreamOptions = InputStreamOptions.Partial;
            var parser = new FrameParser();
            while (!session.IsCancellationRequested)
            {
                uint count = await reader.LoadAsync(2048);
                if (count == 0)
                {
                    break;
                }

                var chunk = new byte[count];
                reader.ReadBytes(chunk);
                parser.Append(chunk);
                foreach (RcspMessage message in parser.Drain())
                {
                    _logs.Add(LogDirection.Rx, message);
                    HandleMessage(message);
                }
            }
        }
        catch (Exception ex) when (ex is not ObjectDisposedException)
        {
            RaiseDisconnected(session, $"连接中断：{ex.Message}");
            return;
        }

        RaiseDisconnected(session, "蓝牙连接已断开。");
    }

    /// <summary>统一处理一条消息：先补全待处理请求，再按类型分发到响应或入站处理。</summary>
    private void HandleMessage(RcspMessage message)
    {
        TryCompletePending(message);
        if (message.Type == RcspMessageType.Response && message.Opcode == 0x00)
        {
            _logs.AddSystem($"[协议] UNEXPECTED_ACK seq={message.Sequence}（SET 可能未被设备接受）");
        }

        switch (message.Type)
        {
            case RcspMessageType.Response:
                ParseResponseContent(message);
                break;
            case RcspMessageType.EarbudsRequest:
            case RcspMessageType.EarbudsNotify:
                HandleInbound(message);
                break;
        }
    }

    /// <summary>解析各类响应内容（设备信息、运行信息、配置回读）。</summary>
    private void ParseResponseContent(RcspMessage message)
    {
        switch (message.Opcode)
        {
            case RcspOpcodes.GetDeviceInfo:
                ParseDeviceInfo(message.Payload);
                break;
            case RcspOpcodes.GetRunInfo:
                ParseRunInfo(message.Payload);
                break;
            case RcspOpcodes.GetConfig:
                ParseGetConfig(message.Payload);
                break;
        }
    }

    /// <summary>处理耳机主动发来的请求/通知，并对需要 ACK 的消息回响应。</summary>
    private void HandleInbound(RcspMessage message)
    {
        switch (message.Opcode)
        {
            case RcspOpcodes.AuthChallenge when message.Type != RcspMessageType.Response:
                HandleInboundAuthChallenge(message);
                break;
            case RcspOpcodes.AuthConfirm when message.Type != RcspMessageType.Response:
                _ = SendResponseAsync(RcspOpcodes.AuthConfirm, message.Sequence, PayloadBuilder.AuthConfirmResponse());
                break;
            case RcspOpcodes.ReportStatus:
                ParseDeviceUpdate(message.Payload);
                _ = SendResponseAsync(RcspOpcodes.ReportStatus, message.Sequence, Array.Empty<byte>());
                break;
            case RcspOpcodes.NotifyConfig:
                ParseNotifyConfig(message.Payload);
                _ = SendResponseAsync(RcspOpcodes.NotifyConfig, message.Sequence, Array.Empty<byte>());
                break;
        }
    }

    /// <summary>应答耳机主动发起的鉴权挑战。</summary>
    private void HandleInboundAuthChallenge(RcspMessage message)
    {
        if (message.Payload.Length < 16)
        {
            return;
        }

        byte[] challenge = message.Payload.Length >= 17
            ? message.Payload[^16..]
            : message.Payload;
        byte[] response = new SaferPlus().ComputeChallengeResponse(challenge);
        _ = SendResponseAsync(RcspOpcodes.AuthChallenge, message.Sequence, PayloadBuilder.AuthChallengeResponse(response));
    }

    /// <summary>解析 GetDeviceInfo 响应：固件版本、VID/PID 与三块电量。</summary>
    private void ParseDeviceInfo(byte[] payload)
    {
        foreach (TlvEntry entry in TlvParser.Walk(payload, 1))
        {
            switch (entry.Index)
            {
                case 0x01 when entry.Data.Length >= 4:
                    _snapshot.FirmwareVersion1 = NibbleVersion(entry.Data[0], entry.Data[1]);
                    _snapshot.FirmwareVersion2 = NibbleVersion(entry.Data[2], entry.Data[3]);
                    break;
                case 0x03 when entry.Data.Length >= 4:
                    _snapshot.VidPid = $"VID: 0x{entry.Data[0]:X2}{entry.Data[1]:X2}, PID: 0x{entry.Data[2]:X2}{entry.Data[3]:X2}";
                    break;
                case 0x07 when entry.Data.Length >= 3:
                    _snapshot.LeftBattery = BatteryReading.FromByte(entry.Data[0]);
                    _snapshot.RightBattery = BatteryReading.FromByte(entry.Data[1]);
                    _snapshot.CaseBattery = BatteryReading.FromByte(entry.Data[2]);
                    break;
            }
        }

        SnapshotUpdated?.Invoke(_snapshot);
    }

    /// <summary>解析 GetRunInfo 响应中的降噪模式。</summary>
    private void ParseRunInfo(byte[] payload)
    {
        foreach (TlvEntry entry in TlvParser.Walk(payload, 1))
        {
            if (entry.Index == 0x09 && entry.Data.Length >= 1)
            {
                AncModeChanged?.Invoke(entry.Data[0]);
            }
        }
    }

    /// <summary>解析 GetConfig 响应并转发配置 ID 与数值。</summary>
    private void ParseGetConfig(byte[] payload)
    {
        if (payload.Length < 3)
        {
            return;
        }

        byte configId = payload[2];
        byte[] values = payload[3..];
        ConfigChanged?.Invoke(configId, values);
    }

    /// <summary>解析设备主动上报的电量与降噪状态。</summary>
    private void ParseDeviceUpdate(byte[] payload)
    {
        foreach (TlvEntry entry in TlvParser.Walk(payload, 1))
        {
            switch (entry.Index)
            {
                case 0x00 when entry.Data.Length >= 3:
                    _snapshot.LeftBattery = BatteryReading.FromByte(entry.Data[0]);
                    _snapshot.RightBattery = BatteryReading.FromByte(entry.Data[1]);
                    _snapshot.CaseBattery = BatteryReading.FromByte(entry.Data[2]);
                    SnapshotUpdated?.Invoke(_snapshot);
                    break;
                case 0x04 when entry.Data.Length >= 1:
                    AncModeChanged?.Invoke(entry.Data[0]);
                    break;
            }
        }
    }

    /// <summary>解析配置通知：EffectStrength 拆成模式与强度，其余原样转发。</summary>
    private void ParseNotifyConfig(byte[] payload)
    {
        foreach (TlvEntry entry in TlvParser.Walk(payload, 2))
        {
            if (entry.Index == RcspConfigIds.EffectStrength && entry.Data.Length >= 2)
            {
                byte mode = entry.Data[0];
                byte strength = entry.Data[1];
                AncModeChanged?.Invoke(mode);
                byte target = mode == RcspAncModes.NoiseCancelling ? RcspStrengthTargets.Anc : RcspStrengthTargets.Transparency;
                ConfigChanged?.Invoke(RcspConfigIds.EffectStrength, new[] { target, strength });
            }
            else
            {
                ConfigChanged?.Invoke(entry.Index, entry.Data);
            }
        }
    }

    /// <summary>
    /// 按操作码匹配待处理请求并完成其等待任务。
    /// 不可用 seq 单独匹配：设备通知（如 0xF4）可能与请求 seq 撞车，
    /// 会把 0x50 鉴权等待错误地完成成一条通知帧。
    /// </summary>
    private void TryCompletePending(RcspMessage message)
    {
        TaskCompletionSource<RcspMessage>? tcs;
        lock (_pendingLock)
        {
            tcs = null;
            foreach ((_, PendingRequest pending) in _pending)
            {
                if (pending.Opcode != message.Opcode)
                {
                    continue;
                }

                if (pending.RequireDeviceInitiated)
                {
                    // 等待耳机主动发起：只接受 Request/Notify，排除 Response。
                    if (message.Type == RcspMessageType.Response)
                    {
                        continue;
                    }
                }
                else
                {
                    // 手机请求：只接受 Response，排除耳机主动帧。
                    if (message.Type != RcspMessageType.Response)
                    {
                        continue;
                    }
                }

                tcs = pending.Completion;
                break;
            }
        }

        tcs?.TrySetResult(message);
    }

    /// <summary>以当前会话触发断开事件。</summary>
    private void RaiseDisconnected(string reason) => RaiseDisconnected(_cts, reason);

    /// <summary>确保同一会话只上报一次断开，并清除连接标志。</summary>
    private void RaiseDisconnected(CancellationTokenSource? session, string reason)
    {
        lock (_stateLock)
        {
            if (_disconnectRaised || (session is not null && !ReferenceEquals(_cts, session)))
            {
                return;
            }

            _disconnectRaised = true;
            _connected = false;
        }

        Disconnected?.Invoke(reason);
    }

    /// <summary>把固件两个字节拆成 X.X.X.X 的版本文字。</summary>
    private static string NibbleVersion(byte high, byte low) =>
        $"{(high >> 4) & 0xF}.{high & 0xF}.{(low >> 4) & 0xF}.{low & 0xF}";
}
