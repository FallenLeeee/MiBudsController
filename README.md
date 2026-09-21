# MiBudsController

Windows 桌面应用：通过蓝牙 Classic RFCOMM（RCSP）控制 **Redmi Buds 8 Pro**，以及协议兼容的 Redmi / Xiaomi 耳机。

部分其他小米/红米耳机可用部分功能，后续可能考虑扩展其他耳机

> 非官方项目，与小米 / Redmi 无隶属关系。请自行承担使用风险。

## 功能

- 自动发现 **系统已连接** 的耳机
- 降噪：通透 / 降噪 / 关闭功能切换
- 音效：低延迟等协议已覆盖项
- 空间音频：沉浸声、小米 / 杜比模式、场景渲染、头部追踪
- 系统托盘迷你面板
- 设置：开机自启、最小化到托盘
- 协议日志页：十六进制帧 + 摘要；调试模式开启后可看，手动「开始记录」才写盘

## 环境

### 最终用户（下载 Release）

| 项目 | 要求 |
|------|------|
| OS | Windows 10 1809+ 或 Windows 11，**仅 x64** |
| 蓝牙 | 系统蓝牙 Classic，耳机已在系统设置中连接 |
| 运行时 | **无需**另装 .NET / Windows App SDK（zip 为自包含） |

### 开发者

| 项目 | 要求 |
|------|------|
| SDK | .NET 10 SDK |
| IDE | Visual Studio 2022 / 18+（可选） |

## 构建

打开解决方案 **`MiBudsController.sln`**，启动项目必须是 **`src\MiBudsController`**（WinExe）。

```powershell
powershell -File scripts\publish-release.ps1
# 或 VS：生成 → 发布
```

Debug：

```powershell
.\src\MiBudsController\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\MiBudsController.exe
```

若 VS 报「无法直接启动带有类库输出类型的项目」：删除 `.vs` 后用 sln 重新打开，将 **MiBudsController** 设为启动项目，配置选 **Unpackaged**。

本机 `dotnet restore` 若报 `path1` 空引用：脚本已改用 `tools\nuget.exe` + VS MSBuild。

## 使用

1. 在 **Windows 设置 → 蓝牙和其他设备** 中连接耳机  
2. 解压下载后的zip，运行AAA-Run-MiBudsController，建议设置开机自启
3. 启动本应用，自动扫描已连接设备并接入  

> 暂未检查多设备同时连接兼容性，若无法正常控制耳机请断开其他设备连接。

## 项目结构

```text
MiBudsController.sln
src/
  MiBudsController/          # WinUI 3 界面（WinExe）
  MiBudsController.Core/     # RFCOMM、SAFER+、协议解析
```


## 已知限制

- 低延迟等能力因固件 / 区域而异，以真机为准  
- 某些配置 GET 回读可能与 SET 后界面不一致（设备侧已知行为）  
- 耳机须先由系统蓝牙连接；本应用不负责配对  
- 不保证与未来固件兼容
- 本软件大量采用AI制作，请谨慎学习参考

## 许可证

本项目采用 [MIT License](LICENSE) 开源。

协议与 UI 细节仅用于兼容自有设备与学习研究；请遵守当地法律与厂商服务条款。
