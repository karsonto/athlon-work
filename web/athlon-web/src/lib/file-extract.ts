import type { AttachmentRole, DocumentParseMode } from "../types";
import pdfWorkerUrl from "pdfjs-dist/legacy/build/pdf.worker.min.mjs?url";

const MAX_ATTACHMENT_CHARACTERS = 80_000;
const MAX_ATTACHMENT_FILE_SIZE = 12 * 1024 * 1024;
const MAX_PDF_PAGES = 100;
const MAX_PPTX_SLIDES = 200;
const MAX_SMART_VISUAL_ATTACHMENTS_PER_DOCUMENT = 3;
const MAX_ALL_VISUAL_ATTACHMENTS_PER_DOCUMENT = 12;
const MAX_PREVIEW_DIMENSION = 1400;
const PDF_LOW_TEXT_THRESHOLD = 80;
const PPTX_LOW_TEXT_THRESHOLD = 100;
const EMPTY_TEXT_NOTICE = "[未提取到可读文本]";
const TRUNCATED_TEXT_NOTICE = "[内容过长，已截断后发送给模型]";

export interface ExtractedAttachment {
  kind: "text" | "image";
  content: string;
  name: string;
  size: number;
  type: string;
  sourceFileName?: string;
  sourcePage?: number;
  role?: AttachmentRole;
  summary?: string;
}

const PDF_EXTENSIONS = new Set(["pdf"]);
const DOCX_EXTENSIONS = new Set(["docx"]);
const WORKBOOK_EXTENSIONS = new Set(["xlsx", "xls", "csv"]);
const PRESENTATION_EXTENSIONS = new Set(["pptx"]);
const LEGACY_PRESENTATION_EXTENSIONS = new Set(["ppt"]);

/** 与 extractAttachments 中文档分支（非图片、非纯 readPlainText 兜底）的扩展名一致，用于 UI 是否展示解析模式按钮 */
const DOCUMENT_PARSE_MODE_RELEVANT_EXTENSIONS = new Set<string>([
  ...PDF_EXTENSIONS,
  ...DOCX_EXTENSIONS,
  ...WORKBOOK_EXTENSIONS,
  ...PRESENTATION_EXTENSIONS,
  ...LEGACY_PRESENTATION_EXTENSIONS,
]);

let pdfWorkerConfigured = false;

function getFileExtension(fileName: string) {
  const extension = fileName.split(".").pop();
  return extension ? extension.toLowerCase() : "";
}

export function isDocumentParseModeRelevantFileName(fileName: string): boolean {
  const extension = getFileExtension(fileName);
  return extension !== "" && DOCUMENT_PARSE_MODE_RELEVANT_EXTENSIONS.has(extension);
}

export function isLegacyPresentationFileName(fileName: string): boolean {
  return LEGACY_PRESENTATION_EXTENSIONS.has(getFileExtension(fileName));
}

function getBaseName(fileName: string) {
  return fileName.replace(/\.[^.]+$/, "");
}

function getModeLabel(mode: DocumentParseMode) {
  switch (mode) {
    case "text_only":
      return "纯文本";
    case "smart_visual":
      return "智能补图";
    case "all_visual":
      return "全部页图";
    default:
      return "智能补图";
  }
}

export function isImageFile(file: File) {
  return file.type.startsWith("image/") || /\.(png|jpe?g|gif|webp)$/i.test(file.name);
}

function ensureFileSizeWithinLimit(file: File) {
  if (file.size <= MAX_ATTACHMENT_FILE_SIZE) {
    return;
  }

  throw new Error(
    `文件大小超过 ${(MAX_ATTACHMENT_FILE_SIZE / 1024 / 1024).toFixed(0)} MB 限制，请拆分后重试`,
  );
}

function readAsDataUrl(file: File) {
  return new Promise<string>((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(String(reader.result ?? ""));
    reader.onerror = () => reject(reader.error ?? new Error(`读取文件 "${file.name}" 失败`));
    reader.readAsDataURL(file);
  });
}

