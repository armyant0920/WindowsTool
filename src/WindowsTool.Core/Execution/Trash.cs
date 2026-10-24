using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace WindowsTool.Core.Execution;

/// <param name="Success">已經放進資源回收筒。</param>
/// <param name="RecycledPath">在資源回收筒中的位置（Windows 的 <c>$Recycle.Bin\…\$R…</c>），可能取不到。</param>
/// <param name="Error">失敗原因（可以直接顯示給使用者）。</param>
public sealed record TrashResult(bool Success, string? RecycledPath, string? Error);

/// <summary>資源回收筒 / 垃圾桶（PLAN 15）。</summary>
public interface ITrash
{
    bool IsSupported { get; }

    /// <summary>事先判斷不能放進資源回收筒的原因（例如網路磁碟）；可以時回傳 null。</summary>
    string? CannotRecycleReason(string path);

    TrashResult Recycle(string path);
}

/// <summary>還沒有實作的平台（Linux / macOS 在 Phase 7）。</summary>
public sealed class UnsupportedTrash : ITrash
{
    public bool IsSupported => false;

    public string? CannotRecycleReason(string path) => "這個作業系統目前還不支援資源回收筒，請改用隔離區。";

    public TrashResult Recycle(string path) => new(false, null, CannotRecycleReason(path));
}

/// <summary>
/// Windows 資源回收筒：使用 Shell 的 <c>IFileOperation</c>。
/// <para>
/// 不用 <c>Microsoft.VisualBasic.FileIO.FileSystem</c>（SHFileOperation）：項目無法回收時（網路磁碟、隨身碟、超過資源回收筒容量），
/// 它在「不提示」模式下會直接永久刪除，違反「刪除預設可還原」的原則。
/// IFileOperation 在刪除每個項目前會呼叫 <c>PreDeleteItem</c>，旗標中沒有 <c>TSF_DELETE_RECYCLE_IF_POSSIBLE</c> 就代表會永久刪除，
/// 這時回傳取消，該項目就不會被刪除。
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsTrash : ITrash
{
    private const uint FofSilent = 0x0004;
    private const uint FofNoConfirmation = 0x0010;
    private const uint FofAllowUndo = 0x0040;
    private const uint FofNoErrorUi = 0x0400;
    private const uint FofxRecycleOnDelete = 0x00080000;
    private const uint TsfDeleteRecycleIfPossible = 0x80;
    private const int ErrorCancelled = unchecked((int)0x800704C7);
    private const uint SigdnFileSysPath = 0x80058000;

    public bool IsSupported => true;

    public string? CannotRecycleReason(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return "網路位置沒有資源回收筒，請改用隔離區。";
        }

        try
        {
            var drive = new DriveInfo(root);
            return drive.DriveType switch
            {
                DriveType.Fixed => null,
                DriveType.Removable => $"{root} 是卸除式磁碟（例如隨身碟），沒有資源回收筒，請改用隔離區。",
                DriveType.Network => $"{root} 是網路磁碟，沒有資源回收筒，請改用隔離區。",
                _ => $"{root} 沒有資源回收筒，請改用隔離區。",
            };
        }
        catch (ArgumentException)
        {
            return "無法判斷這個位置的磁碟類型，請改用隔離區。";
        }
    }

    public TrashResult Recycle(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (CannotRecycleReason(path) is { } reason)
        {
            return new TrashResult(false, null, reason);
        }

        // Shell 的檔案操作必須在 STA 執行緒上進行
        TrashResult? result = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = RecycleOnSta(path);
            }
#pragma warning disable CA1031 // 轉成結果回報，不讓執行緒崩潰
            catch (Exception ex)
