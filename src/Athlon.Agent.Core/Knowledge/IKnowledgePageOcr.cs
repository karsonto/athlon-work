namespace Athlon.Agent.Core.Knowledge;

/// <summary>
/// Vision/OCR backend for knowledge PDF images. Callers pass at most
/// <see cref="KnowledgeOcrSettings.BatchSize"/> images per call.
/// PageNumber may be a batch slot id used only for response headers.
/// </summary>
public interface IKnowledgePageOcr
{
    Task<IReadOnlyDictionary<int, string>> RecognizePagesAsync(
        IReadOnlyList<KnowledgeOcrPageImage> pages,
        CancellationToken cancellationToken = default);
}

public sealed record KnowledgeOcrPageImage(int PageNumber, ImageAttachment Image);
