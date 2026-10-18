using K7.Server.Application.Helpers;
using K7.Server.Domain.Entities;
using K7.Server.Domain.Entities.Medias;
using K7.Server.Domain.Enums;
using K7.Server.Infrastructure.Database.Context.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace K7.Server.Application.UnitTests.Helpers;

[TestFixture]
public class SameAlbumDuplicateTrackRepairHelperTests
{
    private SqliteConnection _connection = null!;
    private ApplicationDbContext _context = null!;
    private ILogger _logger = null!;
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
        _logger = Substitute.For<ILogger>();

        var groupId = Guid.NewGuid();
        _libraryId = Guid.NewGuid();
        _context.LibraryGroups.Add(new LibraryGroup
        {
            Id = groupId,
            Title = "Music",
            MediaType = LibraryMediaType.Music
        });
        _context.Libraries.Add(new Library
        {
            Id = _libraryId,
            LibraryGroupId = groupId,
            Title = "Music",
            MediaType = LibraryMediaType.Music,
            RootPath = "/music",
            MetadataProviderName = "musicbrainz",
            MetadataLanguage = "en",
            MetadataFallbackLanguage = "en"
        });
        _context.SaveChanges();
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Test]
    public async Task RepairAsync_ShouldFoldFileLessShell_OntoPlayableTitle()
    {
        var album = new MusicAlbum { Title = "Iowa" };
        var playable = new MusicTrack { Title = "Disasterpiece", Album = album, TrackNumber = 1 };
        var shell = new MusicTrack { Title = "Disasterpiece", Album = album, TrackNumber = 1 };
        _context.Medias.AddRange(album, playable, shell);
        await _context.SaveChangesAsync();

        AddFile(playable.Id, "/music/Slipknot/Iowa/03 - Disasterpiece.flac", ".flac", 10_000);
        await _context.SaveChangesAsync();
        var shellId = shell.Id;
        var playableId = playable.Id;

        var result = await SameAlbumDuplicateTrackRepairHelper.RepairAsync(_context, _logger);

        result.FileLessFolded.Should().Be(1);
        (await _context.Medias.OfType<MusicTrack>().AnyAsync(t => t.Id == shellId)).Should().BeFalse();
        (await _context.Medias.OfType<MusicTrack>().AnyAsync(t => t.Id == playableId)).Should().BeTrue();
    }

    [Test]
    public async Task RepairAsync_ShouldKeepFlac_WhenSameTitleHasMp3Duplicate()
    {
        var album = new MusicAlbum { Title = "Iowa" };
        var flacTrack = new MusicTrack { Title = "Disasterpiece", Album = album, TrackNumber = 3 };
        var mp3Track = new MusicTrack { Title = "Disasterpiece", Album = album, TrackNumber = 3 };
        _context.Medias.AddRange(album, flacTrack, mp3Track);
        await _context.SaveChangesAsync();

        var flacId = AddFile(flacTrack.Id, "/music/Slipknot/Iowa/03 - Disasterpiece.flac", ".flac", 20_000);
        var mp3Id = AddFile(mp3Track.Id, "/music/Slipknot/Iowa/03 - Disasterpiece.mp3", ".mp3", 5_000);
        await _context.SaveChangesAsync();

        var result = await SameAlbumDuplicateTrackRepairHelper.RepairAsync(_context, _logger);

        result.FormatDuplicatesFolded.Should().Be(1);
        (await _context.Medias.OfType<MusicTrack>().CountAsync()).Should().Be(1);
        (await _context.IndexedFiles.SingleAsync(f => f.Id == flacId)).MediaId.Should().Be(flacTrack.Id);
        (await _context.IndexedFiles.SingleAsync(f => f.Id == mp3Id)).MediaId.Should().Be(flacTrack.Id);
        (await _context.Medias.OfType<MusicTrack>().AnyAsync(t => t.Id == mp3Track.Id)).Should().BeFalse();
    }

    [Test]
    public async Task RepairAsync_ShouldNotFoldSameTitle_AcrossDiscs()
    {
        var album = new MusicAlbum { Title = "Breakfast in America" };
        var studio = new MusicTrack
        {
            Title = "Goodbye Stranger",
            Album = album,
            DiscNumber = 1,
            TrackNumber = 3
        };
        var live = new MusicTrack
        {
            Title = "Goodbye Stranger",
            Album = album,
            DiscNumber = 2,
            TrackNumber = 2
        };
        _context.Medias.AddRange(album, studio, live);
        await _context.SaveChangesAsync();

        AddFile(studio.Id, "/music/Supertramp/BIA/Disc 1/03 - Goodbye Stranger.flac", ".flac", 20_000);
        AddFile(live.Id, "/music/Supertramp/BIA/Disc 2/02 - Goodbye Stranger.flac", ".flac", 18_000);
        await _context.SaveChangesAsync();

        var result = await SameAlbumDuplicateTrackRepairHelper.RepairAsync(_context, _logger);

        result.FormatDuplicatesFolded.Should().Be(0);
        result.FileLessFolded.Should().Be(0);
        (await _context.Medias.OfType<MusicTrack>().CountAsync()).Should().Be(2);
    }

    private Guid AddFile(Guid mediaId, string path, string extension, long size)
    {
        var id = Guid.NewGuid();
        _context.IndexedFiles.Add(new IndexedFile
        {
            Id = id,
            LibraryId = _libraryId,
            Path = path,
            Name = Path.GetFileName(path),
            Extension = extension,
            ParentDirectory = "Iowa",
            Hash = 1,
            Size = size,
            MediaId = mediaId
        });
        return id;
    }
}
