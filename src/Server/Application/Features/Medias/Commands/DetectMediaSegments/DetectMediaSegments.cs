using System.Numerics;
using K7.Server.Application.Common.Interfaces;
using K7.Server.Application.Features.BackgroundTasks.Commands.CreateBackgroundTask;
using K7.Server.Application.Features.Medias.Commands.ExtractSerieThemeSong;
using K7.Server.Application.Helpers;
using K7.Server.Domain.Constants;
using K7.Server.Domain.Entities;
using K7.Server.Domain.Entities.Medias;
using K7.Server.Domain.Entities.Metadatas.Files;
using K7.Server.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace K7.Server.Application.Features.Medias.Commands.DetectMediaSegments;

public record DetectMediaSegmentsCommand : IRequest
{
    public required Guid SeasonId { get; init; }
}

public class DetectMediaSegmentsCommandHandler : IRequestHandler<DetectMediaSegmentsCommand>
{
    private readonly IApplicationDbContext _context;
    private readonly IChromaprintService _chromaprintService;
    private readonly ISegmentDetectionService _segmentDetectionService;
    private readonly ISender _sender;
    private readonly ILogger<DetectMediaSegmentsCommandHandler> _logger;

    // Segment duration limits (seconds) - keep in sync with ChapterSegmentFallbackHelper / matcher calls
    private const double MinIntroDurationSeconds = 15;
    private const double MaxIntroDurationSeconds = 120;
    private const double MinOutroDurationSeconds = 15;
    private const double MaxOutroDurationSeconds = 450;

    // Analysis window
    private const double AnalysisLengthLimitSeconds = 600;
    private const double AnalysisPercent = 0.25;

    // Boundary adjustment
    private const double SnapToStartThresholdSeconds = 5.0;
    private const double SnapToEndThresholdSeconds = 2.0;
    private const double AdjustWindowInwardSeconds = 5.0;
    private const double AdjustWindowOutwardSeconds = 2.0;

    public DetectMediaSegmentsCommandHandler(
        IApplicationDbContext context,
        IChromaprintService chromaprintService,
        ISegmentDetectionService segmentDetectionService,
        ISender sender,
        ILogger<DetectMediaSegmentsCommandHandler> logger)
    {
        _context = context;
        _chromaprintService = chromaprintService;
        _segmentDetectionService = segmentDetectionService;
        _sender = sender;
        _logger = logger;
    }

    public async Task Handle(DetectMediaSegmentsCommand request, CancellationToken cancellationToken)
    {
        var season = await _context.Medias
            .AsNoTracking()
            .OfType<SerieSeason>()
            .Include(s => s.Episodes)
                .ThenInclude(e => e.IndexedFiles)
                    .ThenInclude(f => f.FileMetadata)
            .Include(s => s.Episodes)
                .ThenInclude(e => e.Segments)
            .FirstOrDefaultAsync(s => s.Id == request.SeasonId, cancellationToken);

        if (season is null)
        {
            _logger.LogWarning("Season {SeasonId} not found for segment detection", request.SeasonId);
            return;
        }

        var serieLocked = await _context.Medias
            .OfType<Serie>()
            .AsNoTracking()
            .Where(s => s.Id == season.SerieId)
            .Select(s => s.LockedFields)
            .FirstOrDefaultAsync(cancellationToken);

        if (serieLocked is not null && serieLocked.Contains(MediaLockFields.IntroOutro))
        {
            _logger.LogInformation(
                "Skipping segment detection for season {SeasonId}: serie {SerieId} has {Lock} locked",
                request.SeasonId,
                season.SerieId,
                MediaLockFields.IntroOutro);
            return;
        }

        var episodes = season.Episodes
            .Where(e => e.IndexedFiles.Any(f => f.FileMetadata is not null && File.Exists(f.Path)))
            .OrderBy(e => e.EpisodeNumber)
            .ToList();

        if (episodes.Count < 2)
        {
            _logger.LogDebug("Season {SeasonId} has fewer than 2 episodes, skipping detection", request.SeasonId);
            return;
        }

        var hasExistingSegments = episodes.Any(e => e.Segments.Count > 0);

        List<MediaSegment> newSegments;
        if (hasExistingSegments)
            newSegments = await RunIncrementalDetectionAsync(episodes, cancellationToken);
        else
            newSegments = await RunFullDetectionAsync(episodes, cancellationToken);

        var chapterFallbacks = await BuildChapterFallbackSegmentsAsync(episodes, newSegments, cancellationToken);
        if (chapterFallbacks.Count > 0)
            newSegments.AddRange(chapterFallbacks);

        if (newSegments.Count > 0)
        {
            // Remove existing detected segments for episodes that will get new ones
            var episodeIds = newSegments.Select(s => s.MediaId).Distinct().ToList();
            var segmentTypes = newSegments.Select(s => s.Type).Distinct().ToList();

            await _context.MediaSegments
                .Where(s => episodeIds.Contains(s.MediaId) && segmentTypes.Contains(s.Type))
                .ExecuteDeleteAsync(cancellationToken);

            _context.MediaSegments.AddRange(newSegments);
            await _context.SaveChangesAsync(cancellationToken);
        }

        await TryQueueThemeExtractAsync(season.SerieId, cancellationToken);
    }

