# WPF UI Refactoring Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use subagent-driven development to implement this plan task-by-task.

**Goal:** Refine the Athlon Agent WPF desktop application's UI — color palette, control styles, layout polish, and premium visual consistency — following design-taste-frontend anti-slop principles.

**Architecture:** This is a .NET 8 WPF / MVVM app. The UI is defined in XAML resource dictionaries (Themes/) and inline in MainWindow.xaml (2019 lines). We refine the palette system, extract repeated patterns into styles, and polish the chat/sidebar/composer/settings UI without breaking existing functionality.

**Tech Stack:** WPF / .NET 8 / XAML / C# / CommunityToolkit.Mvvm

---

## Files to Modify

| File | Responsibility | Change Scope |
|------|---------------|-------------|
| `src/Athlon.Agent.App/Themes/DarkAppThemePalette.cs` | Dark theme color definitions | Refine palette for premium feel |
| `src/Athlon.Agent.App/Themes/LightAppThemePalette.cs` | Light theme color definitions | Mirror dark theme refinements |
| `src/Athlon.Agent.App/Themes/UiChromeColors.cs` | Color token struct | Add new tokens if needed |
| `src/Athlon.Agent.App/Themes/AppThemeResourceBuilder.cs` | Brush registration | Add new brush mappings |
| `src/Athlon.Agent.App/Themes/Controls.xaml` | Control styles (812 lines) | Polish all control templates |
| `src/Athlon.Agent.App/Themes/Overlays.xaml` | ContextMenu/Window styles | Polish overlay styles |
| `src/Athlon.Agent.App/MainWindow.xaml` | Main window layout (2019 lines) | Polish spacing, bubbles, composer, settings |
| `src/Athlon.Agent.App/Controls/FileEditorView.xaml` | Code editor tab strip | Polish tab appearance |
| `src/Athlon.Agent.App/Controls/MarkdownMessageView.xaml` | Markdown rendering wrapper | Add subtle styling |
| `src/Athlon.Agent.App/Controls/RightSidebarToggleIcon.xaml` | Sidebar toggle icon | Polish icon appearance |
| `src/Athlon.Agent.App/Licensing/LicenseActivationWindow.xaml` | License activation dialog | Polish window styling |

---

### Task 1: Refine Dark Theme Color Palette

**Files:**
- Modify: `src/Athlon.Agent.App/Themes/DarkAppThemePalette.cs` (full file)
- Modify: `src/Athlon.Agent.App/Themes/UiChromeColors.cs` (add `ComposerBorder` token)
- Modify: `src/Athlon.Agent.App/Themes/AppThemeResourceBuilder.cs` (add `Brush.ComposerBorder` mapping)

- [ ] **Step 1: Add new token to UiChromeColors.cs**

Add `ComposerBorder` color property after `Composer`:

```csharp
public required Color ComposerBorder { get; init; }
```

- [ ] **Step 2: Add brush mapping in AppThemeResourceBuilder.cs**

After `["Brush.Composer"] = Brush(c.Composer),` add:
```csharp
["Brush.ComposerBorder"] = Brush(c.ComposerBorder),
```

- [ ] **Step 3: Update LightAppThemePalette.cs**

Add the new `ComposerBorder` field:
```csharp
ComposerBorder = C(ReportHtmlLightColors.Slate200),
```

- [ ] **Step 4: Refine DarkAppThemePalette.cs with premium palette**

Replace the `CreateChrome()` method:

