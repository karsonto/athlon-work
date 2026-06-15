#!/usr/bin/env python3
"""Generate IMP SSO implementation Word document with embedded SVG diagrams."""

from __future__ import annotations

from datetime import date
from pathlib import Path

from docx import Document
from docx.enum.text import WD_ALIGN_PARAGRAPH
from docx.shared import Inches, Pt, RGBColor
from docx.oxml.ns import qn
from reportlab.graphics import renderPM
from svglib.svglib import svg2rlg

ROOT = Path(__file__).resolve().parent
DIAGRAMS = ROOT / "diagrams"
OUTPUT_DOCX = ROOT / "IMP_SSO_实现方案.docx"


def svg_to_png(svg_path: Path, png_path: Path, scale: float = 2.0) -> None:
    drawing = svg2rlg(str(svg_path))
    if drawing is None:
        raise RuntimeError(f"Failed to parse SVG: {svg_path}")
    renderPM.drawToFile(drawing, str(png_path), fmt="PNG", dpi=72 * scale)


def set_doc_default_font(document: Document, font_name: str = "Microsoft YaHei", size: int = 11) -> None:
    style = document.styles["Normal"]
    style.font.name = font_name
    style.font.size = Pt(size)
    style._element.rPr.rFonts.set(qn("w:eastAsia"), font_name)


def add_title(document: Document, text: str) -> None:
    p = document.add_heading(text, level=0)
    p.alignment = WD_ALIGN_PARAGRAPH.CENTER


def add_heading(document: Document, text: str, level: int = 1) -> None:
    document.add_heading(text, level=level)


def add_paragraph(document: Document, text: str, bold: bool = False) -> None:
    p = document.add_paragraph()
    run = p.add_run(text)
    run.bold = bold


def add_bullets(document: Document, items: list[str]) -> None:
    for item in items:
        document.add_paragraph(item, style="List Bullet")


def add_table(document: Document, headers: list[str], rows: list[list[str]]) -> None:
    table = document.add_table(rows=1, cols=len(headers))
    table.style = "Table Grid"
    hdr = table.rows[0].cells
    for i, header in enumerate(headers):
        hdr[i].text = header
        for p in hdr[i].paragraphs:
            for run in p.runs:
                run.bold = True
    for row in rows:
        cells = table.add_row().cells
        for i, value in enumerate(row):
            cells[i].text = value


def add_diagram(document: Document, title: str, svg_name: str, caption: str) -> None:
    svg_path = DIAGRAMS / svg_name
    png_path = DIAGRAMS / svg_name.replace(".svg", ".png")
    svg_to_png(svg_path, png_path)

    add_heading(document, title, level=2)
    document.add_picture(str(png_path), width=Inches(6.5))
    cap = document.add_paragraph()
    cap.alignment = WD_ALIGN_PARAGRAPH.CENTER
    run = cap.add_run(caption)
    run.italic = True
    run.font.color.rgb = RGBColor(0x66, 0x66, 0x66)
    note = document.add_paragraph()
    note.add_run(f"SVG 源文件：diagrams/{svg_name}").italic = True


