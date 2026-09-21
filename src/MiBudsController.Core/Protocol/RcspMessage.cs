namespace MiBudsController.Core.Protocol;

/// <summary>
/// RCSP 消息：负责按 0xFE 0xDC 0xBA 头 + 0xEF 尾的帧格式编码/解码，
/// 请求方向多带 1 字节长度、响应方向多带 2 字节。
/// </summary>
public sealed class RcspMessage
{
    private const byte Header0 = 0xFE;
    private const byte Header1 = 0xDC;
    private const byte Header2 = 0xBA;
    private const byte Trailer = 0xEF;

    /// <summary>根据类型、操作码、序列号和载荷构造消息。</summary>
    public RcspMessage(RcspMessageType type, byte opcode, byte sequence, byte[] payload)
    {
        Type = type;
        Opcode = opcode;
        Sequence = sequence;
        Payload = payload;
    }

    /// <summary>消息类型。</summary>
    public RcspMessageType Type { get; }

    /// <summary>操作码。</summary>
    public byte Opcode { get; }

    /// <summary>消息序列号，用于请求/响应配对。</summary>
    public byte Sequence { get; }

    /// <summary>载荷内容。</summary>
    public byte[] Payload { get; }

    /// <summary>
    /// 编码为二进制帧：头部 3 字节 + 类型 + 操作码 + 2 字节长度 + 序列号 + 载荷 + 尾字节。
    /// </summary>
    public byte[] Encode()
    {
        bool isRequest = Type.IsRequest();
        int payloadLength = Payload.Length + (isRequest ? 1 : 2);
        var frame = new byte[8 + payloadLength];
        frame[0] = Header0;
        frame[1] = Header1;
        frame[2] = Header2;
        frame[3] = (byte)Type;
        frame[4] = Opcode;
        frame[5] = (byte)(payloadLength >> 8);
        frame[6] = (byte)payloadLength;

        int offset = 7;
        if (!isRequest)
        {
            frame[offset++] = 0x00;
        }

        frame[offset++] = Sequence;
        Payload.CopyTo(frame, offset);
        frame[^1] = Trailer;
        return frame;
    }

    /// <summary>从二进制帧解码消息，严格校验头部、尾部、长度及载荷范围。</summary>
    public static RcspMessage Decode(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 9)
        {
            throw new ArgumentException("Frame is too short.", nameof(frame));
        }

        if (frame[0] != Header0 || frame[1] != Header1 || frame[2] != Header2)
        {
            throw new ArgumentException("Frame header is invalid.", nameof(frame));
        }

        if (frame[^1] != Trailer)
        {
            throw new ArgumentException("Frame trailer is invalid.", nameof(frame));
        }

        var type = (RcspMessageType)frame[3];
        bool isRequest = type.IsRequest();
        int payloadLength = (frame[5] << 8) | frame[6];
        if (frame.Length != 8 + payloadLength)
        {
            throw new ArgumentException("Frame length does not match declared payload length.", nameof(frame));
        }

        int payloadOffset = 7 + (isRequest ? 1 : 2);
        byte sequence = frame[payloadOffset - 1];
        byte[] payload = frame[payloadOffset..^1].ToArray();
        return new RcspMessage(type, frame[4], sequence, payload);
    }
}