```csharp
private static UiChromeColors CreateChrome() => new()
{
    // Base — deep neutral with subtle warmth instead of pure gray
    AppBackground = C("#0C0C0E"),
    Chrome = C("#161618"),
    Panel = C("#202022"),
    PanelAlt = C("#262628"),
    Composer = C("#1C1C1E"),
    ComposerBorder = C("#2C2C30"),
    
    // Borders — softer than before
    Border = C("#323236"),
    
    // Text — high contrast
    Text = C("#F4F4F5"),
    SubtleText = C("#9CA3AF"),
    DisabledText = C("#6B7280"),
    DisabledBackground = C("#323236"),
    
    // Accent — more vibrant blue
    Accent = C("#3B82F6"),
    AccentHover = C("#2563EB"),
    
    // Chat bubbles
    UserBubble = C("#1E3A5F"),
    UserBubbleOpacity = 0.88,
    AssistantBubble = C("#202022"),
    
    // Semantic
    Success = C("#10B981"),
    Danger = C("#EF4444"),
    DangerHover = C("#DC2626"),
    Warning = C("#F59E0B"),
    
    // Navigation
    NavActiveBg = C("#1E3A5F"),
    NavActiveText = C("#93C5FD"),
    
    // Tool call cards — refined purple tones
    ToolThinkingBorder = C("#5B21B6"),
    ToolThinkingBg = C("#1C1A2E"),
    ToolThinkingText = C("#C4B5FD"),
    ToolSuccessBorder = C("#059669"),
    ToolSuccessBg = C("#142A22"),
    ToolSuccessText = C("#6EE7B7"),
    ToolFailureBorder = C("#DC2626"),
    ToolFailureBg = C("#2A1418"),
    ToolFailureText = C("#FDA4AF"),
    
    // Icon badges
    IconBadgeStart = C("#0284C7"),
    IconBadgeEnd = C("#7DD3FC"),
    
    // Hover states — more distinct
    HoverNeutral = C("#27272A"),
    HoverNeutralAlt = C("#2D2D31"),
    HoverActive = C("#254766"),
    HoverTool = C("#242237"),
    HoverToolPressed = C("#2C2942"),
    HoverSurface = C("#28282B"),
    HoverSurfacePressed = C("#303034"),
    
    // Selection
    SelectionActive = C("#1E3A5F"),
    SelectionInactive = C("#1F2D45"),
    SelectionBorder = C("#2F5C8E"),
    
    // Completion popup badges
    AtCompletionSkillBadgeBg = C("#1C1A2E"),
    AtCompletionSkillBadgeBorder = C("#5B21B6"),
    AtCompletionSkillBadgeText = C("#C4B5FD"),
    AtCompletionFileBadgeBg = C("#262628"),
    AtCompletionFileBadgeBorder = C("#323236"),
    AtCompletionFileBadgeText = C("#9CA3AF"),
    
    // Code blocks
    CodeBackground = C("#18181B"),
    CodeBackgroundAlt = C("#202023"),
    CodeForeground = C("#F1F5F9"),
    CodeBorder = C("#1E293B"),
    CodeHighlightBlue = C("#60A5FA"),
    TableBorder = C("#404048"),
    
    // Menus
    MenuBackground = C("#202022"),
    MenuHover = C("#323236"),
    
    // Toast
    ToastBackground = C("#0F172A"),
    ToastBorder = C("#2D3A5A"),
    
    // Preview
    PreviewContentBackground = Colors.White,
    
    // Scroll
    ScrollThumb = C("#8888A0"),
    ScrollThumbOpacity = 0.50,
    
    // Chat gradient — warmer deep tone
    ChatBackgroundTop = C("#111114"),
    ChatBackgroundBottom = C("#0C0C0E"),
    
    // Icon badge gradient
    IconBadgeGradientStart = C("#DBEAFE"),
    IconBadgeGradientEnd = C("#3B82F6"),
};
```

- [ ] **Step 5: Also update LightAppThemePalette.cs with ComposerBorder**

Find the line `Composer = C(ReportHtmlLightColors.White),` in the light palette. After it, add:
```csharp
ComposerBorder = C(ReportHtmlLightColors.Slate200),
```

- [ ] **Step 6: Verify build**

Run: `dotnet build src/Athlon.Agent.App/Athlon.Agent.App.csproj`
Expected: Build succeeds with no errors.

---

### Task 2: Polish Control Styles

**Files:**
- Modify: `src/Athlon.Agent.App/Themes/Controls.xaml` (lines 1-812)

This file defines styles for: TextBlock, Button (5 variants), CheckBox (toggle), TextBox, PasswordBox, ScrollBar parts, GridSplitter, ScrollViewer, ListBox (chat messages), TreeViewItem, StatusIndicator.

Key changes:
1. **TextBlock** — keep Segoe UI but refine default sizing
2. **Button styles** — refine corner radii and padding system
3. **GhostButtonStyle** — refine hover background
4. **NavButtonStyle** — refine active state
5. **Editor tab styles** — refine selection appearance
6. **ScrollBar** — refine thumb opacity and margin
7. **GridSplitter** — refine hover line appearance
8. **ChatMessageListBoxStyle** — ensure pixel-perfect scrolling
9. **WorkspaceTreeViewItemStyle** — refine selection/hover states
10. **SettingsCardStyle** — refine padding and shadow

