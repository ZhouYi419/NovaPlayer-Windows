# Nova Player

Windows x64 本地视频播放器。当前完成 PRD 第一阶段内核验证、第二阶段主界面及第三阶段核心功能闭环。

## 运行

本次已生成便携版本：`artifacts/player/NovaPlayer.App.exe`。双击运行即可，无需全局安装 .NET 10；必须保留整个 `artifacts/player` 目录。

使用“打开视频”、`Ctrl+O`、拖入文件或命令行文件参数导入本地视频。支持多选文件、拖入文件夹及 `Ctrl+Shift+O` 选择文件夹。文件夹递归导入、过滤非视频、去重，单次最多添加 10000 个视频。

首页包含顶部搜索、左侧导航、视频与控制层、右侧播放列表、底部信息标签及快捷设置。队列支持切换、拖动排序、上下移动与移除；搜索范围仅限已添加的队列和本机历史。导航至历史或设置时保持当前播放会话。

SQLite 保存每个视频的进度、独立倍速、观看时间、完成状态及播放队列；重启恢复队列，点击视频后按设置续播。支持自动续播、询问续播、始终从头播放，及顺序播放、单曲循环、列表循环、关闭连播。播放不足 5 秒不作为有效续播点，距离结尾不足 30 秒或已基本看完时从头播放。文件大小或修改时间变化时重置旧记录，失效文件可重新定位，自动连播跳过失效或无法解码的媒体。

首页、历史和队列显示真实视频提帧封面。只为可见条目请求封面，后台最多并发两路提帧，失败保留统一视频图标。历史支持搜索、继续播放、从头播放、单条删除及确认清空；删除历史保留视频元数据与队列，不删除原文件。

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
./scripts/Verify-Stage2.ps1
./scripts/Verify-Stage3.ps1
```

首次准备依赖和 NuGet 还原需要网络。程序播放本地视频时不需要网络。SDK 安装于 `.tools/dotnet`，不改变全局 SDK；构建脚本自动使用本地 SDK。

`Verify-Stage1.ps1` 默认生成 90 秒 H.264/AAC 测试视频，也接受 `-VideoPath 'D:\视频\示例.mp4'`，建议选取至少 45 秒的可解码视频。验证需要可用的 Windows 图形桌面和 OpenGL 驱动，会创建播放器窗口并自动退出。报告输出至 `artifacts/stage1-summary.json` 和对应的单次 JSON。

## 项目结构

- `NovaPlayer.App/`：保留现有 WPF 项目位置；MVVM、视频宿主、控制浮层、显式启用的集成验收入口。
- `src/NovaPlayer.Core/`：播放/持久化接口、不可变状态、视频记录、续播与队列规则。
- `src/NovaPlayer.Infrastructure/`：libmpv、GPU 渲染、串行后台 SQLite 服务、FFmpeg 封面缓存。
- `tests/NovaPlayer.Tests/`：xUnit 边界测试。
- `native/win-x64/`：本地 libmpv 与 FFmpeg 运行工具，通过脚本下载校验，忽略 Git 跟踪。
- `docs/`：渲染方案、验收证据及当前范围。

日志位于 `%LOCALAPPDATA%\NovaPlayer\logs`，按天轮转，保留最近 14 份。界面偏好存放在 `%LOCALAPPDATA%\NovaPlayer\settings\appearance.json`，快捷设置和设置页共用同一个配置实例，退出前刷新最后一次设置快照。

数据库位于 `%LOCALAPPDATA%\NovaPlayer\data\player.db`；封面位于 `%LOCALAPPDATA%\NovaPlayer\cache\thumbnails`。进度每 5 秒及暂停、跳转、倍速变化、切换、自然结束、退出时保存。SQLite 使用参数化 SQL、版本迁移和事务；写入失败只提示并记录日志，保持播放继续。损坏数据库先归档原文件和日志文件，再尝试恢复。封面以路径、大小、修改时间建立指纹，启动时按 30 天 / 2048 张 / 256 MiB 上限清理。未读到的媒体信息显示未知，不使用示例数据。

主界面支持深色、浅色、跟随系统主题；小于 1200 像素时隐藏播放列表，小于 1050 像素时导航切换为图标模式，底部信息在窄窗口改为纵向排列。已接入基础画中画（同一主窗口缩为置顶小窗）、外挂字幕加载与字幕显示开关；完整字幕/音轨管理、可修改快捷键及高级播放设置仍待后续完善。

第三阶段脚本连续启动三个独立进程，共享专用测试档案，验证真实跨进程保存与恢复；只操作样片副本，不使用日常播放档案。图形验收请顺序执行，避免多个窗口互相遮挡原生命中测试。

详细结果见 [第三阶段验收记录](docs/stage3-verification.md)、[第三阶段逐环节代码说明](docs/stage3-code-walkthrough.md)、[第一阶段验收记录](docs/stage1-verification.md)、[第二阶段验收记录](docs/stage2-verification.md)，依赖来源见 [第三方依赖说明](THIRD_PARTY.md)。
