namespace Athlon.Agent.Infrastructure.Sso;

internal static class ImpSsoAuthPageHtml
{
    public static string Build(string completePath) => $$"""
        <!DOCTYPE html>
        <html lang="zh-CN">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>Athlon Agent · SSO 登录</title>
          <style>
            :root {
              --bg: #F1F5F9;
              --chrome: #FFFFFF;
              --panel: #F8FAFC;
              --border: #E2E8F0;
              --text: #0F172A;
              --text-secondary: #475569;
              --subtle: #64748B;
              --accent: #6366F1;
              --accent-glow: rgba(99, 102, 241, 0.14);
              --success: #16A34A;
              --success-bg: #F0FDF4;
              --danger: #E11D48;
              --danger-bg: #FFF1F2;
            }

            * { box-sizing: border-box; margin: 0; padding: 0; }

            body {
              min-height: 100vh;
              display: flex;
              align-items: center;
              justify-content: center;
              padding: 24px;
              font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif;
              background: var(--bg);
              color: var(--text);
              -webkit-font-smoothing: antialiased;
            }

            body::before {
              content: "";
              position: fixed;
              inset: 0;
              background:
                linear-gradient(180deg, #F4F8FF 0%, var(--bg) 45%, #E8EDF4 100%),
                radial-gradient(ellipse 60% 50% at 50% -10%, var(--accent-glow), transparent 70%),
                radial-gradient(ellipse 40% 30% at 80% 100%, rgba(99, 102, 241, 0.06), transparent 60%);
              pointer-events: none;
            }

            .card {
              position: relative;
              width: 100%;
              max-width: 420px;
              background: var(--chrome);
              border: 1px solid var(--border);
              border-radius: 16px;
              padding: 40px 32px 32px;
              text-align: center;
              box-shadow: 0 8px 32px rgba(15, 23, 42, 0.08), 0 1px 2px rgba(15, 23, 42, 0.04);
              animation: fadeIn 0.4s ease-out;
            }

            @keyframes fadeIn {
              from { opacity: 0; transform: translateY(12px); }
              to { opacity: 1; transform: translateY(0); }
            }

            .logo {
              width: 64px;
              height: 64px;
              margin: 0 auto 16px;
              border-radius: 14px;
              overflow: hidden;
              box-shadow: 0 6px 20px rgba(3, 105, 161, 0.18);
            }

            .logo svg { display: block; width: 100%; height: 100%; }

            .brand {
              font-size: 20px;
              font-weight: 600;
              letter-spacing: -0.02em;
              color: var(--text);
            }

            .tagline {
              margin-top: 6px;
              font-size: 13px;
              color: var(--subtle);
            }

            .divider {
              height: 1px;
              background: var(--border);
              margin: 28px 0;
            }

            .state-panel {
              min-height: 120px;
              display: flex;
              flex-direction: column;
              align-items: center;
              justify-content: center;
              gap: 14px;
            }

            .state-panel[hidden] { display: none !important; }

            .spinner {
              width: 36px;
              height: 36px;
              border: 3px solid #E2E8F0;
              border-top-color: var(--accent);
              border-radius: 50%;
              animation: spin 0.8s linear infinite;
            }

            @keyframes spin { to { transform: rotate(360deg); } }

            .state-title {
              font-size: 16px;
              font-weight: 600;
              color: var(--text);
            }

            .state-desc {
              font-size: 13px;
              line-height: 1.6;
              color: var(--text-secondary);
              max-width: 300px;
            }

            .icon-circle {
              width: 52px;
              height: 52px;
              border-radius: 50%;
              display: flex;
              align-items: center;
              justify-content: center;
            }

            .icon-circle.success {
              background: var(--success-bg);
              color: var(--success);
              border: 1px solid #BBF7D0;
            }

            .icon-circle.error {
              background: var(--danger-bg);
              color: var(--danger);
              border: 1px solid #FECDD3;
            }

            .icon-circle svg { width: 26px; height: 26px; }

            .footer {
              margin-top: 24px;
              font-size: 11px;
              color: var(--subtle);
            }
          </style>
        </head>
        <body>
          <div class="card">
            <div class="logo" aria-hidden="true">
              <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512">
                <defs>
                  <linearGradient id="bg" x1="72" y1="56" x2="440" y2="456" gradientUnits="userSpaceOnUse">
                    <stop offset="0" stop-color="#e0f2fe"/>
                    <stop offset="0.55" stop-color="#7dd3fc"/>
                    <stop offset="1" stop-color="#0284c7"/>
                  </linearGradient>
                  <linearGradient id="mark" x1="151" y1="121" x2="361" y2="389" gradientUnits="userSpaceOnUse">
                    <stop offset="0" stop-color="#ffffff"/>
                    <stop offset="1" stop-color="#dbeafe"/>
                  </linearGradient>
                </defs>
                <rect width="512" height="512" rx="116" fill="url(#bg)"/>
                <path fill="url(#mark)" d="M256 121c15 0 29 8 37 21l108 183c12 20-3 45-26 45h-27c-12 0-22-6-28-16l-18-32h-92l-18 32c-6 10-16 16-28 16h-27c-23 0-38-25-26-45l108-183c8-13 22-21 37-21Zm-25 148h50l-25-44-25 44Z"/>
                <path fill="#0369a1" opacity="0.9" d="M183 212c0-16 13-29 29-29h88c16 0 29 13 29 29v74c0 16-13 29-29 29h-88c-16 0-29-13-29-29v-74Zm52 38a15 15 0 1 0-30 0 15 15 0 0 0 30 0Zm72 0a15 15 0 1 0-30 0 15 15 0 0 0 30 0Zm-74 40c-7 0-12 5-12 12s5 12 12 12h46c7 0 12-5 12-12s-5-12-12-12h-46Z"/>
              </svg>
            </div>
            <div class="brand">Athlon Agent</div>
            <div class="tagline">AI coding agent for your workspace.</div>

            <div class="divider"></div>

            <div id="state-loading" class="state-panel">
              <div class="spinner" role="status" aria-label="加载中"></div>
              <div class="state-title">正在完成登录验证</div>
              <div class="state-desc">请稍候，正在与 IMP 验证您的身份…</div>
            </div>

            <div id="state-success" class="state-panel" hidden>
              <div class="icon-circle success" aria-hidden="true">
                <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round">
                  <polyline points="20 6 9 17 4 12"/>
                </svg>
              </div>
              <div class="state-title">登录成功</div>
              <div class="state-desc">身份验证已完成，请关闭此页面返回应用。</div>
            </div>

            <div id="state-error" class="state-panel" hidden>
              <div class="icon-circle error" aria-hidden="true">
                <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round">
                  <line x1="18" y1="6" x2="6" y2="18"/><line x1="6" y1="6" x2="18" y2="18"/>
                </svg>
              </div>
              <div class="state-title">登录失败</div>
              <div id="error-message" class="state-desc">验证未通过，请返回应用重新登录。</div>
            </div>

            <div class="footer">Powered by Athlon Agent · IMP SSO</div>
          </div>

          <script>
          (function () {
            var loading = document.getElementById('state-loading');
            var success = document.getElementById('state-success');
            var error = document.getElementById('state-error');
            var errorMessage = document.getElementById('error-message');

            function showState(state, message) {
              loading.hidden = state !== 'loading';
              success.hidden = state !== 'success';
              error.hidden = state !== 'error';
              if (message && errorMessage) {
                errorMessage.textContent = message;
              }
            }

            var hash = window.location.hash || '';
            if (hash.indexOf('#/?') === 0) hash = hash.substring(3);
            else if (hash.indexOf('#/') === 0) hash = hash.substring(2);
            else if (hash.charAt(0) === '#') hash = hash.substring(1);

            var params = new URLSearchParams(hash);
            var payload = {
              appId: params.get('appId'),
              userId: params.get('userId'),
              locale: params.get('locale'),
              token: params.get('token'),
              role: params.get('role'),
              depname: params.get('depname'),
              channel_type: params.get('channel_type'),
              msg: params.get('msg')
            };

            if (!payload.token) {
              var msg = payload.msg
                ? 'IMP 返回：' + payload.msg
                : '未收到登录凭证，请返回应用重新登录。';
              showState('error', msg);
              return;
            }

            fetch('{{completePath}}', {
              method: 'POST',
              headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify(payload)
            })
            .then(function (r) {
              if (!r.ok) throw new Error('服务器响应异常');
              return r.json();
            })
            .then(function (data) {
              if (data && data.ok === false) throw new Error('验证未通过');
              showState('success');
            })
            .catch(function () {
              showState('error', '无法完成身份验证，请关闭此页面并在应用中重试。');
            });
          })();
          </script>
        </body>
        </html>
        """;
}
