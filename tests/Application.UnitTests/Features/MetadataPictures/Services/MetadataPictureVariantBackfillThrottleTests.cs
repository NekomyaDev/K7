using K7.Server.Application.Features.MetadataPictures.Services;

namespace K7.Server.Application.UnitTests.Features.MetadataPictures.Services;

[TestFixture]
public class MetadataPictureVariantBackfillThrottleTests
{
    [Test]
    public void TryBegin_ShouldReturnTrue_WhenPictureWasNeverRequested()
    {
        var throttle = new MetadataPictureVariantBackfillThrottle(new FixedTimeProvider(DateTimeOffset.UtcNow));

        throttle.TryBegin(Guid.NewGuid()).Should().BeTrue();
    }

    [Test]
    public void TryBegin_ShouldReturnFalse_WhenPictureWasRequestedWithinTheWindow()
    {
        var time = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var throttle = new MetadataPictureVariantBackfillThrottle(time);
        var pictureId = Guid.NewGuid();

        throttle.TryBegin(pictureId).Should().BeTrue();
        time.Advance(TimeSpan.FromMinutes(9));

        throttle.TryBegin(pictureId).Should().BeFalse();
    }

    [Test]
    public void TryBegin_ShouldReturnTrueAgain_WhenTheWindowHasPassed()
    {
        var time = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var throttle = new MetadataPictureVariantBackfillThrottle(time);
        var pictureId = Guid.NewGuid();

        throttle.TryBegin(pictureId).Should().BeTrue();
        time.Advance(TimeSpan.FromMinutes(11));

        throttle.TryBegin(pictureId).Should().BeTrue();
        throttle.TryBegin(pictureId).Should().BeFalse();
    }

    [Test]
    public void TryBegin_ShouldTrackPicturesIndependently()
    {
        var throttle = new MetadataPictureVariantBackfillThrottle(new FixedTimeProvider(DateTimeOffset.UtcNow));

        throttle.TryBegin(Guid.NewGuid()).Should().BeTrue();
        throttle.TryBegin(Guid.NewGuid()).Should().BeTrue();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }
}
