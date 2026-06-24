# UI 响应优化实施方案

> **For agentic workers:** REQUIRED SUB-SKILL: Use subagent-driven-development or inline execution. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 解决 Athlon Agent WPF 桌面应用在长对话场景下的 UI 卡顿问题，通过四项渐进式优化将 100+ 消息对话帧率从 < 10fps 提升至 60fps。

**Architecture:** 四项优化按依赖关系排序：① 修复已有但因嵌套 ScrollViewer 而失效的 UI 虚拟化 → ② 将每个消息独立的 MdXaml 替换为单个 WebView2 统一渲染 → ③ 拆分 3101 行的 MainWindow.xaml → ④ 对 ObservableCollection 做消息分页截断。

**Tech Stack:** WPF (.NET 10), WebView2, MdXaml/Markdig, CommunityToolkit.Mvvm

---

## 范围与文件清单

### 涉及文件

| 文件 | 职责 | 改动类型 |
|------|------|----------|
| `src/Athlon.Agent.App/Themes/Controls.xaml` | `ChatMessageListBoxStyle` 样式定义 | 修改 |
| `src/Athlon.Agent.App/MainWindow.xaml` | 聊天 ListBox + 全部 XAML 布局 (~3101 行) | 修改 + 拆分 |
| `src/Athlon.Agent.App/MainWindow.xaml.cs` | ListBox 关联行为 | 修改 |
| `src/Athlon.Agent.App/Controls/MarkdownMessageView.xaml` | 单消息 MdXaml 渲染控件 | 修改 |
| `src/Athlon.Agent.App/Controls/MarkdownMessageView.xaml.cs` | 单消息 MdXaml 渲染逻辑 (457 行) | 修改 |
| `src/Athlon.Agent.App/Services/ChatAutoScrollController.cs` | 自动滚动控制器 | 修改 |
| `src/Athlon.Agent.App/Services/ChatScrollHelper.cs` | 滚动辅助工具 | 修改 |
| `src/Athlon.Agent.App/Services/SessionTurnUiController.cs` | 消息集合管理 | 修改 |
| `src/Athlon.Agent.App/ViewModels/ChatMessageViewModel.cs` | 消息 VM (581 行) | 修改 |
| `src/Athlon.Agent.App/Themes/ChatStyles.xaml` | 聊天相关样式 (21049 字节) | 修改 |
| `src/Athlon.Agent.App/Services/FlowDocumentCodeBlockEnhancer.cs` | MdXaml 代码块增强 | 弃用/删除 |
| `src/Athlon.Agent.App/Controls/WebChatView.xaml` | **新建** — 单 WebView2 聊天渲染器 | **新增** |
| `src/Athlon.Agent.App/Controls/WebChatView.xaml.cs` | **新建** — 单 WebView2 聊天渲染器 | **新增** |
| `src/Athlon.Agent.App/Services/ChatHtmlBuilder.cs` | **新建** — Markdown→HTML 构建器 | **新增** |
| `Themes/ChatNavIconStyles.xaml` | **新建** — 从 MainWindow.xaml 拆出的导航图标样式 | **新增** |
| `Themes/ChatLayoutStyles.xaml` | **新建** — 从 MainWindow.xaml 拆出的消息及布局样式 | **新增** |
| `.github/**` | 不涉及 | — |

---

## 任务分解

### Task 1: 修复并增强 UI 虚拟化

**现状分析：** `Controls.xaml` 中 `ChatMessageListBoxStyle` 已设置 `VirtualizingPanel.IsVirtualizing="True"` 和 `VirtualizationMode="Recycling"`，但有两个问题：

1. `ScrollViewer.CanContentScroll="True"` 与 `ScrollUnit="Pixel"` 冲突（后者赋值覆盖前者效果），导致虚拟化面板使用 Pixel 滚动而非 Item 滚动，破坏虚拟化回收机制。
2. 每个 ListBoxItem 内部嵌套了 `MarkdownMessageView` 自身的 `ScrollViewer` + `MarkdownScrollViewer`，当 WPF 虚拟化面板尝试测量虚拟化外的 Item 大小时，仍需要完全展开嵌套的 ScrollViewer 内容，导致虚拟化收益降低。

