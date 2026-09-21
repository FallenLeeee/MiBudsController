namespace MiBudsController.Core.Protocol;

/// <summary>TLV 条目：Index 为协议索引号，Data 为对应字段数据。</summary>
public readonly record struct TlvEntry(byte Index, byte[] Data);

/// <summary>小米 RCSP TLV 流解析器。</summary>
public static class TlvParser
{
    /// <summary>
    /// 遍历小米 RCSP TLV 流。
    /// indexOffset=1 用于 [长度][索引][数据...]，
    /// indexOffset=2 用于 [长度][附加字节][索引][数据...] 的流。
    /// </summary>
    public static IReadOnlyList<TlvEntry> Walk(ReadOnlySpan<byte> payload, int indexOffset)
    {
        var entries = new List<TlvEntry>();
        int i = 0;
        while (i + 1 < payload.Length)
        {
            int length = payload[i];
            if (length <= indexOffset)
            {
                break;
            }

            int indexPosition = i + indexOffset;
            if (indexPosition >= payload.Length)
            {
                break;
            }

            int dataStart = indexPosition + 1;
            int dataLength = length - indexOffset;
            if (dataStart + dataLength > payload.Length)
            {
                break;
            }

            entries.Add(new TlvEntry(payload[indexPosition], payload.Slice(dataStart, dataLength).ToArray()));
            i += length + 1;
        }

        return entries;
    }
}