def build_document() -> Document:
    document = Document()
    set_doc_default_font(document)

    add_title(document, "Athlon Agent IMP SSO 实现方案")
    meta = document.add_paragraph()
    meta.alignment = WD_ALIGN_PARAGRAPH.CENTER
    meta.add_run(f"文档版本：V1.0    创建日期：{date.today().isoformat()}\n").bold = True
    meta.add_run("适用范围：Athlon Agent v2.0.5（feature/imp-sso）")

    add_heading(document, "1. 概述")
    add_paragraph(
        document,
        "Athlon Agent 作为企业级 WPF 桌面应用，通过 IMP（ICBC Asia）SSO 实现统一身份认证。"
        "本实现严格遵循 IMP/SsoFilter 实际协议（ssotoken + check_ssotoken），不采用 OAuth 2.0 模型。",
    )
    add_bullets(
        document,
        [
            "默认 Sso.Enabled = false，前期不影响现有 License 流程",
            "启用后：SSO 与 License 双重门控均须通过",
            "本地会话固定 24 小时有效，不做续期",
            "标题栏显示 IMP 返回的 ename，支持登出",
        ],
    )

    add_heading(document, "2. 设计原则")
    add_table(
        document,
        ["不使用", "使用（IMP 实际）"],
        [
            ["OAuth access/refresh token", "单一 ssotoken（回调 hash 参数 token）"],
            ["POST /refresh_token", "无 refresh；登录时 POST check_ssotoken 一次"],
            ["IMP timeoutRemaining 驱动过期", "本地 LoggedInAt + 24h"],
            ["SsoFilter 5 分钟轮询续期", "不实现续期"],
        ],
    )

    add_heading(document, "3. 配置说明")
    add_paragraph(document, "settings.json 示例（~/.athlon-agent/config/settings.json）：")
    document.add_paragraph(
        '{\n'
        '  "Sso": {\n'
        '    "Enabled": false,\n'
        '    "ImpDomain": "www.icbcasia.com",\n'
        '    "AppId": "252",\n'
        '    "Version": "20251127",\n'
        '    "SessionValidityHours": 24,\n'
        '    "CallbackPort": 5657,\n'
        '    "CallbackPath": "/sso/auth",\n'
        '    "CompletePath": "/sso/complete"\n'
        '  }\n'
        '}',
        style="Intense Quote",
    )
    add_table(
        document,
        ["Sso.Enabled", "行为"],
        [
            ["false（默认）", "跳过 IMP；仅 License；无 SSO UI"],
            ["true", "SSO 通过后再 License；显示 ename；可登出"],
        ],
    )

    add_heading(document, "4. Token 生命周期")
    add_bullets(
        document,
        [
            "IMP 登录成功 → POST check_ssotoken（仅此一次）→ 存 ssotoken + ename",
            "ExpiresAt = LoggedInAt + SessionValidityHours（默认 24h）",
            "24h 内再次启动：读 DPAPI 缓存，不调 IMP",
            "超过 24h：清缓存 → 重新浏览器 IMP 登录",
            "运行期间不续期、不轮询 check_ssotoken",
        ],
    )

    add_diagram(
        document,
        "4.1 启动门控流程图",
        "startup_flow.svg",
        "图 1  应用启动时 SSO / License 门控流程",
    )

    add_diagram(
        document,
        "4.2 IMP 登录时序图",
        "login_sequence.svg",
        "图 2  IMP 浏览器登录与 hash 回调时序",
    )

    add_heading(document, "5. IMP 协议要点")
    add_heading(document, "5.1 端点", level=2)
    add_table(
        document,
        ["端点", "何时调用"],
        [
            ["/icbcasia/imp/index.html", "无有效缓存或已过期时打开浏览器"],
            ["/icbcasia/imp/check_ssotoken", "hash 回调拿到 ssotoken 后调用一次"],
            ["/icbcasia/imp/getsaubapp", "可选；回调 URL 已固定时可跳过"],
        ],
    )

    add_heading(document, "5.2 check_ssotoken 请求", level=2)
    document.add_paragraph(
        "POST https://{ImpDomain}/icbcasia/imp/check_ssotoken?version=20251127\n"
        "Content-Type: application/x-www-form-urlencoded\n\n"
        "ssotoken={token}&impappid=252&dse_sessionId={token}&requestip={localIp}",
        style="Intense Quote",
    )

    add_heading(document, "5.3 回调 URL 格式", level=2)
    document.add_paragraph(
        "http://localhost:5657/sso/auth#/?appId=252&userId=000974115&locale=zh_HK"
        "&token={ssotoken}&role=User&depname=FTD",
        style="Intense Quote",
    )
    add_paragraph(document, "注意：token 在 URL hash 中，HttpListener 无法直接读取，需 /sso/auth 返回 JS 解析页。")

    add_heading(document, "6. 核心组件")
    add_table(
        document,
        ["组件", "路径", "职责"],
        [
            ["SsoSettings", "Athlon.Agent.Core/Sso/", "配置项，Enabled 默认 false"],
            ["ImpSsoSession", "Athlon.Agent.Core/Sso/", "SsoToken、DisplayName、ExpiresAt"],
            ["ImpSsoSessionStore", "Infrastructure/Sso/", "DPAPI 加密持久化"],
            ["ImpSsoAuthService", "Infrastructure/Sso/", "POST check_ssotoken"],
            ["ImpSsoCallbackServer", "Infrastructure/Sso/", "5657 端口 hash 回调"],
            ["ImpSsoStartupGate", "App/Licensing/", "启动门控编排"],
            ["MainWindowViewModel", "App/ViewModels/", "标题栏 ename + 登出"],
        ],
    )

    add_heading(document, "7. 启动顺序")
    document.add_paragraph(
        "1. 若 Sso.Enabled → ImpSsoStartupGate.EnsureAuthenticated\n"
        "2. LicenseStartupGate.EnsureLicensed\n"
        "3. DI 注册 + MainWindow",
        style="List Number",
    )

    add_heading(document, "8. UI 行为")
    add_bullets(
        document,
        [
            "Sso.Enabled=true：标题栏右侧显示 ename（IMP 返回）",
            "点击姓名 → 确认「确定要退出登录吗？」→ 清 ssotoken → 退出应用",
            "Sso.Enabled=false：不显示 SSO 相关 UI",
        ],
    )

    add_heading(document, "9. 安全设计")
    add_bullets(
        document,
        [
            "ssotoken 使用 Windows DPAPI 加密存储（CurrentUser）",
            "回调 token 不可信，必须 check_ssotoken 二次校验",
            "日志中应对 ssotoken 脱敏",
            "全部 IMP 通信使用 HTTPS",
        ],
    )

    add_heading(document, "10. 验收标准")
    add_heading(document, "10.1 Sso.Enabled=false（默认）", level=2)
    add_bullets(
        document,
        [
            "启动时不打开浏览器、不监听 5657、不调 IMP",
            "仅走 License 门控",
            "标题栏无 SSO 姓名/登出入口",
        ],
    )
    add_heading(document, "10.2 Sso.Enabled=true", level=2)
    add_bullets(
        document,
        [
            "首次 IMP 登录 → check_ssotoken 一次 → 进入主界面",
            "24h 内再次启动免浏览器、免 IMP",
            "超过 24h 重新 IMP 登录",
            "SSO + License 双门控",
            "点击姓名可登出",
        ],
    )

    add_heading(document, "11. 附录：Debug 开关")
    add_paragraph(document, "DEBUG 构建可设置环境变量 ATHLON_SKIP_SSO=1 跳过 SSO 门控（与 ATHLON_SKIP_LICENSE 对称）。")

    return document


def main() -> None:
    DIAGRAMS.mkdir(parents=True, exist_ok=True)
    document = build_document()
    document.save(OUTPUT_DOCX)
    print(f"Generated: {OUTPUT_DOCX}")
    print(f"SVG diagrams: {DIAGRAMS / 'startup_flow.svg'}, {DIAGRAMS / 'login_sequence.svg'}")


if __name__ == "__main__":
    main()
