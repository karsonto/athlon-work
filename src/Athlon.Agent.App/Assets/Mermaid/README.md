# Bundled Mermaid (offline)

- **Version**: 11.4.0 (`mermaid.min.js`)
- **Used by**: `MermaidPreviewWindow` via WebView2 virtual host `athlon.assets`
- **No CDN**: chat diagram preview works without internet

To refresh:

```bash
curl -fsSL "https://cdn.jsdelivr.net/npm/mermaid@11.4.0/dist/mermaid.min.js" \
  -o src/Athlon.Agent.App/Assets/Mermaid/mermaid.min.js
```