    private async Task TryQueueThemeExtractAsync(Guid serieId, CancellationToken cancellationToken)
    {
        var themeGenerationEnabled = await (
            from episode in _context.Medias.OfType<SerieEpisode>().AsNoTracking()
            where episode.SerieId == serieId
            join file in _context.IndexedFiles.AsNoTracking() on episode.Id equals file.MediaId
            join library in _context.Libraries.AsNoTracking() on file.LibraryId equals library.Id
            where library.ThemeSongGenerationEnabled && library.IntroDetectionEnabled
            select library.Id
        ).AnyAsync(cancellationToken);

        if (!themeGenerationEnabled)
            return;

        var hasIntro = await (
            from episode in _context.Medias.OfType<SerieEpisode>().AsNoTracking()
            where episode.SerieId == serieId
            join segment in _context.MediaSegments.AsNoTracking() on episode.Id equals segment.MediaId
            where segment.Type == MediaSegmentType.Intro
            select segment.Id
        ).AnyAsync(cancellationToken);

        if (!hasIntro)
            return;

        await _sender.Send(new CreateBackgroundTaskCommand
        {
            Request = new ExtractSerieThemeSongCommand { SerieId = serieId },
            TargetEntityId = serieId,
            TargetEntityTypeName = nameof(Serie),
            Lane = BackgroundTaskLane.MediaAnalysis,
            WorkClass = BackgroundTaskWorkClass.Polish,
            TriggeredBy = BackgroundTaskTriggeredBy.System,
            MaxAttempts = 2
        }, cancellationToken);
    }

    private async Task<List<MediaSegment>> RunFullDetectionAsync(List<SerieEpisode> episodes, CancellationToken cancellationToken)
    {
        var segments = new List<MediaSegment>();
        var episodeData = await ExtractAllFingerprintsAsync(episodes, cancellationToken);

        if (episodeData.Count < 2)
        {
            _logger.LogWarning("Only {Count} episodes with valid fingerprints, need at least 2", episodeData.Count);
            return segments;
        }

        LogFingerprintDiagnostics(episodeData);

        var introMatches = ChromaprintFingerprintMatcher.FindBestMatches(
            episodeData.Select(e => e.IntroFingerprint).ToList(),
            MinIntroDurationSeconds,
            MaxIntroDurationSeconds);
        var outroMatches = ChromaprintFingerprintMatcher.FindBestMatches(
            episodeData.Select(e => e.OutroFingerprint).ToList(),
            MinOutroDurationSeconds,
            MaxOutroDurationSeconds);

        _logger.LogInformation(
            "Found intros for {IntroCount}/{Total} and outros for {OutroCount}/{Total} episodes (neighborRadius={Radius})",
            introMatches.Count, episodeData.Count, outroMatches.Count, episodeData.Count,
            ChromaprintFingerprintMatcher.NeighborRadius);

        for (var i = 0; i < episodeData.Count; i++)
        {
            var data = episodeData[i];
            if (introMatches.TryGetValue(i, out var introMatch))
            {
                var (start, end) = await AdjustSegmentBoundariesAsync(
                    data.File.Path, introMatch.StartSeconds, introMatch.EndSeconds,
                    data.DurationSeconds, cancellationToken);

                segments.Add(new MediaSegment
                {
                    Id = Guid.NewGuid(),
                    MediaId = data.Episode.Id,
                    Type = MediaSegmentType.Intro,
                    StartMs = (long)(start * 1000),
                    EndMs = (long)(end * 1000),
                    DetectedAt = DateTimeOffset.UtcNow
                });
            }

            if (outroMatches.TryGetValue(i, out var outroMatch))
            {
                var outroOffset = data.DurationSeconds - GetAnalysisWindowSeconds(data.DurationSeconds);
                var absoluteStart = outroOffset + outroMatch.StartSeconds;
                var absoluteEnd = outroOffset + outroMatch.EndSeconds;

                var (start, end) = await AdjustSegmentBoundariesAsync(
                    data.File.Path, absoluteStart, absoluteEnd,
                    data.DurationSeconds, cancellationToken);

                segments.Add(new MediaSegment
                {
                    Id = Guid.NewGuid(),
                    MediaId = data.Episode.Id,
                    Type = MediaSegmentType.Outro,
                    StartMs = (long)(start * 1000),
                    EndMs = (long)(end * 1000),
                    DetectedAt = DateTimeOffset.UtcNow
                });
            }
        }

        return segments;
    }

