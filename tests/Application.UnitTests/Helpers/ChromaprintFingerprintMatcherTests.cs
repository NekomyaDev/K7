using K7.Server.Application.Helpers;

namespace K7.Server.Application.UnitTests.Helpers;

[TestFixture]
public class ChromaprintFingerprintMatcherTests
{
    [Test]
    public void FindContiguousAtShift_ShouldBridgeNeighbouringRuns_WhenGapIsSmallRelativeToRuns()
    {
        // Two 10s matching runs (~81 points each) separated by ~4s of mismatch.
        // Without bridging, each run is kept separately; with bridging they merge past MinIntro.
        const int runPoints = 81; // ~10s
        const int gapPoints = 32; // ~4s
        var shared = BuildUniqueFingerprint(runPoints * 2 + gapPoints);
        var lhs = shared.ToArray();
        var rhs = shared.ToArray();

        // Punch a mismatch gap in the middle of rhs so FindContiguous sees two runs.
        for (var i = runPoints; i < runPoints + gapPoints; i++)
            rhs[i] = unchecked(rhs[i] + 0x00FFFFFF);

        var match = ChromaprintFingerprintMatcher.FindContiguousAtShift(lhs, rhs, shift: 0);

        match.Should().NotBeNull();
        var duration = match!.Value.LhsEndSeconds - match.Value.LhsStartSeconds;
        duration.Should().BeGreaterThan(18);
    }

    [Test]
    public void FindBestMatches_ShouldOnlyCompareWithinNeighborRadius()
    {
        // Episodes 0 and 4 share a long identical fingerprint but are farther than radius 2.
        // Episodes 0-1 and 3-4 are neighbours with short unique matches.
        var sharedFar = BuildUniqueFingerprint(200); // ~25s
        var nearA = BuildUniqueFingerprint(120); // ~15s
        var nearB = BuildUniqueFingerprint(120);

        uint[][] fps =
        [
            Concat(nearA, Noise(50)),
            Concat(nearA, Noise(50)),
            Noise(250),
            Concat(nearB, Noise(50)),
            Concat(nearB, sharedFar), // far from 0
        ];
        // Put sharedFar also on episode 0 but after nearA so neighbour match with 1 wins on nearA;
        // episode 0 vs 4 would be longer if compared - ensure they are NOT compared.
        fps[0] = Concat(nearA, sharedFar);

        var matches = ChromaprintFingerprintMatcher.FindBestMatches(
            fps,
            minDurationSeconds: 15,
            maxDurationSeconds: 120);

        matches.Should().ContainKey(0);
        matches.Should().ContainKey(1);
        var duration01 = matches[0].EndSeconds - matches[0].StartSeconds;
        duration01.Should().BeLessThan(20); // neighbour nearA (~15s), not the far sharedFar (~25s+)
    }

    private static uint[] BuildUniqueFingerprint(int length)
    {
        var fp = new uint[length];
        for (var i = 0; i < length; i++)
            fp[i] = unchecked((uint)(0xA0000000 + i * 17));
        return fp;
    }

    private static uint[] Noise(int length)
    {
        var fp = new uint[length];
        for (var i = 0; i < length; i++)
            fp[i] = unchecked((uint)(0xB0000000 + i * 31 + 7));
        return fp;
    }

    private static uint[] Concat(uint[] a, uint[] b)
    {
        var result = new uint[a.Length + b.Length];
        Buffer.BlockCopy(a, 0, result, 0, a.Length * sizeof(uint));
        Buffer.BlockCopy(b, 0, result, a.Length * sizeof(uint), b.Length * sizeof(uint));
        return result;
    }
}
