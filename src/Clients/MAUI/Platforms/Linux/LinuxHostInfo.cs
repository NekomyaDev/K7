namespace K7.Clients.MAUI.Platforms.Linux;

/// <summary>
/// Facts about the Linux host that change rendering choices: WSL (kernel banner, because
/// <c>/mnt/wslg</c> is not mounted inside Flatpak) and hypervisor guests without GPU
/// pass-through (DMI vendor strings), where GTK and WebKit run on llvmpipe.
/// </summary>
internal static class LinuxHostInfo
{
    private static readonly Lazy<bool> _virtualMachine = new(DetectVirtualMachine);
    private static readonly Lazy<bool> _wslKernel = new(DetectWslKernel);

    public static bool IsVirtualMachine => _virtualMachine.Value;

    public static bool IsWslKernel => _wslKernel.Value;

    private static bool DetectWslKernel()
    {
        try
        {
            return File.Exists("/proc/version")
                && File.ReadAllText("/proc/version").Contains("microsoft", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool DetectVirtualMachine()
    {
        try
        {
            var vendor = ReadDmi("sys_vendor");
            var product = ReadDmi("product_name");
            if (vendor.Contains("Microsoft Corporation", StringComparison.OrdinalIgnoreCase)
                && product.Contains("Virtual Machine", StringComparison.OrdinalIgnoreCase))
                return true;

            foreach (var marker in new[] { "QEMU", "VMware", "innotek", "VirtualBox", "Xen", "Parallels" })
            {
                if (vendor.Contains(marker, StringComparison.OrdinalIgnoreCase)
                    || product.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private static string ReadDmi(string name)
    {
        var path = "/sys/class/dmi/id/" + name;
        return File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty;
    }
}
