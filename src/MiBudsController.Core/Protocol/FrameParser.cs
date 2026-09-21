namespace MiBudsController.Core.Protocol;

/// <summary>
/// RCSP 帧解析器：按 0xFE 0xDC 0xBA 头尾切分出完整消息，
/// 处理粘包、半包和长度异常，保证蓝牙流可可靠解析。
/// </summary>
public sealed class FrameParser
{
    private const int MaxBufferSize = 4096;
    private readonly List<byte> _buffer = new();

    /// <summary>追加新收到的数据，超长时丢弃最旧部分以控制内存。</summary>
    public void Append(ReadOnlySpan<byte> data)
    {
        _buffer.AddRange(data);
        if (_buffer.Count > MaxBufferSize)
        {
            _buffer.RemoveRange(0, _buffer.Count - MaxBufferSize);
        }
    }

    /// <summary>从缓冲中解析出所有完整消息；不足一条时返回空列表。</summary>
    public IReadOnlyList<RcspMessage> Drain()
    {
        var messages = new List<RcspMessage>();
        while (true)
        {
            int start = FindHeader();
            if (start < 0)
            {
                _buffer.Clear();
                break;
            }

            if (start > 0)
            {
                _buffer.RemoveRange(0, start);
            }

            if (_buffer.Count < 7)
            {
                break;
            }

            int payloadLength = (_buffer[5] << 8) | _buffer[6];
            int totalLength = 8 + payloadLength;
            if (totalLength > MaxBufferSize)
            {
                _buffer.RemoveAt(0);
                continue;
            }

            if (_buffer.Count < totalLength)
            {
                break;
            }

            if (_buffer[totalLength - 1] != 0xEF)
            {
                _buffer.RemoveAt(0);
                continue;
            }

            var frame = _buffer.GetRange(0, totalLength).ToArray();
            _buffer.RemoveRange(0, totalLength);
            messages.Add(RcspMessage.Decode(frame));
        }

        return messages;
    }

    /// <summary>查找帧头 0xFE 0xDC 0xBA 的起始位置，找不到返回 -1。</summary>
    private int FindHeader()
    {
        for (int i = 0; i < _buffer.Count - 2; i++)
        {
            if (_buffer[i] == 0xFE && _buffer[i + 1] == 0xDC && _buffer[i + 2] == 0xBA)
            {
                return i;
            }
        }

        return -1;
    }
}
