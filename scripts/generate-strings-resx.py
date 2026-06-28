#!/usr/bin/env python3
"""Generate Strings.resx and Strings.en-US.resx for Athlon Agent."""

from pathlib import Path

RESX_HEADER = """<?xml version="1.0" encoding="utf-8"?>
<root>
  <xsd:schema id="root" xmlns="" xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:msdata="urn:schemas-microsoft-com:xml-msdata">
    <xsd:import namespace="http://www.w3.org/XML/1998/namespace" />
    <xsd:element name="root" msdata:IsDataSet="true">
      <xsd:complexType>
        <xsd:choice maxOccurs="unbounded">
          <xsd:element name="metadata">
            <xsd:complexType>
              <xsd:sequence>
                <xsd:element name="value" type="xsd:string" minOccurs="0" />
              </xsd:sequence>
              <xsd:attribute name="name" use="required" type="xsd:string" />
              <xsd:attribute name="type" type="xsd:string" />
              <xsd:attribute name="mimetype" type="xsd:string" />
              <xsd:attribute ref="xml:space" />
            </xsd:complexType>
          </xsd:element>
          <xsd:element name="assembly">
            <xsd:complexType>
              <xsd:attribute name="alias" type="xsd:string" />
              <xsd:attribute name="name" type="xsd:string" />
            </xsd:complexType>
          </xsd:element>
          <xsd:element name="data">
            <xsd:complexType>
              <xsd:sequence>
                <xsd:element name="value" type="xsd:string" minOccurs="0" msdata:Ordinal="1" />
                <xsd:element name="comment" type="xsd:string" minOccurs="0" msdata:Ordinal="2" />
              </xsd:sequence>
              <xsd:attribute name="name" type="xsd:string" use="required" msdata:Ordinal="1" />
              <xsd:attribute name="type" type="xsd:string" msdata:Ordinal="3" />
              <xsd:attribute name="mimetype" type="xsd:string" msdata:Ordinal="4" />
              <xsd:attribute ref="xml:space" />
            </xsd:complexType>
          </xsd:element>
          <xsd:element name="resheader">
            <xsd:complexType>
              <xsd:sequence>
                <xsd:element name="value" type="xsd:string" minOccurs="0" msdata:Ordinal="1" />
              </xsd:sequence>
              <xsd:attribute name="name" type="xsd:string" use="required" />
            </xsd:complexType>
          </xsd:element>
        </xsd:choice>
      </xsd:complexType>
    </xsd:element>
  </xsd:schema>
  <resheader name="resmimetype">
    <value>text/microsoft-resx</value>
  </resheader>
  <resheader name="version">
    <value>2.0</value>
  </resheader>
  <resheader name="reader">
    <value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
  </resheader>
  <resheader name="writer">
    <value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
  </resheader>"""

