using K7.Server.Application.Common.Interfaces;
using K7.Server.Application.Helpers;
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
    ILogger<MergeDuplicateExternalIdsCommandHandler> logger)
    : IRequestHandler<MergeDuplicateExternalIdsCommand, int>
{
    public Task<int> Handle(MergeDuplicateExternalIdsCommand request, CancellationToken cancellationToken) =>
        request.Scope == DuplicateMergeScope.NamedArtistsAndPersons
            ? DuplicateExternalIdMergeHelper.MergeNamedArtistsAndPersonsAsync(
                context, request.LibraryId, request.LimitToIds, logger, cancellationToken)
            : DuplicateExternalIdMergeHelper.MergeAllAsync(
                context, request.LibraryId, logger, cancellationToken, request.LimitToIds);

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
}
