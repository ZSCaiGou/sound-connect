# sound-connect

个人自用的 Windows 桌面工具：把音效和你正在听的音乐送进语音软件，让队友听见。

界面用 Blazor（Razor 组件跑在 WebView2 里），音频全部走原生 WASAPI，两者在同一个进程里，没有 IPC。

## 它是怎么工作的

1. 一个原生采集服务用 WASAPI 的进程回环，从 QQ 音乐或浏览器**进程**里旁路抓一份声音
2. 抓到的音乐按增益渲染进虚拟线的输出端
3. 麦克风和音效由混音服务渲染进**同一条**虚拟线
4. Discord 的输入设备指向这条线的录音端，队友就听到了全部内容

音乐应用零配置，系统默认播放设备也不用改：我们只是从它的进程里旁路取一份，它自己照常播自己的。
因为不存在"采集某个设备"这件事，也就没有回授的来源。

选型过程和实测数据（哪条虚拟线能用、延迟多少）见 [docs/design.md](docs/design.md)。

## 技术栈

| 层 | 技术 |
|---|---|
| 运行时 | .NET 10（`net10.0-windows10.0.19041.0`） |
| 界面 | MAUI Blazor Hybrid（Razor 组件 + WebView2） |
| 音频 | WASAPI：进程回环采集 + 多设备渲染 |
| 虚拟线 | 复用系统里已有的 Steam 虚拟声卡，不额外装驱动 |

## 环境要求

- Windows 10 2004（build 19041）以上，进程回环 API 需要这个版本
- .NET 10 SDK + `maui-windows` workload
- 已安装 Steam（提供那对虚拟设备）

## 常用命令

```bash
dotnet build SoundConnect.slnx        # 构建
dotnet run --project SoundConnect     # 运行桌面应用

# 虚拟线自检
dotnet run --project tools/audio-probe -- list
dotnet run --project tools/audio-probe -- probe --render "名称" --capture "名称"
```

## 目录结构

```text
SoundConnect/            MAUI Blazor Hybrid 宿主（Windows 桌面）
SoundConnect.Shared/     Blazor 界面（Razor 组件库）
tools/audio-probe/       虚拟线探针：判断一对设备是不是回环，并量出往返延迟
docs/design.md           设计方案：数据通路、设备选型、验证清单
```

## 历史

这个仓库最初是一份 Electron 原型，已经废弃。需要找回那版代码，看 `legacy-electron` 标签。