# zh-CN, en-US
ENTRIES: dict[str, tuple[str, str]] = {
    "Common_OK": ("确定", "OK"),
    "Common_Cancel": ("取消", "Cancel"),
    "Common_Close": ("关闭", "Close"),
    "Common_Delete": ("删除", "Delete"),
    "Common_Save": ("保存", "Save"),
    "Common_Browse": ("浏览…", "Browse…"),
    "Common_Clear": ("清空", "Clear"),
    "Common_Confirm": ("确认", "Confirm"),
    "Common_Prompt": ("提示", "Notice"),
    "Common_ProductName": ("Athlon Agent", "Athlon Agent"),
    "Common_Minimize": ("最小化", "Minimize"),
    "Common_Maximize": ("最大化", "Maximize"),
    "Common_Restore": ("还原", "Restore"),
    "Common_BackToChat": ("返回对话", "Back to chat"),
    "Common_Yes": ("是", "Yes"),
    "Common_No": ("否", "No"),
    "Common_Add": ("新增", "Add"),
    "Common_Refresh": ("刷新", "Refresh"),
    "Common_Configure": ("配置", "Configure"),
    "Common_Help": ("Help", "Help"),
    "Common_About": ("About", "About"),
    "Startup_FailedTitle": ("Athlon Agent 启动失败", "Athlon Agent failed to start"),
    "License_WindowTitle": ("激活 Athlon Agent", "Activate Athlon Agent"),
    "License_Subtitle": ("激活 License", "Activate License"),
    "License_CurrentWindowsAccount": ("当前 Windows 账号", "Current Windows account"),
    "License_BrowseDialogTitle": ("选择 License 文件", "Select license file"),
    "License_Missing": (
        "未找到有效的 License 文件。请粘贴或导入管理员提供的 License。",
        "No valid license file found. Paste or import the license provided by your administrator.",
    ),
    "License_InvalidFormat": (
        "License 格式无效。请确认粘贴的是完整的 JSON 内容。",
        "Invalid license format. Ensure you pasted the complete JSON content.",
    ),
    "License_InvalidSignature": (
        "License 签名无效。请向管理员索取新的 License。",
        "Invalid license signature. Request a new license from your administrator.",
    ),
    "License_InvalidPayload": ("License 内容无法解析。", "License content could not be parsed."),
    "License_WrongProduct": ("该 License 不适用于 Athlon Agent。", "This license is not valid for Athlon Agent."),
    "License_UnsupportedVersion": (
        "License 版本不受支持，请升级客户端或联系管理员。",
        "Unsupported license version. Upgrade the client or contact your administrator.",
    ),
    "License_Expired": ("License 已过期。请向管理员申请续期。", "License has expired. Request a renewal from your administrator."),
    "License_AccountMismatch": (
        "License 与当前 Windows 登录账号不匹配。",
        "License does not match the current Windows sign-in account.",
    ),
    "License_ValidationFailed": ("License 校验失败。", "License validation failed."),
    "License_SaveFailed": ("保存 License 失败：{0}", "Failed to save license: {0}"),
    "License_ReadFileFailed": ("读取文件失败：{0}", "Failed to read file: {0}"),
    "License_FileFilter": ("License 文件 (*.lic)|*.lic|所有文件 (*.*)|*.*", "License files (*.lic)|*.lic|All files (*.*)|*.*"),
    "License_UpnNone": ("UPN：(无)", "UPN: (none)"),
    "License_SamAccountLabel": ("Sam：{0}", "Sam: {0}"),
    "License_UpnAccountLabel": ("UPN：{0}", "UPN: {0}"),
    "About_WindowTitle": ("About Athlon Agent", "About Athlon Agent"),
    "About_Description": ("AI coding agent for your workspace.", "AI coding agent for your workspace."),
    "About_Contact": ("如有问题，请联系 FTD AMS 团队。", "For questions, contact the FTD AMS team."),
    "About_CheckUpdate": ("检查更新", "Check for updates"),
    "Sso_WindowTitle": ("IMP SSO 登录", "IMP SSO sign-in"),
    "Sso_WaitingTitle": ("正在等待 IMP 登录", "Waiting for IMP sign-in"),
    "Sso_WaitingMessage": ("请在浏览器中完成登录，成功后自动继续。", "Complete sign-in in your browser. The app will continue automatically."),
    "Sso_CancelLogin": ("取消登录", "Cancel sign-in"),
    "Sso_LoginFailed": ("IMP SSO 登录失败。", "IMP SSO sign-in failed."),
    "Sso_LoginFailedWithMessage": ("IMP SSO 登录失败：{0}", "IMP SSO sign-in failed: {0}"),
    "Sso_LogoutConfirm": ("确定要退出登录吗？", "Sign out of your account?"),
    "Sso_LogoutTitle": ("退出登录", "Sign out"),
    "Sso_LogoutTooltip": ("点击退出登录", "Click to sign out"),
    "Shell_SwitchToDark": ("切换到深色模式", "Switch to dark mode"),
    "Shell_SwitchToLight": ("切换到浅色模式", "Switch to light mode"),
    "Shell_ModifiedFilesHeader": ("已修改 {0} 个文件", "{0} modified file(s)"),
    "Shell_ContextSidebarOpen": ("打开右侧栏 (Ctrl+Alt+B)", "Open right sidebar (Ctrl+Alt+B)"),
    "Shell_ContextSidebarClose": ("关闭右侧栏 (Ctrl+Alt+B)", "Close right sidebar (Ctrl+Alt+B)"),
    "Shell_ShuttingDown": ("正在关闭…", "Shutting down…"),
    "Shell_ClearContextTitle": ("清空上下文", "Clear context"),
    "Shell_ClearContextMessage": (
        "将清空当前对话在模型中的全部可见历史（用户、助手、工具与压缩记录），并清空 Coding 任务计划。\n\n会话 ID、工作区与标题会保留；磁盘上的 transcript 归档不会删除。\n\n下次发送消息时会重新构建系统提示（工作区、工具、技能等）。",
        "This clears all visible model history for the current conversation (user, assistant, tool, and compaction records) and clears the Coding task plan.\n\nSession ID, workspace, and title are kept; transcript archives on disk are not deleted.\n\nThe system prompt will be rebuilt on the next message (workspace, tools, skills, etc.).",
    ),
    "Shell_ClearContextDone": ("上下文已清空。", "Context cleared."),
    "Shell_DeleteConversationTitle": ("删除对话", "Delete conversation"),
    "Shell_DeleteConversationMessage": ("确定删除对话「{0}」吗？此操作无法撤销。", 'Delete conversation "{0}"? This cannot be undone.'),
    "Shell_DeleteConversationDone": ("对话已删除。", "Conversation deleted."),
    "Shell_CannotPreviewTitle": ("无法预览", "Cannot preview"),
    "Shell_CannotPreviewMessage": ("无法解析文件路径。请先配置工作区。", "Could not resolve the file path. Configure a workspace first."),
    "Shell_OpenFolderFailedTitle": ("打开失败", "Open failed"),
    "Shell_OpenFolderFailedMessage": ("无法打开文件夹：{0}", "Could not open folder: {0}"),
    "Shell_DeleteNodeTitle": ("删除", "Delete"),
    "Shell_DeleteFolderMessage": (
        "确定删除文件夹「{0}」及其全部内容吗？此操作无法撤销。",
        'Delete folder "{0}" and all its contents? This cannot be undone.',
    ),
    "Shell_DeleteFileMessage": ("确定删除文件「{0}」吗？此操作无法撤销。", 'Delete file "{0}"? This cannot be undone.'),
    "Shell_TargetMissing": ("目标不存在或已被删除。", "Target does not exist or was already deleted."),
    "Shell_DeleteSuccess": ("已删除{0}「{1}」。", 'Deleted {0} "{1}".'),
    "Shell_DeleteFailedTitle": ("删除失败", "Delete failed"),
    "Shell_DeleteFailedMessage": ("无法删除「{0}」：{1}", 'Could not delete "{0}": {1}'),
    "Shell_NoWorkspace": ("未配置工作区", "No workspace configured"),
    "Shell_SaveConversationFailed": ("保存对话失败：{0}", "Failed to save conversation: {0}"),
    "Shell_LoadingConversation": ("正在加载对话…", "Loading conversation…"),
    "Shell_LoadConversationFailed": ("无法加载该对话。", "Could not load this conversation."),
    "Shell_LoadConversationDone": ("已加载对话：{0}", "Loaded conversation: {0}"),
    "Shell_WorkspaceStatus": ("当前对话工作区：{0}", "Current conversation workspace: {0}"),
    "Shell_ExitTitle": ("退出 Athlon Agent", "Exit Athlon Agent"),
    "Shell_ExitMessage": (
        "有对话正在生成或消息排队中，退出将停止所有任务。确定退出？",
        "A reply is generating or messages are queued. Exiting will stop all tasks. Exit anyway?",
    ),
    "Shell_FolderKind": ("文件夹", "folder"),
    "Shell_FileKind": ("文件", "file"),
    "Shell_StatusBarModel": ("模型: {0}", "Model: {0}"),
    "Shell_StatusBarLogs": ("Logs: {0}", "Logs: {0}"),
    "Nav_NewAgent": ("New Agent", "New Agent"),
    "Nav_NewSessionTooltip": ("新建会话", "New session"),
    "Nav_NoAgentRecords": ("暂无 Agent 记录", "No agent sessions yet"),
    "Nav_StopGeneration": ("停止生成", "Stop generation"),
    "Nav_Queued": ("排队中", "Queued"),
    "Nav_RemoveFromQueue": ("从队列移除", "Remove from queue"),
    "Nav_TaskPlan": ("任务计划", "Task plan"),
    "Nav_Knowledge": ("知识库", "Knowledge"),
    "Nav_Schedule": ("定时任务", "Scheduled tasks"),
    "RecordGroup_Today": ("今天", "Today"),
    "RecordGroup_Last7Days": ("过去 7 天", "Last 7 days"),
    "RecordGroup_Earlier": ("更早", "Earlier"),
    "Context_Title": ("上下文", "Context"),
    "Context_ClearTooltip": ("清空当前对话在模型中的可见历史", "Clear visible model history for this conversation"),
    "Context_TabFiles": ("文件", "Files"),
    "Context_TabSkills": ("技能", "Skills"),
    "Context_Workspace": ("工作区", "Workspace"),
    "Chat_SettingsTooltip": ("设置", "Settings"),
    "Chat_LoadingConversation": ("正在加载对话…", "Loading conversation…"),
    "Chat_UploadImageTooltip": ("上传图片（支持 Ctrl+V 粘贴）", "Upload image (Ctrl+V to paste)"),
    "Chat_SelectKnowledgeModule": ("选择知识空间", "Select knowledge space"),
    "Chat_SearchKnowledgePlaceholder": ("搜索知识空间...", "Search knowledge spaces..."),
    "Chat_PauseReplyTooltip": ("暂停当前回复", "Pause current reply"),
    "Chat_SendTooltip": ("发送（生成中将加入排队）", "Send (queues while generating)"),
    "Chat_ComposerHint": ("按 Enter 发送消息，Shift+Enter 换行", "Press Enter to send, Shift+Enter for a new line"),
    "Chat_ComposerPlaceholder": ("输入消息... (Shift+Enter 换行，Enter 发送)", "Type a message... (Shift+Enter for new line, Enter to send)"),
    "Chat_WelcomeTitle": ("开始新的对话", "Start a new conversation"),
    "Chat_WelcomeTitleWithName": ("你好，{0}", "Hello, {0}"),
    "Chat_WelcomeDescription": (
        "Athlon Agent 可以帮您分析代码、生成原型、优化设计，或执行任何开发任务。",
        "Athlon Agent can help you analyze code, build prototypes, refine designs, or handle development tasks.",
    ),
    "Chat_Copy": ("复制", "Copy"),
    "Chat_Copied": ("已复制", "Copied"),
    "Chat_Code": ("代码", "Code"),
    "Chat_Thinking": ("正在思考", "Thinking"),
    "Chat_ThinkingWithDuration": ("正在思考 ({0})", "Thinking ({0})"),
    "Chat_Thought": ("已思考", "Thought"),
    "Chat_ThoughtWithDuration": ("已思考 ({0})", "Thought ({0})"),
    "Chat_Seconds": ("{0}秒", "{0}s"),
    "Chat_RenderFailed": ("聊天渲染失败：{0}", "Chat render failed: {0}"),
    "Chat_RenderInitFailed": ("聊天渲染初始化失败：{0}", "Chat render initialization failed: {0}"),
    "Settings_Title": ("设置", "Settings"),
    "Settings_Subtitle": (
        "配置模型、MCP、技能、工作区、上下文压缩与工具权限。",
        "Configure model, MCP, skills, workspaces, context compaction, and tool permissions.",
    ),
    "Settings_UiSection": ("界面", "Interface"),
    "Settings_ShowToolCalls": ("显示工具调用", "Show tool calls"),
    "Settings_ShowToolCallsTooltip": (
        "关闭后聊天中不显示 file_read、execute_command 等工具卡片；上下文压缩通知仍会显示。",
        "When off, tool cards such as file_read and execute_command are hidden in chat; compaction notices still appear.",
    ),
    "Settings_UiMemoryHint": (
        "长期记忆与任务列表请在聊天输入区通过 Coding 开关按会话启用；任务进度显示在左侧栏。",
        "Enable long-term memory and task lists per session via the Coding toggle in the composer; task progress appears in the left sidebar.",
    ),
    "Settings_Language": ("语言", "Language"),
    "Settings_Language_Auto": ("跟随系统", "Follow system"),
    "Settings_Language_zh_CN": ("简体中文", "Chinese (Simplified)"),
    "Settings_Language_en_US": ("English (US)", "English (US)"),
    "Settings_SaveSettings": ("保存设置", "Save settings"),
    "Settings_ApiKeySaved": ("已保存 Model API Key：{0}", "Saved model API key: {0}"),
    "Knowledge_Title": ("知识库", "Knowledge"),
    "Knowledge_Modules": ("知识空间", "Knowledge spaces"),
    "Knowledge_NoModules": ("还没有知识空间", "No knowledge spaces yet"),
    "Knowledge_NoModulesHint": ("点击下方按钮创建第一个知识空间。", "Create your first knowledge space below."),
    "Knowledge_NewModule": ("新建知识空间", "New knowledge space"),
    "Knowledge_Name": ("名称", "Name"),
    "Knowledge_Description": ("描述", "Description"),
    "Knowledge_Documents": ("文档", "Documents"),
    "Knowledge_DropHint": ("将文件拖拽到下方区域可上传到当前知识空间。", "Drag files into the area below to upload to the current knowledge space."),
    "Knowledge_Loading": ("正在加载知识库…", "Loading knowledge base…"),
    "Knowledge_Reindex": ("重新索引", "Reindex"),
    "Knowledge_DocumentPreview": ("文档预览", "Document preview"),
    "Knowledge_SearchTest": ("检索测试", "Search test"),
    "Knowledge_TestSearch": ("测试检索", "Test search"),
    "Knowledge_TitleDialog": ("知识库", "Knowledge"),
    "Knowledge_ModuleNameRequired": ("知识空间名称不能为空。", "Knowledge space name cannot be empty."),
    "Knowledge_ModuleSaved": ("知识空间已保存：{0}", "Knowledge space saved: {0}"),
    "Knowledge_ModuleSaveFailed": ("保存知识空间失败：{0}", "Failed to save knowledge space: {0}"),
    "Knowledge_SelectModuleFirst": ("请先选择或创建一个知识空间。", "Select or create a knowledge space first."),
    "Knowledge_UploadDialogTitle": ("上传知识库文档", "Upload knowledge documents"),
    "Knowledge_UploadFilter": (
        "知识库文档|*.txt;*.md;*.pdf;*.docx;*.csv;*.xlsx;*.pptx|所有文件|*.*",
        "Knowledge documents|*.txt;*.md;*.pdf;*.docx;*.csv;*.xlsx;*.pptx|All files|*.*",
    ),
    "Knowledge_IndexingPrepare": ("准备索引...", "Preparing index..."),
    "Knowledge_IndexingFile": ("正在索引 {0} ...", "Indexing {0} ..."),
    "Knowledge_IndexFailedTitle": ("知识库索引失败", "Knowledge indexing failed"),
    "Knowledge_IndexFailedMessage": ("无法索引 {0}：{1}", "Could not index {0}: {1}"),
    "Knowledge_UploadAllSucceeded": ("上传处理完成，共 {0} 个文档。", "Upload finished. {0} document(s) processed."),
    "Knowledge_UploadPartial": ("上传完成：成功 {0} 个，失败 {1} 个。", "Upload finished: {0} succeeded, {1} failed."),
    "Knowledge_IndexComplete": ("索引完成", "Indexing complete"),
    "Knowledge_IndexPartialFailed": ("部分文档索引失败", "Some documents failed to index"),
    "Knowledge_DeleteModuleTitle": ("删除知识空间", "Delete knowledge space"),
    "Knowledge_DeleteModuleMessage": ('确定删除知识空间「{0}」及其全部文档和切片吗？', 'Delete knowledge space "{0}" and all its documents and chunks?'),
    "Knowledge_ModuleDeleted": ('已删除知识空间「{0}」。', 'Deleted knowledge space "{0}".'),
    "Knowledge_DeleteDocumentTitle": ("删除文档", "Delete document"),
    "Knowledge_DeleteDocumentMessage": ("确定删除文档「{0}」吗？", 'Delete document "{0}"?'),
    "Knowledge_DocumentDeleted": ('已删除文档「{0}」。', 'Deleted document "{0}".'),
    "Knowledge_ReindexPrepare": ("准备重新索引...", "Preparing reindex..."),
    "Knowledge_Reindexing": ("正在重新索引 {0} ...", "Reindexing {0} ..."),
    "Knowledge_ReindexDone": ("重新索引完成。", "Reindex complete."),
    "Knowledge_ReindexComplete": ("重新索引完成", "Reindex complete"),
    "Knowledge_ReindexFailed": ("重新索引失败：{0}", "Reindex failed: {0}"),
    "Knowledge_SearchQueryRequired": ("请输入检索问题。", "Enter a search query."),
    "Knowledge_SearchScopeRequired": ("请先在左侧选择一个知识空间或文档。", "Select a knowledge space or document on the left first."),
    "Knowledge_NoSearchHits": ("没有命中结果。检索范围：{0}。", "No results. Scope: {0}."),
    "Knowledge_SearchScopeLine": ("检索范围：{0}", "Scope: {0}"),
    "Knowledge_SelectDocumentPreview": ("选择一个文档查看抽取文本预览。", "Select a document to preview extracted text."),
    "Knowledge_NewModuleDefaultName": ("新知识空间 {0}", "New knowledge space {0}"),
    "Knowledge_ModuleCreated": ('已创建知识空间「{0}」。', 'Created knowledge space "{0}".'),
    "Knowledge_ScopeModule": ('知识空间「{0}」', 'Knowledge space "{0}"'),
    "Knowledge_ScopeDocument": ('文档「{0}」', 'Document "{0}"'),
    "Knowledge_IndexProgress": ("文件 {0}/{1} · {2}：{3}", "File {0}/{1} · {2}: {3}"),
    "Knowledge_DocumentStatus": ("文档状态：{0}", "Document status: {0}"),
    "Knowledge_DocumentStatusWithError": ("文档状态：{0}\n错误：{1}", "Document status: {0}\nError: {1}"),
    "Knowledge_ModuleMeta": ("{0} 个文档 · {1} 个切片", "{0} document(s) · {1} chunk(s)"),
    "Knowledge_DocumentMeta": ("{0} 个切片 · {1} · {2}", "{0} chunk(s) · {1} · {2}"),
    "Knowledge_DeleteModuleMenu": ("删除知识空间", "Delete knowledge space"),
    "Knowledge_DeleteDocumentMenu": ("删除文档", "Delete document"),
    "Knowledge_DefaultStatus": (
        "在聊天输入区开启知识库开关后，Agent 才会使用知识库检索来回答你的问题。",
        "Turn on the knowledge base toggle in the composer for the agent to use retrieval in answers.",
    ),
    "Shell_DeleteFailedStatus": ("删除失败：{0}", "Delete failed: {0}"),
    "Knowledge_ModuleNotSelected": ("未选择", "Not selected"),
    "Knowledge_SearchResultsHint": (
        "在左侧选择知识空间或文档后，输入问题可测试检索效果。",
        "Select a knowledge space or document on the left, then enter a query to test search.",
    ),
    "Schedule_Title": ("定时任务", "Scheduled tasks"),
    "Schedule_Subtitle": ("管理 AI Agent 定时执行的任务。", "Manage scheduled AI agent tasks."),
    "Schedule_NewTask": ("新建定时任务", "New scheduled task"),
    "Schedule_NoTasks": ("暂无定时任务", "No scheduled tasks yet"),
    "Schedule_NoTasksHint": (
        "点击上方「新建定时任务」按钮创建第一个任务，或告诉 AI Agent「每天 9 点提醒我」来自动创建。",
        'Click "New scheduled task" above, or ask the agent to create one (e.g. "remind me every day at 9").',
    ),
    "Schedule_ToggleAllTooltip": ("启用/禁用所有定时任务", "Enable or disable all scheduled tasks"),
    "Schedule_NextRun": ("下次: ", "Next: "),
    "Schedule_ExpandResultTooltip": ("展开或收起运行结果", "Expand or collapse run result"),
    "Schedule_RunNowTooltip": ("立即执行", "Run now"),
    "Schedule_StopTooltip": ("停止运行", "Stop"),
    "Schedule_EditTooltip": ("编辑", "Edit"),
    "Schedule_DeleteTooltip": ("删除", "Delete"),
    "Schedule_LastResult": ("上次运行结果", "Last run result"),
    "Schedule_ViewConversation": ("查看对话", "View conversation"),
    "Schedule_StartLabel": ("开始: ", "Start: "),
    "Schedule_EndLabel": ("结束: ", "End: "),
    "Schedule_EditTitle": ("编辑定时任务", "Edit scheduled task"),
    "Schedule_NewTitle": ("新建定时任务", "New scheduled task"),
    "Schedule_TaskName": ("任务名称", "Task name"),
    "Schedule_Workspace": ("工作目录", "Working directory"),
    "Schedule_WorkspaceRequired": ("必填。Agent 将在此目录下执行任务。", "Required. The agent runs tasks in this directory."),
    "Schedule_Prompt": ("Prompt / 指令", "Prompt / instruction"),
    "Schedule_Kind": ("调度类型", "Schedule type"),
    "Schedule_KindDaily": ("每天固定时间", "Daily at fixed time"),
    "Schedule_KindInterval": ("间隔执行", "Interval"),
    "Schedule_KindAt": ("一次性", "One-time"),
    "Schedule_KindManual": ("手动触发", "Manual"),
    "Schedule_TimeOfDay": ("执行时间", "Time of day"),
    "Schedule_IntervalMinutes": ("间隔时间（分钟）", "Interval (minutes)"),
    "Schedule_AtTime": ("执行时间点", "Run at"),
    "Schedule_TitleRequired": ("请输入任务名称。", "Enter a task name."),
    "Schedule_WorkspaceRequiredPrompt": ("请输入工作目录。", "Enter a working directory."),
    "Schedule_WorkspaceMissing": ("目录不存在：{0}\n仍要保存吗？", "Directory does not exist: {0}\nSave anyway?"),
    "Schedule_InvalidTime": ("请输入有效的时间格式，例如 09:00。", "Enter a valid time, e.g. 09:00."),
    "Schedule_InvalidInterval": ("请输入有效的间隔分钟数（正整数）。", "Enter a valid interval in minutes (positive integer)."),
    "Schedule_InvalidDateTime": ("请输入有效的日期时间格式，例如 2025-12-31 18:00。", "Enter a valid date/time, e.g. 2025-12-31 18:00."),
    "Schedule_DeleteTitle": ("删除定时任务", "Delete scheduled task"),
    "Schedule_DeleteMessage": ('确定要删除定时任务「{0}」吗？', 'Delete scheduled task "{0}"?'),
    "Schedule_StatusReady": ("就绪", "Ready"),
    "Schedule_StatusRunning": ("运行中", "Running"),
    "Schedule_StatusSuccess": ("成功", "Succeeded"),
    "Schedule_StatusError": ("失败", "Failed"),
    "Schedule_DescriptionDaily": ("每天 {0}", "Daily at {0}"),
    "Schedule_DescriptionAt": ("一次性 · {0}", "One-time · {0}"),
    "Schedule_DescriptionInterval": ("每隔 {0} 分钟", "Every {0} minute(s)"),
    "Schedule_DescriptionManual": ("手动触发", "Manual"),
    "Schedule_NoPrompt": ("(无 Prompt)", "(No prompt)"),
    "Schedule_FilterAll": ("全部", "All"),
    "Schedule_FilterEnabled": ("启用", "Enabled"),
    "Schedule_FilterRunning": ("运行中", "Running"),
    "Schedule_FilterCompleted": ("已完成", "Completed"),
    "Schedule_TaskSummary": ("共 {0} 个任务，{1} 个启用", "{0} task(s), {1} enabled"),
    "Schedule_NewTaskDefaultTitle": ("新建定时任务", "New scheduled task"),
    "Settings_SaveStatusBoth": (
        "已保存（{0}），Model 与 Embedding API Key 已更新",
        "Saved ({0}); model and embedding API keys updated",
    ),
    "Settings_SaveStatusModel": (
        "已保存（{0}），Model API Key 已更新",
        "Saved ({0}); model API key updated",
    ),
    "Settings_SaveStatusEmbedding": (
        "已保存（{0}），Embedding API Key 已更新",
        "Saved ({0}); embedding API key updated",
    ),
    "Settings_SaveStatusNoChange": (
        "已保存（{0}）；Model API Key 未变更（PasswordBox 为空时沿用已保存的 Key）",
        "Saved ({0}); model API key unchanged (empty field keeps the stored key)",
    ),
    "Settings_SkillsDescription": (
        "技能从 {0} 自动加载；此页面控制每个技能是否启用。关闭后不会出现在系统提示与 @skill 补全中。保存设置后写入 {1}。",
        "Skills load automatically from {0}; this page controls which are enabled. Disabled skills are omitted from the system prompt and @skill completion. Saved to {1}.",
    ),
    "Settings_DefaultStatus": (
        "设置保存在应用数据目录下的 JSON 文件中。",
        "Settings are stored as JSON files under the app data folder.",
    ),
    "Shell_SelectWorkspace": ("选择 Agent 工作区", "Select agent workspace"),
    "Editor_CannotOpenTitle": ("无法打开", "Cannot open"),
    "Editor_CannotOpenMessage": ("无法打开文件。", "Could not open the file."),
    "Editor_UnsavedTitle": ("未保存的更改", "Unsaved changes"),
    "Editor_UnsavedMessage": ("「{0}」有未保存的更改，是否保存？", '"{0}" has unsaved changes. Save?'),
    "Editor_SaveFailedTitle": ("保存失败", "Save failed"),
    "Editor_SaveFailedMessage": ("保存失败。", "Save failed."),
    "Preview_FailedTitle": ("预览失败", "Preview failed"),
    "Preview_HtmlFailedMessage": ("无法加载 HTML 预览：{0}", "Could not load HTML preview: {0}"),
    "Preview_MermaidNoBlock": ("未找到 ```mermaid 代码块。", "No ```mermaid code block found."),
    "Preview_MermaidMissingAssets": ("缺少离线 Mermaid 资源：{0}", "Offline Mermaid assets missing: {0}"),
    "Preview_MermaidFailedMessage": ("无法加载 Mermaid 预览：{0}", "Could not load Mermaid preview: {0}"),
    "Preview_MermaidTitle": ("Mermaid 图表预览", "Mermaid diagram preview"),
    "Preview_MermaidSubtitle": ("使用安装包内置 Mermaid 离线渲染，无需互联网", "Rendered offline with bundled Mermaid; no internet required"),
    "Update_AvailableMessage": ("发现新版本 {0}，是否现在下载并安装？", "Version {0} is available. Download and install now?"),
    "Update_AvailableTitle": ("发现更新", "Update available"),
    "Update_UpToDate": ("当前已是最新版本。", "You are on the latest version."),
    "Update_Cancelled": ("发现新版本 {0}，已取消安装。", "Version {0} found. Installation cancelled."),
    "Update_Applying": ("正在安装更新并重启…", "Installing update and restarting…"),
    "Notification_TaskCompleteTitle": ("任务完成", "Task complete"),
    "Notification_TaskPlanCompleteTitle": ("任务计划已完成", "Task plan completed"),
    "Preview_HtmlTitle": ("HTML 预览", "HTML preview"),
    "Preview_HtmlSubtitle": (
        "已在沙箱环境中渲染，不会影响当前页面",
        "Rendered in a sandbox environment; does not affect the current page",
    ),
    "Schedule_ShowResult": ("显示运行结果", "Show run result"),
    "Schedule_HideResult": ("收起结果", "Hide result"),
    "Context_OpenInEditor": ("在编辑器中打开", "Open in editor"),
    "Context_McpServers": ("MCP 服务器", "MCP servers"),
    "Knowledge_DropReleaseHint": (
        "松开以上传文件到当前知识空间",
        "Release to upload files to the current knowledge space",
    ),
    "Settings_ModelSection": ("Model", "Model"),
    "Settings_Provider": ("Provider", "Provider"),
    "Settings_Endpoint": ("Endpoint", "Endpoint"),
    "Settings_ModelNameLabel": ("Model Name", "Model Name"),
    "Settings_MaxTokensLabel": ("Max Tokens", "Max Tokens"),
    "Settings_MaxTokensHint": (
        "单次回复最大输出 token（留空则使用 API 默认值）。仅影响对话补全，不影响上下文摘要。",
        "Maximum output tokens per reply (leave empty for API default). Affects completions only, not context summarization.",
    ),
    "Settings_EnableStreaming": ("启用流式输出（SSE）", "Enable streaming output (SSE)"),
    "Settings_ApiKeyLabel": ("API Key", "API Key"),
    "Settings_ApiKeyDpapiHint": (
        "API Key 使用 Windows DPAPI 加密保存在用户目录。PasswordBox 不会显示已保存的 Key；更换 Provider 后请重新粘贴并点「保存设置」。",
        "API keys are encrypted with Windows DPAPI in your user profile. The password box does not show saved keys; paste again after changing provider and click Save settings.",
    ),
    "Settings_KnowledgeEmbeddingTitle": ("知识库 Embedding", "Knowledge embedding"),
    "Settings_KnowledgeEmbeddingHint": (
        "配置向量检索所需的基础设施。在聊天输入区为每个会话单独开启知识库并选择知识空间。",
        "Configure infrastructure for vector retrieval. Enable the knowledge base per session in the composer and choose knowledge spaces.",
    ),
    "Settings_EmbeddingEndpoint": ("Embedding Endpoint", "Embedding Endpoint"),
    "Settings_EmbeddingModel": ("Embedding Model", "Embedding Model"),
    "Settings_VectorDimension": ("向量维度", "Vector dimension"),
    "Settings_BatchSize": ("Batch Size", "Batch Size"),
    "Settings_ChunkTargetChars": ("切片目标字符", "Chunk target characters"),
    "Settings_ChunkTargetCharsHint": ("按固定字符数硬切，与段落无关", "Hard split by character count, regardless of paragraphs"),
    "Settings_ChunkOverlapChars": ("切片重叠字符", "Chunk overlap characters"),
    "Settings_ChunkOverlapHint": ("相邻切片重叠的字符数", "Overlapping characters between adjacent chunks"),
    "Settings_TopK": ("TopK", "TopK"),
    "Settings_MinScore": ("Min Score", "Min Score"),
    "Settings_EmbeddingApiKey": ("Embedding API Key", "Embedding API Key"),
    "Settings_EmbeddingApiKeySaved": ("已保存知识库 Embedding API Key：{0}", "Saved knowledge embedding API key: {0}"),
    "Settings_ContextCompactionTitle": ("上下文与 Token ROI", "Context & token ROI"),
    "Settings_ProactiveCompaction": ("主动压缩（会话持久化层）", "Proactive compaction (session persistence)"),
    "Settings_ProactiveCompactionTooltip": ("启用 truncate / compact 等会话级压缩", "Enable session-level truncate/compact compaction"),
    "Settings_DynamicCompaction": ("动态压缩（预算利用率）", "Dynamic compaction (budget utilization)"),
    "Settings_DynamicCompactionTooltip": ("启用动态三级压缩策略", "Enable dynamic three-tier compaction"),
    "Settings_RequestHygiene": ("发送边界卫生（每轮 API 前）", "Request boundary hygiene (before each API call)"),
    "Settings_ToolStorm": ("Tool storm 抑制", "Tool storm suppression"),
    "Settings_StormThreshold": ("Storm 阈值", "Storm threshold"),
    "Settings_McpSearchTitle": ("MCP Search Mode", "MCP Search Mode"),
    "Settings_McpSearchEnabled": ("启用 MCP 按需工具发现（search 模式）", "Enable MCP on-demand tool discovery (search mode)"),
    "Settings_McpSearchMode": ("模式 (direct/search/auto)", "Mode (direct/search/auto)"),
    "Settings_McpAutoThresholdTools": ("Auto 阈值 (工具数)", "Auto threshold (tool count)"),
    "Settings_McpAutoThresholdChars": ("Auto 阈值 (schema 字符)", "Auto threshold (schema characters)"),
    "Settings_McpServersTitle": ("Installed MCP Servers", "Installed MCP servers"),
    "Settings_McpServersHint": (
        "MCP 服务从 {0} 读取；此页面仅控制每个服务是否启用。新增、删除和命令参数请直接修改 JSON 配置。",
        "MCP servers are loaded from {0}; this page only toggles enablement. Add, remove, and command args in the JSON config.",
    ),
    "Settings_SkillsTitle": ("Installed Skills", "Installed skills"),
    "Settings_TrainingDataTitle": ("训练数据采集", "Training data collection"),
    "Settings_TrainingDataDescription": (
        "自动从修正轨迹、溢出恢复轨迹、工具选型 DPO 中提取训练数据，写入 ~/.athlon-agent/training-data/。仅当 agent 调用工具失败→用户修正→重试成功、或超时→继续→完成时产出样本，不影响正常使用。",
        "Automatically extracts training data from correction trajectories, overflow recovery, and tool-selection DPO into ~/.athlon-agent/training-data/. Samples are produced only when tool failure→user fix→retry succeeds, or timeout→continue→complete; normal use is unaffected.",
    ),
    "Settings_TrainingDataTooltip": ("启用训练数据采集", "Enable training data collection"),
    "Harness_EnabledTooltip": ("Coding 已启用 · 长期记忆 + 任务列表", "Coding on · long-term memory + task list"),
    "Harness_DisabledTooltip": ("启用 Coding（长期记忆与 todo_write）", "Enable Coding (long-term memory and todo_write)"),
    "Harness_PickerOn": ("Coding 开", "Coding on"),
    "Harness_PickerOff": ("Coding", "Coding"),
    "Harness_TaskInProgress": ("进行中", "In progress"),
    "Harness_TaskCompleted": ("已完成", "Completed"),
    "Harness_TaskCancelled": ("已取消", "Cancelled"),
    "Harness_TaskPending": ("待办", "Pending"),
    "Chat_RoleUser": ("您", "You"),
    "Chat_RoleTool": ("工具", "Tool"),
    "Chat_RoleContext": ("上下文", "Context"),
    "Chat_RoleAssistant": ("Athlon 助手", "Athlon"),
    "Chat_ImageAttachmentCount": ("已附图片 {0} 张", "{0} image(s) attached"),
    "Chat_ToolCallTitle": ("工具调用", "Tool call"),
    "Chat_CompactionDefault": ("上下文压缩", "Context compaction"),
    "Chat_ToolStatusPreparing": ("准备中…", "Preparing…"),
    "Chat_ToolStatusRunning": ("执行中…", "Running…"),
    "Chat_ToolStatusSucceeded": ("成功", "Succeeded"),
    "Chat_ToolStatusFailed": ("失败", "Failed"),
    "Chat_ToolStatusCancelled": ("已停止", "Stopped"),
    "Chat_ToolStopped": ("已停止", "Stopped"),
    "Chat_ToolStoppedContent": ("（已停止）", "(stopped)"),
    "Chat_CompactionNotice": (
        "以下记录仍完整保留；模型上下文已压缩，新消息基于摘要 + 最近历史。",
        "Records below are kept in full; model context was compacted. New messages use the summary plus recent history.",
    ),
    "Shutdown_StoppingScheduler": ("正在停止定时任务…", "Stopping scheduled tasks…"),
    "Shutdown_StoppingTurns": ("正在停止生成任务…", "Stopping generation…"),
    "Shutdown_FlushingToolLogs": ("正在刷新工具调用日志…", "Flushing tool call logs…"),
    "Shutdown_KillingProcesses": ("正在结束命令行进程…", "Ending shell processes…"),
    "Shutdown_SavingSettings": ("正在保存设置…", "Saving settings…"),
    "Shutdown_ClosingMcp": ("正在关闭 MCP 连接…", "Closing MCP connections…"),
    "Shutdown_ReleasingLogs": ("正在释放日志…", "Releasing logs…"),
    "FlowDoc_CopyNotice": ("已复制", "Copied"),
    "Markdown_PreviewHtml": ("预览 HTML", "Preview HTML"),
    "Markdown_PreviewMermaid": ("查看 Mermaid 图表", "View Mermaid diagram"),
    "Markdown_CopyContent": ("复制内容", "Copy content"),
    "Markdown_CopyCode": ("复制代码块", "Copy code block"),
    "Markdown_ContentCopied": ("内容已复制到剪贴板", "Content copied to clipboard"),
    "Markdown_PreviewButton": ("预览", "Preview"),
    "ComposerKnowledge_ConfigRequired": ("请先在设置页配置 Embedding Endpoint、Model 和 API Key", "Configure embedding endpoint, model, and API key in Settings first"),
    "ComposerKnowledge_Enabled": ("知识库已启用 · {0} 个知识空间", "Knowledge on · {0} space(s)"),
    "ComposerKnowledge_SelectModules": ("点击选择知识空间", "Click to select knowledge spaces"),
    "ComposerKnowledge_PickerSelect": ("选择知识空间", "Select knowledge spaces"),
    "ComposerKnowledge_PickerLabel": ("知识库 · {0}", "Knowledge · {0}"),
    "ComposerKnowledge_ModuleMeta": ("{0} 个文档 · {1} 个切片", "{0} document(s) · {1} chunk(s)"),
    "Tool_DefaultHeader": ("工具调用", "Tool call"),
    "Tool_EvictedHeader": ("① 工具结果归档 · {0}", "① Tool result archived · {0}"),
    "Tool_NoArgs": ("(无参数)", "(no arguments)"),
    "FileWrite_StreamingContent": ("content = 传输中…", "content = streaming…"),
    "Chat_SelectImages": ("选择图片", "Select images"),
    "Chat_QueuedStatus": ("已加入排队", "Added to queue"),
}


def write_resx(path: Path, lang_index: int) -> None:
    lines = [RESX_HEADER]
    for key in sorted(ENTRIES.keys()):
        value = ENTRIES[key][lang_index].replace("&", "&amp;").replace("<", "&lt;")
        lines.append(f'  <data name="{key}" xml:space="preserve">')
        lines.append(f"    <value>{value}</value>")
        lines.append("  </data>")
    lines.append("</root>")
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")


def main() -> None:
    base = Path(__file__).resolve().parents[1] / "src" / "Athlon.Agent.App" / "Resources"
    write_resx(base / "Strings.resx", 0)
    write_resx(base / "Strings.en-US.resx", 1)
    print(f"Wrote {len(ENTRIES)} keys to {base}")


if __name__ == "__main__":
    main()