function estimateDataUrlSize(dataUrl: string) {
  const payload = dataUrl.split(",")[1] ?? "";
  return Math.ceil(payload.length * 0.75);
}

function normalizeExtractedText(text: string) {
  const normalized = text.replace(/\u0000/g, "").trim();
  if (!normalized) {
    return EMPTY_TEXT_NOTICE;
  }

  if (normalized.length <= MAX_ATTACHMENT_CHARACTERS) {
    return normalized;
  }

  return [
    normalized.slice(0, MAX_ATTACHMENT_CHARACTERS),
    "",
    `${TRUNCATED_TEXT_NOTICE} 原始长度约 ${normalized.length.toLocaleString()} 个字符。`,
  ].join("\n");
}

function appendTextNotice(content: string, notice?: string) {
  if (!notice) {
    return content;
  }
  return `${content}\n\n${notice}`;
}

function createTextAttachment(
  file: File,
  content: string,
  summary: string,
  overrides?: Partial<ExtractedAttachment>,
): ExtractedAttachment {
  return {
    kind: "text",
    content: normalizeExtractedText(content),
    name: file.name,
    size: file.size,
    type: file.type || "text/plain",
    sourceFileName: file.name,
    role: "document_text",
    summary,
    ...overrides,
  };
}

function createImageAttachment(
  file: File,
  options: {
    content: string;
    name: string;
    type?: string;
    sourcePage?: number;
    role?: AttachmentRole;
    summary?: string;
  },
): ExtractedAttachment {
  return {
    kind: "image",
    content: options.content,
    name: options.name,
    size: estimateDataUrlSize(options.content),
    type: options.type ?? "image/jpeg",
    sourceFileName: file.name,
    sourcePage: options.sourcePage,
    role: options.role ?? "page_preview",
    summary: options.summary,
  };
}

function buildTruncationNotice(totalCount: number, attachedCount: number, label: string) {
  if (totalCount <= attachedCount) {
    return "";
  }
  return `[${label}较多，仅附带前 ${attachedCount} 项，共 ${totalCount} 项]`;
}

async function readPlainText(file: File) {
  ensureFileSizeWithinLimit(file);
  const buffer = await file.arrayBuffer();
  const encodingCandidates = ["utf-8", "gb18030", "gbk", "gb2312"];

  for (const encoding of encodingCandidates) {
    try {
      const decoder = new TextDecoder(encoding, { fatal: true });
      const content = decoder.decode(buffer);
      if (content.trim()) {
        return normalizeExtractedText(content);
      }
    } catch {
      continue;
    }
  }

  return normalizeExtractedText(new TextDecoder().decode(buffer));
}

function mapPdfExtractionError(error: unknown) {
  if (!(error instanceof Error)) {
    return "PDF 解析失败，请检查文件是否损坏";
  }

  const details = `${error.name} ${error.message}`.toLowerCase();
  if (
    details.includes("tohex is not a function") ||
    (details.includes("fingerprint") && details.includes("tohex"))
  ) {
    return "当前浏览器环境与 PDF 解析器兼容性不足，请刷新页面后重试；若仍失败，请切换浏览器";
  }

  if (details.includes("password")) {
    return "PDF 已加密或需要密码，暂不支持解析";
  }

  if (details.includes("invalidpdf")) {
    return "PDF 文件格式无效或已损坏";
  }

  if (details.includes("missingpdf")) {
    return "PDF 文件内容不完整，无法解析";
  }

  if (details.includes("unexpectedresponse")) {
    return "PDF 读取异常，请重试或更换文件";
  }

  return `PDF 解析失败: ${error.message}`;
}

