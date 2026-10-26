# 第五阶段发布交付

版本：`1.0.0-rc.1`。本阶段交付内部验收候选包、安装/文件关联、图标、依赖资料导出和真实发布验证工具。Windows 10 目标机、原生对应源码和项目发行许可未全部完成，因此不标为正式发行通过。

## 构建与交付

```powershell
./scripts/Setup-Dependencies.ps1
./scripts/Setup-InstallerTool.ps1
./scripts/Build-Release.ps1
./scripts/Test-ReleaseManifest.ps1
```

打包支持 Windows 自带的 PowerShell 5.1 和 PowerShell 7，无需先调用 `pwsh` 或把开发工具内部的 Shell 加入 PATH。如果终端限制脚本执行，在项目根目录执行 `powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./scripts/Build-Release.ps1`；该参数仅作用于本次子进程，不更改系统或用户的长期执行策略。首次构建联网获取固定 SDK、NuGet、原生依赖和 Inno Setup；运行/安装不下载依赖。工具均位于 `.tools`。Inno Setup 固定 6.7.3，下载 SHA256 和已解压编译器 SHA256 均固定，下载后验证官方签名。

默认执行 Release 构建与单元测试，然后显式还原 `win-x64` 运行时资产，以自包含模式生成 `NovaPlayer.exe`。构建发布前仅清理经过绝对路径核对的版本输出目录，防止旧文件混入；`-SkipBuild` 仅供开发时对现有发布目录重新打包。

产物目录 `artifacts/release/1.0.0-rc.1/`：

| 文件 | 用途 |
| --- | --- |
| `player/NovaPlayer.exe` | 直接启动；必须保留整个 player 目录 |
| `NovaPlayer-1.0.0-rc.1-win-x64-setup.exe` | 当前用户安装器，无需管理员或全局 .NET |
| `NovaPlayer-1.0.0-rc.1-win-x64-portable.zip` | 自包含便携目录归档 |
| `NovaPlayer-1.0.0-rc.1-project-source.zip` | 本项目源码、SQLite 初始化/迁移、构建/验收脚本；不是全部原生依赖对应源码归档 |
| `release-manifest.json` | 发布目录逐文件大小、SHA256、发行状态 |
| `SHA256SUMS.txt` | EXE 与 ZIP 归档校验和 |
| `player/licenses/dependency-audit.json` | 真实分发依赖、版本、版权、源码仓库、包 SHA512、原生 SHA256 与待办 |

安装包当前未签名；不会以临时自签名证书伪装为受信任发行签名。需要正式签名时，由产品权利人提供其代码签名身份和发布策略。

## 图标与文件关联

SVG 母版与七档 ICO 位于 `NovaPlayer.App/Assets`。`New-ApplicationIcon.ps1` 生成 16/24/32/48/64/128/256 像素的独立 PNG 图像并写入 ICO，正确释放 GDI 对象。项目 ApplicationIcon 嵌入可执行程序，窗口显式使用同一资源，安装器/卸载入口/快捷方式/媒体文件图标一致。

`installer/NovaPlayer.iss` 注册 HKCU `NovaPlayer.Video`、`Capabilities`、`RegisteredApplications` 及 12 个扩展名的 `OpenWithProgids`。打开命令为 `"{app}\NovaPlayer.exe" "%1"`，保留路径引号，支持中文和空格。不会写扩展名默认值或 Windows `UserChoice`。

文件关联任务默认选中，可在安装时取消。卸载仅删除自己的 ProgID、Capabilities、候选值与 RegisteredApplications 值；扩展名候选键仅在空时删除。卸载保留 `%LOCALAPPDATA%\NovaPlayer` 的数据库、设置、缓存和日志。覆盖安装使用固定 AppId，不重置用户资料。

## 兼容性范围

当前实测机器：Windows 11 Pro 23H2，build `22631.2428`，x64，NVIDIA GeForce RTX 4060 Ti，.NET 10.0.13。注册表历史 ProductName 可能仍写 Windows 10，报告使用真实内核 build 识别系统。该 Windows 11 Pro 版本已经不在当前 .NET 官方支持的维护矩阵内；本机功能通过不能替代受维护版本的验证。

