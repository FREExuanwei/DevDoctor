# 构建与本地打包

## 应用和测试

在 Windows 11 x64 上使用 .NET SDK 10.0.402 可复现本次验证的构建环境。更高的受支持补丁由 global.json 的 SDK 选择策略识别。PowerShell 7 用于仓库脚本。构建机从 NuGet.Config 配置的 NuGet.org 还原依赖，应用运行不需要 SDK。

~~~powershell
dotnet --version
dotnet restore .\DevDoctor.slnx --configfile .\NuGet.Config
dotnet build .\DevDoctor.slnx -c Release --no-restore
dotnet test .\tests\DevDoctor.Tests\DevDoctor.Tests.csproj -c Release --no-build --no-restore
~~~

可选的 scripts/dotnet.ps1 封装把 CLI 状态和 NuGet 缓存放在项目的 .tools/，优先调用项目内现有 SDK，其次调用已安装 SDK。源码目录中没有 SDK 或缓存。本次从空项目依赖缓存执行还原、构建和测试。精确软件版本见 [第三方声明](../THIRD-PARTY-NOTICES.md)。

## 安装包和便携包

本地发行脚本使用已经能运行的 .NET 10 SDK 和 Inno Setup 6.7.3。将从官方发行取得的 innosetup-6.7.3.exe 放入 .tools/installer-cache/，将同版本的便携或已提取编译器目录放入 .tools/inno-6.7.3/。官方 Inno 便携模式可由其文档查阅。编译器、其安装文件和引擎的 Authenticode 签名应为 Valid，签名者为 Pyrsys B.V.。脚本还校验原始安装文件的 SHA-256 为 9c73c3bae7ed48d44112a0f48e66742c00090bdb5bef71d9d3c056c66e97b732。 [Inno 6.7.3 固定发行](https://github.com/jrsoftware/issrc/releases/tag/is-6_7_3) · [官方校验说明](https://jrsoftware.org/isdl-verify.php)

~~~powershell
pwsh -NoProfile -File .\scripts\release.ps1 -Version 1.2.0
~~~

输出在本机 artifacts/，包括自包含 Windows x64 应用、带 portable.mode 的便携 ZIP、中文 Inno Setup 和校验清单。应用的自包含目标固定为 .NET / Windows Desktop 10.0.15。重建能复现相同版本的包结构和测试结果，编译时间、ZIP 时间戳及工具环境可能影响成品是否逐字节一致。二进制公开发行依赖 [第三方条款](../THIRD-PARTY-NOTICES.md) 和项目授权。

测试目录包含正常 xUnit、WPF 界面工具和归档升级工具。正常解决方案构建与 xUnit 无需旧版安装包。历史升级工具需要另行获得的真正归档版本和校验清单，示例文件位置由相应脚本说明。验收应使用独立数据目录；可以给进程设置 DEVDOCTOR_DATA_ROOT 指向新建本地测试目录。出现现有 DevDoctor 登记时，完整安装验证应在独立测试环境执行。

GitHub 上的 v1.2.0 提供源码与构建说明。二进制产物由开发环境在本地生成。
