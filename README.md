# 轻匣 · DesktopNook

依赖 .NET 10 Windows Desktop Runtime（Windows x64）的桌面收纳工具，当前版本 **0.12.9**。支持桌面快捷方式、文件、文件夹和回收站入口收纳，分类、拖拽排序、任务栏开关、开机自启与确认后在线更新。

[项目交接](docs/项目交接.md) · [使用说明](docs/使用说明.md) · [开发与构建](docs/开发与构建.md) · [架构与数据](docs/架构与数据.md) · [界面规范](docs/界面与交互规范.md) · [清理记录](docs/清理记录.md)

![轻匣主界面](docs/images/overview.png)

截图使用隔离演示数据，不代表用户的实际分类与收纳内容。

## 下载与更新

[最新正式版](https://github.com/fangmingzhao69-glitch/desktop-nook-releases/releases/latest) · [0.12.9 发行说明](https://github.com/fangmingzhao69-glitch/desktop-nook-releases/releases/tag/v0.12.9)

- 使用安装包可自动补装缺少的 .NET 10 桌面运行时；免安装 EXE 仍需先具备运行时。
- `DesktopNook-Setup.exe`：安装或覆盖升级，无需先卸载；缺少运行时时确认下载并安装必要组件后继续；取消或失败保留原程序。
- `DesktopNook-win-x64.exe`：含运行时的免安装版，放在固定目录运行。
- `SHA256SUMS.txt`：发行包校验值。

本机仅保留一套正式包，位于 `发布/轻匣-v0.12.9/`，不提交二进制到 Git。历史原型、旧包、候选稿、测试沙箱和构建缓存已清理，结果见[清理记录](docs/清理记录.md)。日常安装和用户数据位于项目之外，未被清理。

## 目录

```text
native/DesktopNook.App/    WPF 界面、Shell 集成、更新与图标
native/DesktopNook.Core/   收纳、分类、恢复与数据保护
native/DesktopNook.Tests/  核心回归检查
native/scripts/           图标生成、构建、安装与发布脚本
native/releases/          当前版本说明
 docs/                    当前交接、使用与开发文档、截图和验收摘要
发布/轻匣-v0.12.9/        本机正式交付包
```

## 构建

需要 Windows、.NET 10 SDK、PowerShell，安装包另外需要 Unicode NSIS 3。

```powershell
pwsh -NoProfile -File native/scripts/Build.ps1
```

开发运行必须使用独立 `--sandbox` 目录，参见[开发与构建](docs/开发与构建.md)。不要用普通启动或真实数据目录执行收纳/恢复测试。
