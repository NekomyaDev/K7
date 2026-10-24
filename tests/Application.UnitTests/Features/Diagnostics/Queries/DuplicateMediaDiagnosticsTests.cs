using K7.Server.Application.Common.Configuration;
using K7.Server.Application.Features.Diagnostics.Queries.GetDiagnosticItems;
using K7.Server.Application.Features.Diagnostics.Queries.GetDiagnosticsSummary;
using K7.Server.Domain.Entities;
using K7.Server.Domain.Entities.Federation;
using K7.Server.Domain.Entities.Medias;
using K7.Server.Domain.Entities.Metadatas;
using K7.Server.Domain.Entities.Metadatas.PersonRoles;
using K7.Server.Domain.Enums;
using K7.Server.Infrastructure.Database.Context.Data;
using K7.Shared.Dtos.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace K7.Server.Application.UnitTests.Features.Diagnostics.Queries;

/// <summary>
/// Duplicate media detection is signal-only: media creation is a find-or-create over a fuzzy
/// identity, so duplicates cannot be prevented by construction (see DuplicateMediaDiagnosticHelper).
/// These tests run against Sqlite in-memory, which also validates that the detection queries are
/// translatable to SQL.
/// </summary>
[TestFixture]
public class DuplicateMediaDiagnosticsTests
{
    private SqliteConnection _connection = null!;
    private ApplicationDbContext _context = null!;
    private GetDiagnosticItemsQueryHandler _itemsHandler = null!;
    private GetDiagnosticsSummaryQueryHandler _summaryHandler = null!;

    private Guid _libraryId;
    private Guid _libraryGroupId;

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

        var paths = Options.Create(new PathsConfiguration());
        _itemsHandler = new GetDiagnosticItemsQueryHandler(_context, paths);
        _summaryHandler = new GetDiagnosticsSummaryQueryHandler(_context, paths);

