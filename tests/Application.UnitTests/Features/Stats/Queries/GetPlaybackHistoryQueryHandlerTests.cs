using K7.Server.Application.Common.Exceptions;
using K7.Server.Application.Common.Interfaces;
using K7.Server.Application.Features.Stats.Queries.GetPlaybackHistory;
using K7.Server.Domain.Constants;
using K7.Server.Domain.Entities.Medias;
using K7.Server.Domain.Entities.Users;
using K7.Server.Domain.Enums;
using K7.Server.Infrastructure.Database.Context.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace K7.Server.Application.UnitTests.Features.Stats.Queries;

[TestFixture]
public class GetPlaybackHistoryQueryHandlerTests
{
    private SqliteConnection _connection = null!;
    private ApplicationDbContext _context = null!;
    private IUser _currentUser = null!;
    private IIdentityService _identityService = null!;
    private GetPlaybackHistoryQueryHandler _handler = null!;
    private Guid _userId;
    private Guid _otherUserId;
    private Guid _movieId;
    private Guid _sharedProfileId;

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

        _userId = Guid.NewGuid();
        _otherUserId = Guid.NewGuid();
        _movieId = Guid.NewGuid();
        _sharedProfileId = Guid.NewGuid();

        _context.Users.AddRange(
            new User { Id = _userId, IdentityUserId = "ident", DisplayName = "viewer" },
            new User { Id = _otherUserId, IdentityUserId = "other", DisplayName = "other" });
        _context.Medias.Add(new Movie { Id = _movieId, Title = "Film" });
        _context.SharedProfiles.Add(new SharedProfile
        {
            Id = _sharedProfileId,
            Name = "Couple",
            HostUserId = _userId,
            CreatedByUserId = _userId
        });
        _context.SaveChanges();

        _currentUser = Substitute.For<IUser>();
        _currentUser.Id.Returns(_userId);
        _currentUser.GetSharedProfileIdAsync(Arg.Any<CancellationToken>()).Returns((Guid?)null);

        _identityService = Substitute.For<IIdentityService>();
        _identityService.GetRolesAsync("ident").Returns([Roles.User]);
        _identityService.GetRolesAsync("other").Returns([Roles.User]);

