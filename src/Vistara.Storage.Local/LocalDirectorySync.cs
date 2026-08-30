using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Vistara.Storage.Local;

internal static partial class LocalDirectorySync
{
    private const int InvalidArgument = 22;
    private const int LinuxOperationNotSupported = 95;
    private const int MacOperationNotSupported = 45;

    public static void Flush(string directoryPath)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        int descriptor = Open(directoryPath, 0);
        if (descriptor < 0)
        {
            throw new IOException(
                "The local blob directory could not be opened for durability synchronization.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        try
        {
            if (Fsync(descriptor) == 0)
            {
                return;
            }

            int error = Marshal.GetLastPInvokeError();
            if (error is InvalidArgument or
                LinuxOperationNotSupported or
                MacOperationNotSupported)
            {
                return;
            }

            throw new IOException(
                "The local blob directory could not be durability synchronized.",
                new Win32Exception(error));
        }
        finally
        {
            _ = Close(descriptor);
        }
    }

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int Fsync(int descriptor);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int Close(int descriptor);
}
