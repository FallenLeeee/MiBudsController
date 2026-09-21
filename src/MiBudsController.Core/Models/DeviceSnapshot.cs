namespace MiBudsController.Core.Models;

/// <summary>
/// 单侧电量的协议表示：低 7 位为百分比，最高位为充电标志；
/// 0xFF 表示无读数。
/// </summary>
public sealed record BatteryReading(int? Level, bool IsCharging)
{
    /// <summary>把协议字节解析为电量读数；0xFF 返回 null。</summary>
    public static BatteryReading? FromByte(byte value)
    {
        if (value == 0xFF)
        {
            return null;
        }

        return new BatteryReading(value & 0x7F, (value & 0x80) != 0);
    }
}

/// <summary>设备快照：汇总电量、固件版本和 VID/PID，由协议响应更新。</summary>
public sealed class DeviceSnapshot
{
    /// <summary>左耳电量。</summary>
    public BatteryReading? LeftBattery { get; set; }

    /// <summary>右耳电量。</summary>
    public BatteryReading? RightBattery { get; set; }

    /// <summary>充电盒电量。</summary>
    public BatteryReading? CaseBattery { get; set; }

    /// <summary>固件版本第一段。</summary>
    public string? FirmwareVersion1 { get; set; }

    /// <summary>固件版本第二段。</summary>
    public string? FirmwareVersion2 { get; set; }

    /// <summary>设备 VID/PID 文本。</summary>
    public string? VidPid { get; set; }
}
