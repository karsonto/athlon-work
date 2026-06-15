using System.Text.Json;
using Athlon.Agent.Core.Sso;

namespace Athlon.Agent.Infrastructure.Sso;

internal static class ImpSsoResponseParser
{
    private const string HtmlLoginMarker = "https://www.w3.org/TR/xhtml1/DTD/xhtml1-strict.dtd";

    public static ImpSsoParsedResponse Parse(string? httpResult)
    {
        if (string.IsNullOrWhiteSpace(httpResult))
        {
            return ImpSsoParsedResponse.Invalid("IMP 返回为空。");
        }

        if (httpResult.Contains(HtmlLoginMarker, StringComparison.Ordinal))
        {
            return ImpSsoParsedResponse.LoginRequired();
        }

        Dictionary<string, JsonElement>? resultMap;
        try
        {
            resultMap = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(httpResult.Trim());
        }
        catch (JsonException ex)
        {
            return ImpSsoParsedResponse.Invalid($"IMP 响应解析失败：{ex.Message}");
        }

        if (resultMap is null)
        {
            return ImpSsoParsedResponse.Invalid("IMP 响应为空 JSON。");
        }

        var retcode = GetString(resultMap, "retcode");
        var retmsg = GetString(resultMap, "retmsg");
        if (string.Equals(retcode, "1", StringComparison.Ordinal))
        {
            return ImpSsoParsedResponse.ReLoginRequired(retmsg ?? "需要重新登录。");
        }

        var roleUserRelNumString = GetString(resultMap, "roleUserRelNum");
        if (!string.IsNullOrEmpty(roleUserRelNumString)
            && int.TryParse(roleUserRelNumString, out var roleUserRelNum)
            && roleUserRelNum <= 0)
        {
            return ImpSsoParsedResponse.NoRole();
        }

        var userid = GetString(resultMap, "userid");
        var timeoutRemainingString = GetString(resultMap, "timeoutRemaining");
        if (!int.TryParse(timeoutRemainingString, out var timeoutRemaining) || timeoutRemaining <= 0)
        {
            return ImpSsoParsedResponse.Invalid("IMP 会话已失效。");
        }

        if (string.IsNullOrWhiteSpace(userid))
        {
            return ImpSsoParsedResponse.Invalid("IMP 未返回有效用户。");
        }

        var ename = GetString(resultMap, "ename");
        var displayName = !string.IsNullOrWhiteSpace(ename) ? ename! : userid!;

        return ImpSsoParsedResponse.Valid(userid!, displayName);
    }

    private static string? GetString(Dictionary<string, JsonElement> map, string key) =>
        map.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

internal sealed class ImpSsoParsedResponse
{
    public ImpSsoCheckStatus Status { get; init; }

    public string Message { get; init; } = "";

    public string? UserId { get; init; }

    public string? DisplayName { get; init; }

    public static ImpSsoParsedResponse Valid(string userId, string displayName) =>
        new()
        {
            Status = ImpSsoCheckStatus.Valid,
            UserId = userId,
            DisplayName = displayName
        };

    public static ImpSsoParsedResponse LoginRequired() =>
        new() { Status = ImpSsoCheckStatus.LoginRequired, Message = "需要 IMP 登录。" };

    public static ImpSsoParsedResponse ReLoginRequired(string message) =>
        new() { Status = ImpSsoCheckStatus.ReLoginRequired, Message = message };

    public static ImpSsoParsedResponse NoRole() =>
        new() { Status = ImpSsoCheckStatus.NoRole, Message = "当前登录用户无可用角色。" };

    public static ImpSsoParsedResponse Invalid(string message) =>
        new() { Status = ImpSsoCheckStatus.Invalid, Message = message };
}