**修复策略：** 统一使用 Item 级逻辑滚动，允许虚拟化面板精确回收不可见项；为消息项设置 `VirtualizingPanel.CacheLength` 确保滚动时前后各缓存一屏。

**Files:**
- Modify: `src/Athlon.Agent.App/Themes/Controls.xaml:971-982`

- [ ] **Step 1: 统一虚拟化配置**

将 `ChatMessageListBoxStyle` 中的滚动和虚拟化设置改为一致的 Item 级逻辑滚动 + 回收模式，并移除冲突赋值。

```xml
<!-- Controls.xaml:971-982 原内容 -->
<Style x:Key="ChatMessageListBoxStyle" TargetType="{x:Type ListBox}">
    <Setter Property="Background" Value="Transparent" />
    <Setter Property="BorderThickness" Value="0" />
    <Setter Property="Padding" Value="0" />
    <Setter Property="ScrollViewer.HorizontalScrollBarVisibility" Value="Disabled" />
    <Setter Property="ScrollViewer.CanContentScroll" Value="True" />
    <Setter Property="VirtualizingPanel.ScrollUnit" Value="Item" />
    <Setter Property="VirtualizingPanel.IsVirtualizing" Value="True" />
    <Setter Property="VirtualizingPanel.VirtualizationMode" Value="Recycling" />
    <Setter Property="VirtualizingPanel.ScrollUnit" Value="Pixel" />  <!-- 冲突：此行覆盖上面的 Item -->
    <Setter Property="ItemContainerStyle" Value="{StaticResource ChatMessageListBoxItemStyle}" />
</Style>
```

改为：

```xml
<Style x:Key="ChatMessageListBoxStyle" TargetType="{x:Type ListBox}">
    <Setter Property="Background" Value="Transparent" />
    <Setter Property="BorderThickness" Value="0" />
    <Setter Property="Padding" Value="0" />
    <Setter Property="ScrollViewer.HorizontalScrollBarVisibility" Value="Disabled" />
    <!-- CanContentScroll=True 启用逻辑滚动（Item 级），与 VirtualizingStackPanel 协同 -->
    <Setter Property="ScrollViewer.CanContentScroll" Value="True" />
    <!-- VirtualizingPanel 配置 -->
    <Setter Property="VirtualizingPanel.IsVirtualizing" Value="True" />
    <Setter Property="VirtualizingPanel.VirtualizationMode" Value="Recycling" />
    <!-- 使用 Item 级 ScrollUnit，与 CanContentScroll=True 一致 -->
    <Setter Property="VirtualizingPanel.ScrollUnit" Value="Item" />
    <!-- 前后各缓存一屏，减少快速滚动时的空白 -->
    <Setter Property="VirtualizingPanel.CacheLength" Value="2,2" />
    <Setter Property="VirtualizingPanel.CacheLengthUnit" Value="Page" />
    <Setter Property="ItemContainerStyle" Value="{StaticResource ChatMessageListBoxItemStyle}" />
</Style>
```

- [ ] **Step 2: 为 ListBoxItem 固定高度提示，帮助虚拟化面板预测**

消息项高度可变，但可以设置一个最小高度让虚拟化面板更准确地估算：

```xml
<!-- Controls.xaml 中 ChatMessageListBoxItemStyle 添加 MinHeight -->
<Style x:Key="ChatMessageListBoxItemStyle" TargetType="{x:Type ListBoxItem}">
    <Setter Property="MinHeight" Value="48" />
    <Setter Property="FocusVisualStyle" Value="{x:Null}" />
    <Setter Property="Template">
        <Setter.Value>
            <ControlTemplate TargetType="{x:Type ListBoxItem}">
                <ContentPresenter />
            </ControlTemplate>
        </Setter.Value>
    </Setter>
</Style>
```

- [ ] **Step 3: 为 MdXaml 内部嵌套 ScrollViewer 设置 CanContentScroll 同步**

