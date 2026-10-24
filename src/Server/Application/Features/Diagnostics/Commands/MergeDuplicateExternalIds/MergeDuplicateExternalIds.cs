using K7.Server.Application.Common.Interfaces;
using K7.Server.Application.Features.Medias.Commands.RefreshMediaMetadatas;
using K7.Server.Application.Helpers;
using K7.Server.Domain.Entities.Medias;
using K7.Server.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace K7.Server.Application.Features.Diagnostics.Commands.MergeDuplicateExternalIds;

public enum DuplicateMergeScope
{
    ExternalIds,
    NamedArtistsAndPersons
}

/// <summary>
/// Worker payload for duplicate merges. Enqueued only via authorized diagnostics fix commands.
/// No <c>Authorize</c> attribute: background execution has no HTTP user.
/// For shared external ids, music albums involved are refreshed first so wrong recording/ISRC
/// stamps can be corrected before any merge runs.
/// </summary>
public record MergeDuplicateExternalIdsCommand : IRequest<int>
{
    public Guid? LibraryId { get; init; }

    public DuplicateMergeScope Scope { get; init; } = DuplicateMergeScope.ExternalIds;

    /// <summary>
    /// When set, only groups that contain one of these ids are merged.
    /// </summary>
    public IReadOnlyCollection<Guid>? LimitToIds { get; init; }
}

public class MergeDuplicateExternalIdsCommandHandler(
    IApplicationDbContext context,
    ISender sender,
    ILogger<MergeDuplicateExternalIdsCommandHandler> logger)
    : IRequestHandler<MergeDuplicateExternalIdsCommand, int>
{
    public async Task<int> Handle(MergeDuplicateExternalIdsCommand request, CancellationToken cancellationToken)
    {
        if (request.Scope == DuplicateMergeScope.NamedArtistsAndPersons)
        {
            return await DuplicateExternalIdMergeHelper.MergeNamedArtistsAndPersonsAsync(
                context, request.LibraryId, request.LimitToIds, logger, cancellationToken);
        }

        await RefreshAlbumsForDuplicateMusicTracksAsync(request, cancellationToken);

        return await DuplicateExternalIdMergeHelper.MergeAllAsync(
            context, request.LibraryId, logger, cancellationToken, request.LimitToIds);
    }

    /// <summary>
    /// Dedup key for background enqueue. Library-wide uses the library id (or empty).
    /// Named scope XORs the last byte so it does not collide with ExternalIds.
    /// Selected merges XOR-fold entity ids so concurrent selections stay distinct.
    /// </summary>
    public static Guid ResolveMergeTargetEntityId(
        Guid? libraryId,
        DuplicateMergeScope scope,
        IReadOnlyCollection<Guid>? limitToIds = null)
    {
        byte[] bytes;
        if (limitToIds is { Count: > 0 })
        {
            bytes = new byte[16];
            foreach (var id in limitToIds.OrderBy(x => x))
            {
                var idBytes = id.ToByteArray();
                for (var i = 0; i < 16; i++)
                    bytes[i] ^= idBytes[i];
            }
        }
        else
        {
            bytes = (libraryId ?? Guid.Empty).ToByteArray();
        }

        if (scope == DuplicateMergeScope.NamedArtistsAndPersons)
            bytes[^1] ^= 0xA5;

        return new Guid(bytes);
    }

    private async Task RefreshAlbumsForDuplicateMusicTracksAsync(
        MergeDuplicateExternalIdsCommand request,
        CancellationToken cancellationToken)
    {
        var groups = await DuplicateMediaDiagnosticHelper.GetDuplicateExternalIdGroupsAsync(
            context, cancellationToken);

        if (request.LibraryId is Guid selectedLibraryId)
        {
            var inLibrary = await context.MediaLibraryAvailabilities
                .AsNoTracking()
                .Where(a => a.LibraryId == selectedLibraryId)
                .Select(a => a.MediaId)
                .ToListAsync(cancellationToken);
            var allowed = inLibrary.ToHashSet();
            groups = groups.Where(g => g.MediaIds.Any(allowed.Contains)).ToList();
        }

        if (request.LimitToIds is not null)
        {
            var limit = request.LimitToIds.ToHashSet();
            groups = groups.Where(g => g.MediaIds.Any(limit.Contains)).ToList();
        }

        var candidateIds = groups.SelectMany(g => g.MediaIds).Distinct().ToList();
        if (candidateIds.Count == 0)
            return;

        var albumIds = await context.Medias
            .OfType<MusicTrack>()
            .AsNoTracking()
            .Where(t => candidateIds.Contains(t.Id))
            .Select(t => t.AlbumId)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (albumIds.Count == 0)
            return;

        logger.LogInformation(
            "Refreshing {AlbumCount} albums before duplicate external-id merge",
            albumIds.Count);

        var refreshed = 0;
        foreach (var albumId in albumIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await TryRefreshAlbumInlineAsync(albumId, cancellationToken))
                    refreshed++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to refresh album {AlbumId} before duplicate merge", albumId);
            }
        }

        logger.LogInformation(
            "Refreshed {RefreshedCount}/{AlbumCount} albums before duplicate external-id merge",
            refreshed,
            albumIds.Count);
    }

    private async Task<bool> TryRefreshAlbumInlineAsync(Guid albumId, CancellationToken cancellationToken)
    {
        var album = await context.Medias
            .OfType<MusicAlbum>()
            .Include(a => a.ExternalIds)
            .FirstOrDefaultAsync(a => a.Id == albumId, cancellationToken);
        if (album is null)
            return false;

        var library = await MediaLibraryLinkageHelper.FindLibraryAsync(context, album, cancellationToken);
        if (library is null)
            return false;

        // Prefer the library provider id, then release / release-group MusicBrainz ids already on the album.
        // Skip auto-resolve here: Fix-all should not block on search when the album has no provider id yet.
        var externalId = album.ExternalIds.FirstOrDefault(e =>
                string.Equals(e.ProviderName, library.MetadataProviderName, StringComparison.OrdinalIgnoreCase))
            ?? album.ExternalIds.FirstOrDefault(e =>
                string.Equals(e.ProviderName, "musicbrainz-release", StringComparison.OrdinalIgnoreCase))
            ?? album.ExternalIds.FirstOrDefault(e =>
                string.Equals(e.ProviderName, "musicbrainz", StringComparison.OrdinalIgnoreCase));
        if (externalId is null || string.IsNullOrWhiteSpace(externalId.Value))
            return false;

        var refreshProviderName = string.Equals(
                externalId.ProviderName,
                "musicbrainz-release",
                StringComparison.OrdinalIgnoreCase)
            ? "musicbrainz"
            : MetadataProviderHostMapper.NormalizeProviderName(externalId.ProviderName);

        await sender.Send(new RefreshMediaMetadatasCommand
        {
            MediaId = album.Id,
            MetadataProviderExternalId = externalId.Value,
            MetadataProviderName = refreshProviderName,
            Language = library.MetadataLanguage,
            FallbackLanguage = library.MetadataFallbackLanguage
        }, cancellationToken);

        return true;
    }
}