function extractTextFromPdfItems(items: Array<{ str?: string; transform?: number[]; hasEOL?: boolean }>) {
  const lines: string[] = [];
  let currentLine: string[] = [];
  let previousY: number | null = null;

  for (const item of items) {
    const text = item.str?.trim();
    if (!text) {
      if (item.hasEOL && currentLine.length > 0) {
        lines.push(currentLine.join(" ").trim());
        currentLine = [];
        previousY = null;
      }
      continue;
    }

    const y = typeof item.transform?.[5] === "number" ? item.transform[5] : previousY;
    const movedToNewLine =
      previousY !== null &&
      y !== null &&
      Math.abs(previousY - y) > 2.5;

    if (movedToNewLine && currentLine.length > 0) {
      lines.push(currentLine.join(" ").trim());
      currentLine = [];
    }

    currentLine.push(text);
    previousY = y ?? previousY;

    if (item.hasEOL) {
      lines.push(currentLine.join(" ").trim());
      currentLine = [];
      previousY = null;
    }
  }

  if (currentLine.length > 0) {
    lines.push(currentLine.join(" ").trim());
  }

  return lines.filter(Boolean).join("\n");
}

function pickKeyPageNumbers(textLengths: number[], lowTextThreshold: number, limit: number) {
  const lowTextPages = textLengths
    .map((length, index) => ({ length, pageNumber: index + 1 }))
    .filter((entry) => entry.length <= lowTextThreshold)
    .sort((left, right) => left.length - right.length)
    .slice(0, limit)
    .map((entry) => entry.pageNumber);

  if (lowTextPages.length > 0) {
    return lowTextPages;
  }

  const totalTextLength = textLengths.reduce((sum, length) => sum + length, 0);
  if (totalTextLength <= lowTextThreshold * textLengths.length) {
    return textLengths
      .map((_, index) => index + 1)
      .slice(0, limit);
  }

  return [];
}

async function renderPdfPagePreview(
  page: Awaited<ReturnType<(typeof import("pdfjs-dist"))["getDocument"]>> extends never ? never : any,
) {
  const baseViewport = page.getViewport({ scale: 1 });
  const scale = Math.min(1.5, MAX_PREVIEW_DIMENSION / Math.max(baseViewport.width, baseViewport.height));
  const viewport = page.getViewport({ scale: Math.max(scale, 0.8) });
  const canvas = document.createElement("canvas");
  const context = canvas.getContext("2d");
  if (!context) {
    throw new Error("无法创建 PDF 预览画布");
  }

  canvas.width = Math.floor(viewport.width);
  canvas.height = Math.floor(viewport.height);
  await page.render({
    canvasContext: context,
    viewport,
  }).promise;

  return canvas.toDataURL("image/jpeg", 0.82);
}

async function collectPdfDocumentData(file: File) {
  ensureFileSizeWithinLimit(file);
  const pdfjs = await import("pdfjs-dist/legacy/build/pdf.mjs");
  if (!pdfWorkerConfigured) {
    pdfjs.GlobalWorkerOptions.workerSrc = pdfWorkerUrl;
    pdfWorkerConfigured = true;
  }

  const loadingTask = pdfjs.getDocument({
    data: await file.arrayBuffer(),
  });
  const pdfDocument = await loadingTask.promise;
  const pageLimit = Math.min(pdfDocument.numPages, MAX_PDF_PAGES);
  const pageContents: string[] = [];
  const pageTextLengths: number[] = [];

  for (let pageNumber = 1; pageNumber <= pageLimit; pageNumber += 1) {
    const page = await pdfDocument.getPage(pageNumber);
    const textContent = await page.getTextContent();
    const pageText = extractTextFromPdfItems(
      textContent.items as Array<{ str?: string; transform?: number[]; hasEOL?: boolean }>,
    ).trim();

    pageTextLengths.push(pageText.length);
    if (pageText) {
      pageContents.push(`## 第 ${pageNumber} 页\n${pageText}`);
    }
  }

  const textBody = pageContents.length > 0
    ? `${pageContents.join("\n\n")}${pdfDocument.numPages > MAX_PDF_PAGES
        ? `\n\n[PDF 页数过多，仅提取前 ${MAX_PDF_PAGES} 页，共 ${pdfDocument.numPages} 页]`
        : ""}`
    : "[未提取到可读文本，可能是扫描版 PDF、图片型 PDF，或文档受保护]";

  return {
    pdfDocument,
    textBody,
    pageTextLengths,
  };
}

