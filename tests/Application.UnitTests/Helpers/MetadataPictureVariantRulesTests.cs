using K7.Server.Application.Common.Helpers;
using K7.Server.Domain.Enums;

namespace K7.Server.Application.UnitTests.Helpers;

[TestFixture]
public class MetadataPictureVariantRulesTests
{
    [Test]
    public void TryGetTargetWidth_ShouldReturnExpectedWidths()
    {
        MetadataPictureVariantRules.TryGetTargetWidth(MetadataPictureType.Poster, MetadataPictureSize.Medium, out var posterMedium)
            .Should().BeTrue();
        posterMedium.Should().Be(400);

        MetadataPictureVariantRules.TryGetTargetWidth(MetadataPictureType.Backdrop, MetadataPictureSize.Medium, out var backdropMedium)
            .Should().BeTrue();
        backdropMedium.Should().Be(1920);

        MetadataPictureVariantRules.TryGetTargetWidth(MetadataPictureType.Still, MetadataPictureSize.Small, out var stillSmall)
            .Should().BeTrue();
        stillSmall.Should().Be(320);

        MetadataPictureVariantRules.TryGetTargetWidth(MetadataPictureType.Still, MetadataPictureSize.Medium, out var stillMedium)
            .Should().BeTrue();
        stillMedium.Should().Be(640);
    }

    [Test]
    public void TryGetTargetWidth_ShouldReturnAvatarWidths()
    {
        MetadataPictureVariantRules.TryGetTargetWidth(MetadataPictureType.UserAvatar, MetadataPictureSize.Small, out var userSmall)
            .Should().BeTrue();
        userSmall.Should().Be(160);

        MetadataPictureVariantRules.TryGetTargetWidth(MetadataPictureType.UserAvatar, MetadataPictureSize.Medium, out var userMedium)
            .Should().BeTrue();
        userMedium.Should().Be(400);

        MetadataPictureVariantRules.TryGetTargetWidth(MetadataPictureType.SharedProfileAvatar, MetadataPictureSize.Small, out var profileSmall)
            .Should().BeTrue();
        profileSmall.Should().Be(160);

        MetadataPictureVariantRules.ShouldGenerateVariant(MetadataPictureType.UserAvatar, MetadataPictureSize.Small, 1024)
            .Should().BeTrue();
        MetadataPictureVariantRules.ShouldGenerateVariant(MetadataPictureType.UserAvatar, MetadataPictureSize.Small, 128)
            .Should().BeFalse();
    }

    [Test]
    public void TryGetTargetWidth_ShouldReturnFalse_WhenBackdropSmallOrLogoSmall()
    {
        MetadataPictureVariantRules.TryGetTargetWidth(MetadataPictureType.Backdrop, MetadataPictureSize.Small, out _)
            .Should().BeFalse();
        MetadataPictureVariantRules.TryGetTargetWidth(MetadataPictureType.Logo, MetadataPictureSize.Small, out _)
            .Should().BeFalse();
    }

    [Test]
    public void IsPermanentVariantFallback_ShouldReturnTrue_WhenOriginalIsSmallerThanTarget()
    {
        MetadataPictureVariantRules.IsPermanentVariantFallback(
                MetadataPictureType.Still,
                MetadataPictureSize.Medium,
                320)
            .Should().BeTrue();
    }

    [Test]
    public void IsPermanentVariantFallback_ShouldReturnFalse_WhenOriginalCanProduceVariant()
    {
        MetadataPictureVariantRules.IsPermanentVariantFallback(
                MetadataPictureType.Still,
                MetadataPictureSize.Medium,
                1920)
            .Should().BeFalse();
    }

    [Test]
    public void IsPermanentVariantFallback_ShouldReturnFalse_WhenOriginalWidthIsUnknown()
    {
        MetadataPictureVariantRules.IsPermanentVariantFallback(
                MetadataPictureType.Still,
                MetadataPictureSize.Medium,
                null)
            .Should().BeFalse();
    }

    [Test]
    public void IsPermanentVariantFallback_ShouldReturnTrue_WhenNoRuleExistsForTypeAndSize()
    {
        MetadataPictureVariantRules.IsPermanentVariantFallback(
                MetadataPictureType.Logo,
                MetadataPictureSize.Small,
                800)
            .Should().BeTrue();
    }
}