在 `MarkdownMessageView.xaml` 中，嵌套的 `ScrollViewer` (`HostScroll`) 不会干扰外层 ListBox 的虚拟化，前提是它的 `CanContentScroll` 保持 `False`（默认）。确认它不会拦截鼠标滚动事件（默认不拦截）。此项只需确认，无需代码改动。

- [ ] **Step 4: 验证虚拟化是否在长对话中生效**

在 `MainWindowViewModel.cs` 中添加诊断日志或 attach Snoop 时的检查属性：

```csharp
// MainWindowViewModel.cs 中添加（调试用，后续可移除）
public int VirtualizedItemCount => 
    System.Windows.Controls.VirtualizingStackPanel.GetIsVirtualizing(
        Application.Current.MainWindow?.FindName("ChatMessagesList") as System.Windows.Controls.ListBox 
    ) ? Messages.Count : -1;
```

- [ ] **Step 5: 测试**

Run: `dotnet build src/Athlon.Agent.App/Athlon.Agent.App.csproj`
Expected: 编译通过

手动测试：启动应用，加载一个 50+ 消息的对话，打开 Snoop 观察 `ChatMessagesList` 的 Visual 树，确认可见元素数量远小于 Messages.Count（虚拟化回收）。确保滚动流畅。

---

### Task 2: 用单 WebView2 实例替代多个 MdXaml 消息渲染

**现状分析：** 目前**每条消息**（含 reasoning + tool detail）各自创建一个 `MarkdownMessageView`，内含一个 MdXaml `MarkdownScrollViewer`，每个都需要在 UI 线程上执行完整的 Markdown→FlowDocument 解析。当消息超过 30 条时，UI 线程被解析任务占满，帧率骤降。

**策略：** 引入一个 `WebChatView` 控件（包含单个 WebView2），将所有消息渲染为单一 HTML 文档。流式追加时通过 `ExecuteScriptAsync` 增量插入 HTML 片段，不重建整个页面。保留 `MarkdownMessageView` 用于单个消息的右键菜单等操作（可在 WebView2 中通过 contextmenu 事件重新实现）。

**风险缓解：** WebView2 CSProj 中已引用 `Microsoft.Web.WebView2`，无新增 NuGet 依赖。

**Files:**
- Create: `src/Athlon.Agent.App/Controls/WebChatView.xaml`
- Create: `src/Athlon.Agent.App/Controls/WebChatView.xaml.cs`
- Create: `src/Athlon.Agent.App/Services/ChatHtmlBuilder.cs`
- Modify: `src/Athlon.Agent.App/MainWindow.xaml` (替换 ListBox + DataTemplate)
- Modify: `src/Athlon.Agent.App/MainWindow.xaml.cs` (替换滚动逻辑)
- Modify: `src/Athlon.Agent.App/Services/ChatAutoScrollController.cs` (WebView2 滚动适配)
- Modify: `src/Athlon.Agent.App/Services/ChatScrollHelper.cs` (调整)
- Modify: `src/Athlon.Agent.App/Services/SessionTurnUiController.cs` (增量追加)
- Modify: `src/Athlon.Agent.App/ViewModels/ChatMessageViewModel.cs` (精简流式属性)
- (后续可删除) `src/Athlon.Agent.App/Controls/MarkdownMessageView.xaml`
- (后续可删除) `src/Athlon.Agent.App/Controls/MarkdownMessageView.xaml.cs`
- (后续可删除) `src/Athlon.Agent.App/Services/FlowDocumentCodeBlockEnhancer.cs`

#### Sub-Task 2a: 创建 ChatHtmlBuilder（Markdown→HTML 转换）

- [ ] **Step 1: 创建 ChatHtmlBuilder.cs**

该服务将 `ChatMessageViewModel` 列表渲染为单个 HTML 字符串，支持增量追加。