    private void LogFingerprintDiagnostics(List<EpisodeData> episodeData)
    {
        var introLengths = episodeData
            .Where(e => e.IntroFingerprint is not null)
            .Select(e => e.IntroFingerprint!.Length)
            .ToList();
        var outroLengths = episodeData
            .Where(e => e.OutroFingerprint is not null)
            .Select(e => e.OutroFingerprint!.Length)
            .ToList();

        _logger.LogInformation(
            "Fingerprint extraction: {Total} episodes, intros: {IntroCount} (min={IntroMin}, max={IntroMax}), outros: {OutroCount} (min={OutroMin}, max={OutroMax})",
            episodeData.Count,
            introLengths.Count,
            introLengths.Count > 0 ? introLengths.Min() : 0,
            introLengths.Count > 0 ? introLengths.Max() : 0,
            outroLengths.Count,
            outroLengths.Count > 0 ? outroLengths.Min() : 0,
            outroLengths.Count > 0 ? outroLengths.Max() : 0);

        // Diagnostic: PopCount distribution for first pair at shift=0
        if (episodeData.Count >= 2)
        {
            var fp1 = episodeData[0].IntroFingerprint;
            var fp2 = episodeData[1].IntroFingerprint;
            if (fp1 is not null && fp2 is not null)
            {
                var overlapLen = Math.Min(fp1.Length, fp2.Length);
                var popCounts = new int[33]; // 0-32 bits
                for (var i = 0; i < overlapLen; i++)
                {
                    var pc = BitOperations.PopCount(fp1[i] ^ fp2[i]);
                    popCounts[pc]++;
                }

                var matchingAtThreshold = popCounts
                    .Take(ChromaprintFingerprintMatcher.MaxFingerprintPointDifferences + 1)
                    .Sum();
                _logger.LogInformation(
                    "Diagnostic ep1 vs ep2 at shift=0: overlap={Overlap}, matching (popcount<={Threshold}): {Matching}/{Total} ({Pct:F1}%), popcount[0]={P0}, [1-3]={P13}, [4-6]={P46}, [7-10]={P710}, [11+]={P11}",
                    overlapLen, ChromaprintFingerprintMatcher.MaxFingerprintPointDifferences,
                    matchingAtThreshold, overlapLen,
                    overlapLen > 0 ? 100.0 * matchingAtThreshold / overlapLen : 0,
                    popCounts[0],
                    popCounts[1] + popCounts[2] + popCounts[3],
                    popCounts[4] + popCounts[5] + popCounts[6],
                    popCounts[7] + popCounts[8] + popCounts[9] + popCounts[10],
                    popCounts.Skip(11).Sum());
            }
        }
    }

