namespace Athlon.Agent.Core.Knowledge;

/// <summary>
/// Vision/OCR backend for knowledge PDF pages. Callers pass at most
/// <see cref="KnowledgeOcrSettings.BatchSize"/> images per call.
/// </summary>
public interface IKnowledgePageOcr
{
    Task<IReadOnlyDictionary<int, string>> RecognizePagesAsync(
        IReadOnlyList<KnowledgeOcrPageImage> pages,
        CancellationToken cancellationToken = default);
}

public sealed record KnowledgeOcrPageImage(int PageNumber, ImageAttachment Image);
