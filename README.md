# Nova Player

Windows x64 本地视频播放器。通用兼容目标为 Windows 10 22H2 x64 和 Windows 11 x64；Windows 10 真实目标机与原生依赖对应源码/发行许可条件尚未完成，不标为正式发行通过。

## 运行

构建命令及发行条件见 [发布](docs/stage5-release.md)。

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
| 上一条 / 下一条 | Ctrl + ← / → |
| 画中画 / 返回主窗口 | Ctrl + P |
| 显示 / 隐藏字幕 | Ctrl + S |

## 从源码构建

在项目根目录使用 Windows 自带的 PowerShell 5.1 或 PowerShell 7 均可，无需先运行 `pwsh`：

```powershell
./scripts/Setup-Dependencies.ps1 -IncludeVerificationTools
./scripts/Build.ps1 -Publish
./scripts/Verify-Stage1.ps1 -Runs 3
./scripts/Verify-Stage2.ps1
./scripts/Verify-Stage3.ps1
./scripts/Verify-Stage4.ps1
./scripts/Build-Release.ps1
./scripts/Test-ReleaseManifest.ps1
```

日常打安装包，在 Rider 底部终端执行下面的命令。它使用 Windows 自带的 PowerShell，仅允许本次子进程执行项目脚本，不改变系统或用户的长期执行策略：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "E:\RiderFile\NovaPlayer\scripts\Build-Release.ps1"
```

成功后安装程序位于 `artifacts/release/1.0.0-rc.1/NovaPlayer-1.0.0-rc.1-win-x64-setup.exe`；再运行 `powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./scripts/Test-ReleaseManifest.ps1` 核对文件完整性。

首次准备依赖和 NuGet 还原需要网络。程序播放本地视频时不需要网络。SDK 安装于 `.tools/dotnet`，不改变全局 SDK；构建脚本自动使用本地 SDK。
