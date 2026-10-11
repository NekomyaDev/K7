using System.Text;

namespace K7.Server.Application.Services;

/// <summary>
/// Parses Linux <c>/proc/self/mountinfo</c> so a path can be mapped to the mount that contains it.
/// <see cref="Path.GetPathRoot(string)"/> is always <c>/</c> on Unix, which hides Docker bind mounts.
/// </summary>
internal static class UnixMountTable
{
    internal readonly record struct Mount(string DeviceId, string MountPoint, string Source);

    private const string MountInfoPath = "/proc/self/mountinfo";

    internal static IReadOnlyList<Mount> Read()
    {
        if (!File.Exists(MountInfoPath))
            return [];

        return Parse(File.ReadAllText(MountInfoPath));
    }

    internal static IReadOnlyList<Mount> Parse(string mountInfo)
    {
        if (string.IsNullOrEmpty(mountInfo))
            return [];

        var mounts = new List<Mount>();
        foreach (var rawLine in mountInfo.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0)
                continue;

            if (TryParseLine(line, out var mount))
                mounts.Add(mount);
        }

        return mounts;
    }

    internal static Mount? FindBest(IReadOnlyList<Mount> mounts, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || mounts.Count == 0)
            return null;

        var normalized = NormalizeMountPoint(path);
        Mount? best = null;
        var bestLength = -1;

        foreach (var mount in mounts)
        {
            if (!IsUnderMount(normalized, mount.MountPoint))
                continue;

            if (mount.MountPoint.Length < bestLength)
                continue;

            best = mount;
            bestLength = mount.MountPoint.Length;
        }

        return best;
    }

    internal static bool IsUnderMount(string path, string mountPoint)
    {
        if (mountPoint == "/")
            return path.StartsWith('/');

        if (path.Equals(mountPoint, StringComparison.Ordinal))
            return true;

        return path.StartsWith(mountPoint + "/", StringComparison.Ordinal);
    }

    private static bool TryParseLine(string line, out Mount mount)
    {
        mount = default;
        var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 9)
            return false;

        var separator = -1;
        for (var i = 6; i < tokens.Length; i++)
        {
            if (tokens[i] == "-")
            {
                separator = i;
                break;
            }
        }

        if (separator < 6 || separator + 2 >= tokens.Length)
            return false;

        var deviceId = tokens[2];
        if (!deviceId.Contains(':'))
            return false;

        var mountPoint = NormalizeMountPoint(Unescape(tokens[4]));
        if (!mountPoint.StartsWith('/'))
            return false;

        var source = Unescape(tokens[separator + 2]);
        mount = new Mount(deviceId, mountPoint, source);
        return true;
    }

    internal static string NormalizeMountPoint(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;

        var normalized = path.Length > 1 ? path.TrimEnd('/') : path;
        return normalized.Length == 0 ? "/" : normalized;
    }

    internal static string Unescape(string value)
    {
        if (!value.Contains('\\'))
            return value;

        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\'
                && i + 3 < value.Length
                && IsOctal(value[i + 1])
                && IsOctal(value[i + 2])
                && IsOctal(value[i + 3]))
            {
                var code = ((value[i + 1] - '0') << 6)
                    | ((value[i + 2] - '0') << 3)
                    | (value[i + 3] - '0');
                builder.Append((char)code);
                i += 3;
                continue;
            }

            builder.Append(value[i]);
        }

        return builder.ToString();
    }

    private static bool IsOctal(char value) => value is >= '0' and <= '7';
}
