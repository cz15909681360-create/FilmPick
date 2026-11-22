using System.IO;
using System.Runtime.InteropServices;

namespace FilmPick;

static class FolderPicker
{
    private const int Cancelled = unchecked((int)0x800704C7);
    private const uint UseFolder = 701;
    private static readonly Guid DialogClass = new("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");
    private static readonly Guid ShellItemId = typeof(IShellItem).GUID;

    public static string? Pick(IntPtr owner, string? initial)
    {
        var type = Type.GetTypeFromCLSID(DialogClass, true)!;
        var dialog = (IFileDialog)Activator.CreateInstance(type)!;
        var events = new Events();
        uint cookie = 0;
        try
        {
            dialog.GetOptions(out var options);
            dialog.SetOptions((options | 0x40000000 | 0x40 | 0x8 | 0x1000) & ~0x20u);
            dialog.SetTitle("选择素材文件夹 · 可以浏览图片");
            dialog.SetOkButtonLabel("打开该文件所在目录");
            dialog.SetFileTypes(2, [new("图片、RAW 和视频", Catalog.MediaFilter), new("所有文件", "*.*")]);
            if (initial != null && SHCreateItemFromParsingName(initial, IntPtr.Zero, ShellItemId, out var shell) >= 0)
            {
                try { dialog.SetFolder(shell); }
                finally { Marshal.ReleaseComObject(shell); }
            }
            var custom = (IFileDialogCustomize)dialog;
            custom.AddPushButton(UseFolder, "使用当前文件夹");
            dialog.Advise(events, out cookie);
            var result = dialog.Show(owner);
            if (result == Cancelled) return null;
            Marshal.ThrowExceptionForHR(result);
            if (events.Folder != null) return events.Folder;
            dialog.GetResult(out var item);
            return Path.GetDirectoryName(ReadPath(item));
        }
        finally
        {
            if (cookie != 0) dialog.Unadvise(cookie);
            Marshal.ReleaseComObject(dialog);
        }
    }

    private static string ReadPath(IShellItem item)
    {
        try
        {
            item.GetDisplayName(0x80058000, out var value);
            try { return Marshal.PtrToStringUni(value) ?? throw new IOException("当前不是文件系统目录。"); }
            finally { Marshal.FreeCoTaskMem(value); }
        }
        finally { Marshal.ReleaseComObject(item); }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class Events : IFileDialogEvents, IFileDialogControlEvents
    {
        public string? Folder { get; private set; }
        public int OnFileOk(IFileDialog dialog) => 0;
        public int OnFolderChanging(IFileDialog dialog, IShellItem folder) => 0;
        public int OnFolderChange(IFileDialog dialog) => 0;
        public int OnSelectionChange(IFileDialog dialog) => 0;
        public int OnShareViolation(IFileDialog dialog, IShellItem item, out uint result) { result = 0; return 0; }
        public int OnTypeChange(IFileDialog dialog) => 0;
        public int OnOverwrite(IFileDialog dialog, IShellItem item, out uint result) { result = 0; return 0; }
        public int OnItemSelected(IFileDialogCustomize dialog, uint control, uint item) => 0;
        public int OnCheckButtonToggled(IFileDialogCustomize dialog, uint control, bool value) => 0;
        public int OnControlActivating(IFileDialogCustomize dialog, uint control) => 0;
        public int OnButtonClicked(IFileDialogCustomize custom, uint control)
        {
            if (control != UseFolder) return 0;
            try
            {
                var dialog = (IFileDialog)custom;
                dialog.GetFolder(out var folder);
                Folder = ReadPath(folder);
                dialog.Close(0);
            }
            catch (Exception)
            {
                Folder = null;
                System.Windows.MessageBox.Show("请先进入磁盘上的实际文件夹，再确认。", "选择素材文件夹");
            }
            return 0;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr binding, in Guid id, out IShellItem item);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private readonly struct Filter(string name, string pattern)
    {
        [MarshalAs(UnmanagedType.LPWStr)] public readonly string Name = name;
        [MarshalAs(UnmanagedType.LPWStr)] public readonly string Pattern = pattern;
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler();
        void GetParent();
        void GetDisplayName(uint format, out IntPtr value);
    }

    [ComImport, Guid("42F85136-DB7E-439C-85F1-E4075D135FC8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        [PreserveSig] int Show(IntPtr owner);
        void SetFileTypes(uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] Filter[] filters);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(IFileDialogEvents events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint options);
        void GetOptions(out uint options);
        void SetDefaultFolder(IShellItem folder);
        void SetFolder(IShellItem folder);
        void GetFolder(out IShellItem folder);
        void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName(out IntPtr name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string value);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string value);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string value);
        void GetResult(out IShellItem item);
        void AddPlace(IShellItem item, uint alignment);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
        void Close(int result);
    }

    [ComImport, Guid("E6FDD21A-163F-4975-9C8C-A69F1BA37034"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialogCustomize
    {
        void EnableOpenDropDown(uint control);
        void AddMenu(uint control, [MarshalAs(UnmanagedType.LPWStr)] string label);
        void AddPushButton(uint control, [MarshalAs(UnmanagedType.LPWStr)] string label);
    }

    [ComImport, Guid("973510DB-7D7F-452B-8975-74A85828D354"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialogEvents
    {
        [PreserveSig] int OnFileOk(IFileDialog dialog);
        [PreserveSig] int OnFolderChanging(IFileDialog dialog, IShellItem folder);
        [PreserveSig] int OnFolderChange(IFileDialog dialog);
        [PreserveSig] int OnSelectionChange(IFileDialog dialog);
        [PreserveSig] int OnShareViolation(IFileDialog dialog, IShellItem item, out uint result);
        [PreserveSig] int OnTypeChange(IFileDialog dialog);
        [PreserveSig] int OnOverwrite(IFileDialog dialog, IShellItem item, out uint result);
    }

    [ComImport, Guid("36116642-D713-4B97-9B83-7484A9D00433"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialogControlEvents
    {
        [PreserveSig] int OnItemSelected(IFileDialogCustomize dialog, uint control, uint item);
        [PreserveSig] int OnButtonClicked(IFileDialogCustomize dialog, uint control);
        [PreserveSig] int OnCheckButtonToggled(IFileDialogCustomize dialog, uint control, [MarshalAs(UnmanagedType.Bool)] bool value);
        [PreserveSig] int OnControlActivating(IFileDialogCustomize dialog, uint control);
    }
}
