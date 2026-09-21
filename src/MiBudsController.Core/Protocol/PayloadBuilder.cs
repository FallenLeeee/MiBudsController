namespace MiBudsController.Core.Protocol;

/// <summary>RCSP 协议载荷构造器：集中生成设备信息、配置、鉴权等请求的 payload。</summary>
public static class PayloadBuilder
{
    /// <summary>获取设备信息：4 个 0xFF 表示读取全部字段。</summary>
    public static byte[] GetDeviceInfo() => new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };

    /// <summary>获取运行信息：4 个 0xFF 表示读取全部字段。</summary>
    public static byte[] GetRunInfo() => new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };

    /// <summary>设置降噪模式：[长度=2, 0x04, 模式]。</summary>
    public static byte[] SetAncMode(byte mode) => new byte[] { 0x02, 0x04, mode };

    /// <summary>读取配置：[类型=0x00, 配置ID]。</summary>
    public static byte[] GetConfig(byte configId) => new byte[] { 0x00, configId };

    /// <summary>写入配置：[长度, 类型=0x00, 配置ID, 数值...]。</summary>
    public static byte[] SetConfig(byte configId, params byte[] values)
    {
        var payload = new byte[values.Length + 3];
        payload[0] = (byte)(values.Length + 2);
        payload[1] = 0x00;
        payload[2] = configId;
        values.CopyTo(payload, 3);
        return payload;
    }

    /// <summary>鉴权挑战请求：[0x01, 16 字节随机数]。</summary>
    public static byte[] AuthChallenge(byte[] random)
    {
        if (random.Length != 16)
        {
            throw new ArgumentException("Challenge must be 16 bytes.", nameof(random));
        }

        var payload = new byte[17];
        payload[0] = 0x01;
        random.CopyTo(payload, 1);
        return payload;
    }

    /// <summary>鉴权挑战响应：[0x01, 16 字节响应]。</summary>
    public static byte[] AuthChallengeResponse(byte[] response)
    {
        if (response.Length != 16)
        {
            throw new ArgumentException("Response must be 16 bytes.", nameof(response));
        }

        var payload = new byte[17];
        payload[0] = 0x01;
        response.CopyTo(payload, 1);
        return payload;
    }

    /// <summary>鉴权确认请求：[0x01, 0x00]。</summary>
    public static byte[] AuthConfirmRequest() => new byte[] { 0x01, 0x00 };

    /// <summary>鉴权确认响应：[0x01]。</summary>
    public static byte[] AuthConfirmResponse() => new byte[] { 0x01 };
}
