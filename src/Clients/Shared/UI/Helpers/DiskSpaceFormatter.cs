namespace K7.Clients.Shared.UI.Helpers;

public static class DiskSpaceFormatter
{
    public readonly record struct Amount(double Value, bool Terabytes);

    public static Amount Format(double gigabytes)
    {
        if (double.IsNaN(gigabytes) || gigabytes < 0)
            gigabytes = 0;

        if (gigabytes >= 1024d)
            return new Amount(gigabytes / 1024d, Terabytes: true);

        return new Amount(gigabytes, Terabytes: false);
    }

    public static string Text(double value) => value.ToString("0.#");

    public static string UnitKey(bool terabytes) => terabytes ? "DiskUnitTb" : "DiskUnitGb";

    public static string LevelClass(double freePercent) => freePercent switch
    {
        <= 10 => "dashboard-disk-volume--critical",
        <= 20 => "dashboard-disk-volume--warning",
        _ => ""
    };
}