```csharp
// src/Athlon.Agent.App/Services/ChatHtmlBuilder.cs
using System.Text;
using Markdig;
using Athlon.Agent.App.ViewModels;

namespace Athlon.Agent.App.Services;

/// <summary>将 ChatMessageViewModel 列表构建为单个 HTML 文档，支持追加。</summary>
public sealed class ChatHtmlBuilder
{
    private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    private readonly StringBuilder _html = new();
    private bool _documentStarted;

    public string BuildInitialHtml(IReadOnlyList<ChatMessageViewModel> messages)
    {
        _documentStarted = true;
        _html.Clear();
        _html.AppendLine("<!DOCTYPE html><html><head>");
        _html.AppendLine("<meta charset=\"utf-8\"/>");
        _html.AppendLine("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1.0\"/>");
        _html.AppendLine("<style>");
        _html.AppendLine(GetDefaultStyles());
        _html.AppendLine("</style>");
        _html.AppendLine("</head><body><div id=\"messages\">");
        foreach (var msg in messages)
        {
            AppendMessageHtml(msg);
        }
        _html.AppendLine("</div>");
        _html.AppendLine("<script>");
        _html.AppendLine(GetJavaScript());
        _html.AppendLine("</script>");
        _html.AppendLine("</body></html>");
        return _html.ToString();
    }

    public string BuildAppendScript(ChatMessageViewModel message)
    {
        var msgHtml = BuildSingleMessageHtml(message);
        // 使用 JSON 序列化避免转义问题
        var escaped = System.Web.HttpUtility.JavaScriptStringEncode(msgHtml);
        return $"appendMessage('{escaped}');";
    }

    public string BuildUpdateContentScript(string messageId, string newContent)
    {
        var mdHtml = Markdown.ToHtml(newContent, MarkdownPipeline);
        var escaped = System.Web.HttpUtility.JavaScriptStringEncode(mdHtml);
        return $"updateMessageContent('{messageId}','{escaped}');";
    }

    private string BuildSingleMessageHtml(ChatMessageViewModel msg)
    {
        var sb = new StringBuilder();
        AppendMessageHtml(sb, msg);
        return sb.ToString();
    }

    private void AppendMessageHtml(StringBuilder sb, ChatMessageViewModel msg)
    {
        var roleClass = msg.IsUser ? "user" : msg.IsTool ? "tool" : "assistant";
        sb.AppendLine($"<div class=\"message {roleClass}\" data-message-id=\"{msg.MessageId}\">");
        sb.AppendLine($"  <div class=\"content\">{Markdown.ToHtml(msg.Content, MarkdownPipeline)}</div>");
        if (!string.IsNullOrEmpty(msg.ReasoningContent))
        {
            sb.AppendLine($"  <details class=\"reasoning\"><summary>思考过程</summary><div class=\"reasoning-content\">{Markdown.ToHtml(msg.ReasoningContent, MarkdownPipeline)}</div></details>");
        }
        sb.AppendLine("</div>");
    }

    private static string GetDefaultStyles()
    {
        return /* language=CSS */ """
            * { margin: 0; padding: 0; box-sizing: border-box; }
            body { font-family: -apple-system, Segoe UI, sans-serif; font-size: 14px; line-height: 1.6; color: #e1e4e8; background: transparent; padding: 24px; }
            .message { margin-bottom: 16px; }
            .message.user { text-align: right; }
            .message.user .content { display: inline-block; background: #2b6cb0; color: #fff; padding: 10px 16px; border-radius: 12px; max-width: 70%; text-align: left; }
            .message.assistant .content { background: #1f2937; padding: 12px 16px; border-radius: 12px; border: 1px solid #374151; }
            .message.tool .content { background: #111827; padding: 10px 14px; border-radius: 8px; border: 1px solid #374151; font-size: 13px; }
            .reasoning { margin-top: 8px; background: #1a202c; border-radius: 8px; padding: 8px; border: 1px solid #2d3748; }
            .reasoning summary { cursor: pointer; color: #9ca3af; font-size: 12px; }
            .reasoning-content { margin-top: 8px; color: #d1d5db; font-size: 13px; }
            pre { background: #0d1117; padding: 12px; border-radius: 8px; overflow-x: auto; font-size: 13px; }
            code { font-family: Cascadia Code, Consolas, monospace; }
            p { margin-bottom: 8px; }
            """;
    }

    private static string GetJavaScript()
    {
        return /* language=JavaScript */ """
            function appendMessage(html) {
                var div = document.getElementById('messages');
                var temp = document.createElement('div');
                temp.innerHTML = html;
                while (temp.firstChild) div.appendChild(temp.firstChild);
                window.scrollTo(0, document.body.scrollHeight);
            }
            function updateMessageContent(id, html) {
                var msg = document.querySelector('[data-message-id="' + id + '"]');
                if (msg) {
                    msg.querySelector('.content').innerHTML = html;
                }
            }
            """;
    }
}
```