async function extractPdfTextOnly(file: File): Promise<ExtractedAttachment[]> {
  try {
    const { textBody } = await collectPdfDocumentData(file);
    return [
      createTextAttachment(file, textBody, `正文已提取（${getModeLabel("text_only")}）`),
    ];
  } catch (error) {
    throw new Error(mapPdfExtractionError(error));
  }
}

async function extractPdfVisualAttachments(
  file: File,
  mode: DocumentParseMode,
): Promise<ExtractedAttachment[]> {
  try {
    const { pdfDocument, textBody, pageTextLengths } = await collectPdfDocumentData(file);
    const limit = mode === "all_visual"
      ? Math.min(pdfDocument.numPages, MAX_ALL_VISUAL_ATTACHMENTS_PER_DOCUMENT)
      : MAX_SMART_VISUAL_ATTACHMENTS_PER_DOCUMENT;
    const previewPages = mode === "all_visual"
      ? Array.from({ length: limit }, (_, index) => index + 1)
      : pickKeyPageNumbers(pageTextLengths, PDF_LOW_TEXT_THRESHOLD, limit);
    const attachments: ExtractedAttachment[] = [];
    const allVisualNotice = mode === "all_visual"
      ? buildTruncationNotice(pdfDocument.numPages, previewPages.length, "页面预览")
      : undefined;

    attachments.push(
      createTextAttachment(
        file,
        appendTextNotice(textBody, allVisualNotice),
        previewPages.length > 0
          ? `正文已提取，并补充了${mode === "all_visual" ? "全部页图模式" : "关键页图片"}`
          : `正文已提取（${getModeLabel(mode)}）`,
      ),
    );

    for (const pageNumber of previewPages) {
      const page = await pdfDocument.getPage(pageNumber);
      const content = await renderPdfPagePreview(page);
      const isScanLike = (pageTextLengths[pageNumber - 1] ?? 0) === 0;
      attachments.push(
        createImageAttachment(file, {
          content,
          name: `${getBaseName(file.name)}-第${pageNumber}页预览.jpg`,
          sourcePage: pageNumber,
          role: "page_preview",
          summary: mode === "all_visual"
            ? `第 ${pageNumber} 页页面预览`
            : isScanLike ? `第 ${pageNumber} 页扫描页补图` : `第 ${pageNumber} 页关键页截图`,
        }),
      );
    }

    return attachments;
  } catch (error) {
    throw new Error(mapPdfExtractionError(error));
  }
}

function sortPathsByNumericSuffix(paths: string[], pattern: RegExp) {
  return [...paths].sort((left, right) => {
    const leftIndex = Number(left.match(pattern)?.[1] ?? 0);
    const rightIndex = Number(right.match(pattern)?.[1] ?? 0);
    return leftIndex - rightIndex;
  });
}

async function extractDocxTextOnly(file: File, notice?: string): Promise<ExtractedAttachment[]> {
  ensureFileSizeWithinLimit(file);
  const mammoth = await import("mammoth");
  const result = await mammoth.extractRawText({
    arrayBuffer: await file.arrayBuffer(),
  });

  const warningText = result.messages.length > 0
    ? `\n\n[Mammoth 提示: ${result.messages.map((message) => message.message).join("；")}]`
    : "";

  return [
    createTextAttachment(file, appendTextNotice(`${result.value}${warningText}`, notice), "正文已提取"),
  ];
}

