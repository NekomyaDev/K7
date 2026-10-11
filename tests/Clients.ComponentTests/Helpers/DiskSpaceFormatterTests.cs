using K7.Clients.Shared.UI.Helpers;

namespace K7.Clients.ComponentTests.Helpers;

public class DiskSpaceFormatterTests
{
    [Test]
    public void Format_ShouldStayInGigabytes_WhenBelowOneTerabyte()
    {
        var amount = DiskSpaceFormatter.Format(420.4);

        amount.Terabytes.Should().BeFalse();
        amount.Value.Should().Be(420.4);
        DiskSpaceFormatter.UnitKey(amount.Terabytes).Should().Be("DiskUnitGb");
    }

    [Test]
    public void Format_ShouldUseTerabytes_WhenAtLeastOneTerabyte()
    {
        var amount = DiskSpaceFormatter.Format(12_000);

        amount.Terabytes.Should().BeTrue();
        amount.Value.Should().BeApproximately(11.71875, 0.0001);
        DiskSpaceFormatter.UnitKey(amount.Terabytes).Should().Be("DiskUnitTb");
    }

    [Test]
    public void LevelClass_ShouldStepFromAccentToWarningToCritical()
    {
        DiskSpaceFormatter.LevelClass(21).Should().BeEmpty();
        DiskSpaceFormatter.LevelClass(20).Should().Be("dashboard-disk-volume--warning");
        DiskSpaceFormatter.LevelClass(10).Should().Be("dashboard-disk-volume--critical");
        DiskSpaceFormatter.LevelClass(0).Should().Be("dashboard-disk-volume--critical");
    }
}
