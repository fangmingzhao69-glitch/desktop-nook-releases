---
name: "折页 · 晴空玻璃"
description: "现有 WPF 双栏中的连续玻璃材质与清晰前景；限定晴空玻璃主题。"
colors:
  primary-light: "#387FA8"
  primary-dark: "#91BFE1"
  coral-light: "#C7798F"
  coral-dark: "#E1A5B8"
  mint-light: "#31998B"
  mint-dark: "#83CDBD"
  canvas-light: "#F3F6FA"
  canvas-dark: "#20252E"
  surface-light: "#FBFCFE"
  surface-dark: "#2C323C"
  ink-light: "#202936"
  ink-dark: "#F0F4FA"
  secondary-ink-light: "#344150"
  secondary-ink-dark: "#D2DBE7"
  line-light: "#D1D9E4"
  line-dark: "#424B59"
  edge-light: "#BAC6D4"
  edge-dark: "#667487"
  editor-selection: "#238ACC"
typography:
  brand:
    fontFamily: "Segoe UI Variable, Microsoft YaHei UI, Segoe UI"
    fontSize: "16px"
    fontWeight: 800
  title:
    fontFamily: "Segoe UI Variable, Microsoft YaHei UI, Segoe UI"
    fontSize: "15px"
    fontWeight: 600
  body:
    fontFamily: "Segoe UI Variable, Microsoft YaHei UI, Segoe UI"
    fontSize: "12px"
  label:
    fontFamily: "Segoe UI Variable, Microsoft YaHei UI, Segoe UI"
    fontSize: "11px"
  brand-latin:
    fontFamily: "Consolas"
    fontSize: "11px"
rounded:
  panel: "14px"
  control: "8px"
  primary: "5px"
  icon-hit-area: "10px"
spacing:
  editor-inset: "4px"
  control-inline: "10px"
  content-inset: "20px"
components:
  button-primary-light:
    backgroundColor: "{colors.primary-light}"
    rounded: "{rounded.primary}"
    padding: "6px 12px"
    typography: "{typography.body}"
  button-ghost-light:
    backgroundColor: "transparent"
    textColor: "{colors.ink-light}"
    rounded: "{rounded.control}"
    padding: "6px 10px"
  pin-selected-light:
    backgroundColor: "{colors.primary-light}"
    rounded: "{rounded.control}"
    width: "88px"
    height: "34px"
  category-tab:
    rounded: "{rounded.control}"
  icon-item:
    backgroundColor: "transparent"
    rounded: "{rounded.icon-hit-area}"
    height: "88px"
    padding: "4px"
  search-light:
    backgroundColor: "{colors.surface-light}"
    textColor: "{colors.ink-light}"
    rounded: "{rounded.control}"
    padding: "7px 9px"
  reading-panel-light:
    backgroundColor: "rgba(243, 246, 250, 0.372549)"
  note-editor:
    backgroundColor: "transparent"
    padding: "{spacing.editor-inset}"
    typography: "{typography.body}"
---

# Design System: 折页 · 晴空玻璃

## Overview

**Creative North Star: "连续玻璃，清晰双页"**

本文件记录已经实现的独立「晴空玻璃」主题，仅约束这一主题的材质与前景组件。左侧收纳、右侧便签、顶栏、分类、统计及格式工具、底栏沿用产品结构，操作与数据语义由现有程序定义。产品事实参见 [项目交接](../../项目交接.md) 与 [界面与交互规范](../../界面与交互规范.md)，方向依据见 [方向契约](方向.md)。

中性浅灰与烟灰使后方背景融为一片，细高光提示玻璃边界，天蓝色承担操作与选中反馈。原色图标和名称直接放在玻璃上；右侧叠加稳定阅读面。社区参考的连续材质、克制边缘与 Windows 原生图标优点被用于这一主题，没有引入第三方贴图。

**Key Characteristics:**

- 连续背景柔化与清晰前景分层。
- 原色图标无独立底板，便签保留阅读底色。
- 保留现有双栏、分类与编辑操作。
- 明暗两套语义色，高对比度及采样不可用时回退。

证据为 `FoldTheme.cs` 的 glass 分支、`PanelVisualStyle.cs`、`MainWindow.xaml`、`App.xaml`、`NoteTextEditor.cs` 及背景捕获实现。核对截图为根目录 `.impeccable/review/windows-light.png` 与 `windows-dark.png`。源码中 WPF Color、画刷与字体为实现真源；frontmatter 是本次提取记录，颜色变化应从源码同步。尺寸以 WPF DIP 为准，frontmatter 的 px 及 sidecar HTML/CSS 仅作 1 DIP ≈ 1 CSS px 的预览映射，不是生产 WPF。

