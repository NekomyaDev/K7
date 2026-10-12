using K7.Server.Application.Features.Diagnostics.Commands.MergeDuplicateExternalIds;
using K7.Server.Application.Helpers;
using K7.Server.Domain.Entities;
using K7.Server.Domain.Entities.Medias;
using K7.Server.Domain.Entities.Metadatas;
using K7.Server.Domain.Entities.Metadatas.PersonRoles;
using K7.Server.Domain.Enums;
using K7.Server.Infrastructure.Database.Context.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace K7.Server.Application.UnitTests.Features.Diagnostics.Commands;

[TestFixture]
public class MergeDuplicateExternalIdsTests
{
    private SqliteConnection _connection = null!;
    private ApplicationDbContext _context = null!;
    private MergeDuplicateExternalIdsCommandHandler _handler = null!;
    private Guid _libraryId;

    [SetUp]
    public void SetUp()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new ApplicationDbContext(options);
        _context.Database.EnsureCreated();
        _handler = new MergeDuplicateExternalIdsCommandHandler(
            _context,
            Substitute.For<ILogger<MergeDuplicateExternalIdsCommandHandler>>());

        var groupId = Guid.NewGuid();
        _libraryId = Guid.NewGuid();
        _context.LibraryGroups.Add(new LibraryGroup
        {
            Id = groupId,
            Title = "Series",
            MediaType = LibraryMediaType.Serie
        });
        _context.Libraries.Add(new Library
        {
            Id = _libraryId,
            LibraryGroupId = groupId,
            Title = "Series",
            MediaType = LibraryMediaType.Serie,
            RootPath = "/media/series",
            MetadataProviderName = "tmdb",
            MetadataLanguage = "fr",
            MetadataFallbackLanguage = "en"
        });
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Test]
    public async Task Handle_ShouldMoveSeasonOntoSerieWithMoreEpisodes_AndDropTheDuplicate()
    {
        var populated = AddSerie("Les Carnets de l'apothicaire");
        var duplicate = AddSerie("Les Carnets de l'apothicaire");
        var populatedEpisode = AddEpisode(populated, seasonNumber: 1, episodeNumber: 1);
        AddEpisode(populated, seasonNumber: 1, episodeNumber: 2);
        AddEpisode(populated, seasonNumber: 2, episodeNumber: 1);
        var collidingEpisode = AddEpisode(duplicate, seasonNumber: 1, episodeNumber: 1);
        var seasonThreeEpisode = AddEpisode(duplicate, seasonNumber: 3, episodeNumber: 1);
        var fileId = AddFile(collidingEpisode, "/media/series/Show/Season 01/S01E01.mkv");
        AddExternalId(populated, "tmdb", "220542");
        AddExternalId(duplicate, "tmdb", "220542");
        AddExternalId(populated, "tvdb", "431162");
        AddExternalId(duplicate, "tvdb", "431162");
        await _context.SaveChangesAsync();

        var merged = await _handler.Handle(new MergeDuplicateExternalIdsCommand(), CancellationToken.None);

        merged.Should().Be(1);
        (await _context.Medias.OfType<Serie>().AnyAsync(s => s.Id == duplicate)).Should().BeFalse();
        (await _context.Medias.OfType<SerieEpisode>().AnyAsync(e => e.Id == collidingEpisode)).Should().BeFalse();

        var moved = await _context.Medias.OfType<SerieEpisode>().SingleAsync(e => e.Id == seasonThreeEpisode);
        moved.SerieId.Should().Be(populated);

        var seasonThree = await _context.Medias.OfType<SerieSeason>().SingleAsync(s => s.Id == moved.SeasonId);
        seasonThree.SerieId.Should().Be(populated);
        seasonThree.SeasonNumber.Should().Be(3);

        var file = await _context.IndexedFiles.SingleAsync(f => f.Id == fileId);
        file.MediaId.Should().Be(populatedEpisode);

        var remaining = await DuplicateMediaDiagnosticHelper.GetDuplicateExternalIdMediaIdsAsync(_context, null);
        remaining.Should().BeEmpty();
    }

    [Test]
    public async Task Handle_ShouldMoveMovieFileOntoTheCopyThatAlreadyHasFiles()
    {
        var populated = new Movie { Id = Guid.NewGuid(), Title = "Inception" };
        var duplicate = new Movie { Id = Guid.NewGuid(), Title = "Inception (copy)" };
        _context.Medias.Add(populated);
        _context.Medias.Add(duplicate);
        var fileId = AddFile(populated.Id, "/media/Inception.mkv");
        AddExternalId(populated.Id, "tmdb", "27205");
        AddExternalId(duplicate.Id, "tmdb", "27205");
        await _context.SaveChangesAsync();

        var merged = await _handler.Handle(new MergeDuplicateExternalIdsCommand(), CancellationToken.None);

        merged.Should().Be(1);
        (await _context.Medias.OfType<Movie>().AnyAsync(m => m.Id == duplicate.Id)).Should().BeFalse();
        var file = await _context.IndexedFiles.SingleAsync(f => f.Id == fileId);
        file.MediaId.Should().Be(populated.Id);
    }

    [Test]
    public async Task Handle_ShouldLeaveCrossAlbumTracksUntouched()
    {
        var albumA = new MusicAlbum { Id = Guid.NewGuid(), Title = "Album A" };
        var albumB = new MusicAlbum { Id = Guid.NewGuid(), Title = "Album B" };
        var first = new MusicTrack { Id = Guid.NewGuid(), Title = "Song", AlbumId = albumA.Id };
        var second = new MusicTrack { Id = Guid.NewGuid(), Title = "Song", AlbumId = albumB.Id };
        _context.Medias.AddRange(albumA, albumB, first, second);
        AddExternalId(first.Id, "isrc", "USRC17607839");
        AddExternalId(second.Id, "isrc", "USRC17607839");
        await _context.SaveChangesAsync();

        var merged = await _handler.Handle(new MergeDuplicateExternalIdsCommand(), CancellationToken.None);

        merged.Should().Be(0);
        (await _context.Medias.OfType<MusicTrack>().CountAsync()).Should().Be(2);
    }

    [Test]
    public async Task Handle_ShouldMergeSameAlbumTracks_WhenTheyShareAnIsrc()
    {
        var album = new MusicAlbum { Id = Guid.NewGuid(), Title = "Album" };
        var kept = new MusicTrack { Id = Guid.NewGuid(), Title = "Song", AlbumId = album.Id, TrackNumber = 1 };
        var duplicate = new MusicTrack { Id = Guid.NewGuid(), Title = "Song (copy)", AlbumId = album.Id, TrackNumber = 1 };
        _context.Medias.AddRange(album, kept, duplicate);
        var fileId = AddFile(kept.Id, "/media/music/song.flac");
        AddExternalId(kept.Id, "isrc", "USRC17607839");
        AddExternalId(duplicate.Id, "isrc", "USRC17607839");
        await _context.SaveChangesAsync();

        var merged = await _handler.Handle(new MergeDuplicateExternalIdsCommand(), CancellationToken.None);

        merged.Should().Be(1);
        (await _context.Medias.OfType<MusicTrack>().AnyAsync(t => t.Id == duplicate.Id)).Should().BeFalse();
        (await _context.IndexedFiles.SingleAsync(f => f.Id == fileId)).MediaId.Should().Be(kept.Id);
    }

    [Test]
    public async Task Handle_ShouldMoveTracksOntoAlbumWithMoreTracks_AndDropTheDuplicate()
    {
        var kept = new MusicAlbum { Id = Guid.NewGuid(), Title = "Album" };
        var duplicate = new MusicAlbum { Id = Guid.NewGuid(), Title = "Album (copy)" };
        var keptTrack = new MusicTrack { Id = Guid.NewGuid(), Title = "One", AlbumId = kept.Id, TrackNumber = 1, DiscNumber = 1 };
        var keptExtra = new MusicTrack { Id = Guid.NewGuid(), Title = "Two", AlbumId = kept.Id, TrackNumber = 2, DiscNumber = 1 };
        var keptThird = new MusicTrack { Id = Guid.NewGuid(), Title = "Three", AlbumId = kept.Id, TrackNumber = 3, DiscNumber = 1 };
        var colliding = new MusicTrack { Id = Guid.NewGuid(), Title = "One copy", AlbumId = duplicate.Id, TrackNumber = 1, DiscNumber = 1 };
        var extra = new MusicTrack { Id = Guid.NewGuid(), Title = "Four", AlbumId = duplicate.Id, TrackNumber = 4, DiscNumber = 1 };
        _context.Medias.AddRange(kept, duplicate, keptTrack, keptExtra, keptThird, colliding, extra);
        var fileId = AddFile(colliding.Id, "/media/music/one.flac");
        AddExternalId(kept.Id, "musicbrainz", "release-1");
        AddExternalId(duplicate.Id, "musicbrainz", "release-1");
        await _context.SaveChangesAsync();

        var merged = await _handler.Handle(new MergeDuplicateExternalIdsCommand(), CancellationToken.None);

        merged.Should().Be(1);
        (await _context.Medias.OfType<MusicAlbum>().AnyAsync(a => a.Id == duplicate.Id)).Should().BeFalse();
        (await _context.Medias.OfType<MusicTrack>().AnyAsync(t => t.Id == colliding.Id)).Should().BeFalse();
        (await _context.IndexedFiles.SingleAsync(f => f.Id == fileId)).MediaId.Should().Be(keptTrack.Id);
        (await _context.Medias.OfType<MusicTrack>().SingleAsync(t => t.Id == extra.Id)).AlbumId.Should().Be(kept.Id);
    }

    [Test]
    public async Task Handle_ShouldMoveAlbumsOntoArtistWithMoreAlbums_AndDropTheDuplicate()
    {
        var kept = new MusicArtist { Id = Guid.NewGuid(), Title = "Artist" };
        var duplicate = new MusicArtist { Id = Guid.NewGuid(), Title = "Artist (copy)" };
        var albumA = new MusicAlbum { Id = Guid.NewGuid(), Title = "A", ArtistId = kept.Id };
        var albumB = new MusicAlbum { Id = Guid.NewGuid(), Title = "B", ArtistId = kept.Id };
        var albumC = new MusicAlbum { Id = Guid.NewGuid(), Title = "C", ArtistId = duplicate.Id };
        var track = new MusicTrack { Id = Guid.NewGuid(), Title = "Song", AlbumId = albumC.Id, ArtistId = duplicate.Id };
        _context.Medias.AddRange(kept, duplicate, albumA, albumB, albumC, track);
        _context.MusicArtistCredits.AddRange(
            new MusicArtistCredit { Id = Guid.NewGuid(), MusicArtistId = kept.Id, MediaId = albumA.Id, Order = 0 },
            new MusicArtistCredit { Id = Guid.NewGuid(), MusicArtistId = duplicate.Id, MediaId = albumA.Id, Order = 0 },
            new MusicArtistCredit { Id = Guid.NewGuid(), MusicArtistId = duplicate.Id, MediaId = albumC.Id, Order = 0 });
        AddExternalId(kept.Id, "musicbrainz", "artist-1");
        AddExternalId(duplicate.Id, "musicbrainz", "artist-1");
        await _context.SaveChangesAsync();

        var merged = await _handler.Handle(new MergeDuplicateExternalIdsCommand(), CancellationToken.None);

        merged.Should().Be(1);
        (await _context.Medias.OfType<MusicArtist>().AnyAsync(a => a.Id == duplicate.Id)).Should().BeFalse();
        (await _context.Medias.OfType<MusicAlbum>().SingleAsync(a => a.Id == albumC.Id)).ArtistId.Should().Be(kept.Id);
        (await _context.Medias.OfType<MusicTrack>().SingleAsync(t => t.Id == track.Id)).ArtistId.Should().Be(kept.Id);
        var credits = await _context.MusicArtistCredits.Where(c => c.MusicArtistId == kept.Id).ToListAsync();
        credits.Select(c => c.MediaId).Should().BeEquivalentTo([albumA.Id, albumC.Id]);
    }

    [Test]
    public async Task Handle_ShouldMergeOnlyGroupsAvailableInTheSelectedLibrary()
    {
        var otherLibraryId = Guid.NewGuid();
        var groupId = _context.ChangeTracker.Entries<LibraryGroup>().Single().Entity.Id;
        _context.Libraries.Add(new Library
        {
            Id = otherLibraryId,
            LibraryGroupId = groupId,
            Title = "Other",
            MediaType = LibraryMediaType.Movie,
            RootPath = "/other",
            MetadataProviderName = "tmdb",
            MetadataLanguage = "fr",
            MetadataFallbackLanguage = "en"
        });

        var localKept = new Movie { Id = Guid.NewGuid(), Title = "Local" };
        var localDuplicate = new Movie { Id = Guid.NewGuid(), Title = "Local copy" };
        var otherKept = new Movie { Id = Guid.NewGuid(), Title = "Other" };
        var otherDuplicate = new Movie { Id = Guid.NewGuid(), Title = "Other copy" };
        _context.Medias.AddRange(localKept, localDuplicate, otherKept, otherDuplicate);
        AddFile(localKept.Id, "/media/local.mkv");
        AddAvailability(_libraryId, localKept.Id);
        AddAvailability(_libraryId, localDuplicate.Id);
        AddAvailability(otherLibraryId, otherKept.Id);
        AddAvailability(otherLibraryId, otherDuplicate.Id);
        AddExternalId(localKept.Id, "tmdb", "1");
        AddExternalId(localDuplicate.Id, "tmdb", "1");
        AddExternalId(otherKept.Id, "tmdb", "2");
        AddExternalId(otherDuplicate.Id, "tmdb", "2");
        await _context.SaveChangesAsync();

        var merged = await _handler.Handle(
            new MergeDuplicateExternalIdsCommand { LibraryId = _libraryId },
            CancellationToken.None);

        merged.Should().Be(1);
        (await _context.Medias.OfType<Movie>().AnyAsync(m => m.Id == localDuplicate.Id)).Should().BeFalse();
        (await _context.Medias.OfType<Movie>().AnyAsync(m => m.Id == otherDuplicate.Id)).Should().BeTrue();
    }

    [Test]
    public async Task Handle_ShouldMergeArtistsThatShareANormalizedName()
    {
        var kept = new MusicArtist { Id = Guid.NewGuid(), Title = "Daft Punk" };
        var duplicate = new MusicArtist { Id = Guid.NewGuid(), Title = "daft punk" };
        var keptAlbum = new MusicAlbum { Id = Guid.NewGuid(), Title = "Homework", ArtistId = kept.Id };
        var extraAlbum = new MusicAlbum { Id = Guid.NewGuid(), Title = "Discovery", ArtistId = kept.Id };
        var movedAlbum = new MusicAlbum { Id = Guid.NewGuid(), Title = "Random Access Memories", ArtistId = duplicate.Id };
        _context.Medias.AddRange(kept, duplicate, keptAlbum, extraAlbum, movedAlbum);
        await _context.SaveChangesAsync();

        var merged = await _handler.Handle(
            new MergeDuplicateExternalIdsCommand { Scope = DuplicateMergeScope.NamedArtistsAndPersons },
            CancellationToken.None);

        merged.Should().Be(1);
        (await _context.Medias.OfType<MusicArtist>().AnyAsync(a => a.Id == duplicate.Id)).Should().BeFalse();
        (await _context.Medias.OfType<MusicAlbum>().SingleAsync(a => a.Id == movedAlbum.Id)).ArtistId.Should().Be(kept.Id);
    }

    [Test]
    public async Task Handle_ShouldMergePersonsThatShareANormalizedName_AndMoveRoles()
    {
        var band = new MusicArtist { Id = Guid.NewGuid(), Title = "Band" };
        var other = new MusicArtist { Id = Guid.NewGuid(), Title = "Other" };
        var kept = new Person { Id = Guid.NewGuid(), Name = "Ada Lovelace", Biography = "Mathematician" };
        var duplicate = new Person { Id = Guid.NewGuid(), Name = "ada lovelace" };
        var keptRole = new MusicArtistMember
        {
            Id = Guid.NewGuid(),
            PersonId = kept.Id,
            MediaId = band.Id
        };
        var movedRole = new MusicArtistMember
        {
            Id = Guid.NewGuid(),
            PersonId = duplicate.Id,
            MediaId = other.Id
        };
        _context.Medias.AddRange(band, other);
        _context.Persons.AddRange(kept, duplicate);
        _context.PersonRoles.AddRange(keptRole, movedRole);
        await _context.SaveChangesAsync();

        var merged = await _handler.Handle(
            new MergeDuplicateExternalIdsCommand { Scope = DuplicateMergeScope.NamedArtistsAndPersons },
            CancellationToken.None);

        merged.Should().Be(1);
        (await _context.Persons.AnyAsync(p => p.Id == duplicate.Id)).Should().BeFalse();
        var roles = await _context.PersonRoles.ToListAsync();
        roles.Should().OnlyContain(r => r.PersonId == kept.Id);
        roles.Select(r => r.MediaId).Should().BeEquivalentTo([band.Id, other.Id]);
    }

    [Test]
    public async Task Handle_ShouldMergePersonsThatShareAnExternalId()
    {
        var band = new MusicArtist { Id = Guid.NewGuid(), Title = "Band" };
        var other = new MusicArtist { Id = Guid.NewGuid(), Title = "Other" };
        var kept = new Person { Id = Guid.NewGuid(), Name = "Ada", Biography = "Mathematician" };
        var duplicate = new Person { Id = Guid.NewGuid(), Name = "Augusta" };
        var keptRole = new MusicArtistMember
        {
            Id = Guid.NewGuid(),
            PersonId = kept.Id,
            MediaId = band.Id
        };
        var movedRole = new MusicArtistMember
        {
            Id = Guid.NewGuid(),
            PersonId = duplicate.Id,
            MediaId = other.Id
        };
        _context.Medias.AddRange(band, other);
        _context.Persons.AddRange(kept, duplicate);
        _context.PersonRoles.AddRange(keptRole, movedRole);
        _context.ExternalIds.AddRange(
            new ExternalId { Id = Guid.NewGuid(), ProviderName = "tmdb", Value = "123", PersonId = kept.Id },
            new ExternalId { Id = Guid.NewGuid(), ProviderName = "tmdb", Value = "123", PersonId = duplicate.Id });
        await _context.SaveChangesAsync();

        var merged = await _handler.Handle(new MergeDuplicateExternalIdsCommand(), CancellationToken.None);

        merged.Should().Be(1);
        (await _context.Persons.AnyAsync(p => p.Id == duplicate.Id)).Should().BeFalse();
        var roles = await _context.PersonRoles.ToListAsync();
        roles.Should().OnlyContain(r => r.PersonId == kept.Id);
        roles.Select(r => r.MediaId).Should().BeEquivalentTo([band.Id, other.Id]);
    }

    [Test]
    public async Task Handle_ShouldReattachPersonRoles_WhenMergingArtists()
    {
        var kept = new MusicArtist
        {
            Id = Guid.NewGuid(),
            Title = "Daft Punk",
            Biography = "French duo",
            Country = "FR"
        };
        var duplicate = new MusicArtist { Id = Guid.NewGuid(), Title = "Daft Punk" };
        var album = new MusicAlbum { Id = Guid.NewGuid(), Title = "Homework", ArtistId = duplicate.Id };
        var member = new Person { Id = Guid.NewGuid(), Name = "Thomas Bangalter" };
        var role = new MusicArtistMember
        {
            Id = Guid.NewGuid(),
            PersonId = member.Id,
            MediaId = duplicate.Id,
            Role = "member"
        };
        _context.Medias.AddRange(kept, duplicate, album);
        _context.Persons.Add(member);
        _context.PersonRoles.Add(role);
        AddExternalId(kept.Id, "musicbrainz", "artist-1");
        AddExternalId(duplicate.Id, "musicbrainz", "artist-1");
        await _context.SaveChangesAsync();

        var merged = await _handler.Handle(new MergeDuplicateExternalIdsCommand(), CancellationToken.None);

        merged.Should().Be(1);
        (await _context.Medias.OfType<MusicArtist>().AnyAsync(a => a.Id == duplicate.Id)).Should().BeFalse();
        (await _context.Medias.OfType<MusicAlbum>().SingleAsync(a => a.Id == album.Id)).ArtistId.Should().Be(kept.Id);
        var moved = await _context.PersonRoles.SingleAsync();
        moved.PersonId.Should().Be(member.Id);
        moved.MediaId.Should().Be(kept.Id);
    }

    [Test]
    public async Task Handle_ShouldReattachPersonRoles_WhenMergingMovies()
    {
        var kept = new Movie { Id = Guid.NewGuid(), Title = "Arrival" };
        var duplicate = new Movie { Id = Guid.NewGuid(), Title = "Arrival" };
        var person = new Person { Id = Guid.NewGuid(), Name = "Amy Adams" };
        var role = new Actor
        {
            Id = Guid.NewGuid(),
            PersonId = person.Id,
            MediaId = duplicate.Id,
            CharacterName = "Louise"
        };
        _context.Medias.AddRange(kept, duplicate);
        _context.Persons.Add(person);
        _context.PersonRoles.Add(role);
        AddFile(kept.Id, "/kept/arrival.mkv");
        AddExternalId(kept.Id, "tmdb", "329865");
        AddExternalId(duplicate.Id, "tmdb", "329865");
        await _context.SaveChangesAsync();

        var merged = await _handler.Handle(new MergeDuplicateExternalIdsCommand(), CancellationToken.None);

        merged.Should().Be(1);
        (await _context.Medias.OfType<Movie>().AnyAsync(m => m.Id == duplicate.Id)).Should().BeFalse();
        var moved = await _context.PersonRoles.SingleAsync();
        moved.PersonId.Should().Be(person.Id);
        moved.MediaId.Should().Be(kept.Id);
    }

    private Guid AddSerie(string title)
    {
        var serie = new Serie { Id = Guid.NewGuid(), Title = title, SortTitle = title };
        _context.Medias.Add(serie);
        return serie.Id;
    }

    private Guid AddEpisode(Guid serieId, int seasonNumber, int episodeNumber)
    {
        var season = _context.ChangeTracker.Entries<SerieSeason>()
            .Select(e => e.Entity)
            .FirstOrDefault(s => s.SerieId == serieId && s.SeasonNumber == seasonNumber);
        if (season is null)
        {
            season = new SerieSeason
            {
                Id = Guid.NewGuid(),
                SerieId = serieId,
                SeasonNumber = seasonNumber,
                Title = $"S{seasonNumber}",
                SortTitle = $"S{seasonNumber}"
            };
            _context.Medias.Add(season);
        }

        var episode = new SerieEpisode
        {
            Id = Guid.NewGuid(),
            SerieId = serieId,
            SeasonId = season.Id,
            EpisodeNumber = episodeNumber,
            Title = $"E{episodeNumber}",
            SortTitle = $"E{episodeNumber}",
            IndexedFiles = []
        };
        _context.Medias.Add(episode);
        return episode.Id;
    }

    private Guid AddFile(Guid mediaId, string path)
    {
        var file = new IndexedFile
        {
            Id = Guid.NewGuid(),
            LibraryId = _libraryId,
            Name = Path.GetFileName(path),
            Extension = ".mkv",
            Path = path,
            ParentDirectory = "Season 01",
            Hash = 1,
            Size = 1,
            MediaId = mediaId
        };
        _context.IndexedFiles.Add(file);
        return file.Id;
    }

    private void AddAvailability(Guid libraryId, Guid mediaId) =>
        _context.MediaLibraryAvailabilities.Add(new MediaLibraryAvailability
        {
            LibraryId = libraryId,
            MediaId = mediaId
        });

    private void AddExternalId(Guid mediaId, string providerName, string value) =>
        _context.ExternalIds.Add(new ExternalId
        {
            Id = Guid.NewGuid(),
            ProviderName = providerName,
            Value = value,
            MediaId = mediaId
        });
}
