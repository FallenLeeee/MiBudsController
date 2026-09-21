using System.Diagnostics;
using Microsoft.Win32;

namespace MiBudsController.Services;

/// <summary>
/// 开机自启服务：为无打包 WinExe 写入当前用户注册表 HKCU\...\Run，
/// 启动参数带 --minimized，使应用开机后最小化到托盘。
/// </summary>
public static class AutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MiBudsController";

    /// <summary>取得当前可执行文件路径，多个来源都失败时回退到基目录下的 exe。</summary>
    public static string GetExecutablePath()
    {
        try
        {
            using Process process = Process.GetCurrentProcess();
            string? path = process.MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                return path;
            }
        }
        catch
        {
            // Fall through to process path / base directory.
        }

        if (!string.IsNullOrWhiteSpace(Environment.ProcessPath) && File.Exists(Environment.ProcessPath))
        {
            return Environment.ProcessPath;
        }

        return Path.Combine(AppContext.BaseDirectory, "MiBudsController.exe");
    }

    /// <summary>检查自启注册表项当前是否存在。</summary>
    public static bool IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 设置或移除自启项：启用时写入带引号的 exe 路径 + --minimized，
    /// 禁用时删除同名值；注册表被策略锁定等异常会被忽略。
    /// </summary>
    public static void SetEnabled(bool enabled)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (enabled)
            {
                string exe = GetExecutablePath();
                key.SetValue(ValueName, $"\"{exe}\" --minimized");
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // Registry may be locked by policy; UI keeps the toggle state local.
        }
    }
}
