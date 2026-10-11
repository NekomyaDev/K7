using K7.Server.Application.Common.Interfaces;
using K7.Server.Domain.Entities.Medias;
using K7.Server.Domain.Entities.Users;

namespace K7.Server.Application.Common.Mappings;

/// <summary>
/// Stamps the current user's watch state onto already loaded media so lite
/// mappings can surface the watched chip without a second projection.
/// Series and seasons use episode aggregation, same as library browse.
/// </summary>
internal static class UserWatchStateAttacher
{
    public static async Task AttachAsync(
        IApplicationDbContext context,
        IEnumerable<BaseMedia?> medias,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var list = medias.Where(media => media is not null).Cast<BaseMedia>().ToList();
        if (list.Count == 0)
            return;

        var ids = list.Select(media => media.Id).Distinct().ToList();
        var states = await context.UserMediaStates
            .AsNoTracking()
            .Where(state => state.UserId == userId && ids.Contains(state.MediaId))
            .ToListAsync(cancellationToken);

        var byMediaId = states.ToDictionary(state => state.MediaId);
        foreach (var media in list)
        {
            media.UserMediaStates = byMediaId.TryGetValue(media.Id, out var state)
                ? [state]
                : [];
        }

        var serieIds = list.OfType<Serie>().Select(serie => serie.Id).ToList();
        var seasonIds = list.OfType<SerieSeason>().Select(season => season.Id).ToList();
        if (serieIds.Count == 0 && seasonIds.Count == 0)
            return;

        var aggregated = await SerieWatchStateAggregator.AggregateAsync(
            context,
            serieIds,
            seasonIds,
            userId,
            sharedProfileId: null,
            cancellationToken);

        foreach (var media in list)
        {
            if (!aggregated.TryGetValue(media.Id, out var dto))
                continue;

            media.UserMediaStates =
            [
                new UserMediaState
                {
                    UserId = userId,
                    MediaId = media.Id,
                    IsCompleted = dto.IsCompleted,
                    PlayCount = dto.PlayCount,
                    SkipCount = dto.SkipCount,
                    LastInteractedAt = dto.LastInteractedAt
                }
            ];
        }
    }
}
