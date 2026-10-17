using K7.Server.Application.Helpers;
using K7.Server.Domain.Entities;
using K7.Server.Domain.Entities.Medias;
using K7.Server.Domain.Entities.Users;
using K7.Server.Domain.Enums;
using K7.Server.Infrastructure.Database.Context.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace K7.Server.Application.UnitTests.Helpers;

[TestFixture]
public class ImportedMusicShellReattachHelperTests
{
    private SqliteConnection _connection = null!;
    private ApplicationDbContext _context = null!;
    private ILogger _logger = null!;

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
        _logger = Substitute.For<ILogger>();
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Test]
    public async Task ReattachAsync_ShouldMergePlayCountAndDeleteShell_WhenTitlesMatch()
    {
        var (playableTrackId, userId) = await SeedPairAsync(
            shellArtistIsPlayableArtist: true,
            shellTrackTitle: "Alors on danse",
            extraShellTrackTitle: null);
        _context.ExternalIds.Add(new ExternalId
        {
            ProviderName = "spotify",
            Value = "shell-id",
            MediaId = await ShellTrackIdAsync()
        });
        await _context.SaveChangesAsync();

        var result = await ImportedMusicShellReattachHelper.ReattachAsync(_context, _logger);

        result.TracksReattached.Should().Be(1);
        result.TracksLeft.Should().Be(0);
        result.AlbumsRemoved.Should().Be(1);
        (await _context.Medias.OfType<MusicAlbum>().CountAsync(a => a.Title == "Cheese (imported)")).Should().Be(0);
        var state = await _context.UserMediaStates.SingleAsync(s => s.UserId == userId);
        state.MediaId.Should().Be(playableTrackId);
        state.PlayCount.Should().Be(5);
        var externalId = await _context.ExternalIds.SingleAsync();
        externalId.MediaId.Should().Be(playableTrackId);
    }

    [Test]
    public async Task ReattachAsync_ShouldLeaveUnmatchedTrack_WhenTitleDiffers()
    {
        await SeedPairAsync(
            shellArtistIsPlayableArtist: true,
            shellTrackTitle: "Alors on danse",
            extraShellTrackTitle: "Bonus");

        var result = await ImportedMusicShellReattachHelper.ReattachAsync(_context, _logger);

        result.TracksReattached.Should().Be(1);
        result.TracksLeft.Should().Be(1);
        result.AlbumsRemoved.Should().Be(0);
        (await _context.Medias.OfType<MusicTrack>().CountAsync(t => t.Title == "Bonus")).Should().Be(1);
        (await _context.Medias.OfType<MusicAlbum>().CountAsync(a => a.Title == "Cheese (imported)")).Should().Be(1);
    }

    [Test]
    public async Task ReattachAsync_ShouldFoldFileLessAlbum_WhenTitleAlreadyExists()
    {
        var (playableTrackId, userId) = await SeedPairAsync(
            shellArtistIsPlayableArtist: true,
            shellTrackTitle: "Alors on danse",
            extraShellTrackTitle: null,
            shellTitle: "Cheese");

        var result = await ImportedMusicShellReattachHelper.ReattachAsync(_context, _logger);

        result.TracksReattached.Should().Be(1);
        result.AlbumsRemoved.Should().Be(1);
        (await _context.Medias.OfType<MusicAlbum>().CountAsync(a => a.Title == "Cheese")).Should().Be(1);
        (await _context.UserMediaStates.SingleAsync(s => s.UserId == userId)).PlayCount.Should().Be(5);
        (await _context.UserMediaStates.SingleAsync()).MediaId.Should().Be(playableTrackId);
    }

    [Test]
    public async Task ReattachAsync_ShouldSkip_WhenArtistDiffers()
    {
        await SeedPairAsync(
            shellArtistIsPlayableArtist: false,
            shellTrackTitle: "Alors on danse",
            extraShellTrackTitle: null);

        var result = await ImportedMusicShellReattachHelper.ReattachAsync(_context, _logger);

        result.TracksReattached.Should().Be(0);
        result.AlbumsRemoved.Should().Be(0);
        (await _context.Medias.OfType<MusicAlbum>().CountAsync()).Should().Be(2);
        (await _context.UserMediaStates.SumAsync(s => s.PlayCount)).Should().Be(5);
    }

    private async Task<Guid> ShellTrackIdAsync() =>
        await _context.Medias.OfType<MusicTrack>()
            .Where(t => t.Album!.Title == "Cheese (imported)" && t.Title == "Alors on danse")
            .Select(t => t.Id)
            .SingleAsync();

    private async Task<(Guid PlayableTrackId, Guid UserId)> SeedPairAsync(
        bool shellArtistIsPlayableArtist,
        string shellTrackTitle,
        string? extraShellTrackTitle,
        string shellTitle = "Cheese (imported)")
    {
        var groupId = Guid.NewGuid();
        var libraryId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        _context.LibraryGroups.Add(new LibraryGroup
        {
            Id = groupId,
            Title = "Music",
            MediaType = LibraryMediaType.Music
        });
        _context.Libraries.Add(new Library
        {
            Id = libraryId,
            LibraryGroupId = groupId,
            Title = "Music",
            MediaType = LibraryMediaType.Music,
            RootPath = "/media",
            MetadataProviderName = "musicbrainz",
            MetadataLanguage = "fr",
            MetadataFallbackLanguage = "en"
        });
        _context.Users.Add(new User { Id = userId, IdentityUserId = "u1", DisplayName = "u1" });

        var artist = new MusicArtist { Id = Guid.NewGuid(), Title = "Stromae", SortTitle = "Stromae" };
        var otherArtist = new MusicArtist { Id = Guid.NewGuid(), Title = "Other", SortTitle = "Other" };
        var shellArtistId = shellArtistIsPlayableArtist ? artist.Id : otherArtist.Id;

        var playableAlbum = new MusicAlbum
        {
            Id = Guid.NewGuid(),
            Title = "Cheese",
            SortTitle = "Cheese",
            ArtistId = artist.Id
        };
        var playableTrack = new MusicTrack
        {
            Id = Guid.NewGuid(),
            Title = "Alors on danse",
            SortTitle = "Alors on danse",
            AlbumId = playableAlbum.Id,
            ArtistId = artist.Id
        };
        playableTrack.IndexedFiles.Add(new IndexedFile
        {
            Id = Guid.NewGuid(),
            LibraryId = libraryId,
            MediaId = playableTrack.Id,
            Name = "file",
            Extension = ".flac",
            Path = $"/media/{playableTrack.Id}.flac",
            Hash = 1,
            Size = 10
        });

        var shellAlbum = new MusicAlbum
        {
            Id = Guid.NewGuid(),
            Title = shellTitle,
            SortTitle = shellTitle,
            ArtistId = shellArtistId
        };
        var shellTrack = new MusicTrack
        {
            Id = Guid.NewGuid(),
            Title = shellTrackTitle,
            SortTitle = shellTrackTitle,
            AlbumId = shellAlbum.Id,
            ArtistId = shellArtistId
        };

        _context.Medias.AddRange(artist, otherArtist, playableAlbum, playableTrack, shellAlbum, shellTrack);
        if (extraShellTrackTitle is not null)
        {
            _context.Medias.Add(new MusicTrack
            {
                Id = Guid.NewGuid(),
                Title = extraShellTrackTitle,
                SortTitle = extraShellTrackTitle,
                AlbumId = shellAlbum.Id,
                ArtistId = shellArtistId
            });
        }

        _context.UserMediaStates.Add(new UserMediaState
        {
            UserId = userId,
            MediaId = playableTrack.Id,
            PlayCount = 1
        });
        _context.UserMediaStates.Add(new UserMediaState
        {
            UserId = userId,
            MediaId = shellTrack.Id,
            PlayCount = 4
        });
        await _context.SaveChangesAsync();
        return (playableTrack.Id, userId);
    }
}
