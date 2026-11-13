using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.CSharp.RuntimeBinder;

namespace PicLaundry;

internal static class RecycleBin
{
    private const int ShellRecycleBin = 10;

    internal static string? FindExport(string exportPath)
    {
        // Each drag exports exactly one file to its own GUID directory. Explorer
        // preserves that directory as the original location, even with extensions hidden.
        string? exportDirectory = Path.GetDirectoryName(exportPath);
        object? shell = null, bin = null, items = null;
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type == null) return null;
            shell = Activator.CreateInstance(type);
            if (shell == null) return null;
            bin = ((dynamic)shell).NameSpace(ShellRecycleBin);
            if (bin == null) return null;
            items = ((dynamic)bin).Items();
            int count = ((dynamic)items!).Count;
            for (int i = 0; i < count; i++)
            {
                object? item = ((dynamic)items!).Item(i);
                if (item == null) continue;
                try
                {
                    var location = ((dynamic)item).ExtendedProperty("System.Recycle.DeletedFrom") as string;
                    if (location != null && string.Equals(Path.TrimEndingDirectorySeparator(location),
                        exportDirectory, StringComparison.OrdinalIgnoreCase))
                        return ((dynamic)item).Path as string;
                }
                catch (COMException) { }
                finally { Release(item); }
            }
        }
        catch (Exception error) when (error is COMException or RuntimeBinderException
            or UnauthorizedAccessException or InvalidComObjectException) { }
        finally
        {
            Release(items);
            Release(bin);
            Release(shell);
        }
        return null;
    }

    private static void Release(object? value)
    {
        if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }
}