        var databaseCapabilities = Substitute.For<IDatabaseCapabilities>();
        _handler = new GetPlaybackHistoryQueryHandler(_context, _currentUser, _identityService, databaseCapabilities);
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Test]
    public async Task Handle_ShouldIncludeSharedProfileSessions_InPersonalHistory()
    {
        var sharedReferenceId = Guid.NewGuid();
        var personalReferenceId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        _context.MediaPlaybackSessions.AddRange(
            new MediaPlaybackSession
            {
                Id = Guid.NewGuid(),
                UserId = _userId,
                MediaId = _movieId,
                SessionId = Guid.NewGuid(),
                ReferenceId = sharedReferenceId,
                SharedProfileId = _sharedProfileId,
                SharedProfileNameSnapshot = "Couple",
                StartedAt = now.AddMinutes(-10),
                StoppedAt = now.AddMinutes(-2),
                DurationSeconds = 7200,
                WatchedDurationSeconds = 6000,
                State = PlaybackState.Ended
            },
            new MediaPlaybackSession
            {
                Id = Guid.NewGuid(),
                UserId = _userId,
                MediaId = _movieId,
                SessionId = Guid.NewGuid(),
                ReferenceId = personalReferenceId,
                StartedAt = now.AddMinutes(-30),
                StoppedAt = now.AddMinutes(-20),
                DurationSeconds = 7200,
                WatchedDurationSeconds = 4000,
                State = PlaybackState.Ended
            });
        await _context.SaveChangesAsync();

        EnableCapability(_userId, Capability.CanDeleteHistory);
        EnableCapability(_userId, Capability.CanReassignHistory);
        await _context.SaveChangesAsync();

        var result = await _handler.Handle(new GetPlaybackHistoryQuery { Period = "all" }, CancellationToken.None);

        result.Items.Should().HaveCount(2);
        result.Items.Should().Contain(i => i.ReferenceId == sharedReferenceId && i.SharedProfileName == "Couple" && i.CanReassign && i.CanDelete);
        result.Items.Should().Contain(i => i.ReferenceId == personalReferenceId && i.SharedProfileName == null && i.CanReassign && i.CanDelete);
    }

    [Test]
    public async Task Handle_ShouldIncludeSharedSessions_WhenCoViewer()
    {
        var sharedReferenceId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        _context.MediaPlaybackSessions.Add(new MediaPlaybackSession
        {
            Id = Guid.NewGuid(),
            UserId = _otherUserId,
            MediaId = _movieId,
            SessionId = Guid.NewGuid(),
            ReferenceId = sharedReferenceId,
            SharedProfileId = _sharedProfileId,
            SharedProfileNameSnapshot = "Couple",
            StartedAt = now.AddMinutes(-10),
            StoppedAt = now.AddMinutes(-2),
            DurationSeconds = 7200,
            WatchedDurationSeconds = 6000,
            State = PlaybackState.Ended
        });
        _context.MediaPlaybackSessionCoViewers.Add(new MediaPlaybackSessionCoViewer
        {
            ReferenceId = sharedReferenceId,
            UserId = _userId
        });
        await _context.SaveChangesAsync();

        EnableCapability(_userId, Capability.CanDeleteHistory);
        EnableCapability(_userId, Capability.CanReassignHistory);
        await _context.SaveChangesAsync();

        var result = await _handler.Handle(new GetPlaybackHistoryQuery { Period = "all" }, CancellationToken.None);

        result.Items.Should().ContainSingle()
            .Which.ReferenceId.Should().Be(sharedReferenceId);
        result.Items[0].SharedProfileName.Should().Be("Couple");
        result.Items[0].CanReassign.Should().BeTrue();
        result.Items[0].CanDelete.Should().BeTrue();
    }

    [Test]
    public async Task Handle_ShouldAllowGuestToRemoveThemselvesFromSharedPlay()
    {
        var sharedReferenceId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        _context.SharedProfileMembers.Add(new SharedProfileMember
        {
            SharedProfileId = _sharedProfileId,
            UserId = _otherUserId
        });
        _context.MediaPlaybackSessions.Add(new MediaPlaybackSession
        {
            Id = Guid.NewGuid(),
            UserId = _userId,
            MediaId = _movieId,
            SessionId = Guid.NewGuid(),
            ReferenceId = sharedReferenceId,
            SharedProfileId = _sharedProfileId,
            SharedProfileNameSnapshot = "Couple",
            StartedAt = now.AddMinutes(-10),
            StoppedAt = now.AddMinutes(-2),
            DurationSeconds = 7200,
            WatchedDurationSeconds = 6000,
            State = PlaybackState.Ended
        });
        _context.MediaPlaybackSessionCoViewers.Add(new MediaPlaybackSessionCoViewer
        {
            ReferenceId = sharedReferenceId,
            UserId = _otherUserId
        });
        await _context.SaveChangesAsync();

        _currentUser.Id.Returns(_otherUserId);
        EnableCapability(_otherUserId, Capability.CanDeleteHistory);
        await _context.SaveChangesAsync();

        var result = await _handler.Handle(new GetPlaybackHistoryQuery { Period = "all" }, CancellationToken.None);

        result.Items.Should().ContainSingle();
        result.Items[0].CanReassign.Should().BeFalse();
        result.Items[0].CanDelete.Should().BeTrue();
    }

    [Test]
    public async Task Handle_ShouldHideActions_WhenCapabilitiesNotGranted()
    {
        var personalReferenceId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        _context.MediaPlaybackSessions.Add(new MediaPlaybackSession
        {
            Id = Guid.NewGuid(),
            UserId = _userId,
            MediaId = _movieId,
            SessionId = Guid.NewGuid(),
            ReferenceId = personalReferenceId,
            StartedAt = now.AddMinutes(-10),
            StoppedAt = now.AddMinutes(-2),
            DurationSeconds = 7200,
            WatchedDurationSeconds = 4000,
            State = PlaybackState.Ended
        });
        await _context.SaveChangesAsync();

        var result = await _handler.Handle(new GetPlaybackHistoryQuery { Period = "all" }, CancellationToken.None);

        result.Items.Should().ContainSingle()
            .Which.ReferenceId.Should().Be(personalReferenceId);
        result.Items[0].CanReassign.Should().BeFalse();
        result.Items[0].CanDelete.Should().BeFalse();
    }

    [Test]
    public async Task Handle_ShouldHideDelete_WhenCapabilityDisabled()
    {
        EnableCapability(_userId, Capability.CanReassignHistory);
        _context.UserCapabilityOverrides.Add(new UserCapabilityOverride
        {
            Id = Guid.NewGuid(),
            UserId = _userId,
            Capability = Capability.CanDeleteHistory,
            Enabled = false
        });
        var personalReferenceId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        _context.MediaPlaybackSessions.Add(new MediaPlaybackSession
        {
            Id = Guid.NewGuid(),
            UserId = _userId,
            MediaId = _movieId,
            SessionId = Guid.NewGuid(),
            ReferenceId = personalReferenceId,
            StartedAt = now.AddMinutes(-10),
            StoppedAt = now.AddMinutes(-2),
            DurationSeconds = 7200,
            WatchedDurationSeconds = 4000,
            State = PlaybackState.Ended
        });
        await _context.SaveChangesAsync();

        var result = await _handler.Handle(new GetPlaybackHistoryQuery { Period = "all" }, CancellationToken.None);

        result.Items.Should().ContainSingle()
            .Which.ReferenceId.Should().Be(personalReferenceId);
        result.Items[0].CanReassign.Should().BeTrue();
        result.Items[0].CanDelete.Should().BeFalse();
    }

    [Test]
    public async Task Handle_ShouldHideReassign_WhenCapabilityDisabled()
    {
        EnableCapability(_userId, Capability.CanDeleteHistory);
        _context.UserCapabilityOverrides.Add(new UserCapabilityOverride
        {
            Id = Guid.NewGuid(),
            UserId = _userId,
            Capability = Capability.CanReassignHistory,
            Enabled = false
        });
        var personalReferenceId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        _context.MediaPlaybackSessions.Add(new MediaPlaybackSession
        {
            Id = Guid.NewGuid(),
            UserId = _userId,
            MediaId = _movieId,
            SessionId = Guid.NewGuid(),
            ReferenceId = personalReferenceId,
            StartedAt = now.AddMinutes(-10),
            StoppedAt = now.AddMinutes(-2),
            DurationSeconds = 7200,
            WatchedDurationSeconds = 4000,
            State = PlaybackState.Ended
        });
        await _context.SaveChangesAsync();

        var result = await _handler.Handle(new GetPlaybackHistoryQuery { Period = "all" }, CancellationToken.None);

        result.Items.Should().ContainSingle()
            .Which.ReferenceId.Should().Be(personalReferenceId);
        result.Items[0].CanReassign.Should().BeFalse();
        result.Items[0].CanDelete.Should().BeTrue();
    }

    [Test]
    public async Task Handle_ShouldForbidShowAllUsers_WhenNotAdministrator()
    {
        var act = () => _handler.Handle(
            new GetPlaybackHistoryQuery { Period = "all", ShowAllUsers = true },
            CancellationToken.None);

        await act.Should().ThrowAsync<ForbiddenAccessException>();
    }

    [Test]
    public async Task Handle_ShouldAllowAdminToManageAnyRow_WhenShowAllUsers()
    {
        _identityService.GetRolesAsync("ident").Returns([Roles.Administrator]);
        var otherReferenceId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        _context.MediaPlaybackSessions.Add(new MediaPlaybackSession
        {
            Id = Guid.NewGuid(),
            UserId = _otherUserId,
            MediaId = _movieId,
            SessionId = Guid.NewGuid(),
            ReferenceId = otherReferenceId,
            StartedAt = now.AddMinutes(-10),
            StoppedAt = now.AddMinutes(-2),
            DurationSeconds = 7200,
            WatchedDurationSeconds = 4000,
            State = PlaybackState.Ended
        });
        await _context.SaveChangesAsync();

        var result = await _handler.Handle(
            new GetPlaybackHistoryQuery { Period = "all", ShowAllUsers = true },
            CancellationToken.None);

        result.Items.Should().ContainSingle()
            .Which.ReferenceId.Should().Be(otherReferenceId);
        result.Items[0].CanReassign.Should().BeTrue();
        result.Items[0].CanDelete.Should().BeTrue();
    }

    [Test]
    public async Task Handle_ShouldScopeToSharedProfileOnly_WhenSharedProfileActive()
    {
        var sharedReferenceId = Guid.NewGuid();
        var personalReferenceId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        _context.MediaPlaybackSessions.AddRange(
            new MediaPlaybackSession
            {
                Id = Guid.NewGuid(),
                UserId = _userId,
                MediaId = _movieId,
                SessionId = Guid.NewGuid(),
                ReferenceId = sharedReferenceId,
                SharedProfileId = _sharedProfileId,
                SharedProfileNameSnapshot = "Couple",
                StartedAt = now.AddMinutes(-10),
                StoppedAt = now.AddMinutes(-2),
                DurationSeconds = 7200,
                WatchedDurationSeconds = 6000,
                State = PlaybackState.Ended
            },
            new MediaPlaybackSession
            {
                Id = Guid.NewGuid(),
                UserId = _userId,
                MediaId = _movieId,
                SessionId = Guid.NewGuid(),
                ReferenceId = personalReferenceId,
                StartedAt = now.AddMinutes(-30),
                StoppedAt = now.AddMinutes(-20),
                DurationSeconds = 7200,
                WatchedDurationSeconds = 4000,
                State = PlaybackState.Ended
            });
        await _context.SaveChangesAsync();

        _currentUser.GetSharedProfileIdAsync(Arg.Any<CancellationToken>()).Returns(_sharedProfileId);

        var result = await _handler.Handle(new GetPlaybackHistoryQuery { Period = "all" }, CancellationToken.None);

        result.Items.Should().ContainSingle()
            .Which.ReferenceId.Should().Be(sharedReferenceId);
    }

    [Test]
    public async Task Handle_ShouldNotUseMediaDuration_AsWatchedFallback()
    {
        var referenceId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        _context.MediaPlaybackSessions.Add(new MediaPlaybackSession
        {
            Id = Guid.NewGuid(),
            UserId = _userId,
            MediaId = _movieId,
            SessionId = Guid.NewGuid(),
            ReferenceId = referenceId,
            StartedAt = now.AddMinutes(-5),
            DurationSeconds = 7200,
            PositionSeconds = 0,
            WatchedDurationSeconds = 0,
            State = PlaybackState.Playing
        });
        await _context.SaveChangesAsync();

        var result = await _handler.Handle(new GetPlaybackHistoryQuery { Period = "all" }, CancellationToken.None);

        result.Items.Should().ContainSingle();
        result.Items[0].TotalWatchedSeconds.Should().Be(0);
        result.Items[0].IsCompleted.Should().BeFalse();
        result.Items[0].IsSkipped.Should().BeFalse();
    }

    [Test]
    public async Task Handle_ShouldMarkSkipped_WhenFinishedWithLittleProgress()
    {
        var referenceId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        _context.MediaPlaybackSessions.Add(new MediaPlaybackSession
        {
            Id = Guid.NewGuid(),
            UserId = _userId,
            MediaId = _movieId,
            SessionId = Guid.NewGuid(),
            ReferenceId = referenceId,
            StartedAt = now.AddMinutes(-2),
            StoppedAt = now,
            DurationSeconds = 200,
            PositionSeconds = 8,
            WatchedDurationSeconds = 8,
            State = PlaybackState.Ended
        });
        await _context.SaveChangesAsync();

        var result = await _handler.Handle(new GetPlaybackHistoryQuery { Period = "all" }, CancellationToken.None);

        result.Items.Should().ContainSingle();
        result.Items[0].IsCompleted.Should().BeFalse();
        result.Items[0].IsSkipped.Should().BeTrue();
    }

    [Test]
    public async Task Handle_ShouldNotMarkSkipped_WhenIncompleteWithMeaningfulProgress()
    {
        var referenceId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        _context.MediaPlaybackSessions.Add(new MediaPlaybackSession
        {
            Id = Guid.NewGuid(),
            UserId = _userId,
            MediaId = _movieId,
            SessionId = Guid.NewGuid(),
            ReferenceId = referenceId,
            StartedAt = now.AddMinutes(-5),
            StoppedAt = now,
            DurationSeconds = 200,
            PositionSeconds = 90,
            WatchedDurationSeconds = 90,
            State = PlaybackState.Ended
        });
        await _context.SaveChangesAsync();

        var result = await _handler.Handle(new GetPlaybackHistoryQuery { Period = "all" }, CancellationToken.None);

        result.Items.Should().ContainSingle();
        result.Items[0].IsCompleted.Should().BeFalse();
        result.Items[0].IsSkipped.Should().BeFalse();
    }

    [Test]
    public async Task Handle_ShouldMarkCompleted_WhenCompletedAtSet()
    {
        var referenceId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        _context.MediaPlaybackSessions.Add(new MediaPlaybackSession
        {
            Id = Guid.NewGuid(),
            UserId = _userId,
            MediaId = _movieId,
            SessionId = Guid.NewGuid(),
            ReferenceId = referenceId,
            StartedAt = now.AddMinutes(-5),
            StoppedAt = now,
            DurationSeconds = 200,
            PositionSeconds = 200,
            WatchedDurationSeconds = 200,
            CompletedAt = now,
            State = PlaybackState.Ended
        });
        await _context.SaveChangesAsync();

        var result = await _handler.Handle(new GetPlaybackHistoryQuery { Period = "all" }, CancellationToken.None);

        result.Items.Should().ContainSingle();
        result.Items[0].IsCompleted.Should().BeTrue();
        result.Items[0].IsSkipped.Should().BeFalse();
        result.Items[0].TotalWatchedSeconds.Should().Be(200);
    }

    [Test]
    public async Task Handle_ShouldFilterByMediaTitle_WhenSearchMatches()
    {
        var otherId = Guid.NewGuid();
        _context.Medias.Add(new Movie { Id = otherId, Title = "Arrival" });
        AddSession(_movieId);
        AddSession(otherId);
        await _context.SaveChangesAsync();

        var result = await _handler.Handle(
            new GetPlaybackHistoryQuery { Period = "all", Search = "film" },
            CancellationToken.None);

        result.Items.Should().ContainSingle();
        result.Items[0].MediaId.Should().Be(_movieId);
    }

    [Test]
    public async Task Handle_ShouldIgnoreSearch_WhenShorterThanTwoCharacters()
    {
        var otherId = Guid.NewGuid();
        _context.Medias.Add(new Movie { Id = otherId, Title = "Arrival" });
        AddSession(_movieId);
        AddSession(otherId);
        await _context.SaveChangesAsync();

        var result = await _handler.Handle(
            new GetPlaybackHistoryQuery { Period = "all", Search = "f" },
            CancellationToken.None);

        result.Items.Should().HaveCount(2);
    }

    [Test]
    public async Task Handle_ShouldMatchEpisodeHistory_WhenSeriesTitleMatches()
    {
        var serieId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var episodeId = Guid.NewGuid();
        var serie = new Serie { Id = serieId, Title = "Severance" };
        var season = new SerieSeason
        {
            Id = seasonId,
            SerieId = serieId,
            Serie = serie,
            SeasonNumber = 1,
            Title = "Season 1"
        };
        var episode = new SerieEpisode
        {
            Id = episodeId,
            SerieId = serieId,
            Serie = serie,
            SeasonId = seasonId,
            Season = season,
            EpisodeNumber = 1,
            Title = "Pilot"
        };
        _context.Medias.AddRange(serie, season, episode);
        AddSession(episodeId);
        AddSession(_movieId);
        await _context.SaveChangesAsync();

        var result = await _handler.Handle(
            new GetPlaybackHistoryQuery { Period = "all", Search = "severance" },
            CancellationToken.None);

        result.Items.Should().ContainSingle();
        result.Items[0].MediaId.Should().Be(episodeId);
    }

    [Test]
    public async Task Handle_ShouldMatchFoldedSortTitle_WhenSearchOmitsDiacritics()
    {
        var movieId = Guid.NewGuid();
        _context.Medias.Add(new Movie { Id = movieId, Title = "Amélie", SortTitle = "Amelie" });
        AddSession(movieId);
        AddSession(_movieId);
        await _context.SaveChangesAsync();

        var result = await _handler.Handle(
            new GetPlaybackHistoryQuery { Period = "all", Search = "amelie" },
            CancellationToken.None);

        result.Items.Should().ContainSingle();
        result.Items[0].MediaId.Should().Be(movieId);
    }

    [Test]
    public async Task Handle_ShouldMatchEpisodeHistory_WhenSeasonTitleMatches()
    {
        var serieId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var episodeId = Guid.NewGuid();
        var serie = new Serie { Id = serieId, Title = "Severance" };
        var season = new SerieSeason
        {
            Id = seasonId,
            SerieId = serieId,
            Serie = serie,
            SeasonNumber = 1,
            Title = "Winter"
        };
        var episode = new SerieEpisode
        {
            Id = episodeId,
            SerieId = serieId,
            Serie = serie,
            SeasonId = seasonId,
            Season = season,
            EpisodeNumber = 1,
            Title = "Pilot"
        };
        _context.Medias.AddRange(serie, season, episode);
        AddSession(episodeId);
        AddSession(_movieId);
        await _context.SaveChangesAsync();

        var result = await _handler.Handle(
            new GetPlaybackHistoryQuery { Period = "all", Search = "winter" },
            CancellationToken.None);

        result.Items.Should().ContainSingle();
        result.Items[0].MediaId.Should().Be(episodeId);
    }

    [Test]
    public async Task Handle_ShouldMatchTrackHistory_WhenAlbumOrArtistTitleMatches()
    {
        var artistId = Guid.NewGuid();
        var albumId = Guid.NewGuid();
        var trackId = Guid.NewGuid();
        var artist = new MusicArtist { Id = artistId, Title = "Radiohead" };
        var album = new MusicAlbum
        {
            Id = albumId,
            Title = "OK Computer",
            ArtistId = artistId,
            Artist = artist
        };
        var track = new MusicTrack
        {
            Id = trackId,
            Title = "Paranoid Android",
            AlbumId = albumId,
            Album = album,
            ArtistId = artistId,
            Artist = artist
        };
        _context.Medias.AddRange(artist, album, track);
        AddSession(trackId);
        AddSession(_movieId);
        await _context.SaveChangesAsync();

        var byArtist = await _handler.Handle(
            new GetPlaybackHistoryQuery { Period = "all", Search = "radiohead" },
            CancellationToken.None);
        byArtist.Items.Should().ContainSingle();
        byArtist.Items[0].MediaId.Should().Be(trackId);

        var byAlbum = await _handler.Handle(
            new GetPlaybackHistoryQuery { Period = "all", Search = "computer" },
            CancellationToken.None);
        byAlbum.Items.Should().ContainSingle();
        byAlbum.Items[0].MediaId.Should().Be(trackId);

        var typed = await _handler.Handle(
            new GetPlaybackHistoryQuery { Period = "all", Search = "radiohead", MediaType = MediaType.Movie },
            CancellationToken.None);
        typed.Items.Should().BeEmpty();
    }

    private void AddSession(Guid mediaId)
    {
        _context.MediaPlaybackSessions.Add(new MediaPlaybackSession
        {
            Id = Guid.NewGuid(),
            UserId = _userId,
            MediaId = mediaId,
            SessionId = Guid.NewGuid(),
            ReferenceId = Guid.NewGuid(),
            StartedAt = DateTime.UtcNow.AddMinutes(-10),
            StoppedAt = DateTime.UtcNow,
            DurationSeconds = 100,
            WatchedDurationSeconds = 100,
            State = PlaybackState.Ended
        });
    }

    private void EnableCapability(Guid userId, Capability capability)
    {
        _context.UserCapabilityOverrides.Add(new UserCapabilityOverride
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Capability = capability,
            Enabled = true
        });
    }
}

