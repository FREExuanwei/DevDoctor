using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DevDoctor.Infrastructure;

/// <summary>Owns a suspended-created process and a kill-on-close Job Object, including descendants.</summary>
internal sealed class JobProcess : IDisposable
{
    private readonly SafeJobHandle _job;
    public Process Process { get; }
    public StreamReader StandardOutput { get; }
    public StreamReader StandardError { get; }

    private JobProcess(SafeJobHandle job, Process process, StreamReader stdout, StreamReader stderr)
        => (_job, Process, StandardOutput, StandardError) = (job, process, stdout, stderr);

    public static JobProcess Start(ProcessStartInfo info)
    {
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        SafeFileHandle? outRead = null, outWrite = null, errRead = null, errWrite = null;
        StreamReader? stdout = null, stderr = null; Process? process = null;
        IntPtr attributes = IntPtr.Zero, handles = IntPtr.Zero, environment = IntPtr.Zero;
        ProcessInformation created = default;
        var assigned = false;
        var attributesInitialized = false;
        try
        {
            var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = 0x2000 } }; // KILL_ON_JOB_CLOSE
            if (!SetInformationJobObject(job, 9, in limits, (uint)Marshal.SizeOf<ExtendedLimits>())) throw new Win32Exception(Marshal.GetLastWin32Error());
            var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = true };
            if (!CreatePipe(out outRead, out outWrite, ref security, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!CreatePipe(out errRead, out errWrite, ref security, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!SetHandleInformation(outRead, 1, 0) || !SetHandleInformation(errRead, 1, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
            using var input = File.OpenHandle("NUL", FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Inheritable);

            // Restrict inheritance to these three pipe/NUL handles, even when scans overlap in tests.
            nuint attributeBytes = 0;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeBytes);
            if (attributeBytes == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            attributes = Marshal.AllocHGlobal(checked((int)attributeBytes));
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref attributeBytes)) throw new Win32Exception(Marshal.GetLastWin32Error());
            attributesInitialized = true;
            handles = Marshal.AllocHGlobal(IntPtr.Size * 3);
            Marshal.WriteIntPtr(handles, 0, input.DangerousGetHandle());
            Marshal.WriteIntPtr(handles, IntPtr.Size, outWrite.DangerousGetHandle());
            Marshal.WriteIntPtr(handles, IntPtr.Size * 2, errWrite.DangerousGetHandle());
            if (!UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x20002, handles, (nuint)(IntPtr.Size * 3), IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var startup = new StartupInfoEx
            {
                Startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100, StandardInput = input.DangerousGetHandle(),
                    StandardOutput = outWrite.DangerousGetHandle(), StandardError = errWrite.DangerousGetHandle() },
                AttributeList = attributes
            };
            var variables = info.Environment.Where(pair => pair.Value is not null).OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => pair.Key + "=" + pair.Value);
            environment = Marshal.StringToHGlobalUni(string.Join('\0', variables) + "\0\0");
            var arguments = info.Arguments.Length > 0 ? info.Arguments : string.Join(' ', info.ArgumentList.Select(QuoteArgument));
            var command = new StringBuilder(Quote(info.FileName) + " " + arguments);
            // No child code runs before assignment, closing the launch/assignment race.
            const uint flags = 0x4 | 0x400 | 0x80000 | 0x08000000; // suspended, Unicode, extended startup, no window
            if (!CreateProcessW(info.FileName, command, IntPtr.Zero, IntPtr.Zero, true, flags, environment, info.WorkingDirectory, ref startup, out created))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!AssignProcessToJobObject(job, created.Process)) throw new Win32Exception(Marshal.GetLastWin32Error());
            assigned = true;
            process = Process.GetProcessById((int)created.ProcessId);
            _ = process.Handle; // Retain an owned handle so ExitCode remains available after forced termination.
            stdout = new StreamReader(new FileStream(outRead, FileAccess.Read, 4096, isAsync: false), info.StandardOutputEncoding ?? Encoding.UTF8);
            outRead = null;
            stderr = new StreamReader(new FileStream(errRead, FileAccess.Read, 4096, isAsync: false), info.StandardErrorEncoding ?? Encoding.UTF8);
            errRead = null;
            if (ResumeThread(created.Thread) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
            return new(job, process, stdout, stderr);
        }
        catch
        {
            if (created.Process != IntPtr.Zero && !assigned) TerminateProcess(created.Process, 1);
            job.Dispose(); process?.Dispose(); stdout?.Dispose(); stderr?.Dispose(); throw;
        }
        finally
        {
            outRead?.Dispose(); outWrite?.Dispose(); errRead?.Dispose(); errWrite?.Dispose();
            if (created.Thread != IntPtr.Zero) CloseHandle(created.Thread);
            if (created.Process != IntPtr.Zero) CloseHandle(created.Process);
            if (attributes != IntPtr.Zero) { if (attributesInitialized) DeleteProcThreadAttributeList(attributes); Marshal.FreeHGlobal(attributes); }
            if (handles != IntPtr.Zero) Marshal.FreeHGlobal(handles);
            if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
        }
    }

    public void Terminate() { if (!_job.IsClosed && !_job.IsInvalid) TerminateJobObject(_job, 1); }
    public void Dispose() { _job.Dispose(); StandardOutput.Dispose(); StandardError.Dispose(); Process.Dispose(); }
    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    // Legacy py examines raw switches before forwarding them; quote only arguments that need it.
    private static string QuoteArgument(string value) => value.Length > 0 && !value.Any(c => char.IsWhiteSpace(c) || c == '"') ? value : Quote(value);

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessUserTime, JobUserTime; public uint LimitFlags; public nuint MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit; public nuint Affinity; public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits { public BasicLimits Basic; public IoCounters Io; public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size; public IntPtr Reserved, Desktop, Title; public uint X, Y, Width, Height, XChars, YChars, Fill, Flags;
        public ushort ShowWindow, ReservedBytes; public IntPtr ReservedData, StandardInput, StandardOutput, StandardError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx { public StartupInfo Startup; public IntPtr AttributeList; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    private sealed class SafeJobHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
    { protected override bool ReleaseHandle() => CloseHandle(handle); }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeJobHandle CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(SafeJobHandle job, int informationClass, in ExtendedLimits information, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(SafeJobHandle job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateJobObject(SafeJobHandle job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attributes, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returnedSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateProcessW(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}
