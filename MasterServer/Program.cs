namespace MasterServer;

static class Program
{
    static readonly string LogPath = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "master_log.txt"));

    static async Task Main(string[] args)
    {
        var cfg = MasterConfig.Load();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--password" when i + 1 < args.Length: cfg.Password = args[++i]; break;
                case "--http-port" when i + 1 < args.Length: cfg.HttpPort = int.Parse(args[++i]); break;
                case "--registry-port" when i + 1 < args.Length: cfg.RegistryPort = int.Parse(args[++i]); break;
                case "--name" when i + 1 < args.Length: cfg.ClusterName = args[++i]; break;
                case "--local-only": cfg.HostMaster = false; break;
                case "--probe": MasterServerHost.ProbeOffsets = true; break;
                case "--capture-port" when i + 1 < args.Length:
                    MasterServerHost.CapturePort = int.Parse(args[++i]); break;
            }
        }

        await MasterServerHost.RunAsync(cfg, Log);
    }

    static void Log(string msg)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {msg}";
        Console.WriteLine(line);
        try { File.AppendAllText(LogPath, line + Environment.NewLine); } catch { }
    }
}