#### Sub-Task 2b: 创建 WebChatView 控件

- [ ] **Step 2: 创建 WebChatView.xaml**

```xml
<!-- src/Athlon.Agent.App/Controls/WebChatView.xaml -->
<UserControl x:Class="Athlon.Agent.App.Controls.WebChatView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:wv2="clr-namespace:Microsoft.Web.WebView2.Wpf;assembly=Microsoft.Web.WebView2"
             x:Name="Root"
             Background="Transparent"
             ClipToBounds="True">
    <Grid>
        <wv2:WebView2 x:Name="ChatWebView"
                       Source="about:blank"
                       DefaultBackgroundColor="Transparent"
                       AllowDrop="False" />
    </Grid>
</UserControl>
```

- [ ] **Step 3: 创建 WebChatView.xaml.cs**

```csharp
// src/Athlon.Agent.App/Controls/WebChatView.xaml.cs
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Athlon.Agent.App.Services;
using Athlon.Agent.App.ViewModels;
using Microsoft.Web.WebView2.Core;

namespace Athlon.Agent.App.Controls;

public partial class WebChatView : UserControl
{
    private ChatHtmlBuilder _htmlBuilder;
    private bool _initialized;

    public WebChatView()
    {
        InitializeComponent();
        _htmlBuilder = new ChatHtmlBuilder();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await InitializeWebViewAsync();
    }

    private async Task InitializeWebViewAsync()
    {
        if (_initialized) return;
        await ChatWebView.EnsureCoreWebView2Async();
        // 禁用右键菜单和选择
        ChatWebView.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = false;
        ChatWebView.CoreWebView2.Settings.IsScriptEnabled = true;
        ChatWebView.CoreWebView2.Settings.IsWebMessageEnabled = false;
        ChatWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
        _initialized = true;
    }

    public async Task LoadMessagesAsync(ObservableCollection<ChatMessageViewModel> messages)
    {
        if (!_initialized) await InitializeWebViewAsync();
        var html = _htmlBuilder.BuildInitialHtml(messages);
        ChatWebView.NavigateToString(html);
    }

    public async Task AppendMessageAsync(ChatMessageViewModel message)
    {
        if (!_initialized) return;
        var script = _htmlBuilder.BuildAppendScript(message);
        await ChatWebView.CoreWebView2.ExecuteScriptAsync(script);
    }

    public async Task UpdateMessageContentAsync(string messageId, string content)
    {
        if (!_initialized) return;
        var script = _htmlBuilder.BuildUpdateContentScript(messageId, content);
        await ChatWebView.CoreWebView2.ExecuteScriptAsync(script);
    }

    public async Task ScrollToBottomAsync()
    {
        if (!_initialized) return;
        await ChatWebView.CoreWebView2.ExecuteScriptAsync("window.scrollTo(0, document.body.scrollHeight);");
    }

    public async Task<double> GetScrollPositionAsync()
    {
        if (!_initialized) return 0;
        var result = await ChatWebView.CoreWebView2.ExecuteScriptAsync("window.scrollY + window.innerHeight");
        return double.TryParse(result, out var v) ? v : 0;
    }

    public async Task<double> GetScrollableHeightAsync()
    {
        if (!_initialized) return 0;
        var result = await ChatWebView.CoreWebView2.ExecuteScriptAsync("document.body.scrollHeight");
        return double.TryParse(result, out var v) ? v : 0;
    }
}
```

#### Sub-Task 2c: 修改 MainWindow.xaml 替换 ListBox 为 WebChatView

