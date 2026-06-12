# 主题与 UI 开发规范

> 供 AI 与开发者在修改 Athlon Agent WPF 界面时参考。  
> 目标：**颜色主题相关逻辑集中在公用类；主题切换后不得遗留旧色。**

---

## 1. 架构总览

```
调色板（唯一色值来源）
  DarkAppThemePalette / LightAppThemePalette (+ ReportHtmlLightColors 浅色常量)
        ↓
AppThemeManager.Apply(kind)
  → 更新 AppThemeManager.Current
  → AppThemeResourceBuilder 注入 Brush.* 到 Application.Resources
  → 触发 ThemeChanged 事件
        ↓
消费者
  ├── XAML：{DynamicResource Brush.*}（自动跟随）
  ├── ThemeBrushResolver：代码侧取 Brush / Color
  ├── ChatMessageToneColors：助手/用户消息前景与链接
  ├── FlowDocumentMarkdownThemeFactory：MdXaml FlowDocument 样式
  ├── FlowDocumentThemeNormalizer + FlowDocumentCodeBlockEnhancer
  ├── ThemeHtmlStyles：HTML / Mermaid 预览 CSS
  └── 各控件订阅 ThemeChanged 后重应用
```

**原则：色值只定义在调色板；UI 只引用 token，不手写 hex。**

---

## 2. 核心文件职责

| 文件 | 职责 |
|------|------|
| `Themes/AppThemeManager.cs` | 主题切换入口、`Current` 调色板、`ThemeChanged` 事件 |
| `Themes/DarkAppThemePalette.cs` | 深色完整色值 |
| `Themes/LightAppThemePalette.cs` | 浅色完整色值 |
| `Themes/ReportHtmlLightColors.cs` | 浅色 Tailwind 风格常量（仅被 Light 调色板引用） |
| `Themes/UiChromeColors.cs` | Chrome 色字段定义，对应 `Brush.*` 资源键 |
| `Themes/AppThemeResourceBuilder.cs` | `UiChromeColors` → WPF `Brush.*` 资源字典 |
| `Themes/AppThemeColor.cs` | `FromHex`、`ToFrozenBrush`、`ToHex`、`ToRgba` |
| `Themes/ChatMessageToneColors.cs` | 助手/用户消息文字色、HTML 链接色 |
| `Services/ThemeBrushResolver.cs` | 代码侧解析 `Brush.*` 的**唯一入口** |
| `Services/FlowDocumentMarkdownThemeFactory.cs` | 从当前主题构建 MdXaml `FlowDocument` Style |
| `Services/FlowDocumentThemeNormalizer.cs` | 修正 MdXaml 遗留的亮/暗色；表格/代码块归一化 |
| `Services/FlowDocumentCodeBlockEnhancer.cs` | 代码块卡片 UI；`ReapplyTheme` 刷新已有卡片 |
| `Services/ThemeHtmlStyles.cs` | WebView HTML / Mermaid 预览 CSS（从 `Current.Chrome` 派生） |
| `Themes/Controls.xaml` | 全局控件、滚动条、ComboBox 等样式 |
| `Themes/ChatStyles.xaml` | 聊天气泡、Composer、CodeBlock 按钮等 |
| `Themes/Overlays.xaml` | 菜单、Toast、预览窗口壳 |

---

## 3. 添加或修改颜色的标准流程

### 3.1 新增语义色

1. 在 `UiChromeColors.cs` 增加字段。
2. 在 `DarkAppThemePalette` / `LightAppThemePalette` 分别赋值。
3. 在 `AppThemeResourceBuilder.BuildChromeResources` 注册 `Brush.YourToken`。
4. XAML 使用 `{DynamicResource Brush.YourToken}`。
5. 代码使用 `ThemeBrushResolver.Get("Brush.YourToken")` 或 `GetColor(...)`。

**禁止：** 在 ViewModel、控件代码、XAML 中直接写 `#RRGGBB`（调试临时值除外，提交前必须清除）。

### 3.2 浅色主题品牌色

- 浅色与深色 **Accent 统一为 Indigo**（`#6366F1` 系），不得在浅色单独改用 Sky 等其他色相。
- 用户气泡、选中态、链接色应与 `DarkAppThemePalette` 的 Accent 语义一致。

### 3.3 HTML / CSS 颜色

- 通过 `AppThemeColor.ToHex` / `ToRgba` 从 `AppThemeManager.Current.Chrome` 转换。
- 在 `ThemeHtmlStyles` 集中定义，**禁止**在 `MarkdownHtmlRenderer` 等处重复硬编码深色 hex 表。

---

## 4. 主题切换：不得遗留旧色

### 4.1 自动跟随（无需额外代码）

XAML 中所有 `{DynamicResource Brush.*}` 会随 `AppThemeManager.Apply` 自动更新。

### 4.2 必须订阅 `ThemeChanged` 的组件

| 组件 | 处理方式 |
|------|----------|
| `MarkdownMessageView` | `ApplyTheme()` + `RefreshDisplayMarkdown(forceRebuild: true)` |
| `FileEditorView` | 重设 AvalonEdit 画刷 + 重新 Resolve 语法高亮 |
| `WorkspaceFileIcon` | `ApplyKind` 重取 `FileIcons` 色 |
| `HtmlPreviewWindow` | 重新 `NavigateToString` |
| `MermaidPreviewWindow` | 重新 `NavigateToString` |
| `FlowDocumentCodeBlockEnhancer` | `ReapplyTheme(document)` 刷新已有代码块卡片 |

