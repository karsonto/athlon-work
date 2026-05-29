using Athlon.Agent.Core.Licensing;

namespace Athlon.Agent.App.Licensing;

internal static class LicenseFailureMessages
{
    public static string Describe(LicenseFailureCode code) => code switch
    {
        LicenseFailureCode.Missing => "未找到有效的 License 文件。请粘贴或导入管理员提供的 License。",
        LicenseFailureCode.InvalidFormat => "License 格式无效。请确认粘贴的是完整的 JSON 内容。",
        LicenseFailureCode.InvalidSignature => "License 签名无效。请向管理员索取新的 License。",
        LicenseFailureCode.InvalidPayload => "License 内容无法解析。",
        LicenseFailureCode.WrongProduct => "该 License 不适用于 Athlon Agent。",
        LicenseFailureCode.UnsupportedVersion => "License 版本不受支持，请升级客户端或联系管理员。",
        LicenseFailureCode.Expired => "License 已过期。请向管理员申请续期。",
        LicenseFailureCode.AccountMismatch => "License 与当前 Windows 登录账号不匹配。",
        _ => "License 校验失败。"
    };
}