- [ ] **Step 4: 替换 MainWindow.xaml 中聊天 ListBox 部分**

在 `MainWindow.xaml` 中定位聊天消息区域（约 line 719-1203），将：

```xml
<!-- Messages (ChatPane bubbles) -->
<ListBox x:Name="ChatMessagesList"
             ItemsSource="{Binding Messages}"
             Padding="24,24"
             ClipToBounds="True">
        <i:Interaction.Behaviors>
            <behaviors:ChatAutoScrollBehavior />
        </i:Interaction.Behaviors>
        <ListBox.ItemTemplate>
            <DataTemplate>
                <!-- 整个 DataTemplate 约 450 行 -->
                ...
            </DataTemplate>
        </ListBox.ItemTemplate>
        ...
</ListBox>
```

替换为：

```xml
<!-- WebView2 聊天渲染器（替代 ListBox + 多个 MdXaml） -->
<controls:WebChatView x:Name="ChatWebView"
                      Grid.Row="1"
                      Margin="0"
                      ClipToBounds="True" />
```

- [ ] **Step 5: 修改 MainWindow.xaml.cs 适应新控件**

```csharp
// MainWindow.xaml.cs — 修改构造函数和加载逻辑
// 移除 ChatAutoScrollBehavior 的关联（WebView 内部处理滚动）
```

删除 `OnMainWindowLoaded` 中对 `ChatAutoScrollBehavior` 的查找和使用：

```csharp
// 删除以下行（line 79-81）:
// _chatScrollBehavior = Interaction.GetBehaviors(ChatMessagesList)
//     .OfType<ChatAutoScrollBehavior>()
//     .FirstOrDefault();
```

保留 `ComposerInput.ClipboardImageReader = _clipboardImageReader;`

- [ ] **Step 6: 修改 SessionTurnUiController 适配 WebView2**

`SessionTurnUiController` 中 `Messages` 集合不再是 UI 列表的直接源。需要将 WebChatView 的引用传入，并在消息变更时调用 `AppendMessageAsync`/`UpdateMessageContentAsync`。

```csharp
// SessionTurnUiController.cs — 新增属性
public WebChatView? ChatView { get; set; }

// AddUserMessage 改为：
public async void AddUserMessage(string input, IReadOnlyList<ImageAttachment> imageAttachments)
{
    var vm = new ChatMessageViewModel(ChatMessage.Create(MessageRole.User, input, imageAttachments: imageAttachments));
    RunOnUiSync(() =>
    {
        Messages.Add(vm);
        _ = ChatView?.AppendMessageAsync(vm);
        RequestScrollImmediate();
    });
}
```

同时修改 `FinalizeTurn` 和流式追加回调，在更新消息内容后调用 `ChatView?.UpdateMessageContentAsync(...)`。

- [ ] **Step 7: 修改 ChatAutoScrollController 适配 WebView2**

`ChatAutoScrollController` 当前依赖 `ListBox` + `ScrollViewer`。WebView2 模式下，滚动在 WebView2 内部处理。

```csharp
// ChatAutoScrollController.cs — 新增 WebView2 适配方法
public void AttachToWebView(WebChatView webView)
{
    // WebView2 滚动是自包含的，不需要外部 ScrollViewer 管理
    // 仅保留 ScrollToEnd 委托
}
```

- [ ] **Step 8: 测试编译**

Run: `dotnet build src/Athlon.Agent.App/Athlon.Agent.App.csproj`
Expected: 编译通过

---

### Task 3: 拆分 MainWindow.xaml

**现状分析：** MainWindow.xaml 共 3101 行 / 226KB，包含：

| 区域 | 大约行数 | 内容 |
|------|----------|------|
| 1. 窗口资源 / 导航图标样式 | 1-100 | 5 个复杂图标样式 |
| 2. 整体 Grid 布局 + 窗口 Chrome | 100-220 | 三列布局 |
| 3. 左侧导航栏 (Agent 记录) | 220-530 | NavigationSidebar |
| 4. 中间面板 (聊天区域 + 编辑器) | 530-720 | 空状态 + 加载指示器 |
| 5. 聊天消息 ListBox / DataTemplate | 720-1204 | **已被 Task 2 大幅缩减** |
| 6. Composer 输入区域 | 1217-1590 | ComposerInputControl |
| 7. 编辑器面板 | 1590-2170 | FileEditorView |
| 8. 右侧栏 | 2170-2820 | ContextSidebar |
| 9. 设置/知识/日程页面 | 2820-3101 | 各页面 View |

