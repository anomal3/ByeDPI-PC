using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ByeDpiPc.Core.Engine;

/// <summary>
/// Дочерний процесс без окна консоли: вывод уходит в журнал, на Windows процесс привязан к Job Object
/// и гарантированно завершится вместе с программой (даже при падении).
/// </summary>
public sealed class ChildProcess : IDisposable
{
    private readonly Process _process;
    private readonly string _source;
    private int _exitHandled;

    public event Action<int>? Exited;
    public event Action<string>? Output;

    public int Id => _process.Id;
    public bool HasExited
    {
        get
        {
            try { return _process.HasExited; } catch { return true; }
        }
    }

    private ChildProcess(Process process, string source)
    {
        _process = process;
        _source = source;
    }

    public static ChildProcess Start(string file, IEnumerable<string> args, string source, bool logOutput = true, string? workingDirectory = null)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(file) ?? Environment.CurrentDirectory,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var child = new ChildProcess(p, source);
        p.OutputDataReceived += (_, e) => child.OnLine(e.Data, logOutput);
        p.ErrorDataReceived += (_, e) => child.OnLine(e.Data, logOutput);
        p.Exited += (_, _) => child.OnExited();
        if (!p.Start()) throw new InvalidOperationException("Не удалось запустить " + file);
        JobObject.Assign(p);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        return child;
    }

    private void OnLine(string? line, bool log)
    {
        if (line == null) return;
        if (log) Log.Engine(_source, line);
        Output?.Invoke(line);
    }

    private void OnExited()
    {
        if (Interlocked.Exchange(ref _exitHandled, 1) == 1) return;
        int code;
        try { code = _process.ExitCode; } catch { code = -1; }
        Exited?.Invoke(code);
    }

    public async Task StopAsync(TimeSpan? timeout = null)
    {
        if (HasExited) return;
        try
        {
            _process.Kill(entireProcessTree: true);
            using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5));
            await _process.WaitForExitAsync(cts.Token);
        }
        catch (InvalidOperationException) { }
        catch (Win32Exception ex) { Log.Warn(_source, "Не удалось завершить процесс: " + ex.Message); }
        catch (OperationCanceledException) { Log.Warn(_source, "Процесс не завершился вовремя"); }
    }

    public Task WaitForExitAsync(CancellationToken ct) => _process.WaitForExitAsync(ct);

    public void Dispose()
    {
        if (!HasExited)
        {
            try { _process.Kill(entireProcessTree: true); } catch { }
        }
        _process.Dispose();
    }
}

/// <summary>Job Object с KILL_ON_JOB_CLOSE: дочерние процессы умирают вместе с нами.</summary>
internal static class JobObject
{
    private static readonly Lazy<IntPtr> Handle = new(Create);

    public static void Assign(Process p)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var job = Handle.Value;
            if (job != IntPtr.Zero) AssignProcessToJobObject(job, p.Handle);
        }
        catch (Exception ex)
        {
            Log.Debug("Процессы", "Job Object: " + ex.Message);
        }
    }

    private static IntPtr Create()
    {
        if (!OperatingSystem.IsWindows()) return IntPtr.Zero;
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return IntPtr.Zero;
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = 0x2000 /* KILL_ON_JOB_CLOSE */ },
        };
        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            SetInformationJobObject(job, 9 /* JobObjectExtendedLimitInformation */, ptr, (uint)size);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
        return job;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll")]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
}