- [ ] **Step 1: Refine default TextBlock style**

```xml
<Style TargetType="TextBlock">
    <Setter Property="Foreground" Value="{DynamicResource Brush.Text}" />
    <Setter Property="FontFamily" Value="Segoe UI" />
    <Setter Property="FontSize" Value="14" />
    <Setter Property="TextWrapping" Value="Wrap" />
</Style>
```

- [ ] **Step 2: Refine NavButtonStyle — better active indicator**

Replace lines 49-76:
```xml
<Style x:Key="NavButtonStyle" TargetType="Button">
    <Setter Property="Foreground" Value="{DynamicResource Brush.SubtleText}" />
    <Setter Property="Background" Value="Transparent" />
    <Setter Property="BorderThickness" Value="0" />
    <Setter Property="Padding" Value="14,10" />
    <Setter Property="HorizontalContentAlignment" Value="Left" />
    <Setter Property="Cursor" Value="Hand" />
    <Setter Property="FontSize" Value="14" />
    <Setter Property="FontWeight" Value="Medium" />
    <Setter Property="Template">
        <Setter.Value>
            <ControlTemplate TargetType="Button">
                <Border x:Name="Root"
                        Background="{TemplateBinding Background}"
                        CornerRadius="10"
                        Padding="{TemplateBinding Padding}">
                    <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"
                                      VerticalAlignment="Center" />
                </Border>
                <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True">
                        <Setter TargetName="Root" Property="Background" Value="{DynamicResource Brush.HoverNeutral}" />
                        <Setter Property="Foreground" Value="{DynamicResource Brush.Text}" />
                    </Trigger>
                </ControlTemplate.Triggers>
            </ControlTemplate>
        </Setter.Value>
    </Setter>
</Style>
```

- [ ] **Step 3: Refine GhostButtonStyle — cleaner appearance**

Replace lines 19-46:
```xml
<Style x:Key="GhostButtonStyle" TargetType="Button">
    <Setter Property="Foreground" Value="{DynamicResource Brush.SubtleText}" />
    <Setter Property="Background" Value="Transparent" />
    <Setter Property="BorderBrush" Value="{DynamicResource Brush.Border}" />
    <Setter Property="BorderThickness" Value="1" />
    <Setter Property="Padding" Value="14,7" />
    <Setter Property="Cursor" Value="Hand" />
    <Setter Property="FontSize" Value="13" />
    <Setter Property="Template">
        <Setter.Value>
            <ControlTemplate TargetType="Button">
                <Border x:Name="Root"
                        Background="{TemplateBinding Background}"
                        BorderBrush="{TemplateBinding BorderBrush}"
                        BorderThickness="{TemplateBinding BorderThickness}"
                        CornerRadius="10"
                        Padding="{TemplateBinding Padding}">
                    <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" />
                </Border>
                <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True">
                        <Setter TargetName="Root" Property="Background" Value="{DynamicResource Brush.HoverNeutral}" />
                        <Setter Property="Foreground" Value="{DynamicResource Brush.Text}" />
                    </Trigger>
                    <Trigger Property="IsPressed" Value="True">
                        <Setter TargetName="Root" Property="Background" Value="{DynamicResource Brush.HoverNeutralAlt}" />
                    </Trigger>
                </ControlTemplate.Triggers>
            </ControlTemplate>
        </Setter.Value>
    </Setter>
</Style>
```

- [ ] **Step 4: Refine SettingsCardStyle — more premium**

Replace lines 487-494:
```xml
<Style x:Key="SettingsCardStyle" TargetType="Border">
    <Setter Property="Background" Value="{DynamicResource Brush.Panel}" />
    <Setter Property="BorderBrush" Value="{DynamicResource Brush.Border}" />
    <Setter Property="BorderThickness" Value="1" />
    <Setter Property="CornerRadius" Value="14" />
    <Setter Property="Padding" Value="24" />
    <Setter Property="Margin" Value="0,0,0,20" />
</Style>
```

- [ ] **Step 5: Refine ScrollBarThumbStyle — smoother appearance**

