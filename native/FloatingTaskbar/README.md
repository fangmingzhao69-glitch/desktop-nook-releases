# 折页悬浮任务栏原生组件

基于 [Atrium Desktop](https://github.com/wynter-yaxley/atrium-desktop) 的 MIT 开源核心，固定上游提交为 `9dd15690584bf45a4ce2ed265eebc3465a55ba49`。保留原作者版权及完整许可证，见 [LICENSE](LICENSE)。源文件由该提交的 native、locales、测试语言资源导入；图标替换为折页图标，不携带上游字体和 .NET 设置程序。

折页适配包括：

- 设置、日志、布局和跳转列表缓存均位于当前数据根目录的 `FloatingTaskbar/Engine`。
- 按数据根目录区分宿主窗口、互斥量和设置管道，不复用原版 Atrium 的实例和配置。
- 仅由主程序启动；持有主进程句柄，主进程退出后自动退出，隐藏系统任务栏期间复用原生异常恢复守护。
- 禁用原版更新、安装登记和自启入口。设置回调转到折页；控制中心的效率入口打开折页面板。
- 桌面小组件、台前调度和聊天内容监听默认关闭。Windows 开始菜单布局与悬浮固定列表独立保存在组件数据目录。
- 沙箱强制保留系统任务栏，不接管系统托盘，不写显卡偏好。原生测试全部使用独立临时数据。
- 修正日志线程首次初始化数据路径与进程静态析构并发导致的退出死锁。

`../scripts/Build-Dock.ps1` 使用 Visual Studio Desktop C++、Windows SDK、CMake 3.25+ 和 Ninja 构建。支持 `-CMakePath` 指定工具，`-Tests` 执行原生检查。源码暂存到短 ASCII 路径，规避 C++/WinRT 和 Ninja 的中文路径限制。

生成的 `artifacts/dock/ZheyeDock.zip` 嵌入折页主 EXE，启用时解压到当前数据根目录的组件缓存，每次启动前核验全部文件的 SHA-256。原生程序使用静态 C++ 运行库，不附带额外 .NET 运行时。

上游提交号表示源码来源；折页的适配修改已包含在此目录中，不要求运行时访问上游仓库。
