using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

public static class InstallerTestInterop
{
    public sealed class Window
    {
        public IntPtr Handle { get; set; }
        public string Text { get; set; }
        public string ClassName { get; set; }
        public uint ProcessId { get; set; }
        public int ControlId { get; set; }
        public bool Visible { get; set; }
        public bool Enabled { get; set; }
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=260)] public string ExeFile;
    }
    private delegate bool EnumWindowCallback(IntPtr handle, IntPtr parameter);
    [DllImport("user32.dll")] private static extern int GetDlgCtrlID(IntPtr handle);
    [DllImport("user32.dll", SetLastError=true)] private static extern bool PostMessageW(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr handle);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr handle, StringBuilder text, int capacity);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassNameW(IntPtr handle, StringBuilder text, int capacity);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr handle, out Rect rectangle);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr handle, IntPtr deviceContext, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll", SetLastError=true)] private static extern IntPtr SendMessageTimeoutW(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    public static int[] ProcessTree(int rootId)
    {
        var parents = new Dictionary<uint,uint>();
        var snapshot=CreateToolhelp32Snapshot(2,0);
        if (snapshot==new IntPtr(-1)) throw new InvalidOperationException("Unable to enumerate owned setup processes.");
        try {
            var entry=new ProcessEntry {Size=(uint)Marshal.SizeOf(typeof(ProcessEntry))};
            if (Process32FirstW(snapshot,ref entry)) {
                do { parents[entry.ProcessId]=entry.ParentProcessId; } while(Process32NextW(snapshot,ref entry));
            }
        } finally { CloseHandle(snapshot); }
        var owned=new HashSet<uint> {(uint)rootId};
        bool changed;
        do { changed=false; foreach(var pair in parents) if(owned.Contains(pair.Value) && owned.Add(pair.Key)) changed=true; } while(changed);
        var result=new List<int>();
        foreach(uint id in owned) result.Add((int)id);
        return result.ToArray();
    }
    private static Window ReadWindow(IntPtr handle)
    {
        uint pid; GetWindowThreadProcessId(handle,out pid);
        var text=new StringBuilder(1024); GetWindowTextW(handle,text,text.Capacity);
        var cls=new StringBuilder(256); GetClassNameW(handle,cls,cls.Capacity);
        var value=text.ToString();
        if(cls.ToString().IndexOf("RichEdit",StringComparison.OrdinalIgnoreCase)>=0 || cls.ToString().IndexOf("Memo",StringComparison.OrdinalIgnoreCase)>=0) {
            const int capacity=16384;
            var buffer=Marshal.AllocHGlobal(capacity*2);
            try {
                Marshal.WriteInt16(buffer,0);
                IntPtr length;
                if(SendMessageTimeoutW(handle,0x000D,new IntPtr(capacity),buffer,2,250,out length)!=IntPtr.Zero && length.ToInt64()>=0 && length.ToInt64()<capacity)
                    value=Marshal.PtrToStringUni(buffer,(int)length.ToInt64()) ?? "";
            } finally {Marshal.FreeHGlobal(buffer);}
        }
        return new Window {Handle=handle,Text=value,ClassName=cls.ToString(),ProcessId=pid,ControlId=GetDlgCtrlID(handle),Visible=IsWindowVisible(handle),Enabled=IsWindowEnabled(handle)};
    }
    public static Window[] TopLevelWindows(int rootId)
    {
        var owned=new HashSet<int>(ProcessTree(rootId));
        var result=new List<Window>();
        EnumWindows((handle,_) => {
            uint pid; GetWindowThreadProcessId(handle,out pid);
            if(owned.Contains((int)pid) && IsWindowVisible(handle)) result.Add(ReadWindow(handle));
            return true;
        },IntPtr.Zero);
        return result.ToArray();
    }
    public static Window[] Children(IntPtr parent)
    {
        var result=new List<Window>();
        EnumChildWindows(parent,(handle,_) => {result.Add(ReadWindow(handle));return true;},IntPtr.Zero);
        return result.ToArray();
    }
    public static void Click(IntPtr button)
    {
        if(!PostMessageW(button,0x00F5,IntPtr.Zero,IntPtr.Zero))
            throw new InvalidOperationException("Owned setup button did not respond.");
    }
    public static void ScrollMemoToEnd(IntPtr memo)
    {
        if(!PostMessageW(memo,0x0115,new IntPtr(7),IntPtr.Zero))
            throw new InvalidOperationException("Owned upgrade memo did not accept vertical scrolling.");
    }
}