    private async Task<List<MediaSegment>> RunIncrementalDetectionAsync(List<SerieEpisode> episodes, CancellationToken cancellationToken)
    {
        var segments = new List<MediaSegment>();
        var incomplete = episodes
            .Where(e => IntroOutroSegmentCompleteness.IsIncomplete(
                e.Segments.Select(s => s.Type).ToHashSet()))
            .ToList();

        if (incomplete.Count == 0)
            return segments;

        // Fingerprints for the whole season: incomplete targets match against every other episode
        // (including outro-only peers), so a missing Intro can still be retried after Outro exists.
        var allData = await ExtractAllFingerprintsAsync(episodes, cancellationToken);
        if (allData.Count < 2)
            return segments;

        var incompleteIds = incomplete.Select(e => e.Id).ToHashSet();
        var presentTypesByEpisode = incomplete.ToDictionary(
            e => e.Id,
            e => (IReadOnlyCollection<MediaSegmentType>)e.Segments.Select(s => s.Type).ToHashSet());

        foreach (var target in allData.Where(d => incompleteIds.Contains(d.Episode.Id)))
        {
            var present = presentTypesByEpisode[target.Episode.Id];
            var references = allData.Where(d => d.Episode.Id != target.Episode.Id).ToList();
            if (references.Count == 0)
                continue;

            if (IntroOutroSegmentCompleteness.NeedsType(present, MediaSegmentType.Intro))
            {
                var introSegment = await TryDetectSegmentFromReferencesAsync(
                    target, references, MediaSegmentType.Intro, cancellationToken);
                if (introSegment is not null)
                    segments.Add(introSegment);
            }

            if (IntroOutroSegmentCompleteness.NeedsType(present, MediaSegmentType.Outro))
            {
                var outroSegment = await TryDetectSegmentFromReferencesAsync(
                    target, references, MediaSegmentType.Outro, cancellationToken);
                if (outroSegment is not null)
                    segments.Add(outroSegment);
            }
        }

        return segments;
    }

    private async Task<MediaSegment?> TryDetectSegmentFromReferencesAsync(
        EpisodeData newEp,
        List<EpisodeData> references,
        MediaSegmentType segmentType,
        CancellationToken cancellationToken)
    {
        var isOutro = segmentType == MediaSegmentType.Outro;
        var newFp = isOutro ? newEp.OutroFingerprint : newEp.IntroFingerprint;
        if (newFp is null)
            return null;

        var minDuration = isOutro ? MinOutroDurationSeconds : MinIntroDurationSeconds;
        var maxDuration = isOutro ? MaxOutroDurationSeconds : MaxIntroDurationSeconds;

        ChromaprintFingerprintMatcher.SegmentMatch? best = null;
        var bestDuration = 0.0;

        foreach (var refEp in references)
        {
            var refFp = isOutro ? refEp.OutroFingerprint : refEp.IntroFingerprint;
            if (refFp is null)
                continue;

            var match = ChromaprintFingerprintMatcher.CompareFingerprints(refFp, newFp);
            if (match is null)
                continue;

            var duration = match.Value.RhsEndSeconds - match.Value.RhsStartSeconds;
            if (duration < minDuration || duration > maxDuration)
                continue;

            if (duration > bestDuration)
            {
                bestDuration = duration;
                best = new ChromaprintFingerprintMatcher.SegmentMatch(
                    match.Value.RhsStartSeconds, match.Value.RhsEndSeconds);
            }
        }

        if (best is null)
            return null;

        var startSeconds = best.Value.StartSeconds;
        var endSeconds = best.Value.EndSeconds;

        if (isOutro)
        {
            var outroOffset = newEp.DurationSeconds - GetAnalysisWindowSeconds(newEp.DurationSeconds);
            startSeconds += outroOffset;
            endSeconds += outroOffset;
        }

        var (start, end) = await AdjustSegmentBoundariesAsync(
            newEp.File.Path, startSeconds, endSeconds,
            newEp.DurationSeconds, cancellationToken);

        return new MediaSegment
        {
            Id = Guid.NewGuid(),
            MediaId = newEp.Episode.Id,
            Type = segmentType,
            StartMs = (long)(start * 1000),
            EndMs = (long)(end * 1000),
            DetectedAt = DateTimeOffset.UtcNow
        };
    }

    // -- Fingerprint extraction --

    private async Task<List<EpisodeData>> ExtractAllFingerprintsAsync(
        List<SerieEpisode> episodes,
        CancellationToken cancellationToken)
    {
        var result = new List<EpisodeData>();

        foreach (var episode in episodes)
        {
            var file = episode.IndexedFiles.FirstOrDefault(f => f.FileMetadata is not null && File.Exists(f.Path));
            if (file is null)
                continue;

            var durationSeconds = GetDurationSeconds(file);
            if (durationSeconds <= 0)
                continue;

            var analysisWindow = GetAnalysisWindowSeconds(durationSeconds);

            var introFpBytes = await _chromaprintService.ExtractFingerprintAsync(
                file.Path, TimeSpan.Zero, TimeSpan.FromSeconds(analysisWindow), cancellationToken);

            var outroStart = durationSeconds - analysisWindow;
            var outroFpBytes = await _chromaprintService.ExtractFingerprintAsync(
                file.Path, TimeSpan.FromSeconds(outroStart), TimeSpan.FromSeconds(analysisWindow), cancellationToken);

            var introFp = introFpBytes is not null ? ConvertToUints(introFpBytes) : null;
            var outroFp = outroFpBytes is not null ? ConvertToUints(outroFpBytes) : null;

            if (introFp is null && outroFp is null)
                continue;

            result.Add(new EpisodeData(episode, file, durationSeconds, introFp, outroFp));
        }

        return result;
    }

