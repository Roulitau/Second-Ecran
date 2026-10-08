using SecondEcran;

if (args.Length > 0 && args[0] == "vdtest")
{
    // Analyse des sorties PowerShell (sans Windows)
    var one = "{\"Id\":\"ROOT\\\\DISPLAY\\\\0000\",\"Name\":\"Virtual Display Driver\",\"Status\":\"OK\",\"Hw\":\"Root\\\\MttVDD\"}";
    var arr = "[{\"Id\":\"PCI\\\\VEN_10DE\",\"Name\":\"NVIDIA GeForce\",\"Status\":\"OK\",\"Hw\":\"PCI\\\\VEN_10DE\"},"
            + "{\"Id\":\"ROOT\\\\DISPLAY\\\\0001\",\"Name\":null,\"Status\":\"Error\",\"Hw\":\"Root\\\\MttVDD;x\"}]";
    var other = "{\"Id\":\"PCI\\\\X\",\"Name\":\"Intel UHD\",\"Status\":\"OK\",\"Hw\":\"PCI\\\\X\"}";
    Console.WriteLine(VirtualDisplay.Parse(one));
    Console.WriteLine(VirtualDisplay.Parse(arr));
    Console.WriteLine(VirtualDisplay.Parse(other) == null ? "aucun (attendu)" : "ERREUR");
    Console.WriteLine(VirtualDisplay.Parse("") == null ? "vide (attendu)" : "ERREUR");
    return;
}

// Banc d'essai Linux : mire de test, sans capture ni souris Windows.
int port = args.Length > 0 ? int.Parse(args[0]) : 5599;
var srv = new ScreenServer(new ServerConfig { TestSource = true, Port = port, MonitorIndex = 0 });
srv.Log += s => Console.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " " + s);
srv.Start();
Thread.Sleep(Timeout.Infinite);
