namespace VoxMind.Core.Session;

public interface ISummaryGenerator
{
    Task<SessionSummary> GenerateAsync(ListeningSession session, IReadOnlyList<SessionSegment> segments, CancellationToken ct = default);
}