    // -- Chapter-title fallback (when chromaprint misses Intro/Outro) --

    private async Task<List<MediaSegment>> BuildChapterFallbackSegmentsAsync(
        List<SerieEpisode> episodes,
        List<MediaSegment> chromaprintSegments,
        CancellationToken cancellationToken)
    {
        var segments = new List<MediaSegment>();
        var byEpisode = chromaprintSegments
            .GroupBy(s => s.MediaId)
            .ToDictionary(g => g.Key, g => g.Select(s => s.Type).ToHashSet());

        foreach (var episode in episodes)
        {
            var file = episode.IndexedFiles.FirstOrDefault(f => f.FileMetadata is not null && File.Exists(f.Path));
            if (file is null)
                continue;

            var durationSeconds = GetDurationSeconds(file);
            if (durationSeconds <= 0)
                continue;

            var existing = byEpisode.GetValueOrDefault(episode.Id)
                ?? episode.Segments.Select(s => s.Type).ToHashSet();

            var needIntro = !existing.Contains(MediaSegmentType.Intro);
            var needOutro = !existing.Contains(MediaSegmentType.Outro);
            if (!needIntro && !needOutro)
                continue;

            var chapters = await _segmentDetectionService.GetChaptersAsync(file.Path, cancellationToken);
            if (chapters.Count == 0)
                continue;

            if (needIntro)
            {
                foreach (var chapter in chapters)
                {
                    if (!ChapterSegmentFallbackHelper.TryCreateSegment(
                            chapter, durationSeconds, MediaSegmentType.Intro, out var start, out var end))
                        continue;

                    segments.Add(new MediaSegment
                    {
                        Id = Guid.NewGuid(),
                        MediaId = episode.Id,
                        Type = MediaSegmentType.Intro,
                        StartMs = (long)(start * 1000),
                        EndMs = (long)(end * 1000),
                        DetectedAt = DateTimeOffset.UtcNow
                    });
                    _logger.LogInformation(
                        "Chapter fallback Intro for episode {EpisodeId}: {Start:F1}s-{End:F1}s ({Title})",
                        episode.Id, start, end, chapter.Title);
                    break;
                }
            }

            if (needOutro)
            {
                foreach (var chapter in chapters.Reverse())
                {
                    if (!ChapterSegmentFallbackHelper.TryCreateSegment(
                            chapter, durationSeconds, MediaSegmentType.Outro, out var start, out var end))
                        continue;

                    segments.Add(new MediaSegment
                    {
                        Id = Guid.NewGuid(),
                        MediaId = episode.Id,
                        Type = MediaSegmentType.Outro,
                        StartMs = (long)(start * 1000),
                        EndMs = (long)(end * 1000),
                        DetectedAt = DateTimeOffset.UtcNow
                    });
                    _logger.LogInformation(
                        "Chapter fallback Outro for episode {EpisodeId}: {Start:F1}s-{End:F1}s ({Title})",
                        episode.Id, start, end, chapter.Title);
                    break;
                }
            }
        }

        return segments;
    }

    // -- Boundary adjustment --