async function extractWorkbookAttachments(file: File, mode: DocumentParseMode): Promise<ExtractedAttachment[]> {
  ensureFileSizeWithinLimit(file);
  const XLSX = await import("xlsx");
  const workbook = XLSX.read(await file.arrayBuffer(), {
    type: "array",
    cellDates: true,
  });
  const sheetSections: string[] = [];

  for (const sheetName of workbook.SheetNames) {
    const sheet = workbook.Sheets[sheetName];
    if (!sheet) {
      continue;
    }

    const rows = XLSX.utils.sheet_to_json<Array<string | number | boolean | null>>(sheet, {
      header: 1,
      blankrows: false,
      defval: "",
      raw: false,
    });

    const content = rows
      .map((row) => row.map((cell) => String(cell ?? "")).join("\t").trimEnd())
      .filter((line) => line.length > 0)
      .join("\n");

    if (content) {
      sheetSections.push(`## 工作表: ${sheetName}\n${content}`);
    }
  }

  return [
    createTextAttachment(file, sheetSections.join("\n\n"), `表格内容已提取（${getModeLabel(mode)}）`),
  ];
}

async function extractDocxImageAttachments(file: File, limit: number, summaryLabel: string) {
  const { default: JSZip } = await import("jszip");
  const zip = await JSZip.loadAsync(await file.arrayBuffer());
  const mediaPaths = sortPathsByNumericSuffix(
    Object.keys(zip.files).filter((path) => /^word\/media\/.+$/i.test(path)),
    /(\d+)(?!.*\d)/,
  );

  const limitedPaths = mediaPaths.slice(0, limit);
  const attachments: ExtractedAttachment[] = [];
  for (let index = 0; index < limitedPaths.length; index += 1) {
    const mediaPath = limitedPaths[index];
    const mediaFile = zip.files[mediaPath];
    if (!mediaFile) {
      continue;
    }

    const buffer = await mediaFile.async("arraybuffer");
    const mimeType = guessMimeType(mediaPath);
    const content = `data:${mimeType};base64,${arrayBufferToBase64(buffer)}`;
    attachments.push(
      createImageAttachment(file, {
        content,
        name: `${getBaseName(file.name)}-插图${index + 1}.${getFileExtension(mediaPath) || "img"}`,
        type: mimeType,
        role: "embedded_image",
        summary: `${summaryLabel}${index + 1}`,
      }),
    );
  }

  return {
    totalCount: mediaPaths.length,
    attachments,
  };
}

function collectPptTextNodes(value: unknown): string[] {
  if (typeof value === "string") {
    const text = value.trim();
    return text ? [text] : [];
  }

  if (Array.isArray(value)) {
    return value.flatMap((item) => collectPptTextNodes(item));
  }

  if (!value || typeof value !== "object") {
    return [];
  }

  const node = value as Record<string, unknown>;
  const currentText = typeof node["a:t"] === "string" ? String(node["a:t"]).trim() : "";
  const nested = Object.values(node).flatMap((item) => collectPptTextNodes(item));
  return currentText ? [currentText, ...nested] : nested;
}

function collectEmbedIds(value: unknown): string[] {
  if (Array.isArray(value)) {
    return value.flatMap((item) => collectEmbedIds(item));
  }

  if (!value || typeof value !== "object") {
    return [];
  }

  const node = value as Record<string, unknown>;
  const embed = typeof node["@_r:embed"] === "string" ? [node["@_r:embed"]] : [];
  return embed.concat(Object.values(node).flatMap((item) => collectEmbedIds(item)));
}

function collectPptImageRefs(value: unknown): string[] {
  if (Array.isArray(value)) {
    return value.flatMap((item) => collectPptImageRefs(item));
  }

  if (!value || typeof value !== "object") {
    return [];
  }

  const node = value as Record<string, unknown>;
  const fromBlip = node["a:blip"] ? collectEmbedIds(node["a:blip"]) : [];
  return fromBlip.concat(Object.values(node).flatMap((item) => collectPptImageRefs(item)));
}

function arrayBufferToBase64(buffer: ArrayBuffer) {
  const bytes = new Uint8Array(buffer);
  let binary = "";
  const chunkSize = 0x8000;
  for (let index = 0; index < bytes.length; index += chunkSize) {
    const chunk = bytes.subarray(index, index + chunkSize);
    binary += String.fromCharCode(...chunk);
  }
  return btoa(binary);
}

