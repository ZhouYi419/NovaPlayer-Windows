# 第三方依赖与来源

已生成 `1.0.0-rc.1` 内部验收安装包和便携包。已核查实际分发的 9 个 NuGet 业务/传递包、2 个 .NET 运行时包及 2 个原生文件，并随包保留许可资料；**对应源码及项目发行许可尚未满足对外发布条件，不能将此候选包视为许可审查通过的正式发行包**。

| 依赖 | 固定版本 / 来源 | 许可与说明 |
| --- | --- | --- |
| .NET SDK / Desktop Runtime | Microsoft .NET 10.0.402 SDK（含 10.0.13 runtime） | MIT 等，以 SDK/runtime 随附 LICENSE 与第三方声明为准 |
| CommunityToolkit.Mvvm | NuGet 8.4.0 | MIT |
| Serilog | NuGet 4.3.0 | Apache-2.0 |
| Serilog.Sinks.File | NuGet 7.0.0 | Apache-2.0 |
| Microsoft.Data.Sqlite.Core | NuGet 10.0.13 | Microsoft SQLite 托管访问层，MIT |
| SQLitePCLRaw.bundle_e_sqlite3 / core / provider | NuGet 3.0.5 | Apache-2.0，按 NuGet 包随附声明保留 |
| SQLite 原生运行库 | NuGet SQLite 3.53.4，由 bundle 引入 | 按该包 `LICENSE.txt` 保留 SQLite 与构建分发声明 |
| xUnit / 测试 SDK | 见测试项目 PackageReference | 仅开发与测试使用，以 NuGet 包随附许可为准 |
| libmpv Windows x64 | shinchiro/mpv-winbuild-cmake，20261021，mpv e471496c56 | 第三方预编译，包含 FFmpeg 等组件；按该构建的实际启用项审查 GPL/LGPL 及源码提供义务，不能将其假定为 MIT |
| FFmpeg 提帧与测试工具 | 同一发布，git 9a861e968 | 第三阶段起随便携版部署，由应用在后台生成封面；实际构建包含 GPL 组件，发布时须按实际构建完成许可证及对应源码分发审查 |
| 7zr 解压工具 | 7-zip.org 官方下载 | 仅用于本地依赖解压，保留来源，不随应用发布 |
| Inno Setup | 官方 6.7.3，SHA256 固定于 Setup-InstallerTool.ps1 | 编译工具仅用于开发；安装器引擎遵守随附 Inno Setup License，保留版权和网址；未购买的编译工具标识为 Non-commercial use only，不据此假定已有商业授权 |
| 安装器中文翻译 | kira-96，提交 1ff90acc4ed4aee82b1cda43253243deee3daed4 | MIT，保留译者声明及许可证原文 |

原生运行库不是 NuGet mpv 托管替代品，使用 libmpv 官方 ABI 直接调用。

## 可复现来源

- [.NET 10 官方下载](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
- [libmpv 及 FFmpeg 固定发布](https://github.com/shinchiro/mpv-winbuild-cmake/releases/tag/20261021)
- [mpv 许可说明](https://github.com/mpv-player/mpv/blob/master/Copyright)
- [FFmpeg 许可说明](https://ffmpeg.org/legal.html)
- [7-Zip 官方下载](https://www.7-zip.org/download.html)

SDK SHA512、原生归档 SHA256、DLL SHA256、解压工具 SHA256 均固定在 `scripts/Setup-Dependencies.ps1`。校验不符时脚本停止，不会静默执行新版本文件。

libmpv DLL（SHA256）：`04E4D27DC385DC18DDD93FBE69C75E635B864F3C6EC53A9122DF54C2F7842F4A`。

## 第五阶段审查结论

- `ffmpeg -version` 实测包含 `--enable-gpl --enable-version3`，`ffmpeg -L` 明确输出 GPL version 3 or later。该可执行程序不是 LGPL 构建。
- libmpv 的 `mpv-configuration` 未禁用 GPL，`ffmpeg-version` 与随包 FFmpeg 一致，包含 x264、x265、rubberband、libdvdnav 等组件；应按 GPLv3 或更新版本的组合构建审查，不能因为 libmpv API 或部分 mpv 源文件可用 LGPL 就认定此 DLL 可闭源分发。
- 固定构建工作流提交为 `1f8d2e76255abc48f0456d9be99b4cab9c6566e6`。`licenses/upstream` 保存了其 README、工作流和 mpv/FFmpeg 的构建定义，仅作为审计证据，**这些文件不包含全部组件的对应源码**。上游 cmake 中部分 Git 依赖未锁定提交；日期标签和当前 master 不能证明源码与已有二进制完全对应。
- 对外发行前，需要提供与这两个原生文件对应的全部受许可组件源码、修改、构建脚本及各组件 notices，或改用能够核查并满足分发条件的构建。当前不提供未经履行条件支持的“源码承诺书”。
- 自有代码尚未设置仓库许可证。权利人需要选择与实际链接和发行方式兼容的许可证/方案。本次没有擅自为项目添加 GPL 或 MIT 授权。
- `Export-DependencyAudit.ps1` 从发布产物 `.deps.json` 与 `.runtimeconfig.json` 反查 NuGet 实际分发版本，复制包随附许可证、notices、nuspec 和版权；保存包 SHA512、原生 SHA256 与 FFmpeg 配置。未知许可表达式或原生二进制改变时停止打包。
- 默认产物及 `release-manifest.json` 都明确标注 `externalDistributionReady=false`。本次检查没有宣称解决专利授权或全部第三方组件的完整源码合规问题。

参考原文：[mpv 固定提交 Copyright](https://github.com/mpv-player/mpv/blob/e471496c56/Copyright)、[FFmpeg 固定提交 GPLv3](https://github.com/FFmpeg/FFmpeg/blob/9a861e968/COPYING.GPLv3)、[实际原生构建工作流](https://github.com/shinchiro/mpv-winbuild-cmake/actions/runs/39518569492)、[Inno Setup 官方许可](https://jrsoftware.org/files/is/license.txt)。
