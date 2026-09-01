using System.Diagnostics;
using System.Reflection.PortableExecutable;

namespace Wisp.Updater;

internal sealed record PortableExecutableIdentity(
    bool IsExecutable,
    string? ProductName,
    string? FileDescription,
    string? ProductVersion,
    string? FileVersion);

internal interface IPortableExecutableInspector
{
    PortableExecutableIdentity Inspect(Stream stream, string path);
}

internal sealed class WindowsPortableExecutableInspector : IPortableExecutableInspector
{
    public PortableExecutableIdentity Inspect(Stream stream, string path)
    {
        try
        {
            stream.Position = 0;
            using var reader = new PEReader(stream, PEStreamOptions.LeaveOpen);
            var headers = reader.PEHeaders;
            var characteristics = headers.CoffHeader.Characteristics;
            var isExecutable = headers.PEHeader is not null
                && (characteristics & Characteristics.ExecutableImage) != 0
                && (characteristics & Characteristics.Dll) == 0;
            if (!isExecutable)
            {
                return new PortableExecutableIdentity(false, null, null, null, null);
            }

            var versionInfo = FileVersionInfo.GetVersionInfo(path);
            return new PortableExecutableIdentity(
                true,
                versionInfo.ProductName,
                versionInfo.FileDescription,
                versionInfo.ProductVersion,
                versionInfo.FileVersion);
        }
        catch (Exception exception) when (exception is BadImageFormatException or IOException)
        {
            return new PortableExecutableIdentity(false, null, null, null, null);
        }
    }
}
