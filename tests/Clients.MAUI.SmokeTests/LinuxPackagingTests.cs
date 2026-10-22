using K7.Clients.MAUI.Linux;

namespace K7.Clients.MAUI.SmokeTests;

[TestFixture]
public class LinuxPackagingTests
{
    [Test]
    public void IsFlatpak_ShouldDetectFlatpakId()
    {
        LinuxPackaging.IsFlatpak("com.k7.maui").Should().BeTrue();
        LinuxPackaging.IsFlatpak("  com.k7.maui  ").Should().BeTrue();
        LinuxPackaging.IsFlatpak(null).Should().BeFalse();
        LinuxPackaging.IsFlatpak("").Should().BeFalse();
        LinuxPackaging.IsFlatpak("   ").Should().BeFalse();
    }

    [Test]
    public void ShouldRegisterNativeProtocol_ShouldSkipInsideFlatpak()
    {
        LinuxPackaging.ShouldRegisterNativeProtocol("com.k7.maui").Should().BeFalse();
        LinuxPackaging.ShouldRegisterNativeProtocol(null).Should().BeTrue();
        LinuxPackaging.ShouldRegisterNativeProtocol("").Should().BeTrue();
    }
}

