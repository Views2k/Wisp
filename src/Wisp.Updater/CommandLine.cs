namespace Wisp.Updater;

internal static class CommandLine
{
    internal static string ParseApplyRequestPath(string[] args)
    {
        if (args.Length != 2 || !string.Equals(args[0], "--apply", StringComparison.Ordinal))
        {
            throw new UpdateFailureException(
                UpdaterExitCode.InvalidRequest,
                "UPDATE_ARGUMENTS",
                "The update helper received invalid arguments.");
        }

        return args[1];
    }
}
