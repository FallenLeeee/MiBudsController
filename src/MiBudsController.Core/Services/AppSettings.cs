using System.Text.Json;

namespace MiBudsController.Core.Services;

/// <summary>
/// 面向未打包应用的小型 JSON 设置存储；
/// 未打包场景下 ApplicationData 可能不可用，因此直接使用本地 JSON 文件。
/// </summary>
public sealed class AppSettings
{
    private readonly string _path;
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>在 LocalApplicationData 下创建设置文件并加载已有内容。</summary>
    public AppSettings(string fileName = "settings.json")
    {
        string folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MiBudsController");
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, fileName);
        Load();
    }

    /// <summary>读取指定键的字符串值。</summary>
    public string? Get(string key)
    {
        lock (_gate)
        {
            return _values.TryGetValue(key, out string? value) ? value : null;
        }
    }

    /// <summary>写入键值（null 表示删除），随后立即持久化。</summary>
    public void Set(string key, string? value)
    {
        lock (_gate)
        {
            if (value is null)
            {
                _values.Remove(key);
            }
            else
            {
                _values[key] = value;
            }

            Save();
        }
    }

    /// <summary>从 JSON 文件加载设置；文件损坏时静默忽略。</summary>
    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path));
            if (loaded is null)
            {
                return;
            }

            foreach ((string key, string value) in loaded)
            {
                _values[key] = value;
            }
        }
        catch
        {
            // Corrupt settings should never block startup.
        }
    }

    /// <summary>把当前设置写回 JSON 文件；持久化失败不阻塞使用。</summary>
    private void Save()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(_values));
        }
        catch
        {
            // Settings persistence is best-effort.
        }
    }
}
