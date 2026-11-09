using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
#if OWNED_LAB_ACCEPTANCE_FIXTURE
namespace Workbench.OwnedLab.FixtureLauncher;
#else
namespace Workbench.Testing.Windows;
#endif

internal static class OwnedNativeMethods
{
    internal const uint Infinite = 0xffffffff, Synchronize = 0x100000, QueryLimited = 0x1000;
    internal sealed class KernelHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public KernelHandle() : base(true) { }
        internal KernelHandle(nint value) : base(true) => SetHandle(value);
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Security { internal uint Length; internal nint Descriptor; [MarshalAs(UnmanagedType.Bool)] internal bool Inherit; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct Startup
    {
        internal uint Size; internal nint Reserved, Desktop, Title; internal uint X, Y, XSize, YSize, XCount, YCount, Fill, Flags; internal ushort Show, ReservedSize; internal nint ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct StartupEx { internal Startup Startup; internal nint Attributes; }
    [StructLayout(LayoutKind.Sequential)] internal struct ProcessInfo { internal nint Process, Thread; internal uint Pid, Tid; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits { internal long ProcessTime, JobTime; internal uint Flags; internal nuint MinWorking, MaxWorking; internal uint ActiveLimit; internal nuint Affinity; internal uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { internal ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits { internal BasicLimits Basic; internal IoCounters Io; internal nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [StructLayout(LayoutKind.Sequential)] private struct CompletionPort { internal nint Key, Port; }
    [StructLayout(LayoutKind.Sequential)] private struct Accounting { internal long User, Kernel, PeriodUser, PeriodKernel; internal uint PageFaults, TotalProcesses, ActiveProcesses, Terminated; }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { internal int Left, Top, Right, Bottom; }
    [DllImport("kernel32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern KernelHandle CreateJobObjectW(nint security, string? name);
    [DllImport("kernel32", SetLastError = true)] internal static extern KernelHandle CreateIoCompletionPort(nint file, nint port, nuint key, uint threads);
    [DllImport("kernel32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(KernelHandle job, int kind, nint data, uint length);
    [DllImport("kernel32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryInformationJobObject(KernelHandle job, int kind, out Accounting data, uint length, nint returned);
    [DllImport("kernel32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TerminateJobObject(KernelHandle job, uint code);
    [DllImport("kernel32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TerminateProcess(KernelHandle process, uint code);
    [DllImport("kernel32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsProcessInJob(KernelHandle process, KernelHandle job, [MarshalAs(UnmanagedType.Bool)] out bool result);
    [DllImport("kernel32", SetLastError = true)] internal static extern uint WaitForSingleObject(KernelHandle handle, uint milliseconds);
    [DllImport("kernel32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetExitCodeProcess(KernelHandle process, out int exit);
    [DllImport("kernel32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessTimes(KernelHandle process, out ulong creation, out ulong exit, out ulong kernel, out ulong user);
    [DllImport("kernel32", SetLastError = true)] internal static extern uint GetProcessId(KernelHandle process);
    [DllImport("kernel32", SetLastError = true)] internal static extern KernelHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageNameW(KernelHandle process, uint flags, StringBuilder path, ref uint size);
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetWindowsDirectoryW(StringBuilder path, uint length);
    [DllImport("kernel32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool InitializeProcThreadAttributeList(nint list, int count, uint flags, ref nuint size);
    [DllImport("kernel32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value, nuint size, nint previous, nint returned);
    [DllImport("kernel32")] internal static extern void DeleteProcThreadAttributeList(nint list);
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CreateProcessW(string application, StringBuilder command, nint processSecurity, nint threadSecurity, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, nint environment, string directory, ref StartupEx startup, out ProcessInfo process);
    [DllImport("kernel32", SetLastError = true)] internal static extern uint ResumeThread(KernelHandle thread);
    [DllImport("kernel32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CancelIoEx(SafePipeHandle file, nint overlapped);
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafePipeHandle CreateNamedPipeW(string name, uint open, uint mode, uint instances, uint outSize, uint inSize, uint timeout, ref Security security);
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, ref Security security, uint disposition, uint flags, nint template);
    [DllImport("advapi32", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string text, uint revision, out nint descriptor, out uint length);
    [DllImport("kernel32")] private static extern nint LocalFree(nint memory);
    [DllImport("user32", SetLastError = true)] internal static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32", CharSet = CharSet.Unicode)] internal static extern int GetClassNameW(nint window, StringBuilder name, int maximum);
    [DllImport("user32")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetClientRect(nint window, out Rect rect);
    [DllImport("user32")] internal static extern nint GetDlgItem(nint window, int id);
    [DllImport("user32")] internal static extern nint GetParent(nint window);

    internal static void Check(bool result) { if (!result) throw new Win32Exception(Marshal.GetLastPInvokeError()); }
    private static void Set<T>(KernelHandle job, int kind, T value) where T : struct
    {
        var data = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
        try { Marshal.StructureToPtr(value, data, false); Check(SetInformationJobObject(job, kind, data, (uint)Marshal.SizeOf<T>())); }
        finally { Marshal.FreeHGlobal(data); }
    }
    internal static void ConfigureJob(KernelHandle job, KernelHandle port)
    {
        Set(job, 9, new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } }); // KILL_ON_JOB_CLOSE; no breakaway.
        Set(job, 7, new CompletionPort { Key = 1, Port = port.DangerousGetHandle() });
    }
    internal static uint ActiveProcesses(KernelHandle job) { Check(QueryInformationJobObject(job, 1, out var data, (uint)Marshal.SizeOf<Accounting>(), 0)); return data.ActiveProcesses; }
    internal static ulong Birth(KernelHandle process) { Check(GetProcessTimes(process, out var birth, out _, out _, out _)); return birth; }
    internal static string ProcessPath(KernelHandle process) { var value = new StringBuilder(32768); uint size = 32768; Check(QueryFullProcessImageNameW(process, 0, value, ref size)); return Path.GetFullPath(value.ToString()); }
    internal static string WindowsDirectory() { var value = new StringBuilder(32768); var length = GetWindowsDirectoryW(value, 32768); if (length == 0 || length >= 32768) throw new Win32Exception(); return value.ToString(); }
    internal static SafeFileHandle Null(bool write) { var security = new Security { Length = (uint)Marshal.SizeOf<Security>(), Inherit = true }; var handle = CreateFileW("NUL", write ? 0x40000000u : 0x80000000u, 3, ref security, 3, 0x80, 0); if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(); } return handle; }
    internal static async Task<(NamedPipeServerStream Host, SafeFileHandle Child)> PipeAsync(bool hostWrites)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new Win32Exception();
        Check(ConvertStringSecurityDescriptorToSecurityDescriptorW($"D:P(A;;GA;;;SY)(A;;GA;;;{sid})", 1, out var acl, out _));
        NamedPipeServerStream? server = null; SafeFileHandle? child = null; Task? connection = null;
        try
        {
            var security = new Security { Length = (uint)Marshal.SizeOf<Security>(), Descriptor = acl };
            var name = @"\\.\pipe\PWB-Owned-" + Guid.NewGuid().ToString("N");
            var handle = CreateNamedPipeW(name, 0x40000000u | 0x00080000u | (hostWrites ? 2u : 1u), 8, 1, 65536, 65536, 0, ref security);
            if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(); }
            server = new(hostWrites ? PipeDirection.Out : PipeDirection.In, true, false, handle);
            connection = server.WaitForConnectionAsync();
            security.Inherit = true;
            child = CreateFileW(name, hostWrites ? 0x80000000u : 0x40000000u, 0, ref security, 3, 0, 0);
            if (child.IsInvalid) throw new Win32Exception();
            await connection.ConfigureAwait(false); return (server, child);
        }
        catch
        {
            if (server is not null) { CancelIoEx(server.SafePipeHandle, 0); if (connection is not null) try { await connection.ConfigureAwait(false); } catch (Exception) { } server.Dispose(); }
            child?.Dispose(); throw;
        }
        finally { LocalFree(acl); }
    }

    internal sealed class Attributes : IDisposable
    {
        internal nint List { get; }
        private readonly List<nint> _values = [];
        private bool _initialized;
        internal Attributes(KernelHandle job, params nint[] inherited)
        {
            nuint size = 0; InitializeProcThreadAttributeList(0, 2, 0, ref size); List = Marshal.AllocHGlobal(checked((nint)size));
            try { Check(InitializeProcThreadAttributeList(List, 2, 0, ref size)); _initialized = true; Add(0x2000d, [job.DangerousGetHandle()]); Add(0x20002, inherited); }
            catch { Dispose(); throw; }
        }
        private void Add(nuint kind, nint[] handles)
        {
            var value = Marshal.AllocHGlobal(handles.Length * nint.Size); _values.Add(value);
            for (var i = 0; i < handles.Length; i++) Marshal.WriteIntPtr(value, i * nint.Size, handles[i]);
            Check(UpdateProcThreadAttribute(List, 0, kind, value, (nuint)(handles.Length * nint.Size), 0, 0));
        }
        public void Dispose() { if (_initialized) DeleteProcThreadAttributeList(List); foreach (var value in _values) Marshal.FreeHGlobal(value); Marshal.FreeHGlobal(List); }
    }
#if OWNED_LAB_ACCEPTANCE_FIXTURE
    [DllImport("kernel32", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(KernelHandle job, KernelHandle process);
    internal static KernelHandle EnterOuterProbeJob()
    {
        var job = CreateJobObjectW(0, null);
        try { if (job.IsInvalid) throw new Win32Exception(); using var current = OpenProcess(0x100 | 1, false, (uint)Environment.ProcessId); Check(AssignProcessToJobObject(job, current)); return job; }
        catch { job.Dispose(); throw; }
    }
#endif
}
