using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SecondEcran;

public partial class MainWindow : Window
{
    ScreenServer? _server;
    readonly List<MonitorInfo> _mons = Monitors.List();

    public MainWindow()
    {
        InitializeComponent();
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
        Closing += (_, _) => _server?.Stop();
        Append("Prêt. Clique sur Démarrer.");
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
        if (UrlBox != null && PortBox != null) UrlBox.Text = $"http://127.0.0.1:{Port}";
    }

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
        var (ok, msg) = await Task.Run(() => Tools.AdbReverse(port));
        Append((ok ? "✓ " : "⚠ ") + msg);
    }
}
