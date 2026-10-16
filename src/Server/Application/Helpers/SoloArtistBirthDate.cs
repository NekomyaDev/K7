using K7.Server.Application.Common.Interfaces;
using K7.Server.Application.Features.Medias.Services;
using K7.Server.Domain.Entities.Medias;
using K7.Server.Domain.Entities.Metadatas;
using K7.Server.Domain.Entities.Metadatas.PersonRoles;

namespace K7.Server.Application.Helpers;

/// <summary>
/// A solo artist has no formation date on MusicBrainz. The life span begin is the birth date,
/// stored on the person. Copy it onto the artist when that person is the artist.
/// </summary>
internal static class SoloArtistBirthDate
{
    internal static async Task ApplyAsync(
        IApplicationDbContext context,
        MusicArtist artist,
        CancellationToken cancellationToken = default)
    {
        if (artist.ReleaseDate is not null || artist.IsFieldLocked(nameof(MusicArtist.ReleaseDate)))
            return;

        var members = artist.PersonRoles.OfType<MusicArtistMember>().ToList();
        if (members.Count != 1)
            return;

        var person = await context.Persons
            .Include(p => p.ExternalIds)
            .FirstOrDefaultAsync(p => p.Id == members[0].PersonId, cancellationToken);
        if (person?.Birthday is not DateOnly birthday || !IsSamePerson(artist, person))
            return;

        artist.ReleaseDate = birthday;
    }

    internal static async Task ApplyForPersonAsync(
        IApplicationDbContext context,
        Person person,
        CancellationToken cancellationToken = default)
    {
        if (person.Birthday is not DateOnly birthday)
            return;

        var artistIds = await context.PersonRoles
            .OfType<MusicArtistMember>()
            .Where(role => role.PersonId == person.Id)
            .Select(role => role.MediaId)
            .Distinct()
            .ToListAsync(cancellationToken);

        foreach (var artistId in artistIds)
        {
            var memberCount = await context.PersonRoles
                .OfType<MusicArtistMember>()
                .CountAsync(role => role.MediaId == artistId, cancellationToken);
            if (memberCount != 1)
                continue;

            var artist = await context.Medias
                .OfType<MusicArtist>()
                .Include(a => a.ExternalIds)
                .Include(a => a.PersonRoles)
                .FirstOrDefaultAsync(a => a.Id == artistId, cancellationToken);
            if (artist is null || artist.ReleaseDate is not null || artist.IsFieldLocked(nameof(MusicArtist.ReleaseDate)))
                continue;
            if (!IsSamePerson(artist, person))
                continue;

            artist.ReleaseDate = birthday;
        }
    }

    private static bool IsSamePerson(MusicArtist artist, Person person)
    {
        var artistMb = MusicBrainzId(artist.ExternalIds.Select(e => (e.ProviderName, e.Value)));
        var personMb = MusicBrainzId(person.ExternalIds.Select(e => (e.ProviderName, e.Value)));
        if (artistMb is not null && personMb is not null)
            return string.Equals(artistMb, personMb, StringComparison.OrdinalIgnoreCase);

        return MusicArtistNameNormalizer.NamesMatch(person.Name, artist.Title);
    }

    private static string? MusicBrainzId(IEnumerable<(string ProviderName, string Value)> ids) =>
        ids.FirstOrDefault(e => string.Equals(e.ProviderName, "musicbrainz", StringComparison.OrdinalIgnoreCase)).Value;
}
