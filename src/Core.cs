using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace AIBlackHole;

static class Json
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public static string Text(object value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string file) => JsonSerializer.Deserialize<T>(File.ReadAllText(file), Options)!;
    public static void Save(string file, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, Text(value), new UTF8Encoding(false));
        File.Move(temporary, file, true);
    }
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    public static async Task<string> FileHash(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }
    public static string Secret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    public static bool Equal(string a, string b) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}

sealed class Paths
{
    public const string Version = "2.0.0-beta.2";
    public string Root { get; } = AppContext.BaseDirectory;
    public string State { get; }
    public string VaultFile => Path.Combine(State, "vault", "credentials.abhv");
    public string Data(string name) => Path.Combine(State, "data", name);
    public Paths(string? state = null)
    {
        State = Path.GetFullPath(state ?? (File.Exists(Path.Combine(Root,"installed.marker")) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AI BLACK HOLE") : Root));
        Directory.CreateDirectory(Data("logs")); Directory.CreateDirectory(Path.GetDirectoryName(VaultFile)!);
        var probe = Data(".write-" + Guid.NewGuid().ToString("N"));
        using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
    }
}
record Profile(string Name, string Backend, int Threads, int GpuLayers, int Context, int Batch, int UBatch)
{
    public int Parallel { get; init; } = 1;
    public bool Mmap { get; init; } = true;
    public string FlashAttention { get; init; } = "auto";
    public static Profile Safe => new("SAFE", "cpu", Math.Clamp(Environment.ProcessorCount / 2, 1, 8), 0, 2048, 128, 64);
}
record Hardware(string Cpu, int Threads, ulong TotalRam, ulong FreeRam, string Gpu, int VramMiB, int FreeVramMiB, string Driver, string Fingerprint)
{
    public int PhysicalCores { get; init; } = Native.PhysicalCores();
    public string OperatingSystem { get; init; } = Environment.OSVersion.VersionString;
    public string Architecture { get; init; } = RuntimeInformation.OSArchitecture.ToString();
    public double MemoryPressurePercent => Math.Round(100.0 * (1 - (double)FreeRam / TotalRam), 2);
    public string[] DisplayAdapters { get; init; } = [];
    public static (ulong total, ulong free) Memory()
    {
        var m = new Native.MemoryStatus { Length = (uint)Marshal.SizeOf<Native.MemoryStatus>() };
        if (!Native.GlobalMemoryStatusEx(ref m)) throw new System.ComponentModel.Win32Exception();
        return (m.TotalPhys, m.AvailPhys);
    }
    public static async Task<Hardware> Detect()
    {
        var (total, free) = Memory();
        string cpu = Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", "Unknown x64 CPU")?.ToString()?.Trim() ?? "Unknown";
        string gpu = "CPU fallback (no verified CUDA driver)", driver = "unknown"; int vram = 0, vfree = 0;
        string smi = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe");
        if (File.Exists(smi))
        {
            try
            {
                var p = new Process { StartInfo = new ProcessStartInfo(smi) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
                p.StartInfo.ArgumentList.Add("--query-gpu=name,memory.total,memory.free,driver_version"); p.StartInfo.ArgumentList.Add("--format=csv,noheader,nounits");
                p.Start(); var output = p.StandardOutput.ReadToEndAsync(); var errors = p.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await p.WaitForExitAsync(timeout.Token); } catch { p.Kill(true); throw; }
                string[] values = (await output).Split('\n')[0].Split(',').Select(s => s.Trim()).ToArray();
                if (p.ExitCode == 0 && values.Length == 4 && int.TryParse(values[1], out vram) && int.TryParse(values[2], out vfree)) { gpu = values[0]; driver = values[3]; }
            }
            catch { vram = vfree = 0; }
        }
        return new Hardware(cpu, Environment.ProcessorCount, total, free, gpu, vram, vfree, driver, Json.Hash($"{cpu}|{Environment.ProcessorCount}|{total}|{gpu}|{vram}|{driver}|{Environment.OSVersion.Version}")[..24]) { DisplayAdapters = Native.DisplayNames() };
    }
}

static class Native
{
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetLogicalProcessorInformationEx(int relation, IntPtr buffer, ref uint size);
    public static int PhysicalCores()
    {
        uint length = 0; GetLogicalProcessorInformationEx(0, IntPtr.Zero, ref length);
        if (length == 0) return 0;
        var buffer = Marshal.AllocHGlobal((int)length);
        try { if (!GetLogicalProcessorInformationEx(0, buffer, ref length)) return 0; int count = 0, offset = 0; while (offset + 8 <= length) { int size = Marshal.ReadInt32(buffer, offset + 4); if (size < 8 || offset + size > length) break; count++; offset += size; } return count; }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] public struct DisplayDevice { public uint Size; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString; public uint StateFlags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool EnumDisplayDevices(string? device, uint number, ref DisplayDevice output, uint flags);
    public static string[] DisplayNames()
    {
        var result = new List<string>();
        for (uint i = 0; i < 16; i++) { var device = new DisplayDevice { Size = (uint)Marshal.SizeOf<DisplayDevice>(), DeviceName = "", DeviceString = "", DeviceId = "", DeviceKey = "" }; if (!EnumDisplayDevices(null, i, ref device, 0)) break; if (!string.IsNullOrWhiteSpace(device.DeviceString)) result.Add(device.DeviceString); }
        return result.Distinct().ToArray();
    }
    [StructLayout(LayoutKind.Sequential)] public struct MemoryStatus { public uint Length, MemoryLoad; public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual; }
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("kernel32.dll")] public static extern bool AllocConsole();
    [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr window, int command);
    public delegate bool ConsoleHandler(uint type);
    [DllImport("kernel32.dll")] public static extern bool SetConsoleCtrlHandler(ConsoleHandler handler, bool add);
    [DllImport("kernel32.dll")] public static extern bool GenerateConsoleCtrlEvent(uint type, uint group);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll")] public static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint size);
    [DllImport("kernel32.dll")] public static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
    [StructLayout(LayoutKind.Sequential)] public struct BasicLimits { public long PerProcess, PerJob; public uint Flags; public UIntPtr MinimumWorking, MaximumWorking; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] public struct IoCounters { public ulong ReadOperation, WriteOperation, OtherOperation, ReadTransfer, WriteTransfer, OtherTransfer; }
    [StructLayout(LayoutKind.Sequential)] public struct ExtendedLimits { public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    public static IntPtr KillOnCloseJob(Process process)
    {
        var job = CreateJobObject(IntPtr.Zero, null); var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } };
        int size = Marshal.SizeOf<ExtendedLimits>(); var ptr = Marshal.AllocHGlobal(size);
        try { Marshal.StructureToPtr(limits, ptr, false); if (job == IntPtr.Zero || !SetInformationJobObject(job, 9, ptr, (uint)size) || !AssignProcessToJobObject(job, process.Handle)) throw new System.ComponentModel.Win32Exception(); }
        catch { if (job != IntPtr.Zero) CloseHandle(job); throw; }
        finally { Marshal.FreeHGlobal(ptr); }
        return job;
    }
    public static int FreePort() { var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); int port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop(); return port; }
}
