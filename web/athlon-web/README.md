# Athlon Web

桌面端 AthlonWeb SPA（React + Tailwind），源码自 `report/html` 迁入。

## 功能

- AI 聊天与流式回复
- Markdown 渲染与代码高亮
- 多文件拖拽 / 选择上传
- 本地历史记录与设置持久化
- 全屏切换

## Dev

```bash
npm install --cache ./.npm-cache
npm run dev
```

默认开发端口：`5178`。

## Build into desktop assets

```bash
npm run build
```

Output: `src/Athlon.Agent.App/Assets/AthlonWeb`（由 `AthlonWebStaticServer` 托管）。

## 环境变量

复制 `.env.example` 后按需调整：

```bash
cp .env.example .env
```

- `VITE_API_URL`: 聊天接口地址
- `VITE_API_KEY`: OpenAI 兼容接口的 API Key，默认留空
- `VITE_DEFAULT_MODEL`: 默认模型名称
- `VITE_MAX_TOKENS`: 默认 max tokens
- `VITE_VLLM_METRICS_PROXY_TARGET`: 可选，dev 时 `/__vllm_metrics` 代理目标
