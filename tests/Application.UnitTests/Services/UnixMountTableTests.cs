using K7.Server.Application.Services;

namespace K7.Server.Application.UnitTests.Services;

public class UnixMountTableTests
{
    private const string Sample = """
        22 1 0:41 / / rw,relatime - overlay overlay rw
        48 22 8:1 /var/lib/docker/volumes/k7-data/_data /data rw,relatime - ext4 /dev/sda1 rw
        49 22 8:1 /mnt/media/movies /media/movies ro,relatime - ext4 /dev/sda1 rw
        50 22 8:16 / /media/tv ro,relatime - ext4 /dev/sdb1 rw
        51 22 8:16 /shows /media/my\040shows ro,relatime - ext4 /dev/sdb1 rw
        36 35 98:0 /mnt1 /mnt2 rw,noatime master:1 - ext3 /dev/root rw,errors=continue
        """;

    [Test]
    public void Parse_ShouldReadDeviceMountPointAndSource()
    {
        var mounts = UnixMountTable.Parse(Sample);

        mounts.Should().Contain(mount =>
            mount.DeviceId == "0:41" && mount.MountPoint == "/" && mount.Source == "overlay");
        mounts.Should().Contain(mount =>
            mount.DeviceId == "8:1" && mount.MountPoint == "/data" && mount.Source == "/dev/sda1");
        mounts.Should().Contain(mount =>
            mount.DeviceId == "8:16" && mount.MountPoint == "/media/my shows" && mount.Source == "/dev/sdb1");
        mounts.Should().Contain(mount =>
            mount.DeviceId == "98:0" && mount.MountPoint == "/mnt2" && mount.Source == "/dev/root");
    }

    [Test]
    public void FindBest_ShouldPreferLongestMountAndIgnoreSiblingPrefix()
    {
        var mounts = UnixMountTable.Parse(Sample);

        UnixMountTable.FindBest(mounts, "/media/movies/Film/a.mkv")!.Value.MountPoint.Should().Be("/media/movies");
        UnixMountTable.FindBest(mounts, "/data/config")!.Value.MountPoint.Should().Be("/data");
        UnixMountTable.FindBest(mounts, "/app/K7.Server.Web.dll")!.Value.DeviceId.Should().Be("0:41");
        UnixMountTable.FindBest(mounts, "/media/tv/Show")!.Value.DeviceId.Should().Be("8:16");
        UnixMountTable.FindBest(mounts, "/media/my shows/a")!.Value.MountPoint.Should().Be("/media/my shows");
        UnixMountTable.FindBest(mounts, "/media2/file")!.Value.MountPoint.Should().Be("/");
        UnixMountTable.FindBest(mounts, "/mnt2/file")!.Value.DeviceId.Should().Be("98:0");
    }

    [Test]
    public void FindBest_ShouldLetLaterEqualMountWin()
    {
        var mounts = UnixMountTable.Parse("""
            100 1 8:1 / /data rw - ext4 /dev/sda1 rw
            101 1 8:2 / /data rw - ext4 /dev/sdb1 rw
            """);

        var match = UnixMountTable.FindBest(mounts, "/data/config");

        match!.Value.DeviceId.Should().Be("8:2");
        match.Value.Source.Should().Be("/dev/sdb1");
    }

    [Test]
    public void Parse_ShouldReturnEmpty_WhenTextIsEmpty()
    {
        UnixMountTable.Parse("").Should().BeEmpty();
        UnixMountTable.Parse("not a mount line\n").Should().BeEmpty();
    }
}