Replace lines 531-550:
```xml
<Style x:Key="ScrollBarThumbStyle" TargetType="{x:Type Thumb}">
    <Setter Property="OverridesDefaultStyle" Value="True" />
    <Setter Property="Focusable" Value="False" />
    <Setter Property="IsTabStop" Value="False" />
    <Setter Property="Template">
        <Setter.Value>
            <ControlTemplate TargetType="{x:Type Thumb}">
                <Grid>
                    <Rectangle Fill="Transparent" />
                    <Border Background="{DynamicResource Brush.ScrollThumb}"
                            CornerRadius="6"
                            Margin="4,3"
                            HorizontalAlignment="Stretch"
                            VerticalAlignment="Stretch"
                            IsHitTestVisible="False" />
                </Grid>
            </ControlTemplate>
        </Setter.Value>
    </Setter>
</Style>
```

- [ ] **Step 6: Refine TextBox style — cleaner input**

Replace lines 496-504:
```xml
<Style TargetType="TextBox">
    <Setter Property="Foreground" Value="{DynamicResource Brush.Text}" />
    <Setter Property="CaretBrush" Value="{DynamicResource Brush.Text}" />
    <Setter Property="Background" Value="{DynamicResource Brush.Chrome}" />
    <Setter Property="BorderBrush" Value="{DynamicResource Brush.Border}" />
    <Setter Property="BorderThickness" Value="1" />
    <Setter Property="Padding" Value="14,10" />
    <Setter Property="FontSize" Value="14" />
    <Setter Property="Template">
        <Setter.Value>
            <ControlTemplate TargetType="TextBox">
                <Border Background="{TemplateBinding Background}"
                        BorderBrush="{TemplateBinding BorderBrush}"
                        BorderThickness="{TemplateBinding BorderThickness}"
                        CornerRadius="10">
                    <ScrollViewer x:Name="PART_ContentHost" Padding="{TemplateBinding Padding}" />
                </Border>
                <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True">
                        <Setter Property="BorderBrush" Value="{DynamicResource Brush.Accent}" />
                    </Trigger>
                    <Trigger Property="IsFocused" Value="True">
                        <Setter Property="BorderBrush" Value="{DynamicResource Brush.Accent}" />
                    </Trigger>
                </ControlTemplate.Triggers>
            </ControlTemplate>
        </Setter.Value>
    </Setter>
</Style>
```

- [ ] **Step 7: Refine CollapsibleHeaderButtonStyle — better spacing**

Replace lines 308-336:
```xml
<Style x:Key="CollapsibleHeaderButtonStyle" TargetType="Button">
    <Setter Property="Foreground" Value="{DynamicResource Brush.Text}" />
    <Setter Property="Background" Value="Transparent" />
    <Setter Property="BorderThickness" Value="0" />
    <Setter Property="Padding" Value="14,10" />
    <Setter Property="HorizontalContentAlignment" Value="Stretch" />
    <Setter Property="Cursor" Value="Hand" />
    <Setter Property="Template">
        <Setter.Value>
            <ControlTemplate TargetType="Button">
                <Border x:Name="Root"
                        Background="{TemplateBinding Background}"
                        CornerRadius="10"
                        Padding="{TemplateBinding Padding}">
                    <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"
                                      VerticalAlignment="Center" />
                </Border>
                <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True">
                        <Setter TargetName="Root" Property="Background" Value="{DynamicResource Brush.HoverTool}" />
                    </Trigger>
                    <Trigger Property="IsPressed" Value="True">
                        <Setter TargetName="Root" Property="Background" Value="{DynamicResource Brush.HoverToolPressed}" />
                    </Trigger>
                </ControlTemplate.Triggers>
            </ControlTemplate>
        </Setter.Value>
    </Setter>
</Style>
```

- [ ] **Step 8: Refine WorkspaceTreeViewItemStyle — better visual hierarchy**