用户确认使用“最通用的 Windows 10”，因此通用验收目标确定为 Windows 10 22H2 x64（家庭/专业/企业版），同时保留 LTSC 2021。安装器最低门槛为 Windows 10 21H2 / build 19044，仅允许原生 x64 系统。Windows 10 LTSC 2019、2016 不在本候选包安装门槛内，不能只因 .NET 支持而声称整套原生渲染链兼容。Windows 10 22H2 的维护状态取决于具体部署/ESU策略；能运行与仍受系统厂商维护是不同结论。

| 目标 | 当前结论 |
| --- | --- |
| Windows 11 x64 / 当前 build 22631 | 有本机真实验证；详见 stage5-verification.md |
| 受维护的 Windows 11 版本 | 待对应机器完整运行验收脚本 |
| Windows 10 Enterprise LTSC 2021 x64 | 已准备自包含安装包与同一套脚本；缺少真实目标机实测 |
| Windows 10 22H2 x64（通用版） | 用户确认的兼容目标；缺少真实测试环境，待执行完整脚本 |
| Windows 10 LTSC 2019/2016、x86、ARM64 | 未验证；安装器不承诺支持 |

依据：[.NET 10 官方 OS 支持矩阵](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)。它只说明 .NET 支持范围，不证明显卡驱动、OpenGL、libmpv 和 FFmpeg 一定兼容。

## 真实验收与复现

在目标 Windows 桌面中顺序运行，避免多个验收窗口互相遮挡：

```powershell
$player = 'artifacts/release/1.0.0-rc.1/player/NovaPlayer.exe'
./scripts/Verify-Stage1.ps1 -Runs 3 -PlayerPath $player
./scripts/Verify-Stage2.ps1 -PlayerPath $player
./scripts/Verify-Stage3.ps1 -PlayerPath $player
./scripts/Verify-Stage4.ps1 -PlayerPath $player
./scripts/Verify-Stage5.ps1 -PlayerPath $player -SoakSeconds 120
./scripts/Verify-Installer.ps1 -InstallerPath 'artifacts/release/1.0.0-rc.1/NovaPlayer-1.0.0-rc.1-win-x64-setup.exe' -VideoPath 'artifacts/真实视频 核心验证.mp4'
./scripts/Test-ReleaseManifest.ps1
```

第五阶段生成 16 个真实样片，覆盖所有 12 种导入扩展名，H.264、HEVC 10bit、VP9、AV1、MPEG-4、MPEG-2、WMV2、FLV1，AAC/MP3/MP2/WMA/Opus、无音频、竖屏、1080p 与 4K。自动解码和强制软解分别验证实际尺寸、编码、变化的 GPU 像素、媒体时钟，并记录解码路径、丢帧、帧数。15 倍速不等于显示每一帧；4K 超出设备能力时记录实际速度，不以配置值代替性能。

像素采样仅用于短时确认真实画面；媒体时钟和持续运行阶段关闭 GPU 回读，使用生产播放路径。稳定性段循环播放并在 1/15 倍速之间切换，反复画中画和全屏，采集进程私有内存、托管堆、累计分配、Gen2 次数、句柄、线程和帧数。默认仅 120 秒，不能证明数小时运行或所有驱动不存在泄漏。提供 `-SoakSeconds` 扩展时长；使用已有计划时，计划内的 SoakSeconds 决定应用运行时长，脚本参数用于外部超时预算，应保持一致。

安装测试使用项目下独立中文安装目录和测试档案；发现已有 Nova Player 安装/候选值时立即停止，保护用户已安装版本。测试比较安装前后的扩展名默认值和 UserChoice，验证中文媒体真实打开、跨进程 12 秒续播、同版本覆盖安装、卸载与注册清理。失败也尝试卸载本轮实例，报告保留在 artifacts。

## 正式发行条件

1. 在产品确认的 Windows 10 LTSC / Enterprise 和受维护 Windows 11 机器上完成安装、播放、续播、卸载及图形验证，保留系统、GPU、驱动与报告。
2. 取得实际原生文件所有受许可组件的精确对应源码/修补/构建/版权资料，或更换具备完整来源和可履行分发条件的构建。
3. 由权利人确定本项目发行许可证和 GPL 组合分发方案。当前没有擅自更改自有代码许可，也没有用一个不完整的源码 ZIP 代替原生对应源码。
4. 稳定性报告必须通过；任何未通过的测量必须保留并解释，不能放宽阈值或只显示设置中的 15 倍速。

许可证检查的实际结论见 `THIRD_PARTY.md`；目前机器可验证部分与未验证部分分别记录，不把候选包当作正式发布批准。
