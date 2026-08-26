using Athlon.Agent.Core;
using Athlon.Agent.Core.Knowledge;

namespace Athlon.Agent.Infrastructure.Knowledge;

public sealed class VisionChatKnowledgeOcr(
    IAgentModelClient modelClient,
    IAppLogger logger) : IKnowledgePageOcr
{
    private const string SystemPrompt =
        "You are an OCR engine for financial and business documents. "
        + "Transcribe visible text faithfully. Never invent content that is not in the image.";

    private readonly IAppLogger _logger = logger.ForContext("KnowledgeOcr");

    public async Task<IReadOnlyDictionary<int, string>> RecognizePagesAsync(
        IReadOnlyList<KnowledgeOcrPageImage> pages,
        CancellationToken cancellationToken = default)
    {
        if (pages.Count == 0)
        {
            return new Dictionary<int, string>();
        }

        var pageNumbers = pages.Select(page => page.PageNumber).ToArray();
        Exception? lastError = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var request = BuildRequest(pages, pageNumbers);
                var response = await modelClient
                    .CompleteAsync(request, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                var parsed = KnowledgeOcrResponseParser.Parse(response.Content ?? "", pageNumbers);
                foreach (var pageNumber in pageNumbers)
                {
                    if (!parsed.ContainsKey(pageNumber))
                    {
                        _logger.Warning("OCR response missing page {PageNumber}", pageNumber);
                    }
                }

                return parsed;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                lastError = exception;
                _logger.Warning(
                    "Knowledge OCR batch failed (attempt {Attempt}): {Message}",
                    attempt + 1,
                    exception.Message);
            }
        }

        _logger.Warning(
            "Knowledge OCR batch giving up after retries: {Message}",
            lastError?.Message ?? "unknown");
        return new Dictionary<int, string>();
    }

    private static AgentModelRequest BuildRequest(
        IReadOnlyList<KnowledgeOcrPageImage> pages,
        IReadOnlyList<int> pageNumbers)
    {
        var images = pages.Select(page => page.Image).ToArray();
        var text = KnowledgeOcrResponseParser.BuildUserPrompt(pageNumbers);
        var contentParts = new List<object>
        {
            new Dictionary<string, object?>
            {
                ["type"] = "text",
                ["text"] = text
            }
        };

        foreach (var image in images)
        {
            var dataUrl = ImageAttachmentDataUrlResolver.ResolveDataUrl(image);
            if (string.IsNullOrWhiteSpace(dataUrl))
            {
                continue;
            }

            contentParts.Add(new Dictionary<string, object?>
            {
                ["type"] = "image_url",
                ["image_url"] = new Dictionary<string, object?>
                {
                    ["url"] = dataUrl
                }
            });
        }

        return new AgentModelRequest(
            [
                new AgentModelMessage("system", SystemPrompt),
                new AgentModelMessage("user", contentParts)
            ],
            Array.Empty<ToolDefinition>(),
            AllowToolCalls: false,
            MaxTokens: 4096);
    }
}
