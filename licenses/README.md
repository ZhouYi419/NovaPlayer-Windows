# 许可证资料

`upstream/` 保留第三方原文，不翻译或修改第三方声明。发布脚本还会从实际发布依赖的 NuGet 缓存复制 nuspec、许可证及 notices，并将清单写入安装目录 `licenses/dependency-audit.json`。

原文来源：

- `GPL-3.0.txt`：FFmpeg 固定提交 `9a861e968` 的 COPYING.GPLv3。
- `Apache-2.0.txt`：Serilog 4.3.0 的 LICENSE。
- `mpv-Copyright.txt`：mpv 固定提交 `e471496c56` 的 Copyright。
- `native-build-README.md`：实际构建工作流提交 `1f8d2e76255abc48f0456d9be99b4cab9c6566e6`。
- `native-workflow.yml`、`mpv.cmake`、`ffmpeg.cmake`：同一原生构建提交的原文，供审计使用，不是完整的对应源码。
- `ChineseSimplified-LICENSE.txt`：安装器社区中文翻译的 MIT 许可；翻译固定在 `kira-96/Inno-Setup-Chinese-Simplified-Translation` 提交 `1ff90acc4ed4aee82b1cda43253243deee3daed4`，SHA256 `BF0751FA176569C6FAA2F6E17ED2734617BEF325D5CC06EAE030FDD0258EE778`。
- `Inno-Setup-LICENSE.txt`：官方固定版本 6.7.3 随附许可，保留安装器引擎的版权信息。

仅附许可证及上游链接不能替代 GPL 对应源码。现有二进制发布未附各静态依赖的精确源码归档、修补和构建配置；本项目自有代码也尚未由权利人选择发行许可证。默认输出因此明确标为内部验收候选包，对外发行条件见 `docs/stage5-release.md`。此文件不授予或变更项目自有代码的许可证。
