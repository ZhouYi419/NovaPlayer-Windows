# 第三方依赖与来源

当前为开发验证版本，未生成对外安装包。发布前需要完成 PRD 第五阶段的许可证和对应源码分发审查。

| 依赖 | 固定版本 / 来源 | 许可与说明 |
| --- | --- | --- |
| .NET SDK / Desktop Runtime | Microsoft .NET 10.0.402 SDK（含 10.0.13 runtime） | MIT 等，以 SDK/runtime 随附 LICENSE 与第三方声明为准 |
| CommunityToolkit.Mvvm | NuGet 8.4.0 | MIT |
| Serilog | NuGet 4.3.0 | Apache-2.0 |
| Serilog.Sinks.File | NuGet 7.0.0 | Apache-2.0 |
| xUnit / 测试 SDK | 见测试项目 PackageReference | 仅开发与测试使用，以 NuGet 包随附许可为准 |
| libmpv Windows x64 | shinchiro/mpv-winbuild-cmake，20261021，mpv e471496c56 | 第三方预编译，包含 FFmpeg 等组件；按该构建的实际启用项审查 GPL/LGPL 及源码提供义务，不能将其假定为 MIT |
| FFmpeg 测试工具 | 同一发布，git 9a861e968 | 只用于生成测试视频，不由应用启动或随应用发布；实际构建包含 GPL 组件 |
| 7zr 解压工具 | 7-zip.org 官方下载 | 仅用于本地依赖解压，保留来源，不随应用发布 |

原生运行库不是 NuGet mpv 托管替代品，使用 libmpv 官方 ABI 直接调用。

## 可复现来源

- [.NET 10 官方下载](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
- [libmpv 及 FFmpeg 固定发布](https://github.com/shinchiro/mpv-winbuild-cmake/releases/tag/20261021)
- [mpv 许可说明](https://github.com/mpv-player/mpv/blob/master/Copyright)
- [FFmpeg 许可说明](https://ffmpeg.org/legal.html)
- [7-Zip 官方下载](https://www.7-zip.org/download.html)

SDK SHA512、原生归档 SHA256、DLL SHA256、解压工具 SHA256 均固定在 `scripts/Setup-Dependencies.ps1`。校验不符时脚本停止，不会静默执行新版本文件。

libmpv DLL（SHA256）：`04E4D27DC385DC18DDD93FBE69C75E635B864F3C6EC53A9122DF54C2F7842F4A`。
