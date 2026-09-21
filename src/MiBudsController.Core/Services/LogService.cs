using System.Text;
using MiBudsController.Core.Protocol;
using IOPath = System.IO.Path;

namespace MiBudsController.Core.Services;

/// <summary>日志方向：发送、接收、系统消息。</summary>
public enum LogDirection
{
    Tx,
    Rx,
    System,
}

/// <summary>一条协议日志：时间、方向、原始帧和摘要说明。</summary>
public sealed record LogEntry(DateTimeOffset Timestamp, LogDirection Direction, byte[] Frame, string Summary)
{
    /// <summary>帧的十六进制文本。</summary>
    public string Hex => Convert.ToHexString(Frame);

    /// <summary>显示时间（毫秒精度）。</summary>
    public string Time => Timestamp.ToString("HH:mm:ss.fff");

    /// <summary>方向的中文文字。</summary>
    public string DirectionText => Direction switch
    {
        LogDirection.Tx => "发送",
        LogDirection.Rx => "接收",
        _ => "系统",
    };
}

/// <summary>
/// 协议日志服务：内存会话缓冲 + 可选磁盘记录，
/// 仅调试模式开启后才采集，磁盘写入需日志页手动开启。
/// </summary>
public sealed class LogService
{
    private static readonly object FileLock = new();
    private readonly string _logDirectory;
    private readonly StringBuilder _sessionBuffer = new();

    public LogService()
    {
        _logDirectory = IOPath.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MiBudsController",
            "logs");
    }

    public event Action<LogEntry>? EntryAdded;

    /// <summary>调试模式开启后才会把日志记入内存/UI。默认 false。</summary>
    public bool IsCaptureEnabled { get; set; }

    /// <summary>日志页手动开始记录后才写磁盘。默认 false，不随设置持久化。</summary>
    public bool IsDiskLogEnabled { get; set; }

    public string LogDirectory => _logDirectory;

    /// <summary>今天的协议日志文件路径。</summary>
    public string CurrentLogFile => IOPath.Combine(_logDirectory, $"protocol-{DateTime.Now:yyyyMMdd}.log");

    /// <summary>记录一条协议方向日志；opcode 0x00 的意外 ACK 会附加标记。</summary>
    public void Add(LogDirection direction, RcspMessage message)
    {
        if (!IsCaptureEnabled)
        {
            return;
        }

        string summary =
            $"type={message.Type} opcode=0x{message.Opcode:X2} seq={message.Sequence} payload={Convert.ToHexString(message.Payload)}";

        if (direction == LogDirection.Rx
            && message.Type == RcspMessageType.Response
            && message.Opcode == 0x00)
        {
            summary += " UNEXPECTED_ACK";
        }

        var entry = new LogEntry(
            DateTimeOffset.Now,
            direction,
            message.Encode(),
            summary);
        AppendSession(entry);
        EntryAdded?.Invoke(entry);
    }

    /// <summary>记录一条系统消息（如扫描、连接事件）。</summary>
    public void AddSystem(string summary)
    {
        if (!IsCaptureEnabled)
        {
            return;
        }

        var entry = new LogEntry(DateTimeOffset.Now, LogDirection.System, Array.Empty<byte>(), summary);
        AppendSession(entry);
        EntryAdded?.Invoke(entry);
    }

    /// <summary>返回当前会话内存缓冲的全部文本。</summary>
    public string GetSessionText()
    {
        lock (FileLock)
        {
            return _sessionBuffer.ToString();
        }
    }

    /// <summary>清空当前会话内存缓冲。</summary>
    public void ClearSessionBuffer()
    {
        lock (FileLock)
        {
            _sessionBuffer.Clear();
        }
    }

    /// <summary>
    /// 追加到会话缓冲，超限时丢弃最早内容；
    /// 磁盘记录开启时再写入当天文件，写盘失败不影响协议采集。
    /// </summary>
    private void AppendSession(LogEntry entry)
    {
        string line =
            $"[{entry.Timestamp:O}] {entry.DirectionText,-2} {entry.Hex,-64} {entry.Summary}";

        lock (FileLock)
        {
            _sessionBuffer.AppendLine(line);
            if (_sessionBuffer.Length > 512 * 1024)
            {
                _sessionBuffer.Remove(0, _sessionBuffer.Length - 256 * 1024);
            }

            if (!IsDiskLogEnabled)
            {
                return;
            }
        }

        if (!IsDiskLogEnabled)
        {
            return;
        }

        try
        {
            string path = IOPath.Combine(_logDirectory, $"protocol-{entry.Timestamp:yyyyMMdd}.log");
            Directory.CreateDirectory(_logDirectory);
            lock (FileLock)
            {
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // Disk logging must never break protocol collection.
        }
    }
}