        SeedLibrary();
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Test]
    public async Task Handle_ShouldReportDuplicateExternalId_WhenTwoMediasShareExternalId()
    {
        var first = AddMovie("Inception", 2010);
        var second = AddMovie("Inception (copy)", 2010);
        AddExternalId(first, "tmdb", "27205");
        AddExternalId(second, "tmdb", "27205");
        await _context.SaveChangesAsync();

        var items = await QueryItemsAsync(DiagnosticIssue.DuplicateExternalId);

        items.Should().HaveCount(2);
        items.Select(i => i.EntityId).Should().BeEquivalentTo([first, second]);
        items.Should().OnlyContain(i => i.Severity == DiagnosticSeverity.Warning);
    }

    [Test]
    public async Task Handle_ShouldNotReportDuplicateExternalId_WhenTracksShareRecordingAcrossAlbums()
    {
        var albumA = AddAlbum("Album A");
        var albumB = AddAlbum("Album B");
        var first = AddTrack("Song", albumA);
        var second = AddTrack("Song", albumB);
        AddExternalId(first, "isrc", "USRC17607839");
        AddExternalId(second, "isrc", "USRC17607839");
        AddExternalId(first, "musicbrainz", "recording-1");
        AddExternalId(second, "musicbrainz", "recording-1");
        await _context.SaveChangesAsync();

        var items = await QueryItemsAsync(DiagnosticIssue.DuplicateExternalId);

        items.Should().BeEmpty();
    }

    [Test]
    public async Task Handle_ShouldReportDuplicateExternalId_WhenTracksShareRecordingOnTheSameAlbum()
    {
        var albumId = AddAlbum("Album");
        var first = AddTrack("Song", albumId);
        var second = AddTrack("Song (copy)", albumId);
        AddExternalId(first, "isrc", "USRC17607839");
        AddExternalId(second, "isrc", "USRC17607839");
        await _context.SaveChangesAsync();

        var items = await QueryItemsAsync(DiagnosticIssue.DuplicateExternalId);

        items.Should().HaveCount(2);
        items.Select(i => i.EntityId).Should().BeEquivalentTo([first, second]);
    }

    [Test]
    public async Task Handle_ShouldNotReportDuplicateExternalId_WhenSameAlbumIsrcHasDistinctMusicBrainzRecordings()
    {
        var albumId = AddAlbum("The Velvet Underground");
        var closet = AddTrack("After Hours", albumId);
        var valentin = AddTrack("After Hours", albumId);
        AddExternalId(closet, "isrc", "USPR16900001");
        AddExternalId(valentin, "isrc", "USPR16900001");
        AddExternalId(closet, "musicbrainz", "recording-closet");
        AddExternalId(valentin, "musicbrainz", "recording-valentin");
        await _context.SaveChangesAsync();

        var items = await QueryItemsAsync(DiagnosticIssue.DuplicateExternalId);

        items.Should().BeEmpty();
    }

    [Test]
    public async Task Handle_ShouldReportDuplicateExternalId_WhenSameAlbumIsrcSharesMusicBrainzRecording()
    {
        var albumId = AddAlbum("The Velvet Underground");
        var first = AddTrack("After Hours", albumId);
        var second = AddTrack("After Hours (copy)", albumId);
        AddExternalId(first, "isrc", "USPR16900001");
        AddExternalId(second, "isrc", "USPR16900001");
        AddExternalId(first, "musicbrainz", "recording-same");
        AddExternalId(second, "musicbrainz", "recording-same");
        await _context.SaveChangesAsync();

        var items = await QueryItemsAsync(DiagnosticIssue.DuplicateExternalId);

        items.Should().HaveCount(2);
        items.Select(i => i.EntityId).Should().BeEquivalentTo([first, second]);
    }

    [Test]
    public async Task Handle_ShouldReportDuplicateExternalId_WhenArtistsShareMusicBrainzId()
    {
        var first = AddArtist("Artist");
        var second = AddArtist("Artist (copy)");
        AddExternalId(first, "musicbrainz", "artist-1");
        AddExternalId(second, "musicbrainz", "artist-1");
        await _context.SaveChangesAsync();

        var items = await QueryItemsAsync(DiagnosticIssue.DuplicateExternalId);

        items.Should().HaveCount(2);
        items.Select(i => i.EntityId).Should().BeEquivalentTo([first, second]);
    }

    [Test]
    public async Task Handle_ShouldNotReportDuplicateExternalId_WhenMediasBelongToDifferentPeers()
    {
        var peer = PeerServer.CreatePending("peer", "https://peer.example", "token");
        _context.PeerServers.Add(peer);

        var local = AddMovie("Inception", 2010);
        var federated = AddMovie("Inception", 2010, peerServerId: peer.Id, withAvailability: false);
        AddExternalId(local, "tmdb", "27205");
        AddExternalId(federated, "tmdb", "27205");
        await _context.SaveChangesAsync();

        var items = await QueryItemsAsync(DiagnosticIssue.DuplicateExternalId);

        items.Should().BeEmpty();
    }

    [Test]
    public async Task Handle_ShouldReportSuspectedDuplicate_WhenSameTitleAndYearInSameLibrary()
    {
        // Different raw casing and whitespace: normalization is trim + case-insensitive.
        var first = AddMovie("  Inception ", 2010);
        var second = AddMovie("inception", 2010);
        await _context.SaveChangesAsync();

        var items = await QueryItemsAsync(DiagnosticIssue.SuspectedDuplicateMedia);

        items.Should().HaveCount(2);
        items.Select(i => i.EntityId).Should().BeEquivalentTo([first, second]);
        items.Should().OnlyContain(i => i.Severity == DiagnosticSeverity.Info);
    }

    [Test]
    public async Task Handle_ShouldNotReportSuspectedDuplicate_WhenYearsDiffer()
    {
        AddMovie("Dune", 1984);
        AddMovie("Dune", 2021);
        await _context.SaveChangesAsync();

        var items = await QueryItemsAsync(DiagnosticIssue.SuspectedDuplicateMedia);

        items.Should().BeEmpty();
    }

    [Test]
    public async Task Handle_ShouldNotReportSuspectedDuplicate_ForEpisodesWithSameTitle()
    {
        // Generic episode titles must not enter the grouping (huge clusters + Postgres 22003).
        var serieId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        _context.Medias.Add(new Serie { Id = serieId, Title = "Serie", ReleaseDate = new DateOnly(2020, 1, 1) });
        _context.Medias.Add(new SerieSeason
        {
            Id = seasonId,
            SerieId = serieId,
            SeasonNumber = 1,
            Title = "Season 1",
            ReleaseDate = new DateOnly(2020, 1, 1)
        });
        _context.MediaLibraryAvailabilities.Add(new MediaLibraryAvailability
        {
            LibraryId = _libraryId,
            MediaId = serieId
        });

        for (var i = 1; i <= 3; i++)
        {
            var episodeId = Guid.NewGuid();
            _context.Medias.Add(new SerieEpisode
            {
                Id = episodeId,
                SerieId = serieId,
                SeasonId = seasonId,
                EpisodeNumber = i,
                Title = "Episode 1",
                ReleaseDate = new DateOnly(2020, 1, 1)
            });
            _context.MediaLibraryAvailabilities.Add(new MediaLibraryAvailability
            {
                LibraryId = _libraryId,
                MediaId = episodeId
            });
        }

        await _context.SaveChangesAsync();

        var items = await QueryItemsAsync(DiagnosticIssue.SuspectedDuplicateMedia);

        items.Should().BeEmpty();
    }

    [Test]
    public async Task Handle_ShouldReportNothing_WhenSingleMedia()
    {
        var movie = AddMovie("Inception", 2010);
        AddExternalId(movie, "tmdb", "27205");
        await _context.SaveChangesAsync();

        var duplicateItems = await QueryItemsAsync(DiagnosticIssue.DuplicateExternalId);
        var suspectedItems = await QueryItemsAsync(DiagnosticIssue.SuspectedDuplicateMedia);

        duplicateItems.Should().BeEmpty();
        suspectedItems.Should().BeEmpty();
    }

    [Test]
    public async Task Handle_ShouldReflectDuplicateCountsInSummary()
    {
        var first = AddMovie("Inception", 2010);
        var second = AddMovie("Inception (copy)", 2010);
        AddExternalId(first, "tmdb", "27205");
        AddExternalId(second, "tmdb", "27205");
        AddMovie("Dune", 2021);
        AddMovie("Dune", 2021);
        await _context.SaveChangesAsync();

        var summaries = await _summaryHandler.Handle(new GetDiagnosticsSummaryQuery(), CancellationToken.None);

        var summary = summaries.Should().ContainSingle(s => s.LibraryId == _libraryId).Subject;
        summary.DuplicateExternalIdCount.Should().Be(2);
        summary.SuspectedDuplicateMediaCount.Should().Be(2);
    }

    [Test]
    public async Task Handle_ShouldCountEveryMediaInASuspectedDuplicateCluster()
    {
        AddMovie("Dune", 2021);
        AddMovie("Dune", 2021);
        AddMovie(" dune ", 2021);
        await _context.SaveChangesAsync();

        var summaries = await _summaryHandler.Handle(new GetDiagnosticsSummaryQuery(), CancellationToken.None);

        var summary = summaries.Should().ContainSingle(s => s.LibraryId == _libraryId).Subject;
        summary.SuspectedDuplicateMediaCount.Should().Be(3);

        var items = await QueryItemsAsync(DiagnosticIssue.SuspectedDuplicateMedia);
        items.Should().HaveCount(3);
    }

    [Test]
    public async Task Handle_ShouldNotReportSuspectedDuplicate_WhenSameTitleLivesInDifferentLibraries()
    {
        var otherLibraryId = AddLocalLibrary("Other movies");
        AddMovie("Dune", 2021);
        AddMovie("Dune", 2021, libraryId: otherLibraryId);
        await _context.SaveChangesAsync();

        var summaries = await _summaryHandler.Handle(new GetDiagnosticsSummaryQuery(), CancellationToken.None);
        summaries.Should().OnlyContain(s => s.SuspectedDuplicateMediaCount == 0);

        var items = await QueryItemsAsync(DiagnosticIssue.SuspectedDuplicateMedia);
        items.Should().BeEmpty();
    }

    [Test]
    public async Task Handle_ShouldScopeSuspectedDuplicate_ToTheLibraryThatHasTheCluster()
    {
        var otherLibraryId = AddLocalLibrary("Other movies");
        var shared = AddMovie("Dune", 2021);
        var partner = AddMovie("Dune", 2021);
        _context.MediaLibraryAvailabilities.Add(new MediaLibraryAvailability
        {
            LibraryId = otherLibraryId,
            MediaId = shared
        });
        await _context.SaveChangesAsync();

        var summaries = await _summaryHandler.Handle(new GetDiagnosticsSummaryQuery(), CancellationToken.None);
        summaries.Should().ContainSingle(s => s.LibraryId == _libraryId).Subject
            .SuspectedDuplicateMediaCount.Should().Be(2);
        summaries.Should().ContainSingle(s => s.LibraryId == otherLibraryId).Subject
            .SuspectedDuplicateMediaCount.Should().Be(0);

        var inCluster = await QueryItemsAsync(DiagnosticIssue.SuspectedDuplicateMedia, _libraryId);
        inCluster.Select(i => i.EntityId).Should().BeEquivalentTo([shared, partner]);

        var elsewhere = await QueryItemsAsync(DiagnosticIssue.SuspectedDuplicateMedia, otherLibraryId);
        elsewhere.Should().BeEmpty();
    }

    [Test]
    public async Task Handle_ShouldNotReportSuspectedDuplicate_WhenTypesDiffer()
    {
        AddMovie("Dune", 2021);
        var albumId = Guid.NewGuid();
        _context.Medias.Add(new MusicAlbum
        {
            Id = albumId,
            Title = "Dune",
            ReleaseDate = new DateOnly(2021, 6, 15)
        });
        _context.MediaLibraryAvailabilities.Add(new MediaLibraryAvailability
        {
            LibraryId = _libraryId,
            MediaId = albumId
        });
        await _context.SaveChangesAsync();

        var items = await QueryItemsAsync(DiagnosticIssue.SuspectedDuplicateMedia);
        items.Should().BeEmpty();
    }

    [Test]
    public async Task Handle_ShouldIgnoreFederatedLibrary_ForSuspectedDuplicates()
    {
        var peer = PeerServer.CreatePending("peer", "https://peer.example", "token");
        _context.PeerServers.Add(peer);
        var federatedLibraryId = AddLocalLibrary("Remote movies", peer.Id);
        AddMovie("Dune", 2021, libraryId: federatedLibraryId);
        AddMovie("Dune", 2021, libraryId: federatedLibraryId);
        await _context.SaveChangesAsync();

        var summaries = await _summaryHandler.Handle(new GetDiagnosticsSummaryQuery(), CancellationToken.None);
        summaries.Should().ContainSingle(s => s.LibraryId == federatedLibraryId).Subject
            .SuspectedDuplicateMediaCount.Should().Be(0);

        var items = await QueryItemsAsync(DiagnosticIssue.SuspectedDuplicateMedia);
        items.Should().BeEmpty();
    }

    [Test]
    public async Task Handle_ShouldKeepSuspectedDuplicateOnUnfilteredMediaRow()
    {
        var first = AddMovie("Dune", 2021);
        var second = AddMovie("Dune", 2021);
        await _context.SaveChangesAsync();

        var result = await _itemsHandler.Handle(
            new GetDiagnosticItemsQuery
            {
                EntityType = DiagnosticEntityType.Media,
                PageNumber = 1,
                PageSize = 50
            },
            CancellationToken.None);

        result.Items.Should().HaveCount(2);
        result.Items.Select(i => i.EntityId).Should().BeEquivalentTo([first, second]);
        result.Items.Should().OnlyContain(i => i.Issues.Contains(DiagnosticIssue.SuspectedDuplicateMedia));
    }

    [Test]
    public async Task Handle_ShouldReportSuspectedDuplicate_WhenImportedShellMatchesPlayableAlbum()
    {
        var artist = AddArtist("Stromae");
        var playable = AddReleasedAlbum("Cheese", 2010, artist);
        var shell = new MusicAlbum
        {
            Id = Guid.NewGuid(),
            Title = "Cheese (imported)",
            ArtistId = artist
        };
        _context.Medias.Add(shell);
        await _context.SaveChangesAsync();

        var items = await QueryItemsAsync(DiagnosticIssue.SuspectedDuplicateMedia);

        items.Select(i => i.EntityId).Should().BeEquivalentTo([playable, shell.Id]);
    }

    [Test]
    public async Task Handle_ShouldNotReportSuspectedDuplicate_WhenShellTrackIsNotOnThePlayableAlbum()
    {
        var artist = AddArtist("Stromae");
        var playable = AddReleasedAlbum("Cheese", 2010, artist);
        AddTrack("Alors on danse", playable);
        var shell = new MusicAlbum
        {
            Id = Guid.NewGuid(),
            Title = "Cheese (imported)",
            ArtistId = artist
        };
        _context.Medias.Add(shell);
        _context.Medias.Add(new MusicTrack
        {
            Id = Guid.NewGuid(),
            Title = "Bonus",
            AlbumId = shell.Id,
            ArtistId = artist
        });
        await _context.SaveChangesAsync();

        var items = await QueryItemsAsync(DiagnosticIssue.SuspectedDuplicateMedia);

        items.Should().BeEmpty();
    }

    [Test]
    public async Task Handle_ShouldReportSuspectedDuplicate_WhenFileLessAlbumRepeatsAPlayableTitle()
    {
        var artist = AddArtist("Stromae");
        var playable = AddReleasedAlbum("Cheese", 2010, artist);
        var playableTrack = AddTrack("Alors on danse", playable);
        var shell = new MusicAlbum
        {
            Id = Guid.NewGuid(),
            Title = "Cheese",
            ArtistId = artist
        };
        var shellTrack = Guid.NewGuid();
        _context.Medias.Add(shell);
        _context.Medias.Add(new MusicTrack
        {
            Id = shellTrack,
            Title = "Alors on danse",
            AlbumId = shell.Id,
            ArtistId = artist
        });
        await _context.SaveChangesAsync();

        var items = await QueryItemsAsync(DiagnosticIssue.SuspectedDuplicateMedia);

        items.Select(i => i.EntityId).Should().BeEquivalentTo([playableTrack, shellTrack]);
        items.Select(i => i.EntityName).Should().BeEquivalentTo(["Alors on danse - Cheese", "Alors on danse - Cheese"]);
    }

    [Test]
    public async Task Handle_ShouldNotReportSuspectedDuplicate_WhenImportedShellArtistDiffers()
    {
        var stromae = AddArtist("Stromae");
        var other = AddArtist("Other");
        AddReleasedAlbum("Cheese", 2010, stromae);
        _context.Medias.Add(new MusicAlbum
        {
            Id = Guid.NewGuid(),
            Title = "Cheese (imported)",
            ArtistId = other
        });
        await _context.SaveChangesAsync();

        var items = await QueryItemsAsync(DiagnosticIssue.SuspectedDuplicateMedia);

        items.Should().BeEmpty();
    }

    [Test]
    public async Task Handle_ShouldNotReportSuspectedDuplicate_WhenAlbumsShareTitleAndYearButNotArtist()
    {
        var queen = AddArtist("Queen");
        var bowie = AddArtist("David Bowie");
        AddReleasedAlbum("Greatest Hits", 1981, queen);
        AddReleasedAlbum("Greatest Hits", 1981, bowie);
        await _context.SaveChangesAsync();

        var items = await QueryItemsAsync(DiagnosticIssue.SuspectedDuplicateMedia);

        items.Should().BeEmpty();
    }

    [Test]
    public async Task Handle_ShouldReportSuspectedDuplicate_WhenAlbumsShareTitleYearAndArtist()
    {
        var queen = AddArtist("Queen");
        var first = AddReleasedAlbum("Greatest Hits", 1981, queen);
        var second = AddReleasedAlbum("  greatest hits ", 1981, queen);
        await _context.SaveChangesAsync();

        var items = await QueryItemsAsync(DiagnosticIssue.SuspectedDuplicateMedia);

        items.Select(i => i.EntityId).Should().BeEquivalentTo([first, second]);
    }

    [Test]
    public async Task Handle_ShouldReportSuspectedDuplicate_WhenAlbumsShareCreditArtistAndHaveNoPrimaryArtist()
    {
        var queen = AddArtist("Queen");
        var bowie = AddArtist("David Bowie");
        var sameA = AddReleasedAlbum("Greatest Hits", 1981, artistId: null);
        var sameB = AddReleasedAlbum("Greatest Hits", 1981, artistId: null);
        var other = AddReleasedAlbum("Greatest Hits", 1981, artistId: null);
        AddCredit(sameA, queen);
        AddCredit(sameB, queen);
        AddCredit(other, bowie);
        await _context.SaveChangesAsync();

        var items = await QueryItemsAsync(DiagnosticIssue.SuspectedDuplicateMedia);

        items.Select(i => i.EntityId).Should().BeEquivalentTo([sameA, sameB]);
    }

    [Test]
    public async Task Handle_ShouldNotReportSuspectedDuplicate_WhenArtistsShareProviderIdAndName()
    {
        var first = AddArtist("Daft Punk");
        var second = AddArtist("daft punk");
        AddExternalId(first, "musicbrainz", "artist-1");
        AddExternalId(second, "musicbrainz", "artist-1");
        await _context.SaveChangesAsync();

        var suspected = await QueryItemsAsync(DiagnosticIssue.SuspectedDuplicateMedia);
        var duplicates = await QueryItemsAsync(DiagnosticIssue.DuplicateExternalId);

        suspected.Should().BeEmpty();
        duplicates.Select(i => i.EntityId).Should().BeEquivalentTo([first, second]);
    }

    [Test]
    public async Task Handle_ShouldNotReportSuspectedDuplicate_WhenPersonsShareProviderIdAndName()
    {
        var first = new Person { Id = Guid.NewGuid(), Name = "Ada Lovelace" };
        var second = new Person { Id = Guid.NewGuid(), Name = "ada lovelace" };
        _context.Persons.AddRange(first, second);
        AddPersonExternalId(first.Id, "musicbrainz", "person-1");
        AddPersonExternalId(second.Id, "musicbrainz", "person-1");
        await _context.SaveChangesAsync();

        var suspected = await QueryItemsAsync(DiagnosticIssue.SuspectedDuplicateMedia);
        var duplicates = await QueryItemsAsync(DiagnosticIssue.DuplicateExternalId);

        suspected.Should().BeEmpty();
        duplicates.Select(i => i.EntityId).Should().BeEquivalentTo([first.Id, second.Id]);
    }

    [Test]
    public async Task Handle_ShouldNotReportSuspectedDuplicate_WhenArtistsHaveDistinctProviderIds()
    {
        var first = AddArtist("John Smith");
        var second = AddArtist("John Smith");
        AddExternalId(first, "musicbrainz", "artist-a");
        AddExternalId(second, "musicbrainz", "artist-b");
        await _context.SaveChangesAsync();

        var suspected = await QueryItemsAsync(DiagnosticIssue.SuspectedDuplicateMedia);

        suspected.Should().BeEmpty();
    }

    [Test]
    public async Task Handle_ShouldReportSuspectedDuplicate_WhenArtistCopyHasNoProviderId()
    {
        var identified = AddArtist("Daft Punk");
        var shell = AddArtist("Daft Punk");
        AddExternalId(identified, "musicbrainz", "artist-1");
        await _context.SaveChangesAsync();

        var items = await QueryItemsAsync(DiagnosticIssue.SuspectedDuplicateMedia);

        items.Select(i => i.EntityId).Should().BeEquivalentTo([identified, shell]);
    }

    [Test]
    public async Task Handle_ShouldNotReportSuspectedDuplicate_WhenPersonsHaveDistinctProviderIds()
    {
        var first = new Person { Id = Guid.NewGuid(), Name = "John Smith" };
        var second = new Person { Id = Guid.NewGuid(), Name = "John Smith" };
        _context.Persons.AddRange(first, second);
        AddPersonExternalId(first.Id, "tmdb", "1");
        AddPersonExternalId(second.Id, "tmdb", "2");
        await _context.SaveChangesAsync();

        var suspected = await QueryItemsAsync(DiagnosticIssue.SuspectedDuplicateMedia);

        suspected.Should().BeEmpty();
    }

    [Test]
    public async Task Handle_ShouldReportSuspectedDuplicate_WhenUnidentifiedPersonMatchesOneNamesake()
    {
        var identified = new Person { Id = Guid.NewGuid(), Name = "Ada Lovelace" };
        var shell = new Person { Id = Guid.NewGuid(), Name = "Ada Lovelace" };
        _context.Persons.AddRange(identified, shell);
        AddPersonExternalId(identified.Id, "tmdb", "1");
        await _context.SaveChangesAsync();

        var items = await QueryItemsAsync(DiagnosticIssue.SuspectedDuplicateMedia);

        items.Select(i => i.EntityId).Should().BeEquivalentTo([identified.Id, shell.Id]);
    }

    [Test]
    public async Task Handle_ShouldReportOnlyUnidentifiedPersons_WhenSeveralNamesakesAlreadyHaveProviderIds()
    {
        var johnA = new Person { Id = Guid.NewGuid(), Name = "John Smith" };
        var johnB = new Person { Id = Guid.NewGuid(), Name = "John Smith" };
        var shellA = new Person { Id = Guid.NewGuid(), Name = "John Smith" };
        var shellB = new Person { Id = Guid.NewGuid(), Name = "John Smith" };
        _context.Persons.AddRange(johnA, johnB, shellA, shellB);
        AddPersonExternalId(johnA.Id, "tmdb", "1");
        AddPersonExternalId(johnB.Id, "tmdb", "2");
        await _context.SaveChangesAsync();

        var items = await QueryItemsAsync(DiagnosticIssue.SuspectedDuplicateMedia);

        items.Select(i => i.EntityId).Should().BeEquivalentTo([shellA.Id, shellB.Id]);
    }

    [Test]
    public async Task Handle_ShouldReportSuspectedDuplicate_WhenArtistsShareNormalizedName()
    {
        var first = AddArtist(" Daft Punk ");
        var second = AddArtist("daft punk");
        var punctuated = AddArtist("*NSYNC");
        var plain = AddArtist("NSYNC");
        await _context.SaveChangesAsync();

        var items = await QueryItemsAsync(DiagnosticIssue.SuspectedDuplicateMedia);

        items.Select(i => i.EntityId).Should().BeEquivalentTo([first, second, punctuated, plain]);
        items.Should().OnlyContain(i => i.MediaType == MediaType.MusicArtist);
    }

    [Test]
    public async Task Handle_ShouldReportSuspectedDuplicate_WhenArtistCopyHasNoLibraryAvailability()
    {
        var linked = AddArtist("Daft Punk");
        var imported = new MusicArtist { Id = Guid.NewGuid(), Title = "Daft Punk" };
        _context.Medias.Add(imported);
        await _context.SaveChangesAsync();

        var items = await QueryItemsAsync(DiagnosticIssue.SuspectedDuplicateMedia);

        items.Select(i => i.EntityId).Should().BeEquivalentTo([linked, imported.Id]);
    }

    [Test]
    public async Task Handle_ShouldReportSuspectedDuplicate_WhenPersonsShareNormalizedName()
    {
        var first = new Person { Id = Guid.NewGuid(), Name = "Ada Lovelace" };
        var second = new Person { Id = Guid.NewGuid(), Name = " ada lovelace " };
        _context.Persons.AddRange(first, second);
        await _context.SaveChangesAsync();

        var items = await QueryItemsAsync(DiagnosticIssue.SuspectedDuplicateMedia);

        items.Select(i => i.EntityId).Should().BeEquivalentTo([first.Id, second.Id]);
        items.Should().OnlyContain(i => i.EntityType == DiagnosticEntityType.Person);
    }

    [Test]
    public async Task Handle_ShouldReportDuplicateExternalId_WhenPersonsShareProviderId()
    {
        var first = new Person { Id = Guid.NewGuid(), Name = "Ada" };
        var second = new Person { Id = Guid.NewGuid(), Name = "Augusta" };
        _context.Persons.AddRange(first, second);
        AddPersonExternalId(first.Id, "tmdb", "123");
        AddPersonExternalId(second.Id, "tmdb", "123");
        await _context.SaveChangesAsync();

        var items = await QueryItemsAsync(DiagnosticIssue.DuplicateExternalId);

        items.Select(i => i.EntityId).Should().BeEquivalentTo([first.Id, second.Id]);
        items.Should().OnlyContain(i => i.EntityType == DiagnosticEntityType.Person);
        items.Should().OnlyContain(i => i.Severity == DiagnosticSeverity.Warning);
    }

    [Test]
    public async Task Handle_ShouldPageSuspectedDuplicates_WithoutCountingUniqueTitles()
    {
        for (var i = 0; i < 5; i++)
            AddMovie("Dune", 2021);
        AddMovie("Unique Title", 2021);
        await _context.SaveChangesAsync();

        var page = await _itemsHandler.Handle(
            new GetDiagnosticItemsQuery
            {
                EntityType = DiagnosticEntityType.Media,
                Issue = DiagnosticIssue.SuspectedDuplicateMedia,
                PageNumber = 2,
                PageSize = 2
            },
            CancellationToken.None);

        page.TotalCount.Should().Be(5);
        page.Items.Should().HaveCount(2);
        page.Items.Should().OnlyContain(i => i.EntityName == "Dune");
    }

    private async Task<List<DiagnosticItemDto>> QueryItemsAsync(DiagnosticIssue issue, Guid? libraryId = null)
    {
        var result = await _itemsHandler.Handle(
            new GetDiagnosticItemsQuery
            {
                LibraryId = libraryId,
                EntityType = DiagnosticEntityType.Media,
                Issue = issue,
                PageNumber = 1,
                PageSize = 50
            },
            CancellationToken.None);

        return result.Items.ToList();
    }

    private void SeedLibrary()
    {
        _libraryId = Guid.NewGuid();
        _libraryGroupId = Guid.NewGuid();

        _context.LibraryGroups.Add(new LibraryGroup
        {
            Id = _libraryGroupId,
            Title = "Movies",
            MediaType = LibraryMediaType.Movie
        });
        _context.Libraries.Add(new Library
        {
            Id = _libraryId,
            LibraryGroupId = _libraryGroupId,
            Title = "Movies",
            MediaType = LibraryMediaType.Movie,
            RootPath = "/media",
            MetadataProviderName = "tmdb",
            MetadataLanguage = "fr",
            MetadataFallbackLanguage = "en"
        });
    }

    private Guid AddLocalLibrary(string title, Guid? peerServerId = null)
    {
        var libraryId = Guid.NewGuid();
        _context.Libraries.Add(new Library
        {
            Id = libraryId,
            LibraryGroupId = _libraryGroupId,
            Title = title,
            MediaType = LibraryMediaType.Movie,
            RootPath = $"/media/{libraryId:N}",
            MetadataProviderName = "tmdb",
            MetadataLanguage = "fr",
            MetadataFallbackLanguage = "en",
            PeerServerId = peerServerId
        });
        return libraryId;
    }

    private Guid AddMovie(string title, int year, Guid? peerServerId = null, bool withAvailability = true, Guid? libraryId = null)
    {
        var movie = new Movie
        {
            Id = Guid.NewGuid(),
            Title = title,
            ReleaseDate = new DateOnly(year, 6, 15),
            PeerServerId = peerServerId
        };
        _context.Medias.Add(movie);

        if (withAvailability)
        {
            _context.MediaLibraryAvailabilities.Add(new MediaLibraryAvailability
            {
                LibraryId = libraryId ?? _libraryId,
                MediaId = movie.Id
            });
        }

        return movie.Id;
    }

    private Guid AddAlbum(string title)
    {
        var album = new MusicAlbum
        {
            Id = Guid.NewGuid(),
            Title = title
        };
        _context.Medias.Add(album);
        return album.Id;
    }

    private Guid AddTrack(string title, Guid albumId)
    {
        var track = new MusicTrack
        {
            Id = Guid.NewGuid(),
            Title = title,
            AlbumId = albumId
        };
        _context.Medias.Add(track);
        _context.MediaLibraryAvailabilities.Add(new MediaLibraryAvailability
        {
            LibraryId = _libraryId,
            MediaId = track.Id
        });
        return track.Id;
    }

    private Guid AddArtist(string title)
    {
        var artist = new MusicArtist
        {
            Id = Guid.NewGuid(),
            Title = title
        };
        _context.Medias.Add(artist);
        _context.MediaLibraryAvailabilities.Add(new MediaLibraryAvailability
        {
            LibraryId = _libraryId,
            MediaId = artist.Id
        });
        return artist.Id;
    }

    private void AddExternalId(Guid mediaId, string providerName, string value) =>
        _context.ExternalIds.Add(new ExternalId
        {
            Id = Guid.NewGuid(),
            ProviderName = providerName,
            Value = value,
            MediaId = mediaId
        });

    private void AddPersonExternalId(Guid personId, string providerName, string value) =>
        _context.ExternalIds.Add(new ExternalId
        {
            Id = Guid.NewGuid(),
            ProviderName = providerName,
            Value = value,
            PersonId = personId
        });

    private Guid AddReleasedAlbum(string title, int year, Guid? artistId)
    {
        var album = new MusicAlbum
        {
            Id = Guid.NewGuid(),
            Title = title,
            ReleaseDate = new DateOnly(year, 6, 15),
            ArtistId = artistId
        };
        _context.Medias.Add(album);
        _context.MediaLibraryAvailabilities.Add(new MediaLibraryAvailability
        {
            LibraryId = _libraryId,
            MediaId = album.Id
        });
        _context.IndexedFiles.Add(new IndexedFile
        {
            Id = Guid.NewGuid(),
            LibraryId = _libraryId,
            MediaId = album.Id,
            Name = "track",
            Extension = ".flac",
            Path = $"/music/{album.Id:N}/track.flac",
            Hash = (uint)album.Id.GetHashCode(),
            Size = 1
        });
        return album.Id;
    }

    private void AddCredit(Guid albumId, Guid artistId) =>
        _context.MusicArtistCredits.Add(new MusicArtistCredit
        {
            Id = Guid.NewGuid(),
            MediaId = albumId,
            MusicArtistId = artistId,
            Order = 0
        });
}
