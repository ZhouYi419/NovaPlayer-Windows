# Nova Player

Windows x64 本地视频播放器。当前完成 PRD 第一阶段：播放内核与渲染可行性验证。

## 运行

本次已生成便携版本：`artifacts/player/NovaPlayer.App.exe`。双击运行即可，无需全局安装 .NET 10；必须保留整个 `artifacts/player` 目录。

使用“打开视频”、`Ctrl+O`、拖入单个文件或命令行文件参数打开本地视频。视频名称、分辨率、时长与进度来自真实文件和 libmpv。

| 操作 | 快捷键 |
| --- | --- |
| 播放 / 暂停 | Space |
| 快退 / 快进 10 秒 | ← / → |
| 音量减 / 加 | ↓ / ↑ |
| 静音 | M |
| 全屏 / 退出 | F / Esc |
| 降低 / 增加 0.25 倍速 | `[` / `]` |
| 恢复 1 倍速 | `\` |

倍速菜单支持全部 PRD 预设及 0.25～15 自定义输入。超过 8× 自动静音，回到较低倍速后恢复用户原本的静音状态。全屏静止 3 秒隐藏控制层，菜单打开和拖动进度期间保持显示。

## 从源码构建

在项目根目录使用 PowerShell：

```powershell
./scripts/Setup-Dependencies.ps1 -IncludeVerificationTools
./scripts/Build.ps1 -Publish
./scripts/Verify-Stage1.ps1 -Runs 3
```

首次准备依赖和 NuGet 还原需要网络。程序播放本地视频时不需要网络。SDK 安装于 `.tools/dotnet`，不改变全局 SDK；构建脚本自动使用本地 SDK。

`Verify-Stage1.ps1` 默认生成 90 秒 H.264/AAC 测试视频，也接受 `-VideoPath 'D:\视频\示例.mp4'`，建议选取至少 45 秒的可解码视频。验证需要可用的 Windows 图形桌面和 OpenGL 驱动，会创建播放器窗口并自动退出。报告输出至 `artifacts/stage1-summary.json` 和对应的单次 JSON。

## 项目结构

- `NovaPlayer.App/`：保留现有 WPF 项目位置；MVVM、视频宿主、控制浮层、显式启用的集成验收入口。
- `src/NovaPlayer.Core/`：`IPlayerEngine`、不可变播放状态及播放规则。
- `src/NovaPlayer.Infrastructure/`：libmpv P/Invoke、事件线程、独立 GPU 渲染线程。
- `tests/NovaPlayer.Tests/`：xUnit 边界测试。
- `native/win-x64/`：本地 libmpv 运行库，通过脚本下载校验，忽略 Git 跟踪。
- `docs/`：渲染方案、验收证据及当前范围。

日志位于 `%LOCALAPPDATA%\NovaPlayer\logs`，按天轮转，保留最近 14 份。没有实现播放历史持久化；当前退出不会记录续播位置。

当前界面是与原型配色一致的第一阶段播放器验证界面。完整导航、播放列表、历史、设置、字幕管理、画中画、数据库与安装程序属于后续阶段。

详细结果见 [第一阶段验收记录](docs/stage1-verification.md)，原生依赖来源见 [第三方依赖说明](THIRD_PARTY.md)。
