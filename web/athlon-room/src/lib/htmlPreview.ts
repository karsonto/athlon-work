/** Wrap fragment HTML for sandboxed iframe preview (same approach as report/html). */
export function buildPreviewDocument(rawHtml: string): string {
  if (/<html[\s>]|<!doctype/i.test(rawHtml)) {
    return rawHtml;
  }

  return `<!doctype html>
<html lang="zh-CN">
  <head>
    <meta charset="UTF-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <title>HTML 预览</title>
    <style>
      body {
        margin: 0;
        min-height: 100vh;
        background: #ffffff;
      }
    </style>
  </head>
  <body>
${rawHtml}
  </body>
</html>`;
}

export function isHtmlAttachment(fileName: string, mimeType?: string | null): boolean {
  const name = fileName.trim().toLowerCase();
  if (name.endsWith(".html") || name.endsWith(".htm")) {
    return true;
  }
  const mime = (mimeType ?? "").toLowerCase();
  return mime === "text/html" || mime === "application/xhtml+xml";
}
