namespace Wisp.Updater;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args) => new UpdaterApplication().Run(args);
}
