using Athlon.Agent.Core;

namespace Athlon.Agent.App.Services;

public sealed class SpeechDictationAvailability
{
    private readonly AppSettings _settings;

    public SpeechDictationAvailability(AppSettings settings)
    {
        _settings = settings;
    }

    public bool IsAvailable { get; private set; }

    public string UnavailableReason { get; private set; } =
        "正在检测 Windows 语音识别可用性…";

    public string EffectiveLanguageTag { get; private set; } = "zh-CN";

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (!_settings.Speech.Enabled)
        {
            IsAvailable = false;
            UnavailableReason = "语音听写已在设置中关闭。";
            return;
        }

        try
        {
            var resolvedTag = await WindowsSpeechDictationService
                .ResolveLanguageTagAsync(_settings.Speech.LanguageTag, cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(resolvedTag))
            {
                IsAvailable = false;
                UnavailableReason =
                    "未检测到可用的 Windows 语音识别语言包。请在「设置 → 时间和语言 → 语音」安装中文或英文语音识别。";
                return;
            }

            EffectiveLanguageTag = resolvedTag;
            var recognizer = await WindowsSpeechDictationService
                .CreateRecognizerAsync(resolvedTag, cancellationToken)
                .ConfigureAwait(false);
            if (recognizer is null)
            {
                IsAvailable = false;
                UnavailableReason =
                    $"无法为语言 {resolvedTag} 初始化听写识别器。请确认已安装对应语音识别语言包。";
                return;
            }

            recognizer.Dispose();
            IsAvailable = true;
            UnavailableReason = string.Empty;
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            UnavailableReason = $"语音识别不可用：{ex.Message}";
        }
    }
}