## Colors

冷中性的玻璃底色承载原色内容，操作色与完成色各司其职。frontmatter 成对记录浅色与深色，不从通用 `DesignTokens.xaml` 的启动默认值推断 glass 主题。sidecar 的八阶 OKLCH tonalRamp 是面板展示所需的合成预览，不是新增生产色阶；canonical 与 frontmatter 保持源码颜色。

### Primary

- **晴空蓝**（primary-light / primary-dark）：主要操作、选中图钉及强调入口；悬停与按下色由 `ColorsFor` 向前景色混合生成。
- **选区蓝**（editor-selection）：仅便签富文本选区，选区覆盖透明度由编辑器控制。

### Secondary

- **柔珊瑚**（coral-light / coral-dark）：新便签入口，避免与收纳主操作混淆。
- **薄荷绿**（mint-light / mint-dark）：待办完成状态，不用于玻璃底色。

### Neutral

- **浅灰 / 烟灰基底**（canvas-light / canvas-dark）：连续背景的调色层及不可采样时的实色回退。
- **阅读白 / 深灰表面**（surface-light / surface-dark）：输入与便签的局部承载面。
- **正文墨 / 说明墨**（ink-* / secondary-ink-*）：保持前景可辨；glass 的 TextSecondaryBrush 与 TextFaintBrush 使用相同说明墨。
- **分隔线 / 外缘**（line-* / edge-*）：组织控件，玻璃激活时主面板外缘另用渐变高光。

**The Foreground Clarity Rule.** 背景单独柔化和调色；不降低图标、文字、输入控件整个前景层的透明度。

## Typography

**Body Font:** Segoe UI Variable，回退 Microsoft YaHei UI、Segoe UI。  
**Label/Mono Font:** 品牌英文 ZHEYE 使用 Consolas。

字体延续 Windows 界面语感，正文和工具保持紧凑。没有新增展示字体，也没有独立的海报式 display 层级。

### Hierarchy

- **Brand**：窗首中文品牌，ExtraBold。
- **Title**：分栏标题，SemiBold。
- **Body**：图标名称、正文编辑器及常用操作。
- **Label**：分类统计与底栏状态。
- **Brand Latin**：品牌英文辅文，不扩展为全局等宽字体。

字号见 frontmatter。行高、字距没有确认的统一令牌，交由 WPF 文本排版，不补造比例刻度。

## Layout

主面板设计画布为 1080 × 620 DIP，标题区高 54 DIP。主体初始左右列为 61:39，中间 5 DIP 拖动分隔线；用户已保存分栏比例可覆盖初始值。主体行高为 60、42、剩余空间及 36 DIP。左侧图标模板使用五列 UniformGrid。以上描述现有主面板，不推广为其他窗口通用网格或响应断点。

收纳图标实际绘制为 48 × 48 DIP，外部透明容器为 54 × 54 DIP；按钮命中区域高 88 DIP，图标名称支持省略。背景大小变化由捕获层更新，前景保持布局取整及原生 DPI 取图。页面空间与间距取自已有控件，不建立未经提取的全局八点刻度。

**The Existing Operations Rule.** 材质更新保持双栏和既有操作路径，不更改分类标识、排序索引、收纳位置、便签存储或取消勾选偏好。

## Elevation & Depth

深度由后方真实背景、连续调色、稳定阅读面和边缘光共同形成。MainWindow 的 Gaussian BlurEffect 半径为 22 DIP，仅作用于背景图像。背景调色层使用 CanvasBrush，浅色 alpha 为 185/255、深色为 190/255；GlassSheen 的透明度为 0.7。主面板外缘沿渐变依次使用白色 alpha 180、45、黑色 alpha 42、白色 alpha 85，偏移为 0、0.4、0.78、1。

右侧阅读面叠加 CanvasBrush，浅色 alpha 为 95/255、深色为 72/255。便签表面为 SurfaceBrush alpha 30/255，边框为正文色 alpha 24/255，排序元数据面为 SurfaceBrush alpha 25/255；卡片侧色条厚度归零。不要将这些 alpha 当成整体前景 opacity。

