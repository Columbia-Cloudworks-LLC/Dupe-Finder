using System.Runtime.InteropServices;

namespace DupeFinder.App;

[ComVisible(true), Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IRecycleProgressSink
{
    [PreserveSig] int StartOperations();
    [PreserveSig] int FinishOperations(int result);
    [PreserveSig] int PreRenameItem(uint flags, IntPtr item, [MarshalAs(UnmanagedType.LPWStr)] string name);
    [PreserveSig] int PostRenameItem(uint flags, IntPtr item, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IntPtr created);
    [PreserveSig] int PreMoveItem(uint flags, IntPtr item, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string name);
    [PreserveSig] int PostMoveItem(uint flags, IntPtr item, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IntPtr created);
    [PreserveSig] int PreCopyItem(uint flags, IntPtr item, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string name);
    [PreserveSig] int PostCopyItem(uint flags, IntPtr item, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IntPtr created);
    [PreserveSig] int PreDeleteItem(uint flags, IntPtr item);
    [PreserveSig] int PostDeleteItem(uint flags, IntPtr item, int result, IntPtr created);
    [PreserveSig] int PreNewItem(uint flags, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string name);
    [PreserveSig] int PostNewItem(uint flags, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, uint attributes, int result, IntPtr created);
    [PreserveSig] int UpdateProgress(uint total, uint complete);
    [PreserveSig] int ResetTimer();
    [PreserveSig] int PauseTimer();
    [PreserveSig] int ResumeTimer();
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class RecycleProgressSink : IRecycleProgressSink
{
    public bool Recycled { get; private set; }
    public string Diagnostics { get; private set; } = "";
    public int StartOperations() => 0;
    public int FinishOperations(int result) { Diagnostics += $" Finish=0x{result:X8}"; return 0; }
    public int PreRenameItem(uint flags, IntPtr item, string name) => 0;
    public int PostRenameItem(uint flags, IntPtr item, string name, int result, IntPtr created) => 0;
    public int PreMoveItem(uint flags, IntPtr item, IntPtr destination, string name) => 0;
    public int PostMoveItem(uint flags, IntPtr item, IntPtr destination, string name, int result, IntPtr created) => 0;
    public int PreCopyItem(uint flags, IntPtr item, IntPtr destination, string name) => 0;
    public int PostCopyItem(uint flags, IntPtr item, IntPtr destination, string name, int result, IntPtr created) => 0;
    // Abort rather than fall back to permanent deletion on unsupported/full/disabled Recycle Bins.
    public int PreDeleteItem(uint flags, IntPtr item) { Diagnostics += $" PreFlags=0x{flags:X8}"; return (flags & 0x80) != 0 ? 0 : unchecked((int)0x80004004); }
    public int PostDeleteItem(uint flags, IntPtr item, int result, IntPtr created) { Diagnostics += $" Post=0x{result:X8} Created={created != IntPtr.Zero}"; Recycled = result >= 0 && created != IntPtr.Zero; return 0; }
    public int PreNewItem(uint flags, IntPtr destination, string name) => 0;
    public int PostNewItem(uint flags, IntPtr destination, string name, string template, uint attributes, int result, IntPtr created) => 0;
    public int UpdateProgress(uint total, uint complete) => 0;
    public int ResetTimer() => 0;
    public int PauseTimer() => 0;
    public int ResumeTimer() => 0;
}
