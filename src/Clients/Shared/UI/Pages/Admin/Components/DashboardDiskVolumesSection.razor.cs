using K7.Clients.Shared.UI.Helpers;
using K7.Shared.Dtos;
using Microsoft.AspNetCore.Components;

namespace K7.Clients.Shared.UI.Pages.Admin.Components;

public partial class DashboardDiskVolumesSection
{
    [Parameter] public IReadOnlyList<ServerDiskVolumeDto> Volumes { get; set; } = [];

    private string _volumesFingerprint = string.Empty;

    protected override bool ShouldRender()
    {
        var fingerprint = BuildFingerprint(Volumes);
        if (fingerprint == _volumesFingerprint)
            return false;

        _volumesFingerprint = fingerprint;
        return true;
    }

    private static string BuildFingerprint(IReadOnlyList<ServerDiskVolumeDto> volumes) =>
        string.Join('|', volumes.Select(v =>
            $"{v.Label}:{v.UsedGb:0.#}:{v.TotalGb:0.#}:{v.FreePercent:0.#}"));

    private string FormatUsage(ServerDiskVolumeDto volume)
    {
        var used = DiskSpaceFormatter.Format(volume.UsedGb);
        var total = DiskSpaceFormatter.Format(volume.TotalGb);
        return string.Format(
            L["DiskVolumeUsage"],
            DiskSpaceFormatter.Text(used.Value),
            L[DiskSpaceFormatter.UnitKey(used.Terabytes)],
            DiskSpaceFormatter.Text(total.Value),
            L[DiskSpaceFormatter.UnitKey(total.Terabytes)]);
    }

    private string FormatFree(ServerDiskVolumeDto volume)
    {
        var free = DiskSpaceFormatter.Format(Math.Max(0, volume.TotalGb - volume.UsedGb));
        return string.Format(
            L["DiskVolumeFree"],
            DiskSpaceFormatter.Text(free.Value),
            volume.FreePercent.ToString("0.#"),
            L[DiskSpaceFormatter.UnitKey(free.Terabytes)]);
    }
}
