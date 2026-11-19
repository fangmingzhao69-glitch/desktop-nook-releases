# 任务栏外观引擎源码

`upstream/` 来自 TranslucentTB 2026.2，原始提交为 `d4636e439865df0a1a1419db408e055740ce5c74`。轻匣于 2026-11-19 修改这些源码，为桌面宿主提供可启停的原生任务栏工作线程，去除外观工作线程的独立入口，处理 26340 任务栏视觉树变化并接入轻匣的配置。保留 [原项目许可证](upstream/LICENSE.md)。

运行 `native/scripts/Build-TaskbarCore.ps1` 可构建四个 x64 动态库和按需组件 ZIP。使用 Visual Studio 2026 的桌面 C++ 工作负载和 Spectre 缓解库。测试机没有安装后者时可以传 `-WithoutSpectreLibraries`。该脚本从固定版本的归档恢复构建所需头文件与工具包。发行版源码归档带有 `dependencies/`，可通过 `-DependencyArchiveDirectory` 使用随附的源依赖原件。NuGet 构建工具和 Windows SDK 从 NuGet 官方源恢复。

每次打包组件后，将 ZIP 大小和 SHA-256 更新到 `TaskbarAppearancePackage.cs`，同时将每个动态库的 SHA-256 更新到 `NativeTaskbarBackend.cs`。如果重新构建使用与官方发行不同的参数或工具链，使用新的哈希值、发行地址及对应构建产物。

轻匣可以在任务栏外观启用时运行该组件。Windows Explorer 仍由 Windows 运行。组件的源码、许可证和下载的构建工具都与用户数据分开。
