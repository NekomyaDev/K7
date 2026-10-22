using K7.Clients.MAUI.Linux;

namespace K7.Clients.MAUI.SmokeTests;

[TestFixture]
public class LinuxLibVlcBundleTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "k7-libvlc-bundle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Test]
    public void Locate_ShouldReturnNull_WhenNoBundleExists()
    {
        LinuxLibVlcBundle.Locate(_root, null).Should().BeNull();
    }

    [Test]
    public void Locate_ShouldFindBundleNextToApplication()
    {
        var bundle = CreateBundle(Path.Combine(_root, "libvlc", "linux-x64"));

        var layout = LinuxLibVlcBundle.Locate(_root, null);

        layout.Should().NotBeNull();
        layout!.Directory.Should().Be(bundle);
        layout.LibVlc.Should().Be(Path.Combine(bundle, "libvlc.so.12"));
        layout.LibVlcCore.Should().Be(Path.Combine(bundle, "libvlccore.so.9"));
        layout.LibrariesDirectory.Should().Be(Path.Combine(bundle, "lib"));
        layout.PluginsDirectory.Should().Be(Path.Combine(bundle, "plugins"));
    }

    [Test]
    public void Locate_ShouldPreferOverrideDirectory_WhenItHoldsLibVlc()
    {
        CreateBundle(Path.Combine(_root, "libvlc", "linux-x64"));
        var custom = CreateBundle(Path.Combine(_root, "custom-vlc"));

        LinuxLibVlcBundle.Locate(_root, custom)!.Directory.Should().Be(custom);
    }

    [Test]
    public void Locate_ShouldFallBackToApplicationBundle_WhenOverrideIsEmpty()
    {
        var bundle = CreateBundle(Path.Combine(_root, "libvlc", "linux-x64"));
        var empty = Path.Combine(_root, "empty");
        Directory.CreateDirectory(empty);

        LinuxLibVlcBundle.Locate(_root, empty)!.Directory.Should().Be(bundle);
    }

    [Test]
    public void EnumeratePreloadLibraries_ShouldListPrivateLibrariesThenCore()
    {
        var bundle = CreateBundle(Path.Combine(_root, "libvlc", "linux-x64"));
        var lib = Path.Combine(bundle, "lib");
        Directory.CreateDirectory(lib);
        File.WriteAllText(Path.Combine(lib, "libvlc_pulse.so.0"), string.Empty);
        File.WriteAllText(Path.Combine(lib, "libavcodec.so.60"), string.Empty);
        File.WriteAllText(Path.Combine(lib, "README.txt"), "not a library");

        var layout = LinuxLibVlcBundle.Locate(_root, null)!;
        var preload = LinuxLibVlcBundle.EnumeratePreloadLibraries(layout);

        preload.Should().Equal(
            Path.Combine(lib, "libavcodec.so.60"),
            Path.Combine(lib, "libvlc_pulse.so.0"),
            Path.Combine(bundle, "libvlccore.so.9"));
    }

    [Test]
    public void EnumeratePreloadLibraries_ShouldSkipLibVa()
    {
        var bundle = CreateBundle(Path.Combine(_root, "libvlc", "linux-x64"));
        var lib = Path.Combine(bundle, "lib");
        Directory.CreateDirectory(lib);
        File.WriteAllText(Path.Combine(lib, "libva.so.2"), string.Empty);
        File.WriteAllText(Path.Combine(lib, "libva-drm.so.2"), string.Empty);
        File.WriteAllText(Path.Combine(lib, "libavutil.so.58"), string.Empty);

        var preload = LinuxLibVlcBundle.EnumeratePreloadLibraries(LinuxLibVlcBundle.Locate(_root, null)!);

        preload.Should().NotContain(path => Path.GetFileName(path).StartsWith("libva", StringComparison.Ordinal));
        preload.Should().Contain(Path.Combine(lib, "libavutil.so.58"));
    }

    private static string CreateBundle(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "libvlc.so.12"), string.Empty);
        File.WriteAllText(Path.Combine(directory, "libvlccore.so.9"), string.Empty);
        return directory;
    }
}