function resolveZipPath(baseFilePath: string, relativePath: string) {
  const baseParts = baseFilePath.split("/");
  baseParts.pop();
  for (const part of relativePath.split("/")) {
    if (!part || part === ".") {
      continue;
    }
    if (part === "..") {
      baseParts.pop();
      continue;
    }
    baseParts.push(part);
  }
  return baseParts.join("/");
}

function guessMimeType(filePath: string) {
  const extension = getFileExtension(filePath);
  if (extension === "png") {
    return "image/png";
  }
  if (extension === "jpg" || extension === "jpeg") {
    return "image/jpeg";
  }
  if (extension === "gif") {
    return "image/gif";
  }
  if (extension === "webp") {
    return "image/webp";
  }
  if (extension === "svg") {
    return "image/svg+xml";
  }
  return "application/octet-stream";
}

interface PptSlideInfo {
  slideNumber: number;
  slidePath: string;
  text: string;
  imageRefIds: string[];
}

function pickPptVisualCandidates(slides: PptSlideInfo[]) {
  const lowTextSlides = slides
    .filter((slide) => slide.text.length <= PPTX_LOW_TEXT_THRESHOLD && slide.imageRefIds.length > 0)
    .slice(0, MAX_SMART_VISUAL_ATTACHMENTS_PER_DOCUMENT);

  if (lowTextSlides.length > 0) {
    return lowTextSlides;
  }

  const totalTextLength = slides.reduce((sum, slide) => sum + slide.text.length, 0);
  if (totalTextLength <= PPTX_LOW_TEXT_THRESHOLD * slides.length) {
    return slides
      .filter((slide) => slide.imageRefIds.length > 0)
      .slice(0, MAX_SMART_VISUAL_ATTACHMENTS_PER_DOCUMENT);
  }

  return [];
}

async function collectPptxDocumentData(file: File) {
  ensureFileSizeWithinLimit(file);
  const [{ default: JSZip }, { XMLParser }] = await Promise.all([
    import("jszip"),
    import("fast-xml-parser"),
  ]);
  const zip = await JSZip.loadAsync(await file.arrayBuffer());
  const allSlidePaths = Object.keys(zip.files)
    .filter((path) => /^ppt\/slides\/slide\d+\.xml$/i.test(path))
    .sort((left, right) => {
      const leftIndex = Number(left.match(/slide(\d+)\.xml/i)?.[1] ?? 0);
      const rightIndex = Number(right.match(/slide(\d+)\.xml/i)?.[1] ?? 0);
      return leftIndex - rightIndex;
    });
  const slidePaths = allSlidePaths.slice(0, MAX_PPTX_SLIDES);

  if (slidePaths.length === 0) {
    throw new Error("PPTX 中未找到可解析的幻灯片内容");
  }

  const parser = new XMLParser({
    ignoreAttributes: false,
    trimValues: true,
    parseTagValue: false,
  });
  const sections: string[] = [];
  const slideInfos: PptSlideInfo[] = [];

  for (let index = 0; index < slidePaths.length; index += 1) {
    const slidePath = slidePaths[index];
    const slideXml = await zip.files[slidePath]?.async("text");
    if (!slideXml) {
      continue;
    }

    const parsed = parser.parse(slideXml) as Record<string, unknown>;
    const texts = collectPptTextNodes(parsed)
      .map((text) => text.replace(/\s+/g, " ").trim())
      .filter(Boolean);
    const slideText = texts.join("\n");
    const imageRefIds = Array.from(new Set(collectPptImageRefs(parsed)));

    slideInfos.push({
      slideNumber: index + 1,
      slidePath,
      text: slideText,
      imageRefIds,
    });

    if (slideText) {
      sections.push(`## 第 ${index + 1} 页\n${slideText}`);
    }
  }

  const textBody = sections.length > 0
    ? `${sections.join("\n\n")}${allSlidePaths.length > MAX_PPTX_SLIDES
        ? `\n\n[PPTX 页数过多，仅提取前 ${MAX_PPTX_SLIDES} 页]`
        : ""}`
    : "[未提取到可读文本，可能该 PPTX 主要由图片、图表或嵌入对象组成]";

  return {
    zip,
    parser,
    textBody,
    slideInfos,
  };
}