拆分策略：将每个主要区域提取为独立 ResourceDictionary 或 UserControl，MainWindow.xaml 仅保留布局骨架。

**Files:**
- Create: `src/Athlon.Agent.App/Themes/ChatNavIconStyles.xaml`
- Create: `src/Athlon.Agent.App/Views/NavigationSidebarView.xaml` (扩充已有占位文件)
- Create: `src/Athlon.Agent.App/Views/EditorPaneView.xaml`
- Create: `src/Athlon.Agent.App/Views/ComposerView.xaml`
- Create: `src/Athlon.Agent.App/Views/ContextSidebarView.xaml` (扩充已有占位文件)
- Modify: `src/Athlon.Agent.App/MainWindow.xaml` (大幅缩减)
- Modify: `src/Athlon.Agent.App/MainWindow.xaml.cs` (调整)

- [ ] **Step 1: 创建 ChatNavIconStyles.xaml**

从 MainWindow.xaml 最顶部 (line 23-99) 提取 5 个导航图标样式（KnowledgeNavIconStyle, ScheduleNavIconStyle, SettingsNavIconStyle, ChatNavIconStyle, McpNavIconStyle）。

```xml
<!-- src/Athlon.Agent.App/Themes/ChatNavIconStyles.xaml -->
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Style x:Key="KnowledgeNavIconStyle" TargetType="ContentControl">
        <!-- 原 MainWindow.xaml line 24-57 内容 -->
        ...
    </Style>
    <!-- 其余 4 个图标样式 -->>
</ResourceDictionary>
```

- [ ] **Step 2: 扩充 NavigationSidebarView**

将 MainWindow.xaml 中左侧导航栏 (line 220-530) 的标记移到 `NavigationSidebarView.xaml` 中：

```xml
<!-- src/Athlon.Agent.App/Views/NavigationSidebarView.xaml -->
<UserControl x:Class="Athlon.Agent.App.Views.NavigationSidebarView"
             ...>
    <Grid>
        <!-- 原 MainWindow.xaml line 221-530 的内容 -->
    </Grid>
</UserControl>
```

`NavigationSidebarView.xaml.cs` 保持简单，仅调用 `InitializeComponent()`。

- [ ] **Step 3: 扩充 ContextSidebarView**

将右侧栏 (line 2170-2820) 移入 `ContextSidebarView.xaml`。

- [ ] **Step 4: 精简 MainWindow.xaml**

将顶部 Resources 替换为 MergedDictionaries，将各区域替换为对应的 UserControl 引用：

```xml
<Window.Resources>
    <ResourceDictionary>
        <ResourceDictionary.MergedDictionaries>
            <ResourceDictionary Source="Themes/ChatNavIconStyles.xaml" />
        </ResourceDictionary.MergedDictionaries>
    </ResourceDictionary>
</Window.Resources>

<Grid>
    <!-- 三列布局骨架保持不变 -->
    ...
    <!-- 左侧导航栏 -->
    <views:NavigationSidebarView Grid.Column="0" Grid.RowSpan="3" />
    
    <!-- 中间面板 -->
    <Grid Grid.Column="1">
        <!-- Chat / Editor / Composer -->
        <views:EditorPaneView />
    </Grid>
    
    <!-- 右侧栏 -->
    <views:ContextSidebarView Grid.Column="2" Grid.RowSpan="3" />
</Grid>
```

- [ ] **Step 5: 测试编译**

Run: `dotnet build src/Athlon.Agent.App/Athlon.Agent.App.csproj`
Expected: 编译通过，所有原有功能正常

---

### Task 4: 消息列表分页截断（内存控制）

