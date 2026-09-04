# Chat timeline assets

WebView chat timeline uses a virtual list (`@tanstack/virtual-core`).

## Build

After editing `chat-timeline.entry.js`, `timeline-*.js`, or `timeline-dom.js`:

```bash
cd src/Athlon.Agent.App/Assets/Chat
npm install
npm run build
```

Commit the regenerated `chat-timeline.bundle.js` (loaded by `ChatHtmlBuilder`).

CI (`.github/workflows/ci.yml`) and Release (`.github/workflows/release.yml`) also run this build. CI fails if the committed bundle does not match `npm run build` output.

## Tests

```bash
npm test
```

Store unit tests cover upsert, prepend, files reorder, and tool event accumulation.
