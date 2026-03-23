using Microsoft.Extensions.Logging;

namespace VoxMind.Core.Session;

public class SummaryGenerator : ISummaryGenerator
{
    private readonly ILogger<SummaryGenerator> _logger;

    private static readonly string[] DecisionPatterns =
    {
        "on va faire", "c'est décidé", "je suis d'accord",
        "nous allons", "donc on", "il est décidé", "c'est convenu",
        "on décide", "la décision est", "on s'est mis d'accord"
    };

    private static readonly string[] ActionPatterns =
    {
        "il faut", "je vais", "tu dois", "nous devons",
        "je m'occupe", "tu gères", "je reprends", "à faire",
        "action:", "todo:", "je me charge", "je vais faire"
    };

    private static readonly string[] KeyMomentPatterns =
    {
        "bonjour", "salut", "bonsoir",
        "merci", "je vous remercie",
        "à plus", "au revoir", "à bientôt",
        "j'ai une question", "question:", "pour résumer"
    };

    public SummaryGenerator(ILogger<SummaryGenerator> logger)
    {
        _logger = logger;
    }

    public Task<SessionSummary> GenerateAsync(ListeningSession session, IReadOnlyList<SessionSegment> segments, CancellationToken ct = default)
    {
        _logger.LogInformation("Génération du résumé pour la session {SessionId}...", session.Id);

        var fullTranscript = BuildFullTranscript(segments);
        var keyMoments = ExtractKeyMoments(session, segments);
        var decisions = ExtractByPatterns(fullTranscript, DecisionPatterns);
        var actions = ExtractByPatterns(fullTranscript, ActionPatterns);
        var participants = BuildParticipantSummaries(session, segments);
        var narrative = GenerateNarrative(session, participants, decisions);

        var summary = new SessionSummary
        {
            SessionId = session.Id,
            Name = session.Name,
            StartedAt = session.StartedAt,
            EndedAt = session.EndedAt ?? DateTime.UtcNow,
            Duration = session.Duration,
            Participants = participants,
            KeyMoments = keyMoments,
            Decisions = decisions,
            ActionItems = actions,
            FullTranscript = fullTranscript,
            GeneratedSummary = narrative,
            GeneratedAt = DateTime.UtcNow
        };

        _logger.LogInformation("Résumé généré : {Decisions} décisions, {Actions} actions, {Participants} participants.",
            decisions.Count, actions.Count, participants.Count);

        return Task.FromResult(summary);
    }

    private static string BuildFullTranscript(IReadOnlyList<SessionSegment> segments)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var seg in segments.OrderBy(s => s.StartSeconds))
        {
            var speaker = seg.SpeakerName ?? "Inconnu";
            sb.AppendLine($"{speaker}: {seg.Text}");
        }
        return sb.ToString().Trim();
    }

    private static List<string> ExtractKeyMoments(ListeningSession session, IReadOnlyList<SessionSegment> segments)
    {
        var moments = new List<string>();
        if (!segments.Any()) return moments;

        // Découper en blocs de 5 minutes et prendre la phrase la plus longue de chaque bloc
        const int blockMinutes = 5;
        var startTime = session.StartedAt;
        var totalMinutes = (int)Math.Ceiling(session.Duration.TotalMinutes);

        for (int blockStart = 0; blockStart < Math.Max(totalMinutes, blockMinutes); blockStart += blockMinutes)
        {
            var blockEnd = blockStart + blockMinutes;
            var blockSegments = segments.Where(s =>
                s.StartSeconds >= blockStart * 60 && s.StartSeconds < blockEnd * 60
            ).ToList();

            if (!blockSegments.Any()) continue;

            var representative = blockSegments.OrderByDescending(s => s.Text.Length).First();
            var timeLabel = (startTime + TimeSpan.FromSeconds(representative.StartSeconds)).ToString("HH:mm");
            moments.Add($"{timeLabel} - {representative.Text.Truncate(100)}");
        }

        // Moments clés par pattern : salutations, adieux, questions
        foreach (var seg in segments)
        {
            var lower = seg.Text.ToLowerInvariant();
            if (KeyMomentPatterns.Any(p => lower.Contains(p)))
            {
                var timeLabel = (startTime + TimeSpan.FromSeconds(seg.StartSeconds)).ToString("HH:mm");
                var entry = $"{timeLabel} ⚑ {seg.Text.Truncate(100)}";
                if (!moments.Contains(entry))
                    moments.Add(entry);
            }
        }

        return moments;
    }

    private static List<string> ExtractByPatterns(string transcript, string[] patterns)
    {
        var results = new List<string>();
        if (string.IsNullOrWhiteSpace(transcript)) return results;

        var lines = transcript.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var lowerLine = line.ToLowerInvariant();
            if (patterns.Any(p => lowerLine.Contains(p)))
            {
                // Extraire la partie après "Speaker: "
                var text = line.Contains(": ") ? line.Split(": ", 2)[1] : line;
                if (text.Length > 10) // Ignorer les lignes trop courtes
                    results.Add(text.Truncate(150));
            }
        }
        return results.Distinct().ToList();
    }

    private static List<ParticipantSummary> BuildParticipantSummaries(ListeningSession session, IReadOnlyList<SessionSegment> segments)
    {
        var totalDuration = session.Duration.TotalSeconds;
        if (totalDuration <= 0) totalDuration = 1;

        return segments
            .GroupBy(s => new { s.SpeakerId, s.SpeakerName })
            .Select(g =>
            {
                var speakingTime = g.Sum(s => s.EndSeconds - s.StartSeconds);
                return new ParticipantSummary
                {
                    SpeakerId = g.Key.SpeakerId ?? Guid.Empty,
                    Name = g.Key.SpeakerName ?? "Inconnu",
                    SpeakingTime = TimeSpan.FromSeconds(speakingTime),
                    UtteranceCount = g.Count(),
                    AverageConfidence = g.Average(s => s.Confidence),
                    PercentageOfSession = (float)(speakingTime / totalDuration * 100)
                };
            })
            .OrderByDescending(p => p.SpeakingTime)
            .ToList();
    }

    private static string GenerateNarrative(ListeningSession session, List<ParticipantSummary> participants, List<string> decisions)
    {
        var duration = FormatDuration(session.Duration);
        var participantNames = participants.Select(p => p.Name).ToList();
        var names = participantNames.Count switch
        {
            0 => "participants inconnus",
            1 => participantNames[0],
            2 => $"{participantNames[0]} et {participantNames[1]}",
            _ => $"{string.Join(", ", participantNames.Take(participantNames.Count - 1))} et {participantNames.Last()}"
        };

        var sb = new System.Text.StringBuilder();
        sb.Append($"Session de {duration} entre {names}.");

        if (decisions.Any())
            sb.Append($" {decisions.Count} décision(s) identifiée(s).");

        if (participants.Any())
        {
            var mainSpeaker = participants.First();
            sb.Append($" {mainSpeaker.Name} a pris la parole {mainSpeaker.UtteranceCount} fois ({mainSpeaker.PercentageOfSession:F0}% du temps).");
        }

        return sb.ToString();
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
            return $"{(int)duration.TotalHours}h{duration.Minutes:D2}min";
        if (duration.TotalMinutes >= 1)
            return $"{(int)duration.TotalMinutes} minute(s)";
        return $"{(int)duration.TotalSeconds} seconde(s)";
    }
}

internal static class StringExtensions
{
    public static string Truncate(this string s, int maxLength) =>
        s.Length <= maxLength ? s : s[..maxLength] + "...";
}
