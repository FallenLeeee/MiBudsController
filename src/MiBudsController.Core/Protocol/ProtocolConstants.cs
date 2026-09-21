namespace MiBudsController.Core.Protocol;

/// <summary>RCSP 消息类型：请求/响应/耳机主动请求/通知。</summary>
public enum RcspMessageType : byte
{
    PhoneRequest = 0xC4,
    Response = 0x04,
    EarbudsRequest = 0xC0,
    EarbudsNotify = 0xC7,
}

/// <summary>RCSP 消息类型的辅助扩展。</summary>
public static class RcspMessageTypeExtensions
{
    /// <summary>是否为请求方向（按消息类型的高位判断）。</summary>
    public static bool IsRequest(this RcspMessageType type) => ((byte)type & 0x40) != 0;
}

/// <summary>RCSP 操作码（opcode）常量。</summary>
public static class RcspOpcodes
{
    public const byte GetDeviceInfo = 0x02;
    public const byte SetAnc = 0x08;
    public const byte GetRunInfo = 0x09;
    public const byte ReportStatus = 0x0E;
    public const byte AuthChallenge = 0x50;
    public const byte AuthConfirm = 0x51;
    public const byte SetConfig = 0xF2;
    public const byte GetConfig = 0xF3;
    public const byte NotifyConfig = 0xF4;
}

/// <summary>配置项 ID：与小米耳机协议中的 Config 编号一一对应。</summary>
public static class RcspConfigIds
{
    public const byte AudioMode = 0x01;           // 小米空间音频(0) / 杜比空间音频(1)
    public const byte EffectStrength = 0x0B;      // 降噪强度(target 0x01) / 通透模式(target 0x02)
    // p76c/official: SWITCH_SPATIAL_AUDIO is the primary write channel.
    // DeviceConfigSpatialAudio binds configId 0x1D and carries a full bitmap:
    //   bit0=spatial on, bit1-2=preference, bit3=head tracking, bit4=virtual surround.
    public const byte SpatialAudioSwitch = 0x1D;
    // Diagnostic/aux only on Redmi Buds 8 Pro: SET 0x1E often returns opcode 0x00.
    public const byte SpatialAudioBitmap = 0x1E;
    public const byte AdaptiveAnc = 0x25;
    public const byte LowLatency = 0x2F;
    public const byte SceneRendering = 0x36;
    /// <summary>Official SpatialAudioVM.setSpatialAudioMode writes mode via this id.</summary>
    public const byte DolbyAudioSpatialMode = 0x68;
    public const byte SmartNoise = 0x66;
}

/// <summary>EffectStrength 配置的目标类型。</summary>
public static class RcspStrengthTargets
{
    /// <summary>降噪强度。</summary>
    public const byte Anc = 0x01;

    /// <summary>通透模式预设。</summary>
    public const byte Transparency = 0x02;
}

/// <summary>降噪模式协议值。</summary>
public static class RcspAncModes
{
    /// <summary>关闭。</summary>
    public const byte Off = 0x00;

    /// <summary>降噪。</summary>
    public const byte NoiseCancelling = 0x01;

    /// <summary>通透。</summary>
    public const byte Transparency = 0x02;
}
