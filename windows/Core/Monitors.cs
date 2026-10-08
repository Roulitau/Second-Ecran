namespace SecondEcran;

public record MonitorInfo(int X, int Y, int W, int H, bool Primary)
{
    public override string ToString() => $"{W}x{H}" + (Primary ? " (principal)" : "");
}

public static class Monitors
{
    /// <summary>Écrans en pixels physiques (le manifeste de l'exe active le mode DPI par écran).</summary>
    public static List<MonitorInfo> List()
    {
        var found = new List<MonitorInfo>();
        if (!OperatingSystem.IsWindows())
        {
            found.Add(new MonitorInfo(0, 0, 1280, 720, true));
            return found;
        }
        Native.MonitorEnumProc cb = (IntPtr hMon, IntPtr hdc, ref Native.RECT r, IntPtr data) =>
        {
            var info = new Native.MONITORINFOEX();
            info.cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFOEX>();
            Native.GetMonitorInfo(hMon, ref info);
            var m = info.rcMonitor;
            found.Add(new MonitorInfo(m.Left, m.Top, m.Right - m.Left, m.Bottom - m.Top, (info.dwFlags & 1) != 0));
            return true;
        };
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);
        GC.KeepAlive(cb);
        return found;
    }

    /// <summary>Le dernier écran non principal (l'écran virtuel), sinon le principal.</summary>
    public static int DefaultIndex(List<MonitorInfo> mons)
    {
        for (int i = mons.Count - 1; i >= 0; i--)
            if (!mons[i].Primary) return i;
        return 0;
    }
}
