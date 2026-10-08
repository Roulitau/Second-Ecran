using System.Diagnostics;
using System.Text.Json;

namespace SecondEcran;

public sealed record VirtualDisplayState(bool Installed, bool Enabled, string InstanceId, string Name);

/// <summary>
/// Active / désactive le pilote d'écran virtuel (Virtual Display Driver, IddSampleDriver...)
/// en activant ou désactivant son périphérique Windows. Lire l'état ne demande aucun droit ;
/// changer l'état demande l'autorisation administrateur (fenêtre UAC).
/// </summary>
public static class VirtualDisplay
{
    static readonly string[] Keys = { "MttVDD", "IddSampleDriver", "Virtual Display", "VirtualDisplay" };

    const string ListScript =
        "$ErrorActionPreference='SilentlyContinue'; " +
        "Get-PnpDevice -Class Display | ForEach-Object { " +
        "$h=(Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName 'DEVPKEY_Device_HardwareIds').Data -join ';'; " +
        "[pscustomobject]@{Id=$_.InstanceId;Name=$_.FriendlyName;Status=[string]$_.Status;Hw=$h} } | ConvertTo-Json -Compress";

    public static VirtualDisplayState Query()
    {
        var none = new VirtualDisplayState(false, false, "", "");
        if (!OperatingSystem.IsWindows()) return none;
        try
        {
            var (code, output) = Tools.Run("powershell.exe",
                new[] { "-NoProfile", "-NonInteractive", "-Command", ListScript }, 25000);
            return Parse(output) ?? none;
        }
        catch { return none; }
    }

    /// <summary>Lit la sortie JSON de la liste des cartes d'affichage (objet seul ou tableau).</summary>
    public static VirtualDisplayState? Parse(string output)
    {
        int i = output.IndexOfAny(new[] { '[', '{' });
        if (i < 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(output[i..]);
            var items = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().ToList()
                : new List<JsonElement> { doc.RootElement };
            foreach (var e in items)
            {
                string id = Str(e, "Id"), name = Str(e, "Name"), status = Str(e, "Status"), hw = Str(e, "Hw");
                var all = name + ";" + hw;
                if (Keys.Any(k => all.Contains(k, StringComparison.OrdinalIgnoreCase)))
                    return new VirtualDisplayState(true, status.Equals("OK", StringComparison.OrdinalIgnoreCase), id, name);
            }
        }
        catch { }
        return null;
    }

    static string Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>Active ou désactive le périphérique (fenêtre UAC).</summary>
    public static (bool ok, string message) Set(string instanceId, bool enable)
    {
        if (!OperatingSystem.IsWindows()) return (false, "Windows uniquement");
        if (string.IsNullOrEmpty(instanceId) || instanceId.Contains('\'') || instanceId.Contains('"'))
            return (false, "identifiant d'écran virtuel invalide");
        string verb = enable ? "Enable-PnpDevice" : "Disable-PnpDevice";
        string cmd = $"{verb} -InstanceId '{instanceId}' -Confirm:$false -ErrorAction Stop";
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -Command \"{cmd}\"",
        };
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return (false, "impossible de lancer PowerShell");
            if (!p.WaitForExit(40000)) return (false, "délai dépassé");
            return p.ExitCode == 0
                ? (true, enable ? "Écran virtuel activé." : "Écran virtuel désactivé.")
                : (false, "Windows a refusé le changement (code " + p.ExitCode + ").");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (false, "Annulé : l'autorisation administrateur est nécessaire.");
        }
        catch (Exception e)
        {
            return (false, e.Message);
        }
    }
}
