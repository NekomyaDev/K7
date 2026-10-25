namespace K7.Clients.Shared.Interfaces;

/// <summary>
/// In-memory overlay of the current user's ratings so every RatingStars instance stays in sync.
/// </summary>
public interface IUserRatingSync
{
    event Action<Guid, int?>? Changed;

    bool TryGet(Guid mediaId, out int? value);

    void Set(Guid mediaId, int? value);

    void Clear();

    /// <summary>Media ids with a positive in-session rating (including offline / not yet flushed).</summary>
    IReadOnlyList<Guid> GetPositivelyRatedMediaIds();
}