背景捕获仅在 x64、单显示器且单张原始采样帧不超过 8 MiB 时运行；该预算不是进程总内存保证。采样帧只驻留内存，不写盘、不上传；收起、隐藏、最小化或切换出该主题时停止采样、隐藏采集窗并释放背景像素引用。0.21.25 起保留一个原生实例复用，避免反复创建/销毁的原生提交累积；真正关闭时才注销回调、销毁窗口和运行时。常规更新间隔为 200 ms，静止后为 800 ms。失败或高对比度时保留可读实色表面，不承诺未经实测的混合 DPI 多屏。

上下文菜单已有柔和投影：主菜单 WPF BlurRadius 14、ShadowDepth 3、Opacity 0.13、颜色 #233B6A；这是独立菜单层的既有样式，不作为主玻璃面板的全局阴影令牌。

**The Continuous Material Rule.** 一片后方背景贯穿面板；图标和名称不增加独立底板，便签允许叠加稳定阅读底色。

## Shapes

面板、普通控件、主操作和图标命中区的圆角见 frontmatter。高光内缘为 13 DIP 圆角并内缩 1 DIP。输入框有 1 DIP 描边，键盘焦点改为 AccentInkBrush。选中分类以底部短线提示，不因材质更新重做导航形状。透明的图标容器保留布局与命中区域，透明不表示删除交互边界。

## Components

以下八项在 sidecar 中各有自包含 HTML/CSS **PREVIEW equivalent**。仅展示浅色近似状态；WPF 模板、资源与原生 Shell 图标仍是运行实现，浏览器合成不等价于 Windows 背景捕获。

### Primary button

克制的实色操作入口。沿用 PrimaryButton，小圆角、SemiBold、最小高 34 DIP。使用 OnPrimaryBrush 计算文字色；焦点有 2 DIP 描边，禁用时切换 DisabledBrush 与 DisabledInkBrush。悬停/按下由现有 UiMotion 处理，不在此添加新动效时长。

### Ghost button

低优先级操作。默认透明，使用既有普通按钮模板；悬停显示 SelectionHoverBrush，键盘焦点显示强调色边框，按下 opacity 0.78，禁用 opacity 0.4。

### Pin button

顶栏常驻文字「未钉住 / 已钉住」，尺寸见 frontmatter。选中时实色背景配实心图钉，悬停保持选中反馈；键盘焦点使用 2 DIP 正文色边框。该模板自行处理状态，禁用通用 UiMotion 的背景接管。

### Category navigation

最小高 32 DIP。选中时出现 16 × 2 DIP 的强调色底线，圆角 1 DIP；键盘焦点强调边框。具体分类数据及滚动行为保持原实现。

### Launcher icon

48 DIP 原色 Shell 图标与名称组成透明项目；悬停才显示 SelectionHoverBrush 和细线，焦点强调边框。sidecar 用内联矢量示意图标，不能替代实际 Shell 图标资源。

### Search field

稳定表面承载搜索文字。圆角、内边距见 frontmatter，1 DIP OutlineBrush 描边；焦点切换 AccentInkBrush，禁用 opacity 0.45。与便签富文本编辑区区分。

### Reading panel

右栏整片稳定阅读底色，与连续背景叠加。该项是无交互容器，因此没有虚构 hover / active；阅读面不代替记录卡片和真实编辑器。

### Note editor

透明的 RichTextBox，12 DIP 正文，4 DIP 内边距，零边框、最小高 26 DIP。正文选区使用 EditorSelectionBrush，SelectionOpacity 为 0.2；加粗、删除线和背景标注由真实选区及既有工具控制，归档时只读。标注文字根据背景亮度由 FoldTheme.LabelOn 选择深色 #101422 或白色，并在主题刷新后重施；不把标注文字固定为黑色或正文色。sidecar 只示意可编辑文字和选区，不实现保存或格式业务。

## Do's and Don'ts

### Do:

- **Do** 保持背景柔化与前景图标、文字分层。
- **Do** 使用当前主题语义画刷，并同时核对浅色与深色。
- **Do** 沿用真实 Shell 图标、现有双栏及编辑操作。
- **Do** 在采样不支持或停止时保留可读的实色回退。
- **Do** 将 sidecar HTML/CSS 仅作为 PREVIEW equivalent，生产修改回到 WPF 源码。

### Don't:

- **Don't** 给收纳图标或名称重新添加独立底板。
- **Don't** 将背景 alpha 应用于整块前景内容。
- **Don't** 保存或上传运行时捕获帧，或把 QA 合成背景登记为产品壁纸。
- **Don't** 把 8 MiB 原始帧预算描述为进程总内存上限。
- **Don't** 将这一材质范围文档推广为整款应用改版，或引入新的发货栅格素材。

