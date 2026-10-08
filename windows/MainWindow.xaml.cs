using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SecondEcran;

public partial class MainWindow : Window
{
    ScreenServer? _server;
    List<MonitorInfo> _mons = new();
    VirtualDisplayState _vd = new(false, false, "", "");

    public MainWindow()
    {
        InitializeComponent();
        PortBox_TextChanged(this, null!);
        BuildScreens();
        Closing += (_, _) => _server?.Stop();
        Append("Prêt. Clique sur Démarrer.");
        _ = RefreshVirtualDisplay();
    }

    /// <summary>(Re)crée la liste des écrans à envoyer.</summary>
    void BuildScreens()
    {
        _mons = Monitors.List();
        ScreensPanel.Children.Clear();
        var def = Monitors.DefaultIndex(_mons);
        for (int i = 0; i < _mons.Count; i++)
        {
            var rb = new RadioButton
            {
                Content = $"Écran {i + 1} : {_mons[i]}",
                Tag = i.ToString(),
                GroupName = "screen",
                Style = (Style)FindResource("Seg"),
                IsChecked = i == def,
            };
            ScreensPanel.Children.Add(rb);
        }
    }

    static string Selected(Panel panel, string fallback)
    {
        foreach (var child in panel.Children)
            if (child is RadioButton rb && rb.IsChecked == true && rb.Tag is string tag)
                return tag;
        return fallback;
    }

    void Append(string line)
    {
        LogBox.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + line + Environment.NewLine);
        LogBox.ScrollToEnd();
    }

    void SetRunning(bool running)
    {
        StartBtn.Content = running ? "■  Arrêter" : "▶  Démarrer";
        StatusText.Text = running ? "● En marche ✓" : "■ Arrêté ✕";
        StatusText.Foreground = (Brush)FindResource(running ? "Blue" : "Muted");
    }

    int Port => int.TryParse(PortBox.Text.Trim(), out var p) && p is > 0 and < 65536 ? p : 5555;

    void PortBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (UrlBox != null && PortBox != null)
        {
            var ips = ScreenServer.LocalAddresses();
            UrlBox.Text = ips.Count == 0 ? $"http://127.0.0.1:{Port}"
                : string.Join("   ", ips.Select(a => $"{a}:{Port}"));
        }
    }

    // ---- écran virtuel ---------------------------------------------------------------------
    async Task RefreshVirtualDisplay()
    {
        _vd = await Task.Run(VirtualDisplay.Query);
        if (!_vd.Installed)
        {
            VdStatus.Text = "⚠ Non installé";
            VdStatus.Foreground = (Brush)FindResource("Amber");
            VdBtn.Content = "Comment l'installer ?";
        }
        else if (_vd.Enabled)
        {
            VdStatus.Text = "● Activé ✓";
            VdStatus.Foreground = (Brush)FindResource("Blue");
            VdBtn.Content = "Désactiver l'écran virtuel";
        }
        else
        {
            VdStatus.Text = "■ Désactivé ✕";
            VdStatus.Foreground = (Brush)FindResource("Muted");
            VdBtn.Content = "Activer l'écran virtuel";
        }
        VdBtn.IsEnabled = true;
    }

    async void VdBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!_vd.Installed)
        {
            Append("Écran virtuel non installé. Dans PowerShell : winget install --id=VirtualDrivers.Virtual-Display-Driver -e");
            Append("Puis rouvre cette application.");
            return;
        }
        bool target = !_vd.Enabled;
        VdBtn.IsEnabled = false;
        VdStatus.Text = "… autorisation administrateur demandée";
        var id = _vd.InstanceId;
        var (ok, msg) = await Task.Run(() => VirtualDisplay.Set(id, target));
        Append((ok ? "✓ " : "⚠ ") + msg);
        if (ok) await Task.Delay(2500);        // Windows met un instant à ajouter / retirer l'écran
        await RefreshVirtualDisplay();
        BuildScreens();
        if (ok && _server != null)
            Append("⚠ La liste des écrans a changé : clique sur Arrêter puis Démarrer.");
    }

    // ---- serveur ---------------------------------------------------------------------------
    void StartBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_server != null)
        {
            _server.Stop();
            _server = null;
            SetRunning(false);
            return;
        }

        var cfg = new ServerConfig
        {
            MonitorIndex = int.Parse(Selected(ScreensPanel, "-1")),
            Fps = int.Parse(Selected(FpsPanel, "60")),
            BitrateKbps = int.Parse(Selected(BitratePanel, "8")) * 1000,
            MaxWidth = int.Parse(Selected(WidthPanel, "0")),
            Encoder = Selected(EncoderPanel, "auto"),
            Port = Port,
        };
        var srv = new ScreenServer(cfg);
        srv.Log += msg => Dispatcher.BeginInvoke(new Action(() => Append(msg)));
        srv.Stopped += () => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_server == srv) { _server = null; SetRunning(false); }
        }));
        _server = srv;
        srv.Start();
        SetRunning(true);
    }

    async void Rebrancher_Click(object sender, RoutedEventArgs e)
    {
        int port = Port;
        PortBox_TextChanged(this, null!);
        var (ok, msg) = await Task.Run(() => Tools.AdbReverse(port));
        Append(ok ? "✓ " + msg : "ℹ Débogage USB non utilisé. Adresses du PC : " + UrlBox.Text);
    }
}