**新增任何在代码中设置 `Foreground` / `Background` / `BorderBrush` 的控件时，必须评估是否订阅 `ThemeChanged`。**

### 4.3 FlowDocument / Markdown 特别注意

MdXaml 会在 `FlowDocument` 元素上**冻结**画刷快照。切换主题时：

1. 必须通过 `forceRebuild` 清空并重建 Markdown 文档；不能仅改 `MarkdownStyle` 而不重渲染。
2. `FlowDocumentThemeNormalizer.Normalize` 在文档生成后修正 MdXaml 遗留色。
3. `FlowDocumentCodeBlockEnhancer.ReapplyTheme` 更新已存在的代码块卡片 UI。
4. 用户/助手文字色统一走 `ChatMessageToneColors`，禁止散落 `Brushes.White`。

### 4.4 禁止的反模式

```csharp
// ❌ 在构造函数或字段初始化时缓存画刷后不再更新
private readonly Brush _text = ThemeBrushResolver.Get("Brush.Text");

// ❌ 在 FlowDocumentThemeNormalizer 外重复解析资源
Application.Current.TryFindResource("Brush.Text");

// ❌ ThemeHtmlStyles 与调色板各写一套深色常量
TextColor: "#F4F4F5"

// ✅ 始终通过公用类按当前主题读取
var text = ChatMessageToneColors.GetMessageTextBrush(assistantTone);
var html = AppThemeColor.ToHex(AppThemeManager.Current.Chrome.Text);
```

---

## 5. 聊天消息 Tone（助手 vs 用户）

| 场景 | 公用 API |
|------|----------|
| WPF 文字画刷 | `ChatMessageToneColors.GetMessageTextBrush(assistantTone)` |
| WPF / 归一化 Color | `ChatMessageToneColors.GetMessageTextColor(assistantTone)` |
| HTML 文字色 | `ChatMessageToneColors.GetHtmlTextColor(assistantTone)` |
| HTML 链接色 | `ChatMessageToneColors.GetHtmlLinkColor(assistantTone)` |

`assistantTone == true`：助手消息，使用 `Chrome.Text`。  
`assistantTone == false`：用户气泡，使用白色文字。

---

## 6. Markdown 渲染路径

| 路径 | 技术 | 主题处理 |
|------|------|----------|
| 聊天主界面 | `MarkdownMessageView` + MdXaml | `FlowDocumentMarkdownThemeFactory` + Normalizer + Enhancer |
| HTML 预览弹窗 | WebView2 + `MarkdownHtmlRenderer` | `ThemeHtmlStyles` + `ThemeChanged` 重载 |
| Mermaid 预览弹窗 | WebView2 + `MermaidPreviewHtmlBuilder` | `ThemeHtmlStyles.GetMermaidPalette()` |

聊天**不**使用 WebView 渲染 Markdown；勿向聊天路径引入 HTML 渲染器。

---

## 7. 对比度与可读性基线

开发浅色主题时需注意：

| 元素 | 要求 |
|------|------|
| Accent | 与深色主题 Indigo 一致 |
| 代码块背景/边框 | 背景与面板有区分；边框可见（如 Slate100 + Slate300） |
| 编辑器关键字 | 避免纯 `#0000FF`，使用深蓝如 `#1A56DB` |
| 滚动条 | 对比度约 ≥ 3:1；滑块宽度约 10px 可见区域 |
| 工具卡片 | 使用语义背景（Violet/Green/Rose 50 系） |
| ComboBox | `ContentPresenter` 须绑定 `TextElement.Foreground`，避免系统高亮导致文字消失 |

---

## 8. AI 开发检查清单

修改 UI 或主题相关代码后，逐项确认：

- [ ] 新色值是否已加入 `UiChromeColors` + 双主题调色板 + `AppThemeResourceBuilder`？
- [ ] XAML 是否使用 `DynamicResource` 而非 `StaticResource` 引用 `Brush.*`？
- [ ] 代码是否通过 `ThemeBrushResolver` / `ChatMessageToneColors` 取色？
- [ ] HTML/CSS 是否通过 `ThemeHtmlStyles` + `AppThemeColor.ToHex/ToRgba`？
- [ ] 是否在 `ThemeChanged` 时重建或 `ReapplyTheme` 所有代码侧设置的画刷？
- [ ] Markdown 路径是否在主题切换时 `forceRebuild`？
- [ ] 预览弹窗是否在 `ThemeChanged` 时重新加载？
- [ ] 是否清除了新增的硬编码 `#RRGGBB`？
- [ ] 浅色/深色切换后：表格、代码块、正文、按钮、滚动条是否均正确？

---

## 9. 测试建议

```bash
dotnet build src/Athlon.Agent.App/Athlon.Agent.App.csproj
dotnet test tests/Athlon.Agent.Tests --filter "FullyQualifiedName~FlowDocument"
```

手动验证：

1. 浅色 ↔ 深色切换，检查历史聊天消息（含表格、代码块）。
2. 用户气泡与助手气泡文字可读。
3. 代码块「复制」按钮在两种主题下可见。
4. 打开 HTML / Mermaid 预览后切换主题，预览内容应更新。

---

## 10. 相关文档

- UI 重构计划：`docs/superpowers/plans/2026-06-09-refactor-wpf-ui.md`
- 项目结构：`README.md`

---

*最后更新：2026-06-11 — 主题公用类收敛（ThemeBrushResolver、ChatMessageToneColors、FlowDocumentMarkdownThemeFactory）*
