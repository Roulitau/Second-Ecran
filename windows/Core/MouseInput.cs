using System.Text.Json;

namespace SecondEcran;

/// <summary>Traduit les gestes reçus (JSON) en souris Windows.</summary>
public sealed class MouseInput
{
    const uint LDOWN = 0x0002, LUP = 0x0004, RDOWN = 0x0008, RUP = 0x0010, WHEEL = 0x0800;
    readonly MonitorInfo _mon;
    readonly Action<string>? _log;

    public MouseInput(MonitorInfo mon, Action<string>? log = null) { _mon = mon; _log = log; }

    static double Clamp01(double v) => Math.Max(0.0, Math.Min(1.0, v));

    static double Num(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0.0;

    public void Handle(byte[] json)
    {
        using var doc = JsonDocument.Parse(json);
        var m = doc.RootElement;
        string a = m.TryGetProperty("a", out var av) && av.ValueKind == JsonValueKind.String ? av.GetString()! : "";
        if (!OperatingSystem.IsWindows())
        {
            _log?.Invoke("[tactile] " + System.Text.Encoding.UTF8.GetString(json));
            return;
        }
        if (a is "move" or "down" or "up" or "click" or "rclick")
        {
            int x = _mon.X + (int)(Clamp01(Num(m, "x")) * (_mon.W - 1));
            int y = _mon.Y + (int)(Clamp01(Num(m, "y")) * (_mon.H - 1));
            Native.SetCursorPos(x, y);
        }
        switch (a)
        {
            case "down": Btn(LDOWN); break;
            case "up": Btn(LUP); break;
            case "click": Btn(LDOWN); Btn(LUP); break;
            case "rclick": Btn(RDOWN); Btn(RUP); break;
            case "scroll":
                // dy > 0 : les doigts descendent -> le contenu descend -> molette vers le haut
                int delta = (int)(Num(m, "dy") * 1500);
                if (delta != 0) Native.mouse_event(WHEEL, 0, 0, delta, UIntPtr.Zero);
                break;
        }
    }

    static void Btn(uint flag) => Native.mouse_event(flag, 0, 0, 0, UIntPtr.Zero);
}
