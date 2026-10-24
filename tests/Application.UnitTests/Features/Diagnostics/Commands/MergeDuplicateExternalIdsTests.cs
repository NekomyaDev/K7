using K7.Server.Application.Features.Diagnostics.Commands.MergeDuplicateExternalIds;
using K7.Server.Application.Features.Medias.Commands.RefreshMediaMetadatas;
using K7.Server.Application.Helpers;
using K7.Server.Domain.Entities;
using K7.Server.Domain.Entities.Medias;
using K7.Server.Domain.Entities.Metadatas;
using K7.Server.Domain.Entities.Users;
using K7.Server.Domain.Entities.Metadatas.PersonRoles;
using K7.Server.Domain.Enums;
using K7.Server.Infrastructure.Database.Context.Data;
using MediatR;
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
    private ISender _sender = null!;
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
        _sender = Substitute.For<ISender>();
        _sender.Send(Arg.Any<RefreshMediaMetadatasCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _handler = new MergeDuplicateExternalIdsCommandHandler(
            _context,
            _sender,
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
    public async Task Handle_ShouldRefreshAlbumBeforeMerging_WhenDuplicateTracksShareAnIsrc()
    {
        var album = new MusicAlbum { Id = Guid.NewGuid(), Title = "Shakira." };
        var kept = new MusicTrack { Id = Guid.NewGuid(), Title = "Song", AlbumId = album.Id, TrackNumber = 1 };
        var duplicate = new MusicTrack { Id = Guid.NewGuid(), Title = "Song", AlbumId = album.Id, TrackNumber = 1 };
        _context.Medias.AddRange(album, kept, duplicate);
        var fileId = AddFile(kept.Id, "/media/music/shakira/song.flac");
        AddExternalId(album.Id, "musicbrainz", "rg-shakira");
        AddExternalId(kept.Id, "isrc", "USRC11400411");
        AddExternalId(duplicate.Id, "isrc", "USRC11400411");
        await _context.SaveChangesAsync();

        var callOrder = new List<string>();
        _sender.When(s => s.Send(Arg.Any<RefreshMediaMetadatasCommand>(), Arg.Any<CancellationToken>()))
            .Do(_ => callOrder.Add("refresh"));

        var merged = await _handler.Handle(new MergeDuplicateExternalIdsCommand(), CancellationToken.None);
        callOrder.Add("merge-done");

        merged.Should().Be(1);
        callOrder.Should().Equal("refresh", "merge-done");
        await _sender.Received(1).Send(
            Arg.Is<RefreshMediaMetadatasCommand>(c =>
                c.MediaId == album.Id
                && c.MetadataProviderExternalId == "rg-shakira"
                && c.MetadataProviderName == "musicbrainz"),
            Arg.Any<CancellationToken>());
        (await _context.Medias.OfType<MusicTrack>().AnyAsync(t => t.Id == duplicate.Id)).Should().BeFalse();
        (await _context.IndexedFiles.SingleAsync(f => f.Id == fileId)).MediaId.Should().Be(kept.Id);
    }

    [Test]
    public async Task Handle_ShouldNotRefreshAlbum_WhenAlbumHasNoMusicBrainzId()
    {
        var album = new MusicAlbum { Id = Guid.NewGuid(), Title = "Album" };
        var kept = new MusicTrack { Id = Guid.NewGuid(), Title = "Song", AlbumId = album.Id, TrackNumber = 1 };
        var duplicate = new MusicTrack { Id = Guid.NewGuid(), Title = "Song", AlbumId = album.Id, TrackNumber = 1 };
        _context.Medias.AddRange(album, kept, duplicate);
        AddFile(kept.Id, "/media/music/song.flac");
        AddExternalId(kept.Id, "isrc", "USRC17607839");
        AddExternalId(duplicate.Id, "isrc", "USRC17607839");
        await _context.SaveChangesAsync();

        var merged = await _handler.Handle(new MergeDuplicateExternalIdsCommand(), CancellationToken.None);

        merged.Should().Be(1);
        await _sender.DidNotReceive().Send(
            Arg.Any<RefreshMediaMetadatasCommand>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_ShouldNotMergeSameAlbumTracks_WhenSharedIsrcHasDistinctMusicBrainzRecordings()
    {
        var album = new MusicAlbum { Id = Guid.NewGuid(), Title = "The Velvet Underground" };
        var closet = new MusicTrack { Id = Guid.NewGuid(), Title = "After Hours", AlbumId = album.Id, TrackNumber = 10 };
        var valentin = new MusicTrack { Id = Guid.NewGuid(), Title = "After Hours", AlbumId = album.Id, TrackNumber = 20 };
        _context.Medias.AddRange(album, closet, valentin);
        AddFile(closet.Id, "/media/music/after-hours-closet.flac");
        AddFile(valentin.Id, "/media/music/after-hours-valentin.flac");
        AddExternalId(closet.Id, "isrc", "USPR16900001");
        AddExternalId(valentin.Id, "isrc", "USPR16900001");
        AddExternalId(closet.Id, "musicbrainz", "recording-closet");
        AddExternalId(valentin.Id, "musicbrainz", "recording-valentin");
        await _context.SaveChangesAsync();

        var merged = await _handler.Handle(new MergeDuplicateExternalIdsCommand(), CancellationToken.None);

        merged.Should().Be(0);
        (await _context.Medias.OfType<MusicTrack>().CountAsync()).Should().Be(2);
    }

    [Test]
    public async Task Handle_ShouldMergeMatchingTracks_AndKeepUnmatchedOnSourceAlbum()
    {
        var kept = new MusicAlbum { Id = Guid.NewGuid(), Title = "Album" };
        var duplicate = new MusicAlbum { Id = Guid.NewGuid(), Title = "Album (copy)" };
        var keptTrack = new MusicTrack { Id = Guid.NewGuid(), Title = "One", AlbumId = kept.Id, TrackNumber = 1, DiscNumber = 1 };
        var keptExtra = new MusicTrack { Id = Guid.NewGuid(), Title = "Two", AlbumId = kept.Id, TrackNumber = 2, DiscNumber = 1 };
        var keptThird = new MusicTrack { Id = Guid.NewGuid(), Title = "Three", AlbumId = kept.Id, TrackNumber = 3, DiscNumber = 1 };
        var colliding = new MusicTrack { Id = Guid.NewGuid(), Title = "One", AlbumId = duplicate.Id, TrackNumber = 1, DiscNumber = 1 };
        var extra = new MusicTrack { Id = Guid.NewGuid(), Title = "Bonus Deluxe", AlbumId = duplicate.Id, TrackNumber = 4, DiscNumber = 1 };
        _context.Medias.AddRange(kept, duplicate, keptTrack, keptExtra, keptThird, colliding, extra);
        var fileId = AddFile(colliding.Id, "/media/music/one.flac");
        AddExternalId(kept.Id, "musicbrainz", "release-1");
        AddExternalId(duplicate.Id, "musicbrainz", "release-1");
        await _context.SaveChangesAsync();

        var merged = await _handler.Handle(new MergeDuplicateExternalIdsCommand(), CancellationToken.None);

        merged.Should().Be(0);
        (await _context.Medias.OfType<MusicAlbum>().AnyAsync(a => a.Id == duplicate.Id)).Should().BeTrue();
        (await _context.Medias.OfType<MusicTrack>().AnyAsync(t => t.Id == colliding.Id)).Should().BeFalse();
        (await _context.IndexedFiles.SingleAsync(f => f.Id == fileId)).MediaId.Should().Be(keptTrack.Id);
        (await _context.Medias.OfType<MusicTrack>().SingleAsync(t => t.Id == extra.Id)).AlbumId.Should().Be(duplicate.Id);
    }

    [Test]
    public async Task Handle_ShouldMatchTracksAcrossNullAndOneDiscNumber_WhenTitlesMatch()
    {
        var kept = new MusicAlbum { Id = Guid.NewGuid(), Title = "Album" };
        var duplicate = new MusicAlbum { Id = Guid.NewGuid(), Title = "Album (copy)" };
        var keptTrack = new MusicTrack { Id = Guid.NewGuid(), Title = "One", AlbumId = kept.Id, TrackNumber = 1, DiscNumber = 1 };
        var keptExtra = new MusicTrack { Id = Guid.NewGuid(), Title = "Two", AlbumId = kept.Id, TrackNumber = 2, DiscNumber = 1 };
        var keptThird = new MusicTrack { Id = Guid.NewGuid(), Title = "Three", AlbumId = kept.Id, TrackNumber = 3, DiscNumber = 1 };
        var colliding = new MusicTrack { Id = Guid.NewGuid(), Title = "One", AlbumId = duplicate.Id, TrackNumber = 1, DiscNumber = null };
        _context.Medias.AddRange(kept, duplicate, keptTrack, keptExtra, keptThird, colliding);
        var fileId = AddFile(colliding.Id, "/media/music/one.flac");
        AddExternalId(kept.Id, "musicbrainz", "release-null-disc");
        AddExternalId(duplicate.Id, "musicbrainz", "release-null-disc");
        await _context.SaveChangesAsync();

        var merged = await _handler.Handle(new MergeDuplicateExternalIdsCommand(), CancellationToken.None);

        merged.Should().Be(1);
        (await _context.Medias.OfType<MusicAlbum>().AnyAsync(a => a.Id == duplicate.Id)).Should().BeFalse();
        (await _context.Medias.OfType<MusicTrack>().AnyAsync(t => t.Id == colliding.Id)).Should().BeFalse(
            "track 1 with DiscNumber null must merge into disc 1, not stay as a duplicate row");
        (await _context.IndexedFiles.SingleAsync(f => f.Id == fileId)).MediaId.Should().Be(keptTrack.Id);
    }

    [Test]
    public async Task Handle_ShouldLeaveSourceTrack_WhenSameSlotButDifferentTitle()
    {
        var kept = new MusicAlbum { Id = Guid.NewGuid(), Title = "Fire Within" };
        var duplicate = new MusicAlbum { Id = Guid.NewGuid(), Title = "Fire Within (Deluxe)" };
        var keptTrack = new MusicTrack { Id = Guid.NewGuid(), Title = "Wings", AlbumId = kept.Id, TrackNumber = 1, DiscNumber = 1 };
        var deluxeTrack = new MusicTrack { Id = Guid.NewGuid(), Title = "Skinny Love", AlbumId = duplicate.Id, TrackNumber = 1, DiscNumber = 1 };
        _context.Medias.AddRange(kept, duplicate, keptTrack, deluxeTrack);
        var fileId = AddFile(deluxeTrack.Id, "/media/music/deluxe/01.flac");
        AddExternalId(kept.Id, "musicbrainz", "shared-bad-id");
        AddExternalId(duplicate.Id, "musicbrainz", "shared-bad-id");
        await _context.SaveChangesAsync();

        var merged = await _handler.Handle(new MergeDuplicateExternalIdsCommand(), CancellationToken.None);

        merged.Should().Be(0);
        (await _context.Medias.OfType<MusicAlbum>().AnyAsync(a => a.Id == duplicate.Id)).Should().BeTrue();
        (await _context.Medias.OfType<MusicTrack>().SingleAsync(t => t.Id == deluxeTrack.Id)).AlbumId.Should().Be(duplicate.Id);
        (await _context.IndexedFiles.SingleAsync(f => f.Id == fileId)).MediaId.Should().Be(deluxeTrack.Id);
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
    public async Task Handle_ShouldFoldImportedShellOntoPlayableAlbum_WhenMergingNamedDuplicates()
    {
        var artist = new MusicArtist { Id = Guid.NewGuid(), Title = "Stromae" };
        var playable = new MusicAlbum { Id = Guid.NewGuid(), Title = "Cheese", ArtistId = artist.Id };
        var shell = new MusicAlbum { Id = Guid.NewGuid(), Title = "Cheese (imported)", ArtistId = artist.Id };
        var playableTrack = new MusicTrack
        {
            Id = Guid.NewGuid(),
            Title = "Alors on danse",
            AlbumId = playable.Id,
            ArtistId = artist.Id
        };
        var shellTrack = new MusicTrack
        {
            Id = Guid.NewGuid(),
            Title = "Alors on danse",
            AlbumId = shell.Id,
            ArtistId = artist.Id
        };
        var bonus = new MusicTrack
        {
            Id = Guid.NewGuid(),
            Title = "Bonus",
            AlbumId = shell.Id,
            ArtistId = artist.Id
        };
        _context.Medias.AddRange(artist, playable, shell, playableTrack, shellTrack, bonus);
        AddFile(playableTrack.Id, "/media/music/alors.flac");
        var userId = Guid.NewGuid();
        _context.Users.Add(new User { Id = userId, IdentityUserId = "u1", DisplayName = "u1" });
        _context.UserMediaStates.Add(new UserMediaState { UserId = userId, MediaId = playableTrack.Id, PlayCount = 1 });
        _context.UserMediaStates.Add(new UserMediaState { UserId = userId, MediaId = shellTrack.Id, PlayCount = 4 });
        await _context.SaveChangesAsync();

        var merged = await _handler.Handle(
            new MergeDuplicateExternalIdsCommand { Scope = DuplicateMergeScope.NamedArtistsAndPersons },
            CancellationToken.None);

        merged.Should().Be(1);
        (await _context.Medias.OfType<MusicTrack>().AnyAsync(t => t.Id == shellTrack.Id)).Should().BeFalse();
        (await _context.Medias.OfType<MusicAlbum>().AnyAsync(a => a.Id == shell.Id)).Should().BeTrue();
        (await _context.Medias.OfType<MusicTrack>().SingleAsync(t => t.Id == bonus.Id)).AlbumId.Should().Be(shell.Id);
        (await _context.UserMediaStates.SingleAsync(s => s.MediaId == playableTrack.Id)).PlayCount.Should().Be(5);
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
