using RemoteFileSync.Models;
using RemoteFileSync.Logging;
using RemoteFileSync.State;

namespace RemoteFileSync;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        SyncOptions options;
        try
        {
            options = ParseArgs(args);
            options.Validate();
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            PrintUsage();
            return 3;
        }

        using var logger = new SyncLogger(options.Verbose, options.LogFile);
        logger.Summary($"RemoteFileSync v1.0 — {(options.IsServer ? "Server" : "Client")} mode");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            logger.Warning("Cancellation requested...");
        };

        try
        {
            if (options.IsServer)
            {
                var server = new Network.SyncServer(options, logger);
                return await server.RunAsync(cts.Token);
            }
            else
            {
                SyncStateManager? stateManager = null;
                if (options.DeleteEnabled)
                    stateManager = new SyncStateManager(SyncStateManager.DefaultBaseDir);
                var client = new Network.SyncClient(options, logger, stateManager);
                return await client.RunAsync(cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            logger.Summary("Operation cancelled.");
            return 1;
        }
        catch (Exception ex)
        {
            logger.Error($"Fatal error: {ex.Message}");
            return 3;
        }
    }

    public static SyncOptions ParseArgs(string[] args)
    {
        if (args.Length == 0)
            throw new ArgumentException("No arguments provided. Use 'server' or 'client' as the first argument.");

        var mode = args[0].ToLowerInvariant();
        if (mode != "server" && mode != "client")
            throw new ArgumentException($"Invalid mode '{args[0]}'. Use 'server' or 'client'.");

        var options = new SyncOptions { IsServer = mode == "server" };

        for (int i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--host" or "-h":
                    options.Host = args[++i];
                    break;
                case "--port" or "-p":
                    options.Port = int.Parse(args[++i]);
                    break;
                case "--folder" or "-f":
                    options.Folder = args[++i];
                    break;
                case "--bidirectional" or "-b":
                    options.Bidirectional = true;
                    break;
                case "--backup-folder":
                    options.BackupFolder = args[++i];
                    break;
                case "--include":
                    options.IncludePatterns.Add(args[++i]);
                    break;
                case "--exclude":
                    options.ExcludePatterns.Add(args[++i]);
                    break;
                case "--block-size" or "-bs":
                    options.BlockSize = int.Parse(args[++i]);
                    break;
                case "--max-threads" or "-t":
                    options.MaxThreads = int.Parse(args[++i]);
                    break;
                case "--verbose" or "-v":
                    options.Verbose = true;
                    break;
                case "--log" or "-l":
                    options.LogFile = args[++i];
                    break;
                case "--delete" or "-d":
                    options.DeleteEnabled = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown option: {arg}");
            }
        }

        return options;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine("Usage: RemoteFileSync.exe <server|client> [options]");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Options:");
        Console.Error.WriteLine("  --host, -h <addr>       Server hostname/IP (client only)");
        Console.Error.WriteLine("  --port, -p <port>       TCP port (default: 15782)");
        Console.Error.WriteLine("  --folder, -f <path>     Local sync folder (required)");
        Console.Error.WriteLine("  --bidirectional, -b     Enable bi-directional sync");
        Console.Error.WriteLine("  --delete, -d            Enable deletion propagation (opt-in)");
        Console.Error.WriteLine("  --backup-folder <path>  Backup folder (default: sync folder)");
        Console.Error.WriteLine("  --include <pattern>     Glob include pattern (repeatable)");
        Console.Error.WriteLine("  --exclude <pattern>     Glob exclude pattern (repeatable)");
        Console.Error.WriteLine("  --block-size, -bs <n>   Transfer block size in bytes (default: 65536)");
        Console.Error.WriteLine("  --max-threads, -t <n>   Max concurrent transfers (default: 1)");
        Console.Error.WriteLine("  --verbose, -v           Verbose console output");
        Console.Error.WriteLine("  --log, -l <path>        Log file path");
    }
}
