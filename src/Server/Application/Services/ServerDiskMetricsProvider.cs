using K7.Server.Application.Common.Configuration;
using K7.Server.Application.Common.Interfaces;
using K7.Shared.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace K7.Server.Application.Services;

public interface IServerDiskMetricsProvider
{
    IReadOnlyList<ServerDiskVolumeDto> GetVolumes();
}

public sealed class ServerDiskMetricsProvider(
    IOptions<PathsConfiguration> pathsOptions,
    IServiceScopeFactory scopeFactory,
    ILogger<ServerDiskMetricsProvider> logger) : IServerDiskMetricsProvider
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);
    private readonly PathsConfiguration _paths = pathsOptions.Value;
    private readonly object _lock = new();
    private IReadOnlyList<ServerDiskVolumeDto> _cached = [];
    private DateTime _cachedAt = DateTime.MinValue;

    public IReadOnlyList<ServerDiskVolumeDto> GetVolumes()
    {
        lock (_lock)
        {
            if (DateTime.UtcNow - _cachedAt < CacheDuration)
                return _cached;

            _cached = ComputeVolumes();
            _cachedAt = DateTime.UtcNow;
            return _cached;
        }
    }

    private IReadOnlyList<ServerDiskVolumeDto> ComputeVolumes()
    {
        var monitoredPaths = GetMonitoredPaths();
        if (monitoredPaths.Count == 0)
            return [];

        // On Linux, Path.GetPathRoot is always "/". Match the mount table so bind-mounted
        // library disks are reported separately, and collapse mounts that share a device.
        var mounts = OperatingSystem.IsWindows() ? [] : ReadMounts();
        var groups = GroupMonitoredPaths(monitoredPaths, path => MatchPath(path, mounts));
        if (groups.Count == 0)
            return [];

        var volumes = new List<ServerDiskVolumeDto>(groups.Count);
        foreach (var group in groups)
        {
            try
            {
                var drive = new DriveInfo(group.Match.QueryPath);
                if (!drive.IsReady || drive.TotalSize <= 0)
                    continue;

                volumes.Add(ToVolumeDto(drive, group));
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Skipping disk volume at {Path}", group.Match.QueryPath);
            }
        }

        return volumes;
    }

    private IReadOnlyList<UnixMountTable.Mount> ReadMounts()
    {
        try
        {
            var mounts = UnixMountTable.Read();
            if (mounts.Count == 0)
                logger.LogDebug("Mount table unavailable. Disk metrics fall back to each monitored path.");

            return mounts;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to read mount table for disk metrics");
            return [];
        }
    }

    private List<MonitoredPath> GetMonitoredPaths()
    {
        var paths = new List<MonitoredPath>
        {
            new("Application", ResolvePath(AppContext.BaseDirectory), IsLibrary: false)
        };

        AddConfiguredPath(paths, "Config", _paths.Config, isLibrary: false);
        AddConfiguredPath(paths, "Metadatas", _paths.Metadatas, isLibrary: false);
        AddConfiguredPath(paths, "Logs", _paths.Logs, isLibrary: false);
        AddConfiguredPath(paths, "Transcoding", _paths.Transcoding, isLibrary: false);

        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
            var libraries = db.Libraries.AsNoTracking()
                .Where(l => l.RootPath != null && l.RootPath != "")
                .Select(l => new { l.Title, l.RootPath })
                .ToList();

            foreach (var library in libraries)
                AddConfiguredPath(paths, library.Title, library.RootPath!, isLibrary: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load library paths for disk metrics");
        }

        return paths;
    }

    private static void AddConfiguredPath(List<MonitoredPath> paths, string label, string path, bool isLibrary)
    {
        var resolved = ResolvePath(path);
        if (string.IsNullOrWhiteSpace(resolved))
            return;

        var existingIndex = paths.FindIndex(p =>
            string.Equals(p.ResolvedPath, resolved, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            if (isLibrary && !paths[existingIndex].IsLibrary && !string.IsNullOrWhiteSpace(label))
                paths[existingIndex] = paths[existingIndex] with { Label = label, IsLibrary = true };

            return;
        }

        paths.Add(new MonitoredPath(label, resolved, isLibrary));
    }

    private static string ResolvePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path;

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return path;
        }
    }

    internal static IReadOnlyList<VolumeGroup> GroupMonitoredPaths(
        IReadOnlyList<MonitoredPath> paths,
        Func<string, VolumeMatch?> matchPath)
    {
        if (paths.Count == 0)
            return [];

        var rootMatch = matchPath(paths[0].ResolvedPath);
        var rootKey = rootMatch?.Key;
        var groups = new Dictionary<string, VolumeGroup>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            var match = matchPath(path.ResolvedPath);
            if (match is null)
                continue;

            if (!groups.TryGetValue(match.Value.Key, out var group))
            {
                group = new VolumeGroup(match.Value)
                {
                    IsRoot = rootKey is not null
                        && string.Equals(match.Value.Key, rootKey, StringComparison.OrdinalIgnoreCase)
                };
                groups[match.Value.Key] = group;
            }

            if (!path.IsLibrary || string.IsNullOrWhiteSpace(path.Label))
                continue;

            var alreadyListed = group.Libraries.Any(library =>
                string.Equals(library.Label, path.Label, StringComparison.OrdinalIgnoreCase)
                && string.Equals(library.DisplayRoot, match.Value.DisplayRoot, StringComparison.Ordinal));
            if (alreadyListed)
                continue;

            group.Libraries.Add(new LibraryMount(path.Label, match.Value.DisplayRoot));
        }

        var ordered = new List<VolumeGroup>(groups.Count);
        if (rootKey is not null && groups.TryGetValue(rootKey, out var rootGroup))
            ordered.Add(rootGroup);

        foreach (var group in groups.Values
            .Where(group => rootKey is null || !string.Equals(group.Match.Key, rootKey, StringComparison.OrdinalIgnoreCase))
            .OrderBy(group => group.Match.DisplayRoot, StringComparer.OrdinalIgnoreCase))
        {
            ordered.Add(group);
        }

        return ordered;
    }

    internal static VolumeMatch? MatchPath(string path, IReadOnlyList<UnixMountTable.Mount> mounts)
    {
        if (OperatingSystem.IsWindows())
            return MatchWindowsPath(path);

        return MatchUnixPath(path, mounts);
    }

    internal static VolumeMatch? MatchWindowsPath(string path)
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root))
            return null;

        var display = root.TrimEnd('\\', '/');
        if (display.Length == 0)
            display = root;

        return new VolumeMatch(display, root, display);
    }

    internal static VolumeMatch? MatchUnixPath(string path, IReadOnlyList<UnixMountTable.Mount> mounts)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var mount = UnixMountTable.FindBest(mounts, path);
        if (mount is not null)
            return new VolumeMatch(mount.Value.DeviceId, mount.Value.MountPoint, mount.Value.MountPoint);

        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root))
            return null;

        var normalized = UnixMountTable.NormalizeMountPoint(path);
        return new VolumeMatch(normalized, normalized, normalized);
    }

    private static ServerDiskVolumeDto ToVolumeDto(DriveInfo drive, VolumeGroup group)
    {
        var totalGb = drive.TotalSize / (1024d * 1024 * 1024);
        var freeGb = drive.AvailableFreeSpace / (1024d * 1024 * 1024);
        var usedGb = totalGb - freeGb;
        var freePercent = totalGb > 0 ? freeGb / totalGb * 100 : 0;
        string? volumeLabel;
        try
        {
            volumeLabel = drive.VolumeLabel;
        }
        catch (Exception)
        {
            volumeLabel = null;
        }

        return new ServerDiskVolumeDto
        {
            Label = BuildLabel(volumeLabel, group),
            UsedGb = Math.Round(usedGb, 1),
            TotalGb = Math.Round(totalGb, 1),
            FreePercent = Math.Round(freePercent, 1)
        };
    }

    internal static string BuildLabel(string? volumeLabel, VolumeGroup group)
    {
        var libraries = group.Libraries;
        if (libraries.Count == 1)
            return $"{libraries[0].Label} ({libraries[0].DisplayRoot})";

        if (libraries.Count > 1)
        {
            var sameRoot = libraries
                .Select(library => library.DisplayRoot)
                .Distinct(StringComparer.Ordinal)
                .Count() == 1;
            if (sameRoot)
            {
                var names = string.Join(", ", libraries.Select(library => library.Label));
                return $"{names} ({libraries[0].DisplayRoot})";
            }

            return string.Join(", ", libraries.Select(library => $"{library.Label} ({library.DisplayRoot})"));
        }

        var root = string.IsNullOrEmpty(group.Match.DisplayRoot) ? "/" : group.Match.DisplayRoot;
        if (HasDistinctVolumeLabel(volumeLabel, root))
            return $"{volumeLabel} ({root})";

        if (group.IsRoot)
            return $"Runtime ({root})";

        return root;
    }

    private static bool HasDistinctVolumeLabel(string? volumeLabel, string root)
    {
        if (string.IsNullOrWhiteSpace(volumeLabel))
            return false;

        return !string.Equals(volumeLabel, root, StringComparison.Ordinal)
            && volumeLabel != "/";
    }
}

internal readonly record struct MonitoredPath(string Label, string ResolvedPath, bool IsLibrary);

internal readonly record struct VolumeMatch(string Key, string QueryPath, string DisplayRoot);

internal readonly record struct LibraryMount(string Label, string DisplayRoot);

internal sealed class VolumeGroup(VolumeMatch match)
{
    public VolumeMatch Match { get; } = match;
    public List<LibraryMount> Libraries { get; } = [];
    public bool IsRoot { get; set; }
}