#pragma warning restore CA1031
            {
                failure = ex;
            }
        })
        {
            IsBackground = true,
            Name = "WindowsTool recycle",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        return result ?? new TrashResult(false, null, failure?.Message ?? "未知的錯誤");
    }

    private static TrashResult RecycleOnSta(string path)
    {
        var operation = (IFileOperation)new FileOperationClass();
        try
        {
            operation.SetOperationFlags(FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi | FofxRecycleOnDelete);

            var iid = typeof(IShellItem).GUID;
            var hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var item);
            if (hr != 0 || item is null)
            {
                return new TrashResult(false, null, $"無法開啟這個項目（0x{hr:X8}）。");
            }

            var sink = new ProgressSink();
            operation.DeleteItem(item, sink);
            var performed = operation.PerformOperations();

            if (sink.WouldDeletePermanently)
            {
                return new TrashResult(false, null, "Windows 無法把這個項目放進資源回收筒（可能超過資源回收筒的容量上限），為了避免永久刪除已經取消。請改用隔離區。");
            }

            if (sink.DeleteResult is { } deleteHr && deleteHr < 0) // 正數是成功代碼（例如 COPYENGINE_S_DONT_PROCESS_CHILDREN）
            {
                return new TrashResult(false, null, $"{Marshal.GetExceptionForHR(deleteHr)?.Message ?? "刪除失敗"}（0x{deleteHr:X8}）");
            }

            if (performed < 0)
            {
                return new TrashResult(false, null, $"{Marshal.GetExceptionForHR(performed)?.Message ?? "操作失敗"}（0x{performed:X8}）");
            }

            if (Exists(path))
            {
                return new TrashResult(false, null, "操作完成，但項目仍然存在（可能被其他程式使用中）。");
            }

            return new TrashResult(true, sink.RecycledPath, null);
        }
        finally
        {
            Marshal.FinalReleaseComObject(operation);
        }
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static string? GetPath(IShellItem? item)
    {
        if (item is null || item.GetDisplayName(SigdnFileSysPath, out var pointer) != 0 || pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(pointer);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

#pragma warning disable SYSLIB1054 // 需要回傳 COM 介面，使用內建的 COM interop
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(
        string pszPath, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem? ppv);
#pragma warning restore SYSLIB1054

    // ---- COM 定義（shobjidl_core.h，方法順序必須與 vtable 相同） ----

    [ComImport]
    [Guid("3ad05575-8857-4850-9277-11b85bdb8e09")]
    private class FileOperationClass;

    [ComImport]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig]
        int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);

        [PreserveSig]
        int GetParent(out IShellItem ppsi);

        [PreserveSig]
        int GetDisplayName(uint sigdnName, out IntPtr ppszName);

        [PreserveSig]
        int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);

        [PreserveSig]
        int Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport]
    [Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        uint Advise(IFileOperationProgressSink pfops);

        void Unadvise(uint dwCookie);

        void SetOperationFlags(uint dwOperationFlags);

        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string pszMessage);

        void SetProgressDialog([MarshalAs(UnmanagedType.IUnknown)] object popd);

        void SetProperties([MarshalAs(UnmanagedType.IUnknown)] object pproparray);

        void SetOwnerWindow(IntPtr hwndOwner);

        void ApplyPropertiesToItem(IShellItem psiItem);

        void ApplyPropertiesToItems([MarshalAs(UnmanagedType.IUnknown)] object punkItems);

        void RenameItem(IShellItem psiItem, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, IFileOperationProgressSink? pfopsItem);

        void RenameItems([MarshalAs(UnmanagedType.IUnknown)] object pUnkItems, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);

        void MoveItem(IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName, IFileOperationProgressSink? pfopsItem);

        void MoveItems([MarshalAs(UnmanagedType.IUnknown)] object punkItems, IShellItem psiDestinationFolder);

        void CopyItem(IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszCopyName, IFileOperationProgressSink? pfopsItem);

        void CopyItems([MarshalAs(UnmanagedType.IUnknown)] object punkItems, IShellItem psiDestinationFolder);

        void DeleteItem(IShellItem psiItem, IFileOperationProgressSink? pfopsItem);

        void DeleteItems([MarshalAs(UnmanagedType.IUnknown)] object punkItems);

        void NewItem(IShellItem psiDestinationFolder, uint dwFileAttributes, [MarshalAs(UnmanagedType.LPWStr)] string pszName, [MarshalAs(UnmanagedType.LPWStr)] string? pszTemplateName, IFileOperationProgressSink? pfopsItem);

        [PreserveSig]
        int PerformOperations();

        [return: MarshalAs(UnmanagedType.Bool)]
        bool GetAnyOperationsAborted();
    }

    [ComImport]
    [Guid("04b0f1a7-9490-44bc-96e1-4296a31252e2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperationProgressSink
    {
        [PreserveSig] int StartOperations();

        [PreserveSig] int FinishOperations(int hrResult);

        [PreserveSig] int PreRenameItem(uint dwFlags, IShellItem psiItem, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName);

        [PreserveSig] int PostRenameItem(uint dwFlags, IShellItem psiItem, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName, int hrRename, IShellItem? psiNewlyCreated);

        [PreserveSig] int PreMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName);

        [PreserveSig] int PostMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName, int hrMove, IShellItem? psiNewlyCreated);

        [PreserveSig] int PreCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName);

        [PreserveSig] int PostCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName, int hrCopy, IShellItem? psiNewlyCreated);

        [PreserveSig] int PreDeleteItem(uint dwFlags, IShellItem psiItem);

        [PreserveSig] int PostDeleteItem(uint dwFlags, IShellItem psiItem, int hrDelete, IShellItem? psiNewlyCreated);

        [PreserveSig] int PreNewItem(uint dwFlags, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName);

        [PreserveSig] int PostNewItem(uint dwFlags, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName, [MarshalAs(UnmanagedType.LPWStr)] string? pszTemplateName, uint dwFileAttributes, int hrNew, IShellItem? psiNewItem);

        [PreserveSig] int UpdateProgress(uint iWorkTotal, uint iWorkSoFar);

        [PreserveSig] int ResetTimer();

        [PreserveSig] int PauseTimer();

        [PreserveSig] int ResumeTimer();
    }

    /// <summary>攔截每個刪除動作：會被永久刪除的項目一律取消。</summary>
    [ComVisible(true)]
    private sealed class ProgressSink : IFileOperationProgressSink
    {
        public bool WouldDeletePermanently { get; private set; }

        public int? DeleteResult { get; private set; }

        public string? RecycledPath { get; private set; }

        public int PreDeleteItem(uint dwFlags, IShellItem psiItem)
        {
            if ((dwFlags & TsfDeleteRecycleIfPossible) == 0)
            {
                WouldDeletePermanently = true;
                return ErrorCancelled;
            }

            return 0;
        }

        public int PostDeleteItem(uint dwFlags, IShellItem psiItem, int hrDelete, IShellItem? psiNewlyCreated)
        {
            DeleteResult = hrDelete;
            RecycledPath = GetPath(psiNewlyCreated);
            return 0;
        }

        public int StartOperations() => 0;

        public int FinishOperations(int hrResult) => 0;

        public int PreRenameItem(uint dwFlags, IShellItem psiItem, string? pszNewName) => 0;

        public int PostRenameItem(uint dwFlags, IShellItem psiItem, string? pszNewName, int hrRename, IShellItem? psiNewlyCreated) => 0;

        public int PreMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string? pszNewName) => 0;

        public int PostMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string? pszNewName, int hrMove, IShellItem? psiNewlyCreated) => 0;

        public int PreCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string? pszNewName) => 0;

        public int PostCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, string? pszNewName, int hrCopy, IShellItem? psiNewlyCreated) => 0;

        public int PreNewItem(uint dwFlags, IShellItem psiDestinationFolder, string? pszNewName) => 0;

        public int PostNewItem(uint dwFlags, IShellItem psiDestinationFolder, string? pszNewName, string? pszTemplateName, uint dwFileAttributes, int hrNew, IShellItem? psiNewItem) => 0;

        public int UpdateProgress(uint iWorkTotal, uint iWorkSoFar) => 0;

        public int ResetTimer() => 0;

        public int PauseTimer() => 0;

        public int ResumeTimer() => 0;
    }
}