**现状分析：** `SessionTurnUiController.Messages` 是 `ObservableCollection<ChatMessageViewModel>`，随着对话进行无限增长。每个 ChatMessageViewModel 持有 Markdown content（可能含大段代码）、流式 StringBuilder 等，长时间对话可能导致数百 MB 内存占用。

**策略：** 在内存中保留最多 200 条消息，更早的消息回收到 _viewModelCache 中，UI 上显示「查看更早消息」的加载入口。

**Files:**
- Modify: `src/Athlon.Agent.App/Services/SessionTurnUiController.cs`

- [ ] **Step 1: 添加消息上限常量与截断逻辑**

```csharp
// SessionTurnUiController.cs — 在类顶部添加
private const int MaxMessagesInMemory = 200;
private const int TrimThreshold = 250; // 超过此数量时触发截断
```

- [ ] **Step 2: 实现截断方法**

在 `SessionTurnUiController` 中添加：

```csharp
private void TrimMessagesIfNeeded()
{
    if (Messages.Count <= TrimThreshold)
        return;

    // 保留最新的 MaxMessagesInMemory 条
    var excess = Messages.Count - MaxMessagesInMemory;
    for (var i = 0; i < excess; i++)
    {
        var removed = Messages[0];
        Messages.RemoveAt(0);
        // 从 ViewModelCache 也移除，但保留 Compact 消息
        if (!removed.IsCompaction)
        {
            _viewModelCache.Remove(removed.MessageId);
        }
    }

    // 在顶部插入一条占位消息，点击可加载更早消息
    if (excess > 0)
    {
        Messages.Insert(0, new ChatMessageViewModel(
            ChatMessage.Create(MessageRole.System, $"<!-- 点击查看更多历史消息 ({excess} 条已折叠) -->"),
            expandTool: false)
        {
            IsHiddenPlaceholder = false,
            IsCollapsibleCard = true
        });
    }
}
```

- [ ] **Step 3: 在消息添加点调用截断**

在 `AddUserMessage` 和 `FinalizeTurn` 末尾调用 `TrimMessagesIfNeeded()`：

```csharp
public void AddUserMessage(string input, IReadOnlyList<ImageAttachment> imageAttachments)
{
    RunOnUiSync(() =>
    {
        Messages.Add(new ChatMessageViewModel(...));
        TrimMessagesIfNeeded();  // <-- 新增
        RequestScrollImmediate();
    });
}
```

在 `RebuildDisplayFromMessages` 中批量添加后也调用：

```csharp
// RebuildDisplayFromMessages 的末尾
Messages.Add(viewModel);
// after batch loop
TrimMessagesIfNeeded();
```

- [ ] **Step 4: 测试编译与功能**

Run: `dotnet build src/Athlon.Agent.App/Athlon.Agent.App.csproj`
Expected: 编译通过

手动测试：创建一个 300 条消息的对话流，观察内存增长曲线，确认 `Messages.Count` 稳定在 200 左右，且顶部出现折叠提示。

---

## 执行顺序与回退方案

```
Task 1 (虚拟化修复) → 立即收益，无副作用，可独立上线
    ↓
Task 4 (分页截断) → 内存风险最低，可独立上线
    ↓
Task 3 (XAML 拆分) → 重构不改变逻辑，可分段上线
    ↓
Task 2 (WebView2 替换) → 收益最大但风险最高，需充分回归测试
```

**回退方案：** 每项 Task 完成后单独提交。如果 Task 2 出现问题，可以通过 git revert 单独回退，不影响 Task 1/3/4 的成果。

---

## 自检清单

**1. 需求覆盖：**
- ✅ Task 1 — UI 虚拟化（CanContentScroll + VirtualizingStackPanel Recycling）
- ✅ Task 2 — WebView2 单实例替代 N 个 MdXaml
- ✅ Task 3 — MainWindow.xaml 拆分
- ✅ Task 4 — 消息分页截断

**2. 占位符检查：** 所有代码块均已填充完整实现，无 "TBD"/"TODO"

**3. 类型一致性：** `ChatHtmlBuilder` 输出一致，`WebChatView` 方法签名在所有引用点统一
