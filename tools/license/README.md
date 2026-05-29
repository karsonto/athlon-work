# Athlon Agent License 工具

基于 RSA-2048 + SHA-256 的离线 License 签发与校验。License 绑定 Windows AD 账号（Sam / UPN），客户端启动时验证签名与有效期。

## 环境

```bash
pip install -r requirements.txt
```

## 1. 生成密钥对（仅首次或轮换密钥时）

```bash
python generate_keys.py
```

输出：

- `keys/private.pem` — **仅保存在签发机，勿提交 Git**
- `keys/public.pem` — 同步到 `src/Athlon.Agent.Infrastructure/Licensing/LicensePublicKey.cs`

## 2. 签发 License

默认有效期 30 天：

```bash
python generate_license.py \
  --account "CONTOSO\\jdoe" \
  --output license.lic
```

指定到期日：

```bash
python generate_license.py \
  --account "jdoe@contoso.com" \
  --expires 2026-12-31T23:59:59Z \
  --output license.lic
```

同时写入 Sam 与 UPN（推荐，与客户端双匹配一致）：

```bash
python generate_license.py \
  --account "CONTOSO\\jdoe" \
  --sam "CONTOSO\\jdoe" \
  --upn "jdoe@contoso.com" \
  --days 90 \
  --output license.lic
```

## 3. 客户端部署

License 查找顺序：

1. 与 `Athlon.Agent.App.exe` 同目录的 `license.lic`
2. `%USERPROFILE%\.athlon-agent\config\license.lic`

校验失败时应用会弹出激活对话框，用户可粘贴或导入 `.lic`；通过后保存到用户 config 目录。

## 本地开发跳过校验（仅 Debug）

```bash
set ATHLON_SKIP_LICENSE=1
```
