using System.Globalization;
using RemoteFileSync.Models;
using RemoteFileSync.Logging;
using RemoteFileSync.Progress;
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

        // When --json-progress is active, suppress ALL console output so stdout is pure JSON
        using var logger = new SyncLogger(options.Verbose, options.LogFile, suppressConsole: options.JsonProgress);
        logger.Summary($"RemoteFileSync v1.0 — {(options.IsServer ? "Server" : "Client")} mode");

        var progressWriter = options.JsonProgress
            ? new Progress.JsonProgressWriter(Console.Out)
            : Progress.JsonProgressWriter.Null;
        using var stdinReader = options.JsonProgress
            ? new Progress.StdinCommandReader(Console.In, Console.Out)
            : Progress.StdinCommandReader.Null;
        if (options.JsonProgress)
            stdinReader.Start();

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
                var server = new Network.SyncServer(options, logger, progressWriter, stdinReader);
                return await server.RunAsync(cts.Token);
            }
            else
            {
                SyncDatabase? db = null;
                if (options.DeleteEnabled)
                {
                    var dbPath = SyncDatabase.GetDbPath(SyncDatabase.DefaultBaseDir, options.Folder, options.Host!, options.Port);

                    // Auto-migrate from old binary state if needed
                    var binPath = Path.Combine(Path.GetDirectoryName(dbPath)!, "sync-state.bin");
                    SyncDatabase.MigrateFromBinary(binPath, dbPath);

                    db = new SyncDatabase(dbPath);
                }

                try
                {
                    var client = new Network.SyncClient(options, logger, db: db,
                        progressWriter: progressWriter, stdinReader: stdinReader);
                    return await client.RunAsync(cts.Token);
                }
                finally
                {
                    db?.Dispose();
                }
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

    private static string NextValue(string[] args, ref int i, string flag)
    {
        if (i + 1 >= args.Length)
            throw new ArgumentException($"Missing value for {flag}.");
        return args[++i];
    }

    private static int NextInt(string[] args, ref int i, string flag)
    {
        var raw = NextValue(args, ref i, flag);
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw new ArgumentException($"{flag} expects an integer, got '{raw}'.");
        return value;
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
                    options.Host = NextValue(args, ref i, "--host");
                    break;
                case "--port" or "-p":
                    options.Port = NextInt(args, ref i, "--port");
                    break;
                case "--folder" or "-f":
                    options.Folder = NextValue(args, ref i, "--folder");
                    break;
                case "--bidirectional" or "-b":
                    options.Bidirectional = true;
                    break;
                case "--backup-folder":
                    options.BackupFolder = NextValue(args, ref i, "--backup-folder");
                    break;
                case "--include":
                    options.IncludePatterns.Add(NextValue(args, ref i, "--include"));
                    break;
                case "--exclude":
                    options.ExcludePatterns.Add(NextValue(args, ref i, "--exclude"));
                    break;
                case "--block-size" or "-bs":
                    options.BlockSize = NextInt(args, ref i, "--block-size");
                    break;
                case "--max-threads" or "-t":
                    options.MaxThreads = NextInt(args, ref i, "--max-threads");
                    break;
                case "--verbose" or "-v":
                    options.Verbose = true;
                    break;
                case "--log" or "-l":
                    options.LogFile = NextValue(args, ref i, "--log");
                    break;
                case "--delete" or "-d":
                    options.DeleteEnabled = true;
                    break;
                case "--max-delete-percent":
                    options.MaxDeletePercent = NextInt(args, ref i, "--max-delete-percent");
                    break;
                case "--force-delete":
                    options.ForceDelete = true;
                    break;
                case "--once":
                    options.Once = true;
                    break;
                case "--bind":
                    options.BindAddress = NextValue(args, ref i, "--bind");
                    break;
                case "--json-progress":
                    options.JsonProgress = true;
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
        Console.Error.WriteLine("  --bind <ip>             Server bind address (default: 127.0.0.1).");
        Console.Error.WriteLine("                          Use 0.0.0.0 to expose on all interfaces —");
        Console.Error.WriteLine("                          WARNING: the protocol is UNAUTHENTICATED.");
        Console.Error.WriteLine("  --folder, -f <path>     Local sync folder (required)");
        Console.Error.WriteLine("  --bidirectional, -b     Enable bi-directional sync");
        Console.Error.WriteLine("  --delete, -d            Enable deletion propagation (opt-in)");
        Console.Error.WriteLine("  --max-delete-percent <n> Abort if deletions exceed n% of tracked");
        Console.Error.WriteLine("                          files (default: 25). Guards against a");
        Console.Error.WriteLine("                          repointed or empty peer folder.");
        Console.Error.WriteLine("  --force-delete          Bypass --max-delete-percent");
        Console.Error.WriteLine("  --once                  Server: handle one connection, then exit");
        Console.Error.WriteLine("  --backup-folder <path>  Backup folder (default: .rfs-backups-NAME beside");
        Console.Error.WriteLine("                          the sync folder; must be outside it)");
        Console.Error.WriteLine("  --include <pattern>     Glob include pattern (repeatable)");
        Console.Error.WriteLine("  --exclude <pattern>     Glob exclude pattern (repeatable)");
        Console.Error.WriteLine("  --block-size, -bs <n>   Transfer block size in bytes (default: 65536)");
        Console.Error.WriteLine("  --max-threads, -t <n>   Max concurrent transfers (default: 1)");
        Console.Error.WriteLine("  --verbose, -v           Verbose console output");
        Console.Error.WriteLine("  --log, -l <path>        Log file path");
        Console.Error.WriteLine("  --json-progress         JSON events to stdout (for UI integration)");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Exit codes: 0 success, 1 completed with skipped files, 2 connection");
        Console.Error.WriteLine("failure, 3 protocol/fatal error, 4 aborted by a safety guard.");
    }
}
