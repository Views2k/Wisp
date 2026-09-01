using Microsoft.Win32;
using System.IO;

namespace Wisp.App;

public interface IStartupRegistrationService
{
    void Apply(bool startWithWindows, bool startWithForza);
}

public sealed class StartupRegistrationService : IStartupRegistrationService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Wisp";

    public void SetEnabled(bool enabled) => Apply(enabled, startWithForza: false);

    public void Apply(bool startWithWindows, bool startWithForza)
    {
        var command = BuildCommand(Environment.ProcessPath, startWithWindows, startWithForza);
        if (command is null)
        {
            using var existing = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            existing?.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true) ??
                        Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        key.SetValue(ValueName, command, RegistryValueKind.String);
    }

    internal static string? BuildCommand(string? executablePath, bool startWithWindows, bool startWithForza)
    {
        if (!startWithWindows && !startWithForza)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(executablePath) || !Path.IsPathFullyQualified(executablePath) ||
            executablePath.IndexOfAny(['"', '\r', '\n', '\0']) >= 0)
        {
            throw new InvalidOperationException("Wisp could not determine a safe executable path for startup.");
        }

        // Forza mode takes precedence without erasing the saved Windows
        // preference. Its quiet sign-in companion waits before starting Wisp.
        return $"\"{executablePath}\" {(startWithForza ? "--wait-for-forza" : "--background")}";
    }
}
