# 第三方依赖与声明

此清单适用于 DevDoctor 1.2.0。源码副本保留源文件、依赖声明、图标和上游简体中文安装器翻译。NuGet 运行时、第三方 DLL、字体和安装编译器由构建环境提供。

## 应用及发布依赖

| 组件 | 版本 | 许可证与声明 | 来源 |
| --- | --- | --- | --- |
| CommunityToolkit.Mvvm | 8.4.0 | MIT。保留 License.md 和随包第三方声明。 | [NuGet](https://www.nuget.org/packages/CommunityToolkit.Mvvm/8.4.0) |
| Microsoft.Extensions.DependencyInjection、Abstractions | 10.0.0 | MIT。保留 Microsoft 版权和两包各自第三方声明。 | [Microsoft .NET 源码](https://github.com/dotnet/runtime) |
| Microsoft.Data.Sqlite、Core | 10.0.13 | MIT。保留 Microsoft 版权与许可正文。 | [Microsoft EF Core](https://github.com/dotnet/efcore) |
| SQLitePCLRaw.bundle_e_sqlite3、core、provider.e_sqlite3、lib.e_sqlite3 | 2.1.13 | Apache-2.0，Eric Sink / SourceGear LLC。原许可随源码保存。 | [SQLitePCLRaw](https://github.com/ericsink/SQLitePCL.raw) · [NuGet](https://www.nuget.org/packages/SQLitePCLRaw.lib.e_sqlite3/2.1.13) |
| SQLite 原生库 | 3.53.3 | SQLite 代码位于公有领域。绑定与打包适用上项许可证。 | [SQLite 版权](https://www.sqlite.org/copyright.html) · [版本记录](https://www.sqlite.org/releaselog/3_53_3.html) |
| Microsoft.NETCore.App.Runtime.win-x64、Microsoft.WindowsDesktop.App.Runtime.win-x64、Microsoft.NETCore.App.Host.win-x64 | 10.0.15 | NuGet 元数据为 MIT。保留运行时、宿主版权及第三方声明，Windows 二进制例外见下文。 | [Microsoft .NET](https://github.com/dotnet/runtime) · [WPF](https://github.com/dotnet/wpf) |
| .NET SDK | 10.0.402（本次构建） | SDK 工具含 LICENSE 和 ThirdPartyNotices，由构建环境管理。 | [Microsoft .NET](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) |

构建缓存可能还原 Microsoft.AspNetCore.App.Runtime.win-x64。发布应用的运行时清单未引用它。

## 测试依赖

| 组件 | 版本 | 许可证与声明 | 来源 |
| --- | --- | --- | --- |
| Microsoft.NET.Test.Sdk、Microsoft.CodeCoverage、Microsoft.TestPlatform.ObjectModel、Microsoft.TestPlatform.TestHost | 17.14.1 | MIT。CodeCoverage 和 TestHost 带有第三方声明。 | [VSTest](https://github.com/microsoft/vstest/blob/main/LICENSE) |
| Newtonsoft.Json | 13.0.3 | MIT，James Newton-King。随包包含许可。 | [Json.NET](https://www.nuget.org/packages/Newtonsoft.Json/13.0.3) |
| xunit、xunit.assert、xunit.core、xunit.extensibility.core、xunit.extensibility.execution | 2.9.3 | Apache-2.0。保留上游声明。 | [xUnit](https://github.com/xunit/xunit/blob/main/LICENSE) |
| xunit.abstractions、xunit.analyzers、xunit.runner.visualstudio | 2.0.3、1.18.0、3.1.5 | Apache-2.0。保留上游声明。 | [xunit.abstractions](https://github.com/xunit/abstractions.xunit/blob/main/license.txt) · [xunit.analyzers](https://github.com/xunit/xunit.analyzers) · [Visual Studio runner](https://github.com/xunit/visualstudio.xunit) |

## 安装器、字体和图像

- Inno Setup 6.7.3 使用 Jordan Russell / Martijn Laan 的上游许可证。源码中的简中翻译来自固定标签 is-7_0_2，由 Zhenghan Yang (Kira) 维护，保留版权、原始翻译及许可。Inno 原文允许附条件再分发，包括保留声明、准确说明来源和区分修改版。商业支持条件见上游。 [Inno 6.7.3 许可](https://raw.githubusercontent.com/jrsoftware/issrc/is-6_7_3/license.txt) · [翻译来源](installer/languages/PROVENANCE.md) · [Inno 官方许可 FAQ](https://jrsoftware.org/inno-setup/purchase)
- Segoe UI、Microsoft YaHei UI、Consolas、Cascadia Mono 和 Segoe MDL2 Assets 是 Windows / 系统字体引用。源码使用应用界面渲染及字体回退，无独立字体文件。 [Microsoft 字体说明](https://learn.microsoft.com/en-us/typography/fonts/font-faq)
- 应用图标和装饰使用仓库内的几何图形、SVG 和 ICO 生成代码。README 截图为应用渲染的合成测试记录，已目视审查。 [图标生成脚本](installer/assets/create-icon.ps1)

## Windows 自包含二进制

Microsoft 对部分 Windows 文件列有独立许可。coreclr.dll、Microsoft.DiaSymReader.Native.amd64.dll、PresentationNative_cor3.dll、vcruntime140_cor3.dll 和 wpfgfx_cor3.dll 适用 .NET Library License。D3DCompiler_47_cor3.dll 适用 Windows SDK 再分发条款。公开提供安装包或便携包前，需要核对随应用分发的条件、版权及第三方声明，落实适用的最终用户条款，并补齐 AppHost 的许可与声明。源码副本采用依赖声明构建；其中没有这些二进制。 [Microsoft Windows 二进制许可清单](https://github.com/dotnet/core/blob/main/license-information-windows.md) · [.NET Library License](https://dotnet.microsoft.com/en-us/dotnet_library_license.htm) · [Windows SDK 许可](https://learn.microsoft.com/en-us/legal/windows-sdk/license) · [SDK 再分发清单](https://learn.microsoft.com/en-us/legal/windows-sdk/redist)

以上条款适用于第三方组件。DevDoctor 自有代码采用 MIT 许可证。 [项目许可证](LICENSE)