Replace lines 696-780:
```xml
<Style x:Key="WorkspaceTreeViewItemStyle" TargetType="TreeViewItem">
    <Setter Property="IsExpanded" Value="False" />
    <Setter Property="Foreground" Value="{DynamicResource Brush.Text}" />
    <Setter Property="Background" Value="Transparent" />
    <Setter Property="BorderBrush" Value="Transparent" />
    <Setter Property="Padding" Value="0" />
    <Setter Property="Margin" Value="0,1" />
    <Setter Property="Template">
        <Setter.Value>
            <ControlTemplate TargetType="TreeViewItem">
                <StackPanel>
                    <Border x:Name="ItemBorder"
                            Background="{TemplateBinding Background}"
                            BorderBrush="{TemplateBinding BorderBrush}"
                            BorderThickness="1"
                            CornerRadius="6"
                            Padding="6,3">
                        <Grid>
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="Auto" />
                                <ColumnDefinition Width="*" />
                            </Grid.ColumnDefinitions>
                            <ToggleButton x:Name="Expander"
                                          Width="14"
                                          Height="14"
                                          Margin="0,0,6,0"
                                          IsChecked="{Binding IsExpanded, RelativeSource={RelativeSource TemplatedParent}}"
                                          ClickMode="Press"
                                          Background="Transparent"
                                          BorderThickness="0"
                                          Foreground="{DynamicResource Brush.SubtleText}"
                                          Visibility="Hidden">
                                <TextBlock Text="▸"
                                           FontSize="10"
                                           HorizontalAlignment="Center"
                                           VerticalAlignment="Center" />
                            </ToggleButton>
                            <ContentPresenter Grid.Column="1"
                                              x:Name="PART_Header"
                                              ContentSource="Header"
                                              HorizontalAlignment="Stretch"
                                              VerticalAlignment="Center" />
                        </Grid>
                    </Border>
                    <ItemsPresenter x:Name="ItemsHost" Margin="16,2,0,0" />
                </StackPanel>
                <ControlTemplate.Triggers>
                    <Trigger Property="HasItems" Value="True">
                        <Setter TargetName="Expander" Property="Visibility" Value="Visible" />
                    </Trigger>
                    <Trigger Property="IsExpanded" Value="False">
                        <Setter TargetName="ItemsHost" Property="Visibility" Value="Collapsed" />
                    </Trigger>
                    <Trigger Property="IsExpanded" Value="True">
                        <Setter TargetName="Expander" Property="RenderTransform">
                            <Setter.Value>
                                <RotateTransform Angle="90" CenterX="7" CenterY="7" />
                            </Setter.Value>
                        </Setter>
                    </Trigger>
                    <Trigger Property="IsMouseOver" Value="True">
                        <Setter TargetName="ItemBorder" Property="Background" Value="{DynamicResource Brush.HoverSurface}" />
                    </Trigger>
                    <Trigger Property="IsSelected" Value="True">
                        <Setter TargetName="ItemBorder" Property="Background" Value="{DynamicResource Brush.SelectionActive}" />
                        <Setter TargetName="ItemBorder" Property="BorderBrush" Value="{DynamicResource Brush.SelectionBorder}" />
                        <Setter Property="Foreground" Value="{DynamicResource Brush.Text}" />
                    </Trigger>
                    <MultiTrigger>
                        <MultiTrigger.Conditions>
                            <Condition Property="IsSelected" Value="True" />
                            <Condition Property="IsSelectionActive" Value="False" />
                        </MultiTrigger.Conditions>
                        <Setter TargetName="ItemBorder" Property="Background" Value="{DynamicResource Brush.SelectionInactive}" />
                        <Setter TargetName="ItemBorder" Property="BorderBrush" Value="{DynamicResource Brush.SelectionBorder}" />
                        <Setter Property="Foreground" Value="{DynamicResource Brush.Text}" />
                    </MultiTrigger>
                    <Trigger Property="IsEnabled" Value="False">
                        <Setter Property="Foreground" Value="{DynamicResource Brush.SubtleText}" />
                    </Trigger>
                </ControlTemplate.Triggers>
            </ControlTemplate>
        </Setter.Value>
    </Setter>
</Style>
```

- [ ] **Step 9: Verify build**

Run: `dotnet build src/Athlon.Agent.App/Athlon.Agent.App.csproj`
Expected: Build succeeds with no errors.

---

### Task 3: Polish Overlay Styles

**Files:**
- Modify: `src/Athlon.Agent.App/Themes/Overlays.xaml`

- [ ] **Step 1: Refine ContextMenu and MenuItem styles for consistency**

The current styling is already decent. Refine corner radii to match the new 10px/14px system and add hover refinement:

Replace lines 56-91 (default ContextMenu/MenuItem):
```xml
<Style TargetType="{x:Type ContextMenu}">
    <Setter Property="Background" Value="{DynamicResource Brush.MenuBackground}" />
    <Setter Property="BorderBrush" Value="{DynamicResource Brush.Border}" />
    <Setter Property="BorderThickness" Value="1" />
    <Setter Property="Padding" Value="6" />
    <Setter Property="Foreground" Value="{DynamicResource Brush.Text}" />
    <Setter Property="FontSize" Value="13" />
</Style>

<Style TargetType="{x:Type MenuItem}">
    <Setter Property="Background" Value="Transparent" />
    <Setter Property="Foreground" Value="{DynamicResource Brush.Text}" />
    <Setter Property="Padding" Value="12,8" />
    <Setter Property="FontSize" Value="13" />
    <Setter Property="Template">
        <Setter.Value>
            <ControlTemplate TargetType="{x:Type MenuItem}">
                <Border x:Name="ItemBorder"
                        Background="{TemplateBinding Background}"
                        Padding="{TemplateBinding Padding}"
                        CornerRadius="6">
                    <ContentPresenter ContentSource="Header"
                                      RecognizesAccessKey="True"
                                      VerticalAlignment="Center" />
                </Border>
                <ControlTemplate.Triggers>
                    <Trigger Property="IsHighlighted" Value="True">
                        <Setter TargetName="ItemBorder" Property="Background" Value="{DynamicResource Brush.MenuHover}" />
                    </Trigger>
                    <Trigger Property="IsEnabled" Value="False">
                        <Setter Property="Foreground" Value="{DynamicResource Brush.DisabledText}" />
                    </Trigger>
                </ControlTemplate.Triggers>
            </ControlTemplate>
        </Setter.Value>
    </Setter>
</Style>
```

- [ ] **Step 2: Verify build**

Run: `dotnet build src/Athlon.Agent.App/Athlon.Agent.App.csproj`
Expected: Build succeeds.

---

### Task 4: Polish MainWindow.xaml — Layout & Chat Bubbles

**Files:**
- Modify: `src/Athlon.Agent.App/MainWindow.xaml` (targeted changes)

Key refinements:
1. **Header** — Refine spacing and alignment
2. **Left sidebar** — Better section labels, refined card borders
3. **Chat bubbles** — Smoother appearance, refined spacing
4. **Composer** — Refined placeholder text, better button positioning
5. **Settings page** — Better card layout, refined form controls
6. **Right sidebar** — Refined skill/MCP sections
7. **Status bar** — Better visual balance

- [ ] **Step 1: Refine header area spacing**

In the header Border (lines 52-93), change the inner Grid margin from `Margin="24,0"` to `Margin="28,0"` and add a subtitle with slightly better spacing:

Line 58: Change `Margin="24,0">` to `Margin="28,0">`

Line 71: Change `FontSize="18"` to `FontSize="20"` for the title.

- [ ] **Step 2: Refine left sidebar padding**

Line 100: Change `Padding="16,16,20,16">` to `Padding="16,16,16,16">`

- [ ] **Step 3: Refine workspace selector badge**

Lines 103-138: Change `CornerRadius="6"` to `CornerRadius="8"` for the workspace selector and the queue panel.

- [ ] **Step 4: Refine "对话" section label**

Lines 273-279: Change `Margin="8,0,0,16"` to `Margin="8,0,0,14"` and add `FontWeight="SemiBold"`.

- [ ] **Step 5: Refine session history item border**

Lines 393-408: Change `CornerRadius="12"` to `CornerRadius="10"` and refine border thickness.

- [ ] **Step 6: Refine chat message area spacing**

Line 646: Change `Padding="24,20,20,28"` to `Padding="28,24,24,32"` for better breathing room.

- [ ] **Step 7: Refine chat message user/assistant bubble borders**

Lines 877-904: The chat bubble styles. Change assistant bubble `CornerRadius="24"` to `CornerRadius="20"`. Add a subtle `BorderThickness="1"` refinement. The assistant bubble border should use `Brush.Border`.

Lines 884-903: For assistant bubbles, the border brush should be `Brush.Border` as default. For user bubbles, keep `#2F5C8E`.

- [ ] **Step 8: Refine chat avatar icons**

Lines 667-683: The assistant avatar icon (48x48) should have a slight rounded background for consistency. But the Image approach works fine — keep the app icon.

Lines 1089-1106: The user avatar `CornerRadius="12"` should remain `12`.

- [ ] **Step 9: Refine composer area**

Line 1136: Change `Padding="20,16"` to `Padding="24,16"` for the composer outer border.

Line 1204: Change the composer inner border `CornerRadius="28"` to `CornerRadius="24"`.

