using SecondEcran;

// Banc d'essai Linux : mire de test, sans capture ni souris Windows.
int port = args.Length > 0 ? int.Parse(args[0]) : 5599;
var srv = new ScreenServer(new ServerConfig { TestSource = true, Port = port, MonitorIndex = 0 });
srv.Log += s => Console.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " " + s);
srv.Start();
Thread.Sleep(Timeout.Infinite);
