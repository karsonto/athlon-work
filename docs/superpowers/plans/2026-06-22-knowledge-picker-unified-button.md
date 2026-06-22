# Knowledge Picker: Unified Button Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Merge the separate knowledge toggle switch + picker button into a single unified button that reveals knowledge state at a glance.

**Architecture:** Remove the `IsKnowledgeEnabledForChat` CheckBox toggle in the XAML composer toolbar. The ViewModel derives knowledge "enabled" state implicitly from whether ≥1 knowledge spaces are selected (`SelectedModuleCount > 0`). The picker button is always visible when Embedding is configured, and shows active styling when knowledge is on.

**Tech Stack:** WPF XAML, MVVM (CommunityToolkit.Mvvm), C# 12

---

### Task 1: Refactor ViewModel — Remove toggle, derive state from selection

**Files:**
- Modify: `src/Athlon.Agent.App/ViewModels/ComposerKnowledgeViewModel.cs`

- [ ] **Step 1: Remove `IsKnowledgeEnabledForChat` property and its partial method**

Remove the `[ObservableProperty] private bool _isKnowledgeEnabledForChat;` field and the `OnIsKnowledgeEnabledForChatChanged` partial method. Replace with a computed `IsKnowledgeActive` property.

```csharp
// Replace removed [ObservableProperty] with:
public bool IsKnowledgeActive => SelectedModuleCount > 0;
```

- [ ] **Step 2: Update `ShowKnowledgeChips` to depend only on selection count**

Change:
```csharp
public bool ShowKnowledgeChips => IsKnowledgeEnabledForChat && SelectedModules.Count > 0;
```
To:
```csharp
public bool ShowKnowledgeChips => SelectedModuleCount > 0;
```

- [ ] **Step 3: Remove `ShowKnowledgePicker` property entirely**

Remove:
```csharp
public bool ShowKnowledgePicker => IsKnowledgeEnabledForChat;
```
The button is always visible when embedding is configured.

- [ ] **Step 4: Update `KnowledgeToggleToolTip` → rename to `KnowledgeButtonToolTip`**

Change to reflect the new button:
```csharp
public string KnowledgeButtonToolTip => !IsEmbeddingConfigured()
    ? "请先在设置页配置 Embedding Endpoint、Model 和 API Key"
    : IsKnowledgeActive
        ? $"知识库已启用 · {SelectedModuleCount} 个知识空间"
        : "点击选择知识空间";
```

- [ ] **Step 5: Update `ToggleKnowledgePicker` to remove the guard**

Remove the `if (!IsKnowledgeEnabledForChat) return;` guard — the button is always functional:
```csharp
[RelayCommand]
private void ToggleKnowledgePicker()
{
    IsKnowledgePickerOpen = !IsKnowledgePickerOpen;
}
```

- [ ] **Step 6: Update `LoadForSessionAsync` — remove `IsKnowledgeEnabledForChat` assignment and `_suppressSave` usage**

Change the loading logic. The `Enabled` flag from disk is still loaded but used for backward compat: if old data has `Enabled=true` but 0 modules selected, treat as inactive.

Remove:
```csharp
_suppressSave = true;
IsKnowledgeEnabledForChat = snapshot.Enabled;
// ... (Modules loop) ...
_suppressSave = false;
```

Replace with just:
```csharp
Modules.Clear();
foreach (var summary in await _store.ListModulesAsync())
{
    Modules.Add(new ComposerKnowledgeModuleItemViewModel(
        summary,
        snapshot.ModuleIds.Contains(summary.Module.Id),
        OnModuleSelectionChangedAsync));
}
```

- [ ] **Step 7: Update `PersistStateAsync` — `Enabled` is always derived from selection**

Change:
```csharp
var snapshot = new SessionKnowledgeSnapshot(IsKnowledgeEnabledForChat, moduleIds);
```
To:
```csharp
var snapshot = new SessionKnowledgeSnapshot(SelectedModuleCount > 0, moduleIds);
```

- [ ] **Step 8: Update `NotifyPickerStateChanged` — add `IsKnowledgeActive`**

```csharp
private void NotifyPickerStateChanged()
{
    OnPropertyChanged(nameof(SelectedModuleCount));
    OnPropertyChanged(nameof(KnowledgePickerLabel));
    OnPropertyChanged(nameof(ShowKnowledgeChips));
    OnPropertyChanged(nameof(IsKnowledgeActive));
    OnPropertyChanged(nameof(KnowledgeButtonToolTip));
}
```

- [ ] **Step 9: Update `SetEmbeddingApiKeyAvailable` — reference new property names**

