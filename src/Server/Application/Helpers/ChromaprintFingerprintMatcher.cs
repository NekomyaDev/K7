using System.Numerics;

namespace K7.Server.Application.Helpers;

/// <summary>
/// Pairwise chromaprint matching: inverted index, contiguous runs,
/// and bridging of neighbouring runs so noisy openings still form one intro.
/// </summary>
public static class ChromaprintFingerprintMatcher
{
    public const int MaxFingerprintPointDifferences = 6;
    public const double MaxTimeSkipSeconds = 3.5;
    public const int InvertedIndexShift = 2;
    public const double SampleDuration = 4096.0 / 11025.0 / 3.0;

    /// <summary>Compare each episode only to this many neighbours on each side (episode order).</summary>
    public const int NeighborRadius = 2;

    private const double BridgeMinimumRunDuration = 8.0;
    private const double BridgeMaximumGapShare = 0.5;

    public readonly record struct FingerprintMatch(
        double LhsStartSeconds,
        double LhsEndSeconds,
        double RhsStartSeconds,
        double RhsEndSeconds);

    public readonly record struct SegmentMatch(double StartSeconds, double EndSeconds);

    /// <summary>
    /// All-pairs within <see cref="NeighborRadius"/> of ordered fingerprints.
    /// <paramref name="fingerprints"/> must be in episode order (same index = same episode).
    /// </summary>
    public static Dictionary<int, SegmentMatch> FindBestMatches(
        IReadOnlyList<uint[]?> fingerprints,
        double minDurationSeconds,
        double maxDurationSeconds)
    {
        var bestMatches = new Dictionary<int, SegmentMatch>();

        for (var i = 0; i < fingerprints.Count; i++)
        {
            var fpI = fingerprints[i];
            if (fpI is null || fpI.Length == 0)
                continue;

            var jMax = Math.Min(fingerprints.Count - 1, i + NeighborRadius);
            for (var j = i + 1; j <= jMax; j++)
            {
                var fpJ = fingerprints[j];
                if (fpJ is null || fpJ.Length == 0)
                    continue;

                var match = CompareFingerprints(fpI, fpJ);
                if (match is null)
                    continue;

                TryUpdateBestMatch(
                    bestMatches,
                    i,
                    match.Value.LhsStartSeconds,
                    match.Value.LhsEndSeconds,
                    minDurationSeconds,
                    maxDurationSeconds);
                TryUpdateBestMatch(
                    bestMatches,
                    j,
                    match.Value.RhsStartSeconds,
                    match.Value.RhsEndSeconds,
                    minDurationSeconds,
                    maxDurationSeconds);
            }
        }

        return bestMatches;
    }

    public static FingerprintMatch? CompareFingerprints(uint[] lhs, uint[] rhs)
    {
        if (lhs.Length == 0 || rhs.Length == 0)
            return null;

        var rhsIndex = CreateInvertedIndex(rhs);
        var candidateShifts = FindCandidateShifts(lhs, rhsIndex);
        FingerprintMatch? best = null;
        var bestDuration = 0.0;

        foreach (var shift in candidateShifts)
        {
            var result = FindContiguousAtShift(lhs, rhs, shift);
            if (result is null)
                continue;

            var duration = result.Value.LhsEndSeconds - result.Value.LhsStartSeconds;
            if (duration > bestDuration)
            {
                bestDuration = duration;
                best = result;
            }
        }

        return best;
    }

    public static FingerprintMatch? FindContiguousAtShift(uint[] lhs, uint[] rhs, int shift)
    {
        var lhsOffset = Math.Max(0, shift);
        var rhsOffset = Math.Max(0, -shift);
        var overlapLength = Math.Min(lhs.Length - lhsOffset, rhs.Length - rhsOffset);
        if (overlapLength <= 0)
            return null;

        var runs = new List<(int Start, int End)>();
        for (var i = 0; i < overlapLength; i++)
        {
            var lhsPos = lhsOffset + i;
            var diff = BitOperations.PopCount(lhs[lhsPos] ^ rhs[rhsOffset + i]);
            if (diff > MaxFingerprintPointDifferences)
                continue;

            if (runs.Count > 0
                && (lhsPos - runs[^1].End) * SampleDuration <= MaxTimeSkipSeconds)
            {
                runs[^1] = (runs[^1].Start, lhsPos);
            }
            else
            {
                runs.Add((lhsPos, lhsPos));
            }
        }

        if (runs.Count == 0)
            return null;

        BridgeRuns(runs);

        var bestStart = -1;
        var bestEnd = -1;
        foreach (var (start, end) in runs)
        {
            if (bestStart < 0 || end - start > bestEnd - bestStart)
                (bestStart, bestEnd) = (start, end);
        }

        if (bestStart < 0)
            return null;

        var positionShift = rhsOffset - lhsOffset;
        return new FingerprintMatch(
            LhsStartSeconds: bestStart * SampleDuration,
            LhsEndSeconds: (bestEnd + 1) * SampleDuration,
            RhsStartSeconds: (bestStart + positionShift) * SampleDuration,
            RhsEndSeconds: (bestEnd + 1 + positionShift) * SampleDuration);
    }

    private static void BridgeRuns(List<(int Start, int End)> runs)
    {
        var joined = true;
        while (joined)
        {
            joined = false;
            for (var i = 0; i + 1 < runs.Count; i++)
            {
                if (!Bridges(runs[i], runs[i + 1]))
                    continue;

                runs[i] = (runs[i].Start, runs[i + 1].End);
                runs.RemoveAt(i + 1);
                joined = true;
            }
        }
    }

    private static bool Bridges((int Start, int End) left, (int Start, int End) right)
    {
        var shorter = Math.Min(left.End - left.Start, right.End - right.Start) * SampleDuration;
        if (shorter < BridgeMinimumRunDuration)
            return false;

        var gap = (right.Start - left.End) * SampleDuration;
        return gap <= shorter * BridgeMaximumGapShare;
    }

    private static void TryUpdateBestMatch(
        Dictionary<int, SegmentMatch> matches,
        int index,
        double startSeconds,
        double endSeconds,
        double minDuration,
        double maxDuration)
    {
        var duration = endSeconds - startSeconds;
        if (duration < minDuration || duration > maxDuration)
            return;

        if (!matches.TryGetValue(index, out var existing)
            || duration > existing.EndSeconds - existing.StartSeconds)
        {
            matches[index] = new SegmentMatch(startSeconds, endSeconds);
        }
    }

    private static Dictionary<uint, int> CreateInvertedIndex(uint[] fingerprint)
    {
        var index = new Dictionary<uint, int>(fingerprint.Length);
        for (var i = 0; i < fingerprint.Length; i++)
            index[fingerprint[i]] = i;
        return index;
    }

    private static HashSet<int> FindCandidateShifts(uint[] lhs, Dictionary<uint, int> rhsIndex)
    {
        var shifts = new HashSet<int> { 0 };

        for (var i = 0; i < lhs.Length; i++)
        {
            var value = lhs[i];
            for (var delta = -InvertedIndexShift; delta <= InvertedIndexShift; delta++)
            {
                var target = unchecked((uint)((long)value + delta));
                if (rhsIndex.TryGetValue(target, out var rhsPos))
                    shifts.Add(i - rhsPos);
            }
        }

        return shifts;
    }
}
