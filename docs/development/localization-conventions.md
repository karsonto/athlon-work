# 本地化开发规范

> 供 AI 与开发者在修改 Athlon Agent WPF 界面时参考。

## 架构

```
UiSettings.Language (zh-CN | en-US)
        ↓
AppCultureManager.ApplyFromSettings / SetCulture
  → Thread.CurrentUICulture / CurrentCulture
  → Strings.Culture
  → CultureChanged 事件
        ↓
消费者
  ├── ILocalizationService（ViewModel / Service 注入）
  ├── LocalizationHub + {loc:Localize}（XAML 静态文案）
  ├── IUserNotifier（MessageBox）
  └── ChatHtmlBuilder window.__chatI18n（WebView JS）
```

## 资源文件

| 文件 | 说明 |
|------|------|
| `Resources/Strings.resx` | 默认 zh-CN |
| `Resources/Strings.en-US.resx` | 英文卫星资源 |
| `Resources/Strings.cs` | `ResourceManager` 访问入口 |

**命名**：`{Area}_{Element}`，如 `Schedule_Title`、`License_Missing`。

**占位符**：使用 `{0}`、`{1}`，通过 `ILocalizationService.Format` 或 `Strings.Format` 格式化，禁止字符串拼接用户可见文案。

## 代码约定

- ViewModel 注入 `ILocalizationService`，订阅 `AppCultureManager.CultureChanged` 刷新绑定属性。
- `MessageBox.Show` 统一走 `IUserNotifier`。
- XAML 静态 `Text` / `Content` / `ToolTip` 使用 `xmlns:loc="clr-namespace:Athlon.Agent.App.Localization"` 与 `{loc:Localize Key=...}`。
- **LLM Prompt**（`Athlon.Agent.Core/Prompt/`）与 UI 资源分开，不在 `Strings.resx` 中维护。

## 语言设置

- `UiSettings.Language`：默认 `zh-CN`；可选 `zh-CN`、`en-US`。历史值 `Auto` 会在加载时迁移为 `zh-CN`。
- 启动时在 License/SSO 门禁之前调用 `AppCultureManager.ApplyFromSettings`。

## 新增字符串检查清单

1. 在 `Strings.resx` 与 `Strings.en-US.resx` 添加同名键（可运行 `scripts/generate-strings-resx.py` 维护字典后重新生成）。
2. 替换硬编码引用。
3. 若绑定到 ViewModel，在 `RefreshLocalizedStrings()` 中 `OnPropertyChanged`。
4. 为 en-US 键补充单元测试（`LocalizationTests`）。
