using K7.Server.Application.Common.Interfaces;
using K7.Server.Domain.Enums;
using K7.Shared.Dtos.Entities;
using K7.Shared.Dtos.Federation.Social;
using Microsoft.EntityFrameworkCore;

namespace K7.Server.Application.Features.Federation.Services;

public class FederatedMediaResolver(IApplicationDbContext context) : IFederatedMediaResolver
{
    private static readonly string[] ProviderPriority =
    [
        "tmdb",
        "imdb",
        "tvdb",
        "musicbrainz-release",
        "musicbrainz",
        "isrc",
        "spotify"
    ];

    public async Task<FederatedMediaResolutionResult> ResolveAsync(
        Guid peerServerId,
        FederatedMediaRef mediaRef,
        CancellationToken cancellationToken = default)
    {
        var results = await ResolveManyAsync(peerServerId, [mediaRef], cancellationToken);
        return results[mediaRef];
    }

    public async Task<IReadOnlyDictionary<FederatedMediaRef, FederatedMediaResolutionResult>> ResolveManyAsync(
        Guid peerServerId,
        IReadOnlyList<FederatedMediaRef> mediaRefs,
        CancellationToken cancellationToken = default)
    {
        var results = new Dictionary<FederatedMediaRef, FederatedMediaResolutionResult>(mediaRefs.Count);

        foreach (var mediaRef in mediaRefs)
        {
            var resolved = await ResolveSingleAsync(peerServerId, mediaRef, cancellationToken);
            results[mediaRef] = resolved;
        }

        return results;
    }

    public async Task<bool> MatchesLocalMediaAsync(
        Guid peerServerId,
        FederatedMediaRef mediaRef,
        Guid localMediaId,
        CancellationToken cancellationToken = default)
    {
        var localType = await context.Medias
            .AsNoTracking()
            .Where(m => m.Id == localMediaId)
            .Select(m => (MediaType?)m.Type)
            .FirstOrDefaultAsync(cancellationToken);

        if (localType != mediaRef.Type)
            return false;

        foreach (var externalId in EnumerateMatchableExternalIds(mediaRef))
        {
            var provider = externalId.Provider.ToLowerInvariant();
            var matches = await context.ExternalIds
                .AnyAsync(
                    e => e.MediaId == localMediaId
                        && e.ProviderName.ToLower() == provider
                        && e.Value == externalId.Value
                        && e.ProviderName != "federation",
                    cancellationToken);

            if (matches)
                return true;
        }

        var federationValue = $"{peerServerId}:{mediaRef.RemoteMediaId}";
        var hasFederationLink = await context.ExternalIds
            .AnyAsync(
                e => e.MediaId == localMediaId
                    && e.ProviderName == "federation"
                    && e.Value == federationValue,
                cancellationToken);

        if (hasFederationLink)
            return true;

        return await context.RemoteIndexedFiles
            .AnyAsync(
                f => f.PeerServerId == peerServerId
                    && f.RemoteMediaId == mediaRef.RemoteMediaId
                    && f.MediaId == localMediaId,
                cancellationToken);
    }

    private async Task<FederatedMediaResolutionResult> ResolveSingleAsync(
        Guid peerServerId,
        FederatedMediaRef mediaRef,
        CancellationToken cancellationToken)
    {
        foreach (var externalId in EnumerateMatchableExternalIds(mediaRef))
        {
            var mediaId = await FindLocalMediaIdAsync(externalId, mediaRef.Type, cancellationToken);
            if (mediaId.HasValue)
            {
                return new FederatedMediaResolutionResult
                {
                    Status = FederatedMediaResolutionStatus.ResolvedLocal,
                    LocalMediaId = mediaId.Value
                };
            }
        }

        var federationValue = $"{peerServerId}:{mediaRef.RemoteMediaId}";
        var federationMediaId = await context.ExternalIds
            .Where(e => e.ProviderName == "federation" && e.Value == federationValue && e.MediaId != null)
            .Select(e => e.MediaId)
            .FirstOrDefaultAsync(cancellationToken);

        if (federationMediaId.HasValue)
        {
            var remoteFileId = await context.RemoteIndexedFiles
                .Where(f => f.PeerServerId == peerServerId && f.RemoteMediaId == mediaRef.RemoteMediaId)
                .Select(f => (Guid?)f.Id)
                .FirstOrDefaultAsync(cancellationToken);

            if (remoteFileId.HasValue)
            {
                return new FederatedMediaResolutionResult
                {
                    Status = FederatedMediaResolutionStatus.ResolvedRemote,
                    LocalMediaId = federationMediaId.Value,
                    RemoteIndexedFileId = remoteFileId.Value
                };
            }

            return new FederatedMediaResolutionResult
            {
                Status = FederatedMediaResolutionStatus.ResolvedLocal,
                LocalMediaId = federationMediaId.Value
            };
        }

        var remoteOnly = await context.RemoteIndexedFiles
            .Where(f => f.PeerServerId == peerServerId && f.RemoteMediaId == mediaRef.RemoteMediaId)
            .Select(f => new { f.Id, f.MediaId })
            .FirstOrDefaultAsync(cancellationToken);

        if (remoteOnly is not null)
        {
            return new FederatedMediaResolutionResult
            {
                Status = FederatedMediaResolutionStatus.ResolvedRemote,
                LocalMediaId = remoteOnly.MediaId,
                RemoteIndexedFileId = remoteOnly.Id
            };
        }

        return new FederatedMediaResolutionResult
        {
            Status = FederatedMediaResolutionStatus.Unavailable
        };
    }

    private async Task<Guid?> FindLocalMediaIdAsync(
        PeerExternalIdDto externalId,
        MediaType mediaType,
        CancellationToken cancellationToken)
    {
        var provider = externalId.Provider.ToLowerInvariant();
        return await context.ExternalIds
            .Where(e => e.MediaId != null
                && e.Media!.Type == mediaType
                && e.ProviderName.ToLower() == provider
                && e.Value == externalId.Value
                && e.ProviderName != "federation")
            .Select(e => e.MediaId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static IEnumerable<PeerExternalIdDto> EnumerateMatchableExternalIds(FederatedMediaRef mediaRef)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var provider in ProviderPriority)
        {
            var externalId = mediaRef.ExternalIds.FirstOrDefault(e =>
                string.Equals(e.Provider, provider, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(e.Value));

            if (externalId is null || !seen.Add(externalId.Provider))
                continue;

            yield return externalId;
        }

        foreach (var externalId in mediaRef.ExternalIds)
        {
            if (string.IsNullOrWhiteSpace(externalId.Value))
                continue;

            if (string.Equals(externalId.Provider, "federation", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!seen.Add(externalId.Provider))
                continue;

            yield return externalId;
        }
    }
}