Line 1205: Change `Padding="16,14"` to `Padding="20,16"` for better inner spacing.

Line 1258: Update placeholder text from `"Message Athlon — @ 文件 / 技能 · / 命令 · Enter 发送 · Ctrl+V 粘贴图片"` to `"Message Athlon — @ 文件 / 技能 · / 命令 · Enter 发送 · Ctrl+V 粘贴图片"`.

Line 1422: Refine keyboard shortcut hint text position — change `Margin="32,0,0,0"` to `Margin="28,0,0,0"`.

- [ ] **Step 10: Refine composer bottom row**

Lines 1404-1449: The bottom row of the composer with buttons. Adjust the send button `Margin` and add subtle visual polish. The "Enter 发送 · Shift+Enter 换行 · Ctrl+V 粘贴图片" hint is fine but change its color to be softer.

Line 1422-1426: Change `Foreground="{StaticResource Brush.SubtleText}"` — this is already correct. Change `Margin="32,0,0,0"` to `Margin="28,0,0,0"`.

- [ ] **Step 11: Refine settings page**

Line 1458: Change `Padding="32,28"` to `Padding="40,32"` for the settings scroll viewer.

Line 1472: Change title `FontSize="24"` to `FontSize="28"` and add a bit more bottom margin `Margin="0,0,0,8"`.

Line 1473: Keep the subtitle.

Line 1539: Change MCP server item border background from `#1B1B1E` to use dynamic resource `Brush.Panel`.

Line 1616: Same for Skills item border background.

- [ ] **Step 12: Refine right sidebar**

Lines 1770: Change `Margin="16,16,16,16"` to `Margin="16,16,20,16"` for right sidebar padding.

Lines 1778, 1801, 1890: Change section label font size from `12` to `12` but add `FontWeight="SemiBold"`.

- [ ] **Step 13: Refine workspace tree area in right sidebar**

Lines 1897-1902: Keep `CornerRadius="12"` but refine padding to `Padding="6"`.

- [ ] **Step 14: Refine status bar**

Lines 1721: Change `Padding="20,10"` to `Padding="24,10"` for the status bar.

- [ ] **Step 15: Verify build**

Run: `dotnet build src/Athlon.Agent.App/Athlon.Agent.App.csproj`
Expected: Build succeeds.

---

### Task 5: Polish Control Files

**Files:**
- Modify: `src/Athlon.Agent.App/Controls/FileEditorView.xaml`
- Modify: `src/Athlon.Agent.App/Licensing/LicenseActivationWindow.xaml`

- [ ] **Step 1: Refine FileEditorView.xaml tab strip**

The editor tab strip uses `CornerRadius="8"` for tabs. Change to `CornerRadius="6"` for a tighter appearance. Also add a subtle background to the tab strip border.

In line 16-20, change tab strip height reference and add a refined background. The tab strip border should use `Brush.Chrome` background.

Change line 18-19: The tab strip border already uses Brush.Chrome — good.

Add a subtle top border to the editor area in line 58-61 for visual separation from the tab strip:
```xml
<Border Grid.Row="1"
        Background="#1E1E1E"
        BorderBrush="{DynamicResource Brush.Border}"
        BorderThickness="0,1,0,0"
        Padding="0">
```

- [ ] **Step 2: Refine LicenseActivationWindow.xaml**

Change the window background to use `Brush.AppBackground` instead of hardcoded color, and refine button styles. Let me read the file first.

- [ ] **Step 2a: Read LicenseActivationWindow.xaml**

Read and assess current design, then apply consistent styling.

- [ ] **Step 3: Verify build**

Run: `dotnet build src/Athlon.Agent.App/Athlon.Agent.App.csproj`
Expected: Build succeeds.

---

### Task 6: Final Polish Pass — Review and Fix Any Remaining Issues

- [ ] **Step 1: Review all changed files for consistency**

Check corner radii: should use 6 (small), 8 (compact), 10 (medium), 14 (large), 20 (bubble), 24 (composer).

Check spacing: margins and padding should follow a 4px grid (4, 8, 12, 16, 20, 24, 28, 32, 40).

Check color references: all use `{DynamicResource Brush.*}`.

- [ ] **Step 2: Final build and review**

Run: `dotnet build src/Athlon.Agent.App/Athlon.Agent.App.csproj`
Expected: Clean build, no warnings.

---