    private async Task<(double StartSeconds, double EndSeconds)> AdjustSegmentBoundariesAsync(
        string filePath,
        double startSeconds,
        double endSeconds,
        double totalDurationSeconds,
        CancellationToken cancellationToken)
    {
        var adjustedStart = startSeconds;
        var adjustedEnd = endSeconds;

        var chapters = await _segmentDetectionService.GetChapterTimesAsync(filePath, cancellationToken);

        // Adjust start: snap to beginning or nearest chapter
        if (adjustedStart <= SnapToStartThresholdSeconds)
        {
            adjustedStart = 0;
        }
        else
        {
            var chapterSnap = FindNearestInWindow(chapters,
                adjustedStart,
                adjustedStart - AdjustWindowOutwardSeconds,
                adjustedStart + AdjustWindowInwardSeconds);
            if (chapterSnap is not null)
                adjustedStart = chapterSnap.Value;
        }

        // Adjust end: snap to end, or chapter, or silence, or black frame, or keyframe
        if (adjustedEnd >= totalDurationSeconds - SnapToEndThresholdSeconds)
        {
            adjustedEnd = totalDurationSeconds;
        }
        else
        {
            var chapterSnap = FindNearestInWindow(chapters,
                adjustedEnd,
                adjustedEnd - AdjustWindowInwardSeconds,
                adjustedEnd + AdjustWindowOutwardSeconds);

            if (chapterSnap is not null)
            {
                adjustedEnd = chapterSnap.Value;
            }
            else
            {
                var windowLow = adjustedEnd - AdjustWindowInwardSeconds;
                var windowHigh = adjustedEnd + AdjustWindowOutwardSeconds;
                var ffmpegStart = Math.Max(0, windowLow);
                var ffmpegDuration = windowHigh - ffmpegStart;

                // Try silence
                var silences = await _segmentDetectionService.DetectSilenceAsync(
                    filePath, ffmpegStart, ffmpegDuration, cancellationToken);
                var silenceSnap = FindFirstSilenceInWindow(silences, windowLow, windowHigh);

                if (silenceSnap is not null)
                {
                    adjustedEnd = silenceSnap.Value;
                }
                else
                {
                    // Try black frames
                    var blackFrames = await _segmentDetectionService.DetectBlackFrameTimestampsAsync(
                        filePath, ffmpegStart, ffmpegDuration, cancellationToken);
                    var blackFrameSnap = FindNearestInWindow(blackFrames, adjustedEnd, windowLow, windowHigh);

                    if (blackFrameSnap is not null)
                    {
                        adjustedEnd = blackFrameSnap.Value;
                    }
                    else
                    {
                        // Try keyframes
                        var keyframes = await _segmentDetectionService.DetectKeyframeTimestampsAsync(
                            filePath, ffmpegStart, ffmpegDuration, cancellationToken);
                        var keyframeSnap = FindNearestInWindow(keyframes, adjustedEnd, windowLow, windowHigh);
                        if (keyframeSnap is not null)
                            adjustedEnd = keyframeSnap.Value;
                    }
                }
            }
        }

        if (adjustedStart >= adjustedEnd)
            return (startSeconds, endSeconds);

        return (adjustedStart, adjustedEnd);
    }

    private static double? FindNearestInWindow(
        IReadOnlyList<double> timestamps,
        double target,
        double windowStart,
        double windowEnd)
    {
        double? nearest = null;
        var minDistance = double.MaxValue;

        foreach (var t in timestamps)
        {
            if (t < windowStart || t > windowEnd)
                continue;

            var distance = Math.Abs(t - target);
            if (distance < minDistance)
            {
                minDistance = distance;
                nearest = t;
            }
        }

        return nearest;
    }

    private static double? FindFirstSilenceInWindow(
        IReadOnlyList<SilencePeriod> silences,
        double windowStart,
        double windowEnd)
    {
        foreach (var silence in silences)
        {
            if (silence.StartSeconds >= windowStart && silence.StartSeconds <= windowEnd)
                return silence.StartSeconds;
        }

        return null;
    }

    // -- Helpers --

    private static uint[] ConvertToUints(byte[] data)
    {
        var count = data.Length / sizeof(uint);
        var result = new uint[count];
        Buffer.BlockCopy(data, 0, result, 0, count * sizeof(uint));
        return result;
    }

    private static double GetDurationSeconds(IndexedFile file)
    {
        return file.FileMetadata switch
        {
            VideoFileMetadata v => v.Duration.TotalSeconds,
            AudioFileMetadata a => a.Duration.TotalSeconds,
            _ => 0
        };
    }

    private static double GetAnalysisWindowSeconds(double totalDurationSeconds)
    {
        return Math.Min(totalDurationSeconds * AnalysisPercent, AnalysisLengthLimitSeconds);
    }

    // -- Inner types --

    private record EpisodeData(
        SerieEpisode Episode,
        IndexedFile File,
        double DurationSeconds,
        uint[]? IntroFingerprint,
        uint[]? OutroFingerprint);
}
