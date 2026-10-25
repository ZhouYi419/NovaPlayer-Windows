# libmpv x64

`scripts/Setup-Dependencies.ps1` 将固定版本 `libmpv-2.dll` 放到本目录。构建时自动复制到程序输出目录的 `native/win-x64/`。

DLL 约 121 MB，忽略 Git 跟踪，通过固定下载地址和 SHA256 校验复现。不要手动使用来源不明或不同架构的同名 DLL。