```csharp
public void SetEmbeddingApiKeyAvailable(bool hasStoredApiKey)
{
    _hasStoredEmbeddingApiKey = hasStoredApiKey;
    OnPropertyChanged(nameof(IsKnowledgeButtonEnabled));
    OnPropertyChanged(nameof(KnowledgeButtonToolTip));
}
```

Rename `IsKnowledgeToggleEnabled` to `IsKnowledgeButtonEnabled` (or keep for minimal diff, but update the XAML binding).

Keep as `IsKnowledgeToggleEnabled` for now (rename would be cosmetic only).


### Task 2: Rewrite XAML composer toolbar — unified button

**Files:**
- Modify: `src/Athlon.Agent.App/MainWindow.xaml` (lines ~1384-1505)

- [ ] **Step 1: Replace CheckBox + Button with a single unified Button**

Replace lines 1391-1413 (CheckBox + KnowledgePickerButton) with one button:

```xml
<Button x:Name="KnowledgePickerButton"
        Command="{Binding ComposerKnowledge.ToggleKnowledgePickerCommand}"
        Margin="8,0,0,0"
        Padding="10,5"
        VerticalAlignment="Center"
        IsEnabled="{Binding ComposerKnowledge.IsKnowledgeToggleEnabled}"
        ToolTip="{Binding ComposerKnowledge.KnowledgeButtonToolTip}">
    <Button.Resources>
        <Style x:Key="KnowledgeActiveBorderStyle" TargetType="Border" BasedOn="{StaticResource GhostButtonStyle}">
            <!-- Active styling overrides below -->
        </Style>
    </Button.Resources>
    <Button.Style>
        <Style TargetType="Button" BasedOn="{StaticResource GhostButtonStyle}">
            <Style.Triggers>
                <DataTrigger Binding="{Binding ComposerKnowledge.IsKnowledgeActive}" Value="True">
                    <Setter Property="BorderBrush" Value="{DynamicResource Brush.Accent}" />
                    <Setter Property="Foreground" Value="{DynamicResource Brush.Accent}" />
                    <Setter Property="Background" Value="{DynamicResource Brush.AccentTranslucent}" />
                </DataTrigger>
                <Trigger Property="IsEnabled" Value="False">
                    <Setter Property="Opacity" Value="0.45" />
                </Trigger>
            </Style.Triggers>
        </Style>
    </Button.Style>
    <StackPanel Orientation="Horizontal">
        <!-- Book/knowledge icon (minimal SVG path) -->
        <Path Data="M4,6 C4,4.9 4.9,4 6,4 L18,4 C19.1,4 20,4.9 20,6 L20,18 C20,19.1 19.1,20 18,20 L6,20 C4.9,20 4,19.1 4,18 Z"
              Stroke="{Binding RelativeSource={RelativeSource AncestorType=Button}, Path=Foreground}"
              StrokeThickness="1.5"
              Fill="Transparent"
              Width="16"
              Height="16"
              Margin="0,0,6,0" />
        <TextBlock Text="{Binding ComposerKnowledge.KnowledgePickerLabel}"
                   FontSize="12"
                   VerticalAlignment="Center" />
    </StackPanel>
</Button>
```

Actually, let me use a simpler approach. Since the icon is complex, let me use a simple unicode character or text-based icon to keep it clean. Let me use "📚" or just a simple SVG path.

Let me use a clean approach with the existing KnowledgeNavIconStyle pattern — but simplified for inline use.

- [ ] **Step 2: Remove the visibility trigger from KnowledgePickerButton**

The button is always visible (when embedding configured, controlled by `IsEnabled` instead).

- [ ] **Step 3: Remove the independent CheckBox (McpToggleSwitchStyle)**

Delete lines 1391-1396 (the CheckBox element).

- [ ] **Step 4: Add distinct active/inactive visual states**

When `IsKnowledgeActive` is true:
- Border color → Accent
- Text color → Accent  
- Background → subtle accent fill

When inactive:
- Default GhostButtonStyle (transparent bg, subtle text border)
- Disabled if embedding not configured

- [ ] **Step 5: Verify Popup still works correctly**

Popup's `PlacementTarget` stays as `KnowledgePickerButton`. `IsOpen` binding stays `ComposerKnowledge.IsKnowledgePickerOpen`.

---

### Task 3: Verify no downstream breakage

- [ ] **Step 1: Check all usages of removed properties**

Search for `IsKnowledgeEnabledForChat`, `ShowKnowledgePicker`, `KnowledgeToggleToolTip` across the solution.

- [ ] **Step 2: Build and verify**

Run: `dotnet build src/Athlon.Agent.App/Athlon.Agent.App.csproj`
Expected: Build succeeds with no errors.
