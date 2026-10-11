using K7.Server.Application.Services;

namespace K7.Server.Application.UnitTests.Services;

public class ServerDiskMetricsProviderTests
{
    private const string MountInfo = """
        22 1 0:41 / / rw,relatime - overlay overlay rw
        48 22 8:1 /var/lib/docker/volumes/k7-data/_data /data rw,relatime - ext4 /dev/sda1 rw
        49 22 8:1 /mnt/media/movies /media/movies ro,relatime - ext4 /dev/sda1 rw
        50 22 8:16 / /media/tv ro,relatime - ext4 /dev/sdb1 rw
        51 22 8:16 /shows /media/shows ro,relatime - ext4 /dev/sdb1 rw
        """;

    [Test]
    public void GroupMonitoredPaths_ShouldKeepRootSeparateAndDedupeSameDevice()
    {
        var mounts = UnixMountTable.Parse(MountInfo);
        var paths = new MonitoredPath[]
        {
            new("Application", "/app", false),
            new("Config", "/data/config", false),
            new("Metadatas", "/data/metadatas", false),
            new("Movies", "/media/movies", true),
            new("TV", "/media/tv", true),
            new("Shows", "/media/shows/Show", true)
        };

        var groups = ServerDiskMetricsProvider.GroupMonitoredPaths(
            paths,
            path => ServerDiskMetricsProvider.MatchUnixPath(path, mounts));

        groups.Should().HaveCount(3);
        groups[0].IsRoot.Should().BeTrue();
        groups[0].Match.DisplayRoot.Should().Be("/");
        groups[0].Libraries.Should().BeEmpty();
        ServerDiskMetricsProvider.BuildLabel(null, groups[0]).Should().Be("Runtime (/)");
        ServerDiskMetricsProvider.BuildLabel("/", groups[0]).Should().Be("Runtime (/)");

        groups[1].IsRoot.Should().BeFalse();
        groups[1].Match.Key.Should().Be("8:1");
        groups[1].Libraries.Select(library => library.Label).Should().Equal("Movies");
        ServerDiskMetricsProvider.BuildLabel(null, groups[1]).Should().Be("Movies (/media/movies)");

        groups[2].Match.Key.Should().Be("8:16");
        ServerDiskMetricsProvider.BuildLabel(null, groups[2]).Should().Be("TV (/media/tv), Shows (/media/shows)");
    }

    [Test]
    public void GroupMonitoredPaths_ShouldListEachLibraryWhenDevicesDiffer()
    {
        var mounts = UnixMountTable.Parse("""
            22 1 0:41 / / rw - overlay overlay rw
            50 22 8:16 / /media/tv rw - ext4 /dev/sdb1 rw
            51 22 8:32 / /media/movies rw - ext4 /dev/sdc1 rw
            """);
        var paths = new MonitoredPath[]
        {
            new("Application", "/app", false),
            new("TV", "/media/tv", true),
            new("Movies", "/media/movies", true)
        };

        var groups = ServerDiskMetricsProvider.GroupMonitoredPaths(
            paths,
            path => ServerDiskMetricsProvider.MatchUnixPath(path, mounts));

        groups.Should().HaveCount(3);
        groups[0].Match.DisplayRoot.Should().Be("/");
        ServerDiskMetricsProvider.BuildLabel(null, groups[1]).Should().Be("Movies (/media/movies)");
        ServerDiskMetricsProvider.BuildLabel(null, groups[2]).Should().Be("TV (/media/tv)");
    }

    [Test]
    public void GroupMonitoredPaths_ShouldDedupeLibrariesThatShareADevice()
    {
        var mounts = UnixMountTable.Parse("""
            22 1 0:41 / / rw - overlay overlay rw
            50 22 8:16 / /media/tv rw - ext4 /dev/sdb1 rw
            51 22 8:16 /shows /media/shows rw - ext4 /dev/sdb1 rw
            """);
        var paths = new MonitoredPath[]
        {
            new("Application", "/app", false),
            new("TV", "/media/tv", true),
            new("Shows", "/media/shows", true)
        };

        var groups = ServerDiskMetricsProvider.GroupMonitoredPaths(
            paths,
            path => ServerDiskMetricsProvider.MatchUnixPath(path, mounts));

        groups.Should().HaveCount(2);
        ServerDiskMetricsProvider.BuildLabel(null, groups[1]).Should().Be("TV (/media/tv), Shows (/media/shows)");
    }

    [Test]
    public void GroupMonitoredPaths_ShouldShareLabel_WhenLibrariesUseTheSameMount()
    {
        var mounts = UnixMountTable.Parse("""
            22 1 0:41 / / rw - overlay overlay rw
            50 22 8:16 / /media rw - ext4 /dev/sdb1 rw
            """);
        var paths = new MonitoredPath[]
        {
            new("Application", "/app", false),
            new("Movies", "/media/movies", true),
            new("TV", "/media/tv", true)
        };

        var groups = ServerDiskMetricsProvider.GroupMonitoredPaths(
            paths,
            path => ServerDiskMetricsProvider.MatchUnixPath(path, mounts));

        groups.Should().HaveCount(2);
        ServerDiskMetricsProvider.BuildLabel(null, groups[1]).Should().Be("Movies, TV (/media)");
    }

    [Test]
    public void GroupMonitoredPaths_ShouldKeepLibraryPathsSeparate_WhenMountTableIsMissing()
    {
        var paths = new MonitoredPath[]
        {
            new("Application", "/app", false),
            new("Movies", "/media/movies", true),
            new("TV", "/media/tv", true)
        };

        var groups = ServerDiskMetricsProvider.GroupMonitoredPaths(
            paths,
            path => ServerDiskMetricsProvider.MatchUnixPath(path, []));

        groups.Should().HaveCount(3);
        groups.Select(group => group.Match.Key).Should().Equal("/app", "/media/movies", "/media/tv");
        ServerDiskMetricsProvider.BuildLabel(null, groups[1]).Should().Be("Movies (/media/movies)");
        ServerDiskMetricsProvider.BuildLabel(null, groups[2]).Should().Be("TV (/media/tv)");
    }

    [Test]
    public void BuildLabel_ShouldKeepRealVolumeNameOnRoot()
    {
        var group = new VolumeGroup(new VolumeMatch("C:", @"C:\", "C:"))
        {
            IsRoot = true
        };

        ServerDiskMetricsProvider.BuildLabel("OS", group).Should().Be("OS (C:)");
    }

    [Test]
    public void BuildLabel_ShouldDisambiguateDuplicateLibraryTitles()
    {
        var group = new VolumeGroup(new VolumeMatch("8:1", "/media/a", "/media/a"))
        {
            Libraries =
            {
                new LibraryMount("Movies", "/media/a"),
                new LibraryMount("Movies", "/media/b")
            }
        };

        ServerDiskMetricsProvider.BuildLabel(null, group).Should().Be("Movies (/media/a), Movies (/media/b)");
    }

    [Test]
    [Platform("Win")]
    public void MatchWindowsPath_ShouldKeyByDriveRoot()
    {
        var movies = ServerDiskMetricsProvider.MatchWindowsPath(@"D:\media\movies");
        var shows = ServerDiskMetricsProvider.MatchWindowsPath(@"D:\media\shows");
        var system = ServerDiskMetricsProvider.MatchWindowsPath(@"C:\app");

        movies!.Value.Key.Should().Be("D:");
        movies.Value.Key.Should().Be(shows!.Value.Key);
        system!.Value.Key.Should().Be("C:");
    }
}
