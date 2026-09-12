using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using DupeFinder.Core;
using Microsoft.Win32.SafeHandles;

namespace DupeFinder.App;

/// <summary>Locks, verifies, and renames the opened file by handle before giving its private path to the shell.</summary>
internal sealed class WindowsDeletionBackend : IDeletionBackend
{
    public async Task DeleteVerifiedAsync(Entry file, string root, bool permanently, CancellationToken token)
    {
        PathSafety.Check(file.Path, root);
        // DELETE access lets us rename/dispose THIS handle. Share.Read prevents replacement and writes.
        using var handle = CreateFile(file.Path, 0x80000000 | 0x00010000, 1, IntPtr.Zero, 3, 0x00200000 | 0x08000000, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException(new Win32Exception(Marshal.GetLastWin32Error()).Message);
        using var stream = new FileStream(handle, FileAccess.Read);
        if (Convert.ToHexString(await SHA256.HashDataAsync(stream, token)) != file.Hash)
            throw new IOException("The file changed since scanning. Scan again.");
        PathSafety.Check(file.Path, root);
        token.ThrowIfCancellationRequested();
        if (permanently)
        {
            // FILE_DISPOSITION_INFO deletes the verified opened object, never a substituted path.
            var data = Marshal.AllocHGlobal(1);
            try
            {
                Marshal.WriteByte(data, 1);
                if (!SetFileInformationByHandle(handle, 4, data, 1)) throw new IOException(new Win32Exception(Marshal.GetLastWin32Error()).Message);
            }
            finally { Marshal.FreeHGlobal(data); }
            return;
        }
        // Stage by handle in a unique sibling folder. The shell cannot silently permanently delete:
        // RECYCLEONDELETE requires recycling; EARLYFAILURE stops on any failure.
        var staging = Path.Combine(Path.GetDirectoryName(file.Path)!, ".dupefinder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var staged = Path.Combine(staging, file.Name);
        try { Rename(handle, staged); }
        catch { Directory.Delete(staging, false); throw; }
        stream.Dispose();
        try
        {
            await RecycleAsync(staged);
        }
        catch (Exception ex)
        {
            if (!File.Exists(staged)) throw new IOException("Windows did not confirm the outcome. Check the Recycle Bin and scan again. " + ex.Message, ex);
            try { File.Move(staged, file.Path, false); }
            catch (Exception restoreError)
            {
                throw new IOException($"Recycle failed. Your file is retained at '{staged}'. Restore failed: {restoreError.Message}. Recycle error: {ex.Message}", ex);
            }
            throw new IOException("Recycle failed; the file was restored. " + ex.Message, ex);
        }
        finally
        {
            try { Directory.Delete(staging, false); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static void Rename(SafeFileHandle handle, string destination)
    {
        var name = System.Text.Encoding.Unicode.GetBytes(Path.GetFullPath(destination));
        var offset = IntPtr.Size == 8 ? 20 : 12;
        var data = Marshal.AllocHGlobal(offset + name.Length + 2);
        try
        {
            for (var i = 0; i < offset; i++) Marshal.WriteByte(data, i, 0);
            Marshal.WriteInt32(data, IntPtr.Size == 8 ? 16 : 8, name.Length);
            Marshal.Copy(name, 0, data + offset, name.Length);
            Marshal.WriteInt16(data, offset + name.Length, 0);
            if (!SetFileInformationByHandle(handle, 3, data, (uint)(offset + name.Length + 2)))
                throw new IOException("Cannot stage the verified file: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }
        finally { Marshal.FreeHGlobal(data); }
    }

    private static Task RecycleAsync(string path)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            IFileOperation? operation = null;
            IShellItem? item = null;
            var sink = new RecycleProgressSink();
            try
            {
                operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("3AD05575-8857-4850-9277-11B85BDB8E09"))!)!;
                // FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT | FOFX_RECYCLEONDELETE | FOFX_EARLYFAILURE.
                operation.SetOperationFlags(0x10 | 0x400 | 0x4 | 0x2000 | 0x80000 | 0x100000);
                var iid = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");
                Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out item));
                operation.DeleteItem(item, sink);
                operation.PerformOperations();
                operation.GetAnyOperationsAborted(out var aborted);
                if (!sink.Recycled || File.Exists(path)) throw new IOException($"Windows did not confirm recycling (aborted={aborted}). {sink.Diagnostics}");
                completion.SetResult();
            }
            catch (Exception ex)
            {
                // Some shell extensions fail final bookkeeping after PostDeleteItem succeeded.
                // Trust the per-item recycle receipt, not the unrelated aggregate HRESULT.
                if (sink.Recycled && !File.Exists(path)) completion.TrySetResult();
                else completion.TrySetException(new IOException(ex.Message + " " + sink.Diagnostics, ex));
            }
            finally
            {
                if (item != null) Marshal.ReleaseComObject(item);
                if (operation != null) Marshal.ReleaseComObject(operation);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return completion.Task;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, IntPtr info, uint size);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr context, ref Guid handler, ref Guid iid, out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint type, out IntPtr name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }
    // Preserve the exact native vtable order, including unused methods.
    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        void Advise(IntPtr sink, out uint cookie);
        void Unadvise(uint cookie);
        void SetOperationFlags(uint flags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        void SetProgressDialog(IntPtr dialog);
        void SetProperties(IntPtr properties);
        void SetOwnerWindow(uint owner);
        void ApplyPropertiesToItem(IntPtr item);
        void ApplyPropertiesToItems(IntPtr items);
        void RenameItem(IntPtr item, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        void RenameItems(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string name);
        void MoveItem(IntPtr item, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        void MoveItems(IntPtr items, IntPtr destination);
        void CopyItem(IntPtr item, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        void CopyItems(IntPtr items, IntPtr destination);
        void DeleteItem([MarshalAs(UnmanagedType.Interface)] IShellItem item, [MarshalAs(UnmanagedType.Interface)] IRecycleProgressSink sink);
        void DeleteItems(IntPtr items);
        void NewItem(IntPtr destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, IntPtr sink);
        void PerformOperations();
        void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
}