async function extractPptxImages(
  file: File,
  zip: Awaited<ReturnType<(typeof import("jszip"))["default"]["loadAsync"]>>,
  parser: InstanceType<(typeof import("fast-xml-parser"))["XMLParser"]>,
  slides: PptSlideInfo[],
  limit: number,
  summaryFactory: (slideNumber: number) => string,
) {
  const attachments: ExtractedAttachment[] = [];
  for (const slide of slides) {
    if (attachments.length >= limit) {
      break;
    }

    const relPath = slide.slidePath.replace(/slides\/(slide\d+\.xml)$/i, "slides/_rels/$1.rels");
    const relXml = await zip.files[relPath]?.async("text");
    if (!relXml) {
      continue;
    }

    const relParsed = parser.parse(relXml) as {
      Relationships?: {
        Relationship?: Array<{ "@_Id"?: string; "@_Target"?: string }> | { "@_Id"?: string; "@_Target"?: string };
      };
    };
    const relationships = relParsed.Relationships?.Relationship;
    const relList = Array.isArray(relationships) ? relationships : relationships ? [relationships] : [];
    const relMap = new Map(
      relList
        .filter((item) => typeof item["@_Id"] === "string" && typeof item["@_Target"] === "string")
        .map((item) => [item["@_Id"] as string, item["@_Target"] as string]),
    );

    for (const embedId of slide.imageRefIds) {
      if (attachments.length >= limit) {
        break;
      }

      const target = relMap.get(embedId);
      if (!target) {
        continue;
      }

      const mediaPath = resolveZipPath(slide.slidePath, target);
      const mediaFile = zip.files[mediaPath];
      if (!mediaFile) {
        continue;
      }

      const buffer = await mediaFile.async("arraybuffer");
      const mimeType = guessMimeType(mediaPath);
      const content = `data:${mimeType};base64,${arrayBufferToBase64(buffer)}`;
      attachments.push(
        createImageAttachment(file, {
          content,
          name: `${getBaseName(file.name)}-第${slide.slideNumber}页插图.${getFileExtension(mediaPath) || "img"}`,
          type: mimeType,
          sourcePage: slide.slideNumber,
          role: "embedded_image",
          summary: summaryFactory(slide.slideNumber),
        }),
      );
    }
  }

  return attachments;
}

async function extractPptxTextOnly(file: File): Promise<ExtractedAttachment[]> {
  const { textBody } = await collectPptxDocumentData(file);
  return [
    createTextAttachment(file, textBody, `正文已提取（${getModeLabel("text_only")}）`),
  ];
}

async function extractPptxSmartVisual(file: File): Promise<ExtractedAttachment[]> {
  const { zip, parser, textBody, slideInfos } = await collectPptxDocumentData(file);
  const candidateSlides = pickPptVisualCandidates(slideInfos);
  const images = await extractPptxImages(
    file,
    zip,
    parser,
    candidateSlides,
    MAX_SMART_VISUAL_ATTACHMENTS_PER_DOCUMENT,
    (slideNumber) => `第 ${slideNumber} 页中的关键插图`,
  );

  return [
    createTextAttachment(
      file,
      textBody,
      images.length > 0 ? "正文已提取，并补充了关键插图" : "正文已提取（智能补图）",
    ),
    ...images,
  ];
}

