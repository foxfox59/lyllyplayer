using System;
using System.Threading;
using System.Threading.Tasks;

namespace LyllyPlayer.Updater;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (!CliOptions.TryParse(args, out var options, out var error))
            {
                Console.Error.WriteLine(error);
                Console.Error.WriteLine(
                    "Usage: LyllyPlayer.Updater.exe --install-dir <path> --zip-url <url> " +
                    "[--target-version <ver>] [--parent-pid <pid>] [--no-restart]");
                return 2;
            }

            return await new UpdateRunner().RunAsync(options, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try { UpdateLog.Exception(ex, "Fatal"); } catch { /* ignore */ }
            return 1;
        }
    }
}
