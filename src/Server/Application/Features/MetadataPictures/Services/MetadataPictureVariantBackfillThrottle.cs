using System.Collections.Concurrent;

namespace K7.Server.Application.Features.MetadataPictures.Services;

/// <summary>
/// Remembers which pictures had a missing size variant requested recently, so a picture served
/// from its original enqueues one generation task per window instead of one per image fetch.
/// The background task queue already deduplicates active tasks; this keeps the picture endpoint
/// off the task table on every request and stops a picture whose variant cannot be produced
/// (corrupt file, unsupported format) from being re-enqueued on each view.
/// </summary>
public sealed class MetadataPictureVariantBackfillThrottle
{
    private const int PruneThreshold = 4096;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _requestedAt = new();
    private readonly TimeProvider _timeProvider;

    public MetadataPictureVariantBackfillThrottle()
        : this(TimeProvider.System)
    {
    }

    public MetadataPictureVariantBackfillThrottle(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// True when no backfill was requested for this picture within the window; the caller then
    /// owns the request. Later calls for the same picture return false until the window passes.
    /// </summary>
    public bool TryBegin(Guid pictureId)
    {
        var now = _timeProvider.GetUtcNow();

        while (true)
        {
            if (_requestedAt.TryAdd(pictureId, now))
            {
                if (_requestedAt.Count > PruneThreshold)
                    Prune(now);

                return true;
            }

            if (!_requestedAt.TryGetValue(pictureId, out var previous))
                continue;

            if (now - previous < Window)
                return false;

            if (_requestedAt.TryUpdate(pictureId, now, previous))
                return true;
        }
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var entry in _requestedAt)
        {
            if (now - entry.Value >= Window)
                _requestedAt.TryRemove(entry.Key, out _);
        }
    }
}