async function extractPptxAllVisual(file: File): Promise<ExtractedAttachment[]> {
  const { zip, parser, textBody, slideInfos } = await collectPptxDocumentData(file);
  const candidateSlides = slideInfos.filter((slide) => slide.imageRefIds.length > 0);
  const images = await extractPptxImages(
    file,
    zip,
    parser,
    candidateSlides,
    MAX_ALL_VISUAL_ATTACHMENTS_PER_DOCUMENT,
    (slideNumber) => `第 ${slideNumber} 页中的插图`,
  );
  const notice = buildTruncationNotice(
    candidateSlides.reduce((sum, slide) => sum + slide.imageRefIds.length, 0),
    images.length,
    "PPTX 插图",
  );

  return [
    createTextAttachment(
      file,
      appendTextNotice(textBody, notice ? `${notice}\n[当前 PPTX 的全部页图模式使用全部可提取插图的简化实现]` : "[当前 PPTX 的全部页图模式使用全部可提取插图的简化实现]"),
      images.length > 0 ? "正文已提取，并附带全部可提取插图" : "正文已提取（全部页图）",
    ),
    ...images,
  ];
}

async function extractDocxSmartVisual(file: File): Promise<ExtractedAttachment[]> {
  const { totalCount, attachments } = await extractDocxImageAttachments(
    file,
    MAX_SMART_VISUAL_ATTACHMENTS_PER_DOCUMENT,
    "关键插图 ",
  );
  const notice = buildTruncationNotice(totalCount, attachments.length, "DOCX 插图");
  const [textAttachment] = await extractDocxTextOnly(file, notice);
  return [
    {
      ...textAttachment,
      summary: attachments.length > 0 ? "正文已提取，并补充了关键插图" : "正文已提取（智能补图）",
    },
    ...attachments,
  ];
}

async function extractDocxAllEmbeddedImages(file: File): Promise<ExtractedAttachment[]> {
  const { totalCount, attachments } = await extractDocxImageAttachments(
    file,
    MAX_ALL_VISUAL_ATTACHMENTS_PER_DOCUMENT,
    "内嵌图片 ",
  );
  const notice = buildTruncationNotice(totalCount, attachments.length, "DOCX 内嵌图片");
  const [textAttachment] = await extractDocxTextOnly(file, notice);
  return [
    {
      ...textAttachment,
      summary: attachments.length > 0 ? "正文已提取，并附带全部内嵌图片" : "正文已提取（全部页图）",
    },
    ...attachments,
  ];
}

export async function extractAttachments(
  file: File,
  mode: DocumentParseMode = "smart_visual",
): Promise<ExtractedAttachment[]> {
  if (isImageFile(file)) {
    const content = await readAsDataUrl(file);
    return [
      createImageAttachment(file, {
        content,
        name: file.name,
        type: file.type || "image/*",
        role: "raw_upload",
        summary: "原始图片附件",
      }),
    ];
  }

  const extension = getFileExtension(file.name);

  if (PDF_EXTENSIONS.has(extension)) {
    if (mode === "text_only") {
      return extractPdfTextOnly(file);
    }
    return extractPdfVisualAttachments(file, mode);
  }

  if (DOCX_EXTENSIONS.has(extension)) {
    if (mode === "text_only") {
      return extractDocxTextOnly(file);
    }
    if (mode === "all_visual") {
      return extractDocxAllEmbeddedImages(file);
    }
    return extractDocxSmartVisual(file);
  }

  if (WORKBOOK_EXTENSIONS.has(extension)) {
    return extractWorkbookAttachments(file, mode);
  }

  if (PRESENTATION_EXTENSIONS.has(extension)) {
    if (mode === "text_only") {
      return extractPptxTextOnly(file);
    }
    if (mode === "all_visual") {
      return extractPptxAllVisual(file);
    }
    return extractPptxSmartVisual(file);
  }

  if (LEGACY_PRESENTATION_EXTENSIONS.has(extension)) {
    throw new Error('暂不支持老式 ".ppt" 文件，请另存为 ".pptx" 后上传');
  }

  return [
    createTextAttachment(file, await readPlainText(file), `正文已提取（${getModeLabel(mode)}）`),
  ];
}
