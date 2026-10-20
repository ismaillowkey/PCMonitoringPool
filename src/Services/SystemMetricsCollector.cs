using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Web.Administration;
using PCMonitoringCPURam.Models;

namespace PCMonitoringCPURam.Services
{
    /// <summary>
    /// Layanan pengumpul metrik perangkat keras (CPU per-core, RAM, Disk tempat aplikasi berjalan, Network)
    /// </summary>
    public class SystemMetricsCollector : IDisposable
    {
        // P/Invoke untuk membaca status memori fisik Windows secara akurat
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private class MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;

            public MEMORYSTATUSEX()
            {
                this.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        [DllImport("ntdll.dll")]
        private static extern int NtQuerySystemInformation(
            int systemInformationClass,
            IntPtr systemInformation,
            int systemInformationLength,
            out int returnLength);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(
            IntPtr processHandle,
            int processInformationClass,
            IntPtr processInformation,
            int processInformationLength,
            out int returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        private const int SystemProcessInformation = 5;
        private const int SystemProcessorPerformanceInformation = 8;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const int ProcessCommandLineInformation = 60;

        private readonly PerformanceCounter? _cpuTotalCounter;
        private readonly List<PerformanceCounter> _cpuCoreCounters = new List<PerformanceCounter>();
        
        // Counter khusus untuk disk fisik tempat aplikasi ini berjalan
        private readonly PerformanceCounter? _appDiskCounter;

        // Penyimpanan riwayat bytes network per antarmuka adapter
        private readonly Dictionary<string, long> _previousAdapterBytes = new Dictionary<string, long>();
        private DateTime _previousNetworkTime = DateTime.UtcNow;

        // Penyimpanan riwayat CPU time proses untuk kalkulasi persentase CPU per proses via Native NT API
        private readonly Dictionary<int, (DateTime timestamp, long totalTicks)> _prevProcTicks = new Dictionary<int, (DateTime, long)>();
        // Riwayat CPU per core via Native NT API
        private readonly List<(long idle, long kernel, long user)> _prevCpuCoreTimes = new List<(long, long, long)>();

        // Penyimpanan riwayat CPU time Linux (/proc/stat) untuk total dan per-core
        private readonly Dictionary<string, (long idle, long total)> _prevLinuxCpuTimes = new Dictionary<string, (long, long)>();

        // Cache pemetaan w3wp PID ke Nama Website / Application Pool IIS
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> _w3wpAppPoolCache = new System.Collections.Concurrent.ConcurrentDictionary<int, string>();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _appPoolToSiteCache = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private DateTime _lastW3wpCacheRefresh = DateTime.MinValue;
        private int _isRefreshingIisMapping = 0;
        private readonly Dictionary<int, (DateTime timestamp, long totalTicks)> _prevIisProcTicks = new Dictionary<int, (DateTime, long)>();
        private readonly object _iisLock = new object();

        private readonly object _processLock = new object();
        private readonly object _syncLock = new object();

        public int CoreCount { get; }
        public ulong TotalPhysicalMemoryBytes { get; }
        public string AppDriveLetter { get; }
        public string AppDiskName { get; }

        public SystemMetricsCollector()
        {
            CoreCount = Environment.ProcessorCount;

            // Inisialisasi komponen khusus Windows vs Linux secara eksplisit
            if (OperatingSystem.IsWindows())
            {
                // Inisialisasi kapasitas RAM fisik Windows melalui Win32 GlobalMemoryStatusEx
                var memInit = new MEMORYSTATUSEX();
                if (GlobalMemoryStatusEx(memInit))
                {
                    TotalPhysicalMemoryBytes = memInit.ullTotalPhys;
                }
                else
                {
                    TotalPhysicalMemoryBytes = 0;
                }

                // Picu pemetaan website IIS sejak awal secara non-blocking di background
                TriggerIisMappingRefresh();

                // 1. Inisialisasi performance counter untuk Total CPU di Windows
                try
                {
                    _cpuTotalCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total", true);
                    _cpuTotalCounter.NextValue();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[PERF_COUNTER] Peringatan total CPU: {ex.Message}");
                }

                // 2. Inisialisasi counter untuk setiap Core CPU di Windows
                for (int i = 0; i < CoreCount; i++)
                {
                    try
                    {
                        string coreName = i.ToString();
                        var coreCounter = new PerformanceCounter("Processor", "% Processor Time", coreName, true);
                        coreCounter.NextValue();
                        _cpuCoreCounters.Add(coreCounter);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[PERF_COUNTER] Peringatan core {i}: {ex.Message}");
                    }
                }

                // 3. Deteksi disk fisik khusus tempat aplikasi ini berjalan di Windows
                string appDir = AppContext.BaseDirectory;
                string rootDrive = Path.GetPathRoot(appDir) ?? "C:\\";
                string driveLetter = rootDrive.TrimEnd('\\', '/').ToUpperInvariant(); // Contoh: "D:"

                AppDriveLetter = driveLetter;
                string matchedDiskInstance = "_Total";

                try
                {
                    var diskCategory = new PerformanceCounterCategory("PhysicalDisk");
                    string[] diskInstances = diskCategory.GetInstanceNames();

                    foreach (string diskName in diskInstances)
                    {
                        // Cari instance PhysicalDisk yang memuat drive aplikasi (misal "1 D:")
                        if (diskName.IndexOf(driveLetter, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            matchedDiskInstance = diskName;
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[PERF_COUNTER] Peringatan deteksi disk aplikasi: {ex.Message}");
                }

                AppDiskName = matchedDiskInstance;

                try
                {
                    _appDiskCounter = new PerformanceCounter("PhysicalDisk", "% Disk Time", matchedDiskInstance, true);
                    _appDiskCounter.NextValue();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[PERF_COUNTER] Peringatan counter disk {matchedDiskInstance}: {ex.Message}");
                }

                // Inisialisasi awal sampel CPU Windows Native NT API
                try
                {
                    CollectNativeCpuMetrics(out _, out _);
                }
                catch
                {
                    // Abaikan kesalahan inisialisasi awal native CPU
                }
            }
            else
            {
                // Lingkungan sistem operasi Linux / Cross-Platform Non-Windows
                TotalPhysicalMemoryBytes = GetLinuxTotalRamBytes();
                AppDriveLetter = "/";
                AppDiskName = "/";

                // Inisialisasi awal sampel CPU Linux (/proc/stat)
                try
                {
                    CollectLinuxCpuMetrics(out _, out _);
                }
                catch
                {
                    // Abaikan kesalahan inisialisasi awal CPU Linux
                }
            }

            // Inisialisasi awal nilai network (kompatibel lintas platform)
            _previousNetworkTime = DateTime.UtcNow;
            RecordCurrentAdapterBytes();

            // Inisialisasi awal sampel CPU proses
            try
            {
                GetTopProcesses();
            }
            catch
            {
                // Abaikan kesalahan pembacaan proses awal
            }
        }

        private void RecordCurrentAdapterBytes()
        {
            try
            {
                NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();
                foreach (var nic in interfaces)
                {
                    if (nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    {
                        try
                        {
                            IPInterfaceStatistics stats = nic.GetIPStatistics();
                            long total = stats.BytesReceived + stats.BytesSent;
                            _previousAdapterBytes[nic.Id] = total;
                        }
                        catch
                        {
                            // Adapter ini mungkin down atau belum siap
                        }
                    }
                }
            }
            catch
            {
                // Abaikan jika adapter jaringan sedang sibuk
            }
        }

        /// <summary>
        /// Mengumpulkan snapshot metrik terbaru secara lengkap
        /// </summary>
        public LiveMetricsSnapshot CollectMetrics()
        {
            lock (_syncLock)
            {
                var snapshot = new LiveMetricsSnapshot
                {
                    Timestamp = DateTime.UtcNow,
                    CoreCount = this.CoreCount,
                    AppDiskName = this.AppDiskName
                };

                // 1 & 2. Membaca Penggunaan CPU (Total & Per-Core)
                double totalCpu = 0.0;
                var coreList = new List<double>();

                if (OperatingSystem.IsWindows())
                {
                    bool perfCounterSuccess = false;

                    if (_cpuCoreCounters.Count > 0)
                    {
                        try
                        {
                            foreach (var coreCounter in _cpuCoreCounters)
                            {
                                double coreVal = (double)coreCounter.NextValue();
                                if (coreVal < 0.0)
                                {
                                    coreVal = 0.0;
                                }
                                else if (coreVal > 100.0)
                                {
                                    coreVal = 100.0;
                                }
                                coreList.Add(Math.Round(coreVal, 1));
                            }

                            if (_cpuTotalCounter != null)
                            {
                                totalCpu = (double)_cpuTotalCounter.NextValue();
                            }
                            else if (coreList.Count > 0)
                            {
                                totalCpu = coreList.Average();
                            }

                            if (coreList.Count > 0)
                            {
                                perfCounterSuccess = true;
                            }
                        }
                        catch
                        {
                            perfCounterSuccess = false;
                        }
                    }

                    // Jika PerformanceCounter tidak tersedia atau diblokir (seperti di IIS Windows Server),
                    // gunakan Windows Native NT API (NtQuerySystemInformation) yang bebas izin
                    if (!perfCounterSuccess || coreList.Count == 0)
                    {
                        if (CollectNativeCpuMetrics(out double nativeTotal, out List<double> nativeCores))
                        {
                            totalCpu = nativeTotal;
                            coreList = nativeCores;
                        }
                    }
                }
                else
                {
                    // Pembacaan CPU untuk lingkungan Linux via /proc/stat
                    CollectLinuxCpuMetrics(out double linuxTotal, out List<double> linuxCores);
                    totalCpu = linuxTotal;
                    coreList = linuxCores;
                }

                if (totalCpu < 0.0)
                {
                    totalCpu = 0.0;
                }
                else if (totalCpu > 100.0)
                {
                    totalCpu = 100.0;
                }

                snapshot.CpuTotalPercent = Math.Round(totalCpu, 1);
                snapshot.CpuCores = coreList;

                // 3. Membaca Status RAM
                if (OperatingSystem.IsWindows())
                {
                    var memStatus = new MEMORYSTATUSEX();
                    if (GlobalMemoryStatusEx(memStatus))
                    {
                        snapshot.RamTotalBytes = memStatus.ullTotalPhys;
                        snapshot.RamAvailableBytes = memStatus.ullAvailPhys;

                        if (memStatus.ullTotalPhys >= memStatus.ullAvailPhys)
                        {
                            snapshot.RamUsedBytes = memStatus.ullTotalPhys - memStatus.ullAvailPhys;
                        }
                        else
                        {
                            snapshot.RamUsedBytes = 0;
                        }

                        snapshot.RamUsagePercent = (double)memStatus.dwMemoryLoad;
                    }
                    else
                    {
                        snapshot.RamTotalBytes = TotalPhysicalMemoryBytes;
                        snapshot.RamUsagePercent = 0.0;
                    }
                }
                else
                {
                    // Pembacaan RAM untuk lingkungan Linux via /proc/meminfo
                    CollectLinuxMemoryMetrics(snapshot);
                }

                // 4. Membaca Penggunaan Disk
                double diskPercent = 0.0;
                if (OperatingSystem.IsWindows())
                {
                    if (_appDiskCounter != null)
                    {
                        try
                        {
                            diskPercent = (double)_appDiskCounter.NextValue();
                        }
                        catch
                        {
                            diskPercent = 0.0;
                        }
                    }
                }
                else
                {
                    // Pembacaan Disk untuk lingkungan Linux via DriveInfo
                    diskPercent = GetLinuxDiskPercent();
                }

                if (diskPercent < 0.0)
                {
                    diskPercent = 0.0;
                }
                else if (diskPercent > 100.0)
                {
                    diskPercent = 100.0;
                }

                snapshot.DiskUsagePercent = Math.Round(diskPercent, 1);

                // 5. Membaca Seluruh Adapter Network
                DateTime currentTime = DateTime.UtcNow;
                double elapsedSeconds = (currentTime - _previousNetworkTime).TotalSeconds;

                var adapterList = new List<NetworkMetricItem>();
                double totalNetworkBitsPerSec = 0.0;
                long totalBandwidthCapacityBits = 0;
                double totalAllBytesPerSec = 0.0;

                try
                {
                    NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();
                    foreach (var nic in interfaces)
                    {
                        // Abaikan adapter software internal loopback
                        if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                        {
                            continue;
                        }

                        double adapterBytesPerSec = 0.0;
                        double adapterPercent = 0.0;

                        if (nic.OperationalStatus == OperationalStatus.Up)
                        {
                            try
                            {
                                IPInterfaceStatistics stats = nic.GetIPStatistics();
                                long currentBytes = stats.BytesReceived + stats.BytesSent;

                                long prevBytes = 0;
                                if (_previousAdapterBytes.ContainsKey(nic.Id))
                                {
                                    prevBytes = _previousAdapterBytes[nic.Id];
                                }

                                if (elapsedSeconds > 0.0)
                                {
                                    long delta = currentBytes - prevBytes;
                                    if (delta < 0)
                                    {
                                        delta = 0;
                                    }
                                    adapterBytesPerSec = delta / elapsedSeconds;
                                }

                                _previousAdapterBytes[nic.Id] = currentBytes;

                                double adapterBitsPerSec = adapterBytesPerSec * 8.0;

                                if (nic.Speed > 0)
                                {
                                    adapterPercent = (adapterBitsPerSec / (double)nic.Speed) * 100.0;
                                    totalBandwidthCapacityBits += nic.Speed;
                                }

                                if (adapterPercent < 0.0)
                                {
                                    adapterPercent = 0.0;
                                }
                                else if (adapterPercent > 100.0)
                                {
                                    adapterPercent = 100.0;
                                }

                                totalNetworkBitsPerSec += adapterBitsPerSec;
                                totalAllBytesPerSec += adapterBytesPerSec;
                            }
                            catch
                            {
                                adapterBytesPerSec = 0.0;
                                adapterPercent = 0.0;
                            }
                        }

                        adapterList.Add(new NetworkMetricItem
                        {
                            Name = nic.Name,
                            Description = nic.Description,
                            UsagePercent = Math.Round(adapterPercent, 1),
                            BytesPerSec = Math.Round(adapterBytesPerSec, 0)
                        });
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[NETWORK] Error pembacaan adapter: {ex.Message}");
                }

                _previousNetworkTime = currentTime;

                double totalNetworkPercent = 0.0;
                if (totalBandwidthCapacityBits > 0)
                {
                    totalNetworkPercent = (totalNetworkBitsPerSec / (double)totalBandwidthCapacityBits) * 100.0;
                }

                if (totalNetworkPercent < 0.0)
                {
                    totalNetworkPercent = 0.0;
                }
                else if (totalNetworkPercent > 100.0)
                {
                    totalNetworkPercent = 100.0;
                }

                snapshot.NetworkAdapters = adapterList;
                snapshot.NetworkUsagePercent = Math.Round(totalNetworkPercent, 1);
                snapshot.NetworkBytesPerSec = Math.Round(totalAllBytesPerSec, 0);

                // 6. Membaca Metrik Penggunaan CPU dan RAM untuk setiap Website IIS (w3wp.exe)
                if (OperatingSystem.IsWindows())
                {
                    int coresForIis = CoreCount;
                    if (coresForIis <= 0)
                    {
                        coresForIis = Environment.ProcessorCount;
                    }
                    snapshot.IisWebsites = CollectIisMetrics(currentTime, coresForIis);
                }
                else
                {
                    snapshot.IisWebsites = new List<IisWebsiteMetricItem>();
                }

                return snapshot;
            }
        }

        /// <summary>
        /// Mengumpulkan metrik CPU (Total & Per-Core) menggunakan Windows Native NT API
        /// Sangat andal untuk lingkungan server (seperti Xeon di IIS) yang membatasi hak akses PerformanceCounter
        /// </summary>
        private bool CollectNativeCpuMetrics(out double totalCpu, out List<double> coreList)
        {
            totalCpu = 0.0;
            coreList = new List<double>();

            int structSize = 48; // 6 field x 8 byte per processor
            int bufSize = CoreCount * structSize;
            IntPtr cpuBuf = Marshal.AllocHGlobal(bufSize);

            try
            {
                int retLen;
                int status = NtQuerySystemInformation(SystemProcessorPerformanceInformation, cpuBuf, bufSize, out retLen);
                if (status != 0)
                {
                    return false;
                }

                int returnedCores = retLen / structSize;
                if (returnedCores <= 0)
                {
                    return false;
                }

                bool hasPrev = false;
                if (_prevCpuCoreTimes.Count == returnedCores)
                {
                    hasPrev = true;
                }

                double sumCorePercent = 0.0;

                for (int i = 0; i < returnedCores; i++)
                {
                    IntPtr corePtr = IntPtr.Add(cpuBuf, i * structSize);
                    long idle = Marshal.ReadInt64(corePtr, 0);
                    long kernel = Marshal.ReadInt64(corePtr, 8);
                    long user = Marshal.ReadInt64(corePtr, 16);

                    double corePercent = 0.0;

                    if (hasPrev)
                    {
                        var prev = _prevCpuCoreTimes[i];
                        long usrDiff = user - prev.user;
                        long kerDiff = kernel - prev.kernel; // Pada kernel Windows, kernel time mencakup idle time
                        long idleDiff = idle - prev.idle;
                        long totalCoreTime = usrDiff + kerDiff;

                        if (totalCoreTime > 0)
                        {
                            corePercent = (1.0 - ((double)idleDiff / (double)totalCoreTime)) * 100.0;
                            if (corePercent < 0.0)
                            {
                                corePercent = 0.0;
                            }
                            else if (corePercent > 100.0)
                            {
                                corePercent = 100.0;
                            }
                        }

                        _prevCpuCoreTimes[i] = (idle, kernel, user);
                    }
                    else
                    {
                        _prevCpuCoreTimes.Add((idle, kernel, user));
                    }

                    coreList.Add(Math.Round(corePercent, 1));
                    sumCorePercent += corePercent;
                }

                if (coreList.Count > 0)
                {
                    totalCpu = Math.Round(sumCorePercent / (double)coreList.Count, 1);
                }

                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(cpuBuf);
            }
        }

        /// <summary>
        /// Mengumpulkan top 20 program CPU high usage dan top 20 program high RAM
        /// Menggunakan Windows Native NT API (NtQuerySystemInformation) agar mencakup seluruh service sistem
        /// (seperti MySQL, SADEE, IIS w3wp, dan background service) secara akurat persis seperti Task Manager
        /// </summary>
        public TopProcessesResponse GetTopProcesses()
        {
            lock (_processLock)
            {
                DateTime now = DateTime.UtcNow;
                var processList = new List<ProcessMetricItem>();

                int totalCores = CoreCount;
                if (totalCores <= 0)
                {
                    totalCores = Environment.ProcessorCount;
                }

                if (OperatingSystem.IsWindows())
                {
                    // Coba gunakan Windows Native API (NtQuerySystemInformation) terlebih dahulu
                    bool success = GetProcessesViaNtQuery(now, totalCores, processList);

                    // Jika Native API tidak menghasilkan data, gunakan fallback
                    if (!success || processList.Count == 0)
                    {
                        GetProcessesViaFallback(now, totalCores, processList);
                    }
                }
                else
                {
                    // Di Linux, gunakan pembacaan Process.GetProcesses() yang kompatibel lintas platform
                    GetProcessesViaFallback(now, totalCores, processList);
                }

                // Urutkan Top 20 CPU High Usage
                var topCpu = processList
                    .OrderByDescending(x => x.CpuPercent)
                    .ThenByDescending(x => x.MemoryMb)
                    .Take(20)
                    .ToList();

                // Urutkan Top 20 High RAM
                var topRam = processList
                    .OrderByDescending(x => x.MemoryMb)
                    .ThenByDescending(x => x.CpuPercent)
                    .Take(20)
                    .ToList();

                return new TopProcessesResponse
                {
                    TopCpu = topCpu,
                    TopRam = topRam
                };
            }
        }

        /// <summary>
        /// Membaca seluruh proses Windows melalui kernel snapshot (NtQuerySystemInformation)
        /// Tanpa membutuhkan OpenProcess, sehingga tidak terkena 'Access Denied' pada proses SYSTEM / Services
        /// </summary>
        private bool GetProcessesViaNtQuery(DateTime now, int totalCores, List<ProcessMetricItem> processList)
        {
            int bufSize = 1024 * 1024 * 4; // Buffer awal 4 MB
            IntPtr buf = Marshal.AllocHGlobal(bufSize);

            try
            {
                int retLen;
                int status = NtQuerySystemInformation(SystemProcessInformation, buf, bufSize, out retLen);

                // Tangani kasus jika buffer awal kurang besar (STATUS_INFO_LENGTH_MISMATCH)
                if (status == unchecked((int)0xC0000004))
                {
                    Marshal.FreeHGlobal(buf);
                    bufSize = retLen + (1024 * 512);
                    buf = Marshal.AllocHGlobal(bufSize);
                    status = NtQuerySystemInformation(SystemProcessInformation, buf, bufSize, out retLen);
                }

                if (status != 0)
                {
                    return false;
                }

                IntPtr curr = buf;
                bool is64Bit = (IntPtr.Size == 8);

                while (true)
                {
                    int nextOffset = Marshal.ReadInt32(curr, 0);

                    long userTime = 0;
                    long kernelTime = 0;
                    short nameLen = 0;
                    IntPtr namePtr = IntPtr.Zero;
                    int pid = 0;
                    long workingSet = 0;
                    long privateWorkingSet = 0;

                    if (is64Bit)
                    {
                        privateWorkingSet = Marshal.ReadInt64(curr, 8);
                        userTime = Marshal.ReadInt64(curr, 40);
                        kernelTime = Marshal.ReadInt64(curr, 48);
                        nameLen = Marshal.ReadInt16(curr, 56);
                        namePtr = Marshal.ReadIntPtr(curr, 64);
                        pid = Marshal.ReadIntPtr(curr, 80).ToInt32();
                        workingSet = Marshal.ReadIntPtr(curr, 144).ToInt64();
                    }
                    else
                    {
                        privateWorkingSet = Marshal.ReadInt64(curr, 8);
                        userTime = Marshal.ReadInt64(curr, 24);
                        kernelTime = Marshal.ReadInt64(curr, 32);
                        nameLen = Marshal.ReadInt16(curr, 40);
                        namePtr = Marshal.ReadIntPtr(curr, 44);
                        pid = Marshal.ReadIntPtr(curr, 52).ToInt32();
                        workingSet = Marshal.ReadIntPtr(curr, 84).ToInt64();
                    }

                    // Abaikan System Idle Process (PID 0)
                    if (pid != 0)
                    {
                        string pName = "System";
                        if (namePtr != IntPtr.Zero && nameLen > 0)
                        {
                            string? parsedName = Marshal.PtrToStringUni(namePtr, nameLen / 2);
                            if (!string.IsNullOrWhiteSpace(parsedName))
                            {
                                pName = parsedName;
                            }
                        }

                        // Gunakan Private Working Set jika tersedia (persis kolom Memory Task Manager Details)
                        long memBytes = privateWorkingSet;
                        if (memBytes <= 0)
                        {
                            memBytes = workingSet;
                        }
                        double memMb = Math.Round((double)memBytes / (1024.0 * 1024.0), 1);

                        // Kalkulasi persentase CPU per proses
                        long currentTicks = userTime + kernelTime;
                        double cpuPercent = 0.0;

                        if (_prevProcTicks.TryGetValue(pid, out var prev))
                        {
                            double elapsedSec = (now - prev.timestamp).TotalSeconds;
                            long ticksDiff = currentTicks - prev.totalTicks;

                            if (elapsedSec > 0.0 && ticksDiff > 0)
                            {
                                double cpuSeconds = (double)ticksDiff / 10000000.0;
                                cpuPercent = (cpuSeconds / (elapsedSec * (double)totalCores)) * 100.0;

                                if (cpuPercent > 100.0)
                                {
                                    cpuPercent = 100.0;
                                }
                            }
                        }

                        _prevProcTicks[pid] = (now, currentTicks);

                        processList.Add(new ProcessMetricItem
                        {
                            Id = pid,
                            Name = pName,
                            CpuPercent = Math.Round(cpuPercent, 1),
                            MemoryMb = memMb
                        });
                    }

                    if (nextOffset == 0)
                    {
                        break;
                    }

                    curr = IntPtr.Add(curr, nextOffset);
                }

                // Bersihkan cache jika melebihi 2000 entri
                if (_prevProcTicks.Count > 2000)
                {
                    var activePids = new HashSet<int>(processList.Select(x => x.Id));
                    var deadPids = _prevProcTicks.Keys.Where(k => !activePids.Contains(k)).ToList();
                    foreach (var dead in deadPids)
                    {
                        _prevProcTicks.Remove(dead);
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        /// <summary>
        /// Fallback menggunakan System.Diagnostics.Process jika Native NT API gagal
        /// </summary>
        private void GetProcessesViaFallback(DateTime now, int totalCores, List<ProcessMetricItem> processList)
        {
            Process[] processes = Process.GetProcesses();
            foreach (var p in processes)
            {
                if (p.Id == 0)
                {
                    continue;
                }

                try
                {
                    string pName = p.ProcessName;
                    long memBytes = 0;
                    try
                    {
                        memBytes = p.WorkingSet64;
                    }
                    catch
                    {
                        memBytes = 0;
                    }

                    double memMb = Math.Round((double)memBytes / (1024.0 * 1024.0), 1);
                    double cpuPercent = 0.0;

                    try
                    {
                        TimeSpan currentCpuTime = p.TotalProcessorTime;
                        if (_prevProcTicks.TryGetValue(p.Id, out var prev))
                        {
                            double elapsedSec = (now - prev.timestamp).TotalSeconds;
                            long currentTicks = currentCpuTime.Ticks;
                            long ticksDiff = currentTicks - prev.totalTicks;

                            if (elapsedSec > 0.0 && ticksDiff > 0)
                            {
                                double cpuSec = (double)ticksDiff / 10000000.0;
                                cpuPercent = (cpuSec / (elapsedSec * (double)totalCores)) * 100.0;
                                if (cpuPercent > 100.0)
                                {
                                    cpuPercent = 100.0;
                                }
                            }
                        }
                        _prevProcTicks[p.Id] = (now, currentCpuTime.Ticks);
                    }
                    catch
                    {
                        // Jika akses CPU time ditolak, tetap tambahkan proses dengan memori agar tidak hilang
                    }

                    processList.Add(new ProcessMetricItem
                    {
                        Id = p.Id,
                        Name = pName,
                        CpuPercent = Math.Round(cpuPercent, 1),
                        MemoryMb = memMb
                    });
                }
                catch
                {
                    // Abaikan proses yang telah dihentikan
                }
                finally
                {
                    p.Dispose();
                }
            }
        }

        /// <summary>
        /// Mengumpulkan metrik penggunaan CPU dan RAM per website IIS (w3wp.exe)
        /// menggunakan Windows Native NT API (NtQuerySystemInformation) untuk akurasi tinggi
        /// </summary>
        private List<IisWebsiteMetricItem> CollectIisMetrics(DateTime now, int totalCores)
        {
            lock (_iisLock)
            {
                var iisItems = new List<IisWebsiteMetricItem>();

                // Picu pembaruan pemetaan website secara background setiap 15 detik tanpa memblokir thread
                double secondsSinceLastRefresh = (now - _lastW3wpCacheRefresh).TotalSeconds;
                if (secondsSinceLastRefresh >= 15.0 || _lastW3wpCacheRefresh == DateTime.MinValue)
                {
                    TriggerIisMappingRefresh();
                }

                int bufSize = 1024 * 1024 * 4; // Buffer awal 4 MB
                IntPtr buf = Marshal.AllocHGlobal(bufSize);

                try
                {
                    int retLen;
                    int status = NtQuerySystemInformation(SystemProcessInformation, buf, bufSize, out retLen);

                    if (status == unchecked((int)0xC0000004))
                    {
                        Marshal.FreeHGlobal(buf);
                        bufSize = retLen + (1024 * 512);
                        buf = Marshal.AllocHGlobal(bufSize);
                        status = NtQuerySystemInformation(SystemProcessInformation, buf, bufSize, out retLen);
                    }

                    if (status != 0)
                    {
                        return iisItems;
                    }

                    IntPtr curr = buf;
                    bool is64Bit = (IntPtr.Size == 8);

                    while (true)
                    {
                        int nextOffset = Marshal.ReadInt32(curr, 0);

                        long userTime = 0;
                        long kernelTime = 0;
                        short nameLen = 0;
                        IntPtr namePtr = IntPtr.Zero;
                        int pid = 0;
                        long workingSet = 0;
                        long privateWorkingSet = 0;

                        if (is64Bit)
                        {
                            privateWorkingSet = Marshal.ReadInt64(curr, 8);
                            userTime = Marshal.ReadInt64(curr, 40);
                            kernelTime = Marshal.ReadInt64(curr, 48);
                            nameLen = Marshal.ReadInt16(curr, 56);
                            namePtr = Marshal.ReadIntPtr(curr, 64);
                            pid = Marshal.ReadIntPtr(curr, 80).ToInt32();
                            workingSet = Marshal.ReadIntPtr(curr, 144).ToInt64();
                        }
                        else
                        {
                            privateWorkingSet = Marshal.ReadInt64(curr, 8);
                            userTime = Marshal.ReadInt64(curr, 24);
                            kernelTime = Marshal.ReadInt64(curr, 32);
                            nameLen = Marshal.ReadInt16(curr, 40);
                            namePtr = Marshal.ReadIntPtr(curr, 44);
                            pid = Marshal.ReadIntPtr(curr, 52).ToInt32();
                            workingSet = Marshal.ReadIntPtr(curr, 84).ToInt64();
                        }

                        if (pid > 0 && namePtr != IntPtr.Zero && nameLen > 0)
                        {
                            string? pName = Marshal.PtrToStringUni(namePtr, nameLen / 2);
                            if (!string.IsNullOrWhiteSpace(pName))
                            {
                                bool isW3wp = false;
                                if (string.Equals(pName, "w3wp.exe", StringComparison.OrdinalIgnoreCase))
                                {
                                    isW3wp = true;
                                }
                                else if (string.Equals(pName, "w3wp", StringComparison.OrdinalIgnoreCase))
                                {
                                    isW3wp = true;
                                }

                                if (isW3wp)
                                {
                                    long memBytes = privateWorkingSet;
                                    if (memBytes <= 0)
                                    {
                                        memBytes = workingSet;
                                    }
                                    double memMb = Math.Round((double)memBytes / (1024.0 * 1024.0), 1);

                                    long currentTicks = userTime + kernelTime;
                                    double cpuPercent = 0.0;

                                    if (_prevIisProcTicks.TryGetValue(pid, out var prev))
                                    {
                                        double elapsedSec = (now - prev.timestamp).TotalSeconds;
                                        long ticksDiff = currentTicks - prev.totalTicks;

                                        if (elapsedSec > 0.0 && ticksDiff > 0)
                                        {
                                            double cpuSeconds = (double)ticksDiff / 10000000.0;
                                            cpuPercent = (cpuSeconds / (elapsedSec * (double)totalCores)) * 100.0;

                                            if (cpuPercent > 100.0)
                                            {
                                                cpuPercent = 100.0;
                                            }
                                        }
                                    }

                                    _prevIisProcTicks[pid] = (now, currentTicks);

                                    string websiteName = ResolveWebsiteName(pid);

                                    iisItems.Add(new IisWebsiteMetricItem
                                    {
                                        WebsiteName = websiteName,
                                        ProcessId = pid,
                                        CpuPercent = Math.Round(cpuPercent, 1),
                                        MemoryMb = memMb
                                    });
                                }
                            }
                        }

                        if (nextOffset == 0)
                        {
                            break;
                        }

                        curr = IntPtr.Add(curr, nextOffset);
                    }

                    // Bersihkan cache ticks jika proses sudah mati
                    if (_prevIisProcTicks.Count > 200)
                    {
                        var activePids = new HashSet<int>(iisItems.Select(x => x.ProcessId));
                        var deadPids = _prevIisProcTicks.Keys.Where(k => !activePids.Contains(k)).ToList();
                        foreach (var dead in deadPids)
                        {
                            _prevIisProcTicks.Remove(dead);
                        }
                    }

                    // Agregasikan per website jika ada multiple worker process (web garden)
                    var groupedWebsites = new List<IisWebsiteMetricItem>();
                    var groups = iisItems.GroupBy(x => x.WebsiteName);
                    foreach (var group in groups)
                    {
                        double sumCpu = 0.0;
                        double sumMem = 0.0;
                        int firstPid = 0;

                        foreach (var item in group)
                        {
                            sumCpu += item.CpuPercent;
                            sumMem += item.MemoryMb;
                            if (firstPid == 0)
                            {
                                firstPid = item.ProcessId;
                            }
                        }

                        groupedWebsites.Add(new IisWebsiteMetricItem
                        {
                            WebsiteName = group.Key,
                            ProcessId = firstPid,
                            CpuPercent = Math.Round(sumCpu, 1),
                            MemoryMb = Math.Round(sumMem, 1)
                        });
                    }

                    // Urutkan berdasarkan CPU tertinggi, kemudian Memory tertinggi
                    return groupedWebsites
                        .OrderByDescending(x => x.CpuPercent)
                        .ThenByDescending(x => x.MemoryMb)
                        .ToList();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[IIS_COLLECT_ERROR] {ex.Message}");
                    return iisItems;
                }
                finally
                {
                    Marshal.FreeHGlobal(buf);
                }
            }
        }

        /// <summary>
        /// Menguraikan nama website IIS berdasarkan PID worker process secara instan tanpa memblokir
        /// </summary>
        private string ResolveWebsiteName(int pid)
        {
            // 1. Cek apakah PID sudah ada di cache pemetaan AppPool
            if (_w3wpAppPoolCache.TryGetValue(pid, out string? appPoolName))
            {
                if (!string.IsNullOrWhiteSpace(appPoolName))
                {
                    if (_appPoolToSiteCache.TryGetValue(appPoolName, out string? siteName))
                    {
                        if (!string.IsNullOrWhiteSpace(siteName))
                        {
                            return siteName;
                        }
                    }

                    return appPoolName;
                }
            }

            // 2. Coba baca secara instan via Native NT Process Query (0.01 ms)
            RefreshViaNativeProcessQuery(pid);
            if (_w3wpAppPoolCache.TryGetValue(pid, out string? nativePool))
            {
                if (!string.IsNullOrWhiteSpace(nativePool))
                {
                    if (_appPoolToSiteCache.TryGetValue(nativePool, out string? siteName))
                    {
                        if (!string.IsNullOrWhiteSpace(siteName))
                        {
                            return siteName;
                        }
                    }

                    return nativePool;
                }
            }

            // 3. Picu background refresh untuk mendeteksi website baru secara menyeluruh
            TriggerIisMappingRefresh();

            return string.Format("w3wp (PID: {0})", pid);
        }

        private void TriggerIisMappingRefresh()
        {
            if (Interlocked.CompareExchange(ref _isRefreshingIisMapping, 1, 0) == 0)
            {
                Task.Run(() =>
                {
                    try
                    {
                        RefreshIisProcessMapping();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[IIS_REFRESH_TASK_ERROR] {ex.Message}");
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _isRefreshingIisMapping, 0);
                    }
                });
            }
        }

        /// <summary>
        /// Memperbarui pemetaan PID w3wp ke nama Application Pool dan nama Website IIS
        /// Menggunakan 5 lapisan deteksi terpadu:
        /// Lapisan 1: Environment Variables bawaan IIS (APP_POOL_ID, SITE_NAME) untuk aplikasi sendiri
        /// Lapisan 2: Microsoft.Web.Administration ServerManager (API Native resmi IIS)
        /// Lapisan 3: Performance Counters IIS (W3SVC_W3WP & WAS_W3WP)
        /// Lapisan 4: Utilitas appcmd.exe (list app & list wp)
        /// Lapisan 5: WMI Win32_Process CommandLine
        /// </summary>
        private void RefreshIisProcessMapping()
        {
            // Lapisan 1: Baca langsung variabel lingkungan proses saat ini jika berjalan di bawah IIS
            RefreshViaEnvironment();

            // Lapisan 2: Gunakan API resmi IIS ServerManager (Microsoft.Web.Administration)
            RefreshViaServerManager();

            // Lapisan 3: Baca Performance Counter IIS (W3SVC_W3WP & WAS_W3WP)
            RefreshViaPerformanceCounters();

            // Lapisan 4: Baca dari utilitas appcmd.exe jika tersedia
            string? appcmd = FindAppCmdPath();
            if (!string.IsNullOrWhiteSpace(appcmd))
            {
                RunAppCmdListApp(appcmd);
                RunAppCmdListWp(appcmd);
            }

            // Lapisan 5: Baca dari WMI Win32_Process
            RefreshViaWmi();

            _lastW3wpCacheRefresh = DateTime.UtcNow;
        }

        /// <summary>
        /// Membaca nama website dan pool aplikasi host sendiri langsung dari environment IIS tanpa butuh izin admin
        /// </summary>
        private void RefreshViaEnvironment()
        {
            try
            {
                string? appPool = Environment.GetEnvironmentVariable("APP_POOL_ID");
                if (string.IsNullOrWhiteSpace(appPool))
                {
                    appPool = Environment.GetEnvironmentVariable("APP_POOL_NAME");
                }

                string? siteName = Environment.GetEnvironmentVariable("SITE_NAME");

                if (!string.IsNullOrWhiteSpace(appPool) && !string.IsNullOrWhiteSpace(siteName))
                {
                    _appPoolToSiteCache[appPool] = siteName;
                }

                int currentPid = Process.GetCurrentProcess().Id;
                if (currentPid > 0 && !string.IsNullOrWhiteSpace(appPool))
                {
                    _w3wpAppPoolCache[currentPid] = appPool;
                }
            }
            catch
            {
            }
        }

        /// <summary>
        /// Membaca konfigurasi IIS dan daftar worker process aktif menggunakan Microsoft.Web.Administration
        /// </summary>
        private void RefreshViaServerManager()
        {
            try
            {
                using (var serverManager = new ServerManager())
                {
                    // A. Petakan Application Pool ke Website Name
                    try
                    {
                        var sites = serverManager.Sites;
                        if (sites != null)
                        {
                            foreach (var site in sites)
                            {
                                if (site == null)
                                {
                                    continue;
                                }

                                string siteName = site.Name;
                                if (string.IsNullOrWhiteSpace(siteName))
                                {
                                    continue;
                                }

                                if (site.Applications != null)
                                {
                                    foreach (var app in site.Applications)
                                    {
                                        if (app != null)
                                        {
                                            string pool = app.ApplicationPoolName;
                                            if (!string.IsNullOrWhiteSpace(pool))
                                            {
                                                _appPoolToSiteCache[pool] = siteName;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch
                    {
                    }

                    // B. Petakan ProcessId (PID w3wp) ke Application Pool Name
                    try
                    {
                        var workerProcesses = serverManager.WorkerProcesses;
                        if (workerProcesses != null)
                        {
                            foreach (var wp in workerProcesses)
                            {
                                if (wp != null)
                                {
                                    int pid = wp.ProcessId;
                                    string pool = wp.AppPoolName;
                                    if (pid > 0 && !string.IsNullOrWhiteSpace(pool))
                                    {
                                        _w3wpAppPoolCache[pid] = pool;
                                    }
                                }
                            }
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

        private void RefreshViaPerformanceCounters()
        {
            string[] categories = new string[] { "W3SVC_W3WP", "WAS_W3WP" };
            foreach (string categoryName in categories)
            {
                try
                {
                    if (PerformanceCounterCategory.Exists(categoryName))
                    {
                        var cat = new PerformanceCounterCategory(categoryName);
                        string[] instances = cat.GetInstanceNames();
                        foreach (string inst in instances)
                        {
                            // Format instance: "{pid}_{appPool}"
                            int underscoreIdx = inst.IndexOf('_');
                            if (underscoreIdx > 0)
                            {
                                string pidStr = inst.Substring(0, underscoreIdx);
                                string pool = inst.Substring(underscoreIdx + 1).Trim();
                                if (int.TryParse(pidStr, out int pid) && !string.IsNullOrWhiteSpace(pool))
                                {
                                    _w3wpAppPoolCache[pid] = pool;
                                }
                            }
                        }
                    }
                }
                catch
                {
                }
            }
        }

        private void RefreshViaNativeProcessQuery(int pid)
        {
            IntPtr hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hProc == IntPtr.Zero)
            {
                hProc = OpenProcess(0x0400, false, pid);
            }

            if (hProc == IntPtr.Zero)
            {
                return;
            }

            try
            {
                int bufSize = 1024 * 32;
                IntPtr buf = Marshal.AllocHGlobal(bufSize);
                try
                {
                    int retLen;
                    int status = NtQueryInformationProcess(hProc, ProcessCommandLineInformation, buf, bufSize, out retLen);
                    if (status == 0)
                    {
                        ushort len = (ushort)Marshal.ReadInt16(buf, 0);
                        IntPtr strBuf = IntPtr.Zero;
                        if (IntPtr.Size == 8)
                        {
                            strBuf = Marshal.ReadIntPtr(buf, 8);
                        }
                        else
                        {
                            strBuf = Marshal.ReadIntPtr(buf, 4);
                        }

                        if (len > 0 && strBuf != IntPtr.Zero)
                        {
                            string? cmdLine = Marshal.PtrToStringUni(strBuf, len / 2);
                            if (!string.IsNullOrWhiteSpace(cmdLine))
                            {
                                string pool = ExtractAppPoolName(cmdLine);
                                if (!string.IsNullOrWhiteSpace(pool))
                                {
                                    _w3wpAppPoolCache[pid] = pool;
                                }
                            }
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buf);
                }
            }
            catch
            {
            }
            finally
            {
                CloseHandle(hProc);
            }
        }

        private void RefreshViaWmi()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'w3wp.exe'"))
                {
                    using (var results = searcher.Get())
                    {
                        foreach (ManagementObject obj in results)
                        {
                            try
                            {
                                if (obj["ProcessId"] != null)
                                {
                                    int pid = Convert.ToInt32(obj["ProcessId"]);
                                    string cmdLine = obj["CommandLine"]?.ToString() ?? "";
                                    string appPool = ExtractAppPoolName(cmdLine);
                                    if (!string.IsNullOrWhiteSpace(appPool))
                                    {
                                        _w3wpAppPoolCache[pid] = appPool;
                                    }
                                }
                            }
                            catch
                            {
                            }
                        }
                    }
                }
            }
            catch
            {
            }
        }

        private static string? FindAppCmdPath()
        {
            string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string sys32 = Path.Combine(winDir, "System32", "inetsrv", "appcmd.exe");
            if (File.Exists(sys32))
            {
                return sys32;
            }

            string sysnative = Path.Combine(winDir, "Sysnative", "inetsrv", "appcmd.exe");
            if (File.Exists(sysnative))
            {
                return sysnative;
            }

            return null;
        }

        private static string ExtractAppPoolName(string cmdLine)
        {
            if (string.IsNullOrWhiteSpace(cmdLine))
            {
                return string.Empty;
            }

            // Pola 1: Jika nama dibungkus tanda kutip (misal: -ap "Default Web Site")
            var matchQuoted = Regex.Match(cmdLine, @"-ap\s+[""']([^""']+)[""']", RegexOptions.IgnoreCase);
            if (matchQuoted.Success)
            {
                return matchQuoted.Groups[1].Value.Trim();
            }

            // Pola 2: Jika nama tanpa tanda kutip (misal: -ap DefaultAppPool)
            var matchUnquoted = Regex.Match(cmdLine, @"-ap\s+([^\s]+)", RegexOptions.IgnoreCase);
            if (matchUnquoted.Success)
            {
                return matchUnquoted.Groups[1].Value.Trim();
            }

            return string.Empty;
        }

        private void RunAppCmdListApp(string appcmdPath)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = appcmdPath,
                    Arguments = "list app",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var proc = new Process { StartInfo = psi })
                {
                    proc.Start();
                    string output = proc.StandardOutput.ReadToEnd();
                    proc.StandardError.ReadToEnd();
                    proc.WaitForExit(2000);

                    if (!string.IsNullOrWhiteSpace(output))
                    {
                        // Parsing baris per baris secara eksplisit tanpa risiko regex greedy
                        string[] lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (string line in lines)
                        {
                            // Format: APP "MonitoringPC/" (applicationPool:NetCore)
                            int poolIdx = line.IndexOf("(applicationPool:", StringComparison.OrdinalIgnoreCase);
                            if (poolIdx > 0)
                            {
                                int closeParen = line.IndexOf(')', poolIdx);
                                if (closeParen > poolIdx)
                                {
                                    int startPool = poolIdx + "(applicationPool:".Length;
                                    string pool = line.Substring(startPool, closeParen - startPool).Trim();

                                    int firstQuote = line.IndexOf('"');
                                    int secondQuote = line.IndexOf('"', firstQuote + 1);
                                    if (firstQuote >= 0 && secondQuote > firstQuote)
                                    {
                                        string sitePart = line.Substring(firstQuote + 1, secondQuote - firstQuote - 1).Trim();
                                        int slashIdx = sitePart.IndexOf('/');
                                        string site = sitePart;
                                        if (slashIdx >= 0)
                                        {
                                            site = sitePart.Substring(0, slashIdx).Trim();
                                        }

                                        if (!string.IsNullOrWhiteSpace(pool) && !string.IsNullOrWhiteSpace(site))
                                        {
                                            _appPoolToSiteCache[pool] = site;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
            }
        }

        private void RunAppCmdListWp(string appcmdPath)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = appcmdPath,
                    Arguments = "list wp",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var proc = new Process { StartInfo = psi })
                {
                    proc.Start();
                    string output = proc.StandardOutput.ReadToEnd();
                    proc.StandardError.ReadToEnd();
                    proc.WaitForExit(2000);

                    if (!string.IsNullOrWhiteSpace(output))
                    {
                        // Parsing baris per baris secara eksplisit
                        string[] lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (string line in lines)
                        {
                            // Format: WP "25052" (applicationPool:APIOEEv1)
                            int poolIdx = line.IndexOf("(applicationPool:", StringComparison.OrdinalIgnoreCase);
                            if (poolIdx > 0)
                            {
                                int closeParen = line.IndexOf(')', poolIdx);
                                if (closeParen > poolIdx)
                                {
                                    int startPool = poolIdx + "(applicationPool:".Length;
                                    string pool = line.Substring(startPool, closeParen - startPool).Trim();

                                    int firstQuote = line.IndexOf('"');
                                    int secondQuote = line.IndexOf('"', firstQuote + 1);
                                    string pidStr = string.Empty;
                                    if (firstQuote >= 0 && secondQuote > firstQuote)
                                    {
                                        pidStr = line.Substring(firstQuote + 1, secondQuote - firstQuote - 1).Trim();
                                    }
                                    else
                                    {
                                        string[] tokens = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                                        if (tokens.Length >= 2)
                                        {
                                            pidStr = tokens[1].Trim();
                                        }
                                    }

                                    if (int.TryParse(pidStr, out int pid) && !string.IsNullOrWhiteSpace(pool))
                                    {
                                        _w3wpAppPoolCache[pid] = pool;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
            }
        }

        /// <summary>
        /// Mengumpulkan metrik CPU (Total & Per-Core) di lingkungan Linux melalui file virtual /proc/stat
        /// </summary>
        private void CollectLinuxCpuMetrics(out double totalCpu, out List<double> coreList)
        {
            totalCpu = 0.0;
            coreList = new List<double>();

            try
            {
                if (!File.Exists("/proc/stat"))
                {
                    return;
                }

                string[] lines = File.ReadAllLines("/proc/stat");
                foreach (string line in lines)
                {
                    if (!line.StartsWith("cpu"))
                    {
                        continue;
                    }

                    string[] parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 5)
                    {
                        continue;
                    }

                    string cpuId = parts[0]; // "cpu" untuk total, "cpu0", "cpu1", dst untuk per-core

                    long user = 0;
                    long nice = 0;
                    long system = 0;
                    long idle = 0;
                    long iowait = 0;
                    long irq = 0;
                    long softirq = 0;
                    long steal = 0;

                    long.TryParse(parts[1], out user);
                    long.TryParse(parts[2], out nice);
                    long.TryParse(parts[3], out system);
                    long.TryParse(parts[4], out idle);

                    if (parts.Length > 5)
                    {
                        long.TryParse(parts[5], out iowait);
                    }
                    if (parts.Length > 6)
                    {
                        long.TryParse(parts[6], out irq);
                    }
                    if (parts.Length > 7)
                    {
                        long.TryParse(parts[7], out softirq);
                    }
                    if (parts.Length > 8)
                    {
                        long.TryParse(parts[8], out steal);
                    }

                    long idleTime = idle + iowait;
                    long totalTime = user + nice + system + idle + iowait + irq + softirq + steal;

                    double percent = 0.0;
                    if (_prevLinuxCpuTimes.TryGetValue(cpuId, out var prev))
                    {
                        long totalDiff = totalTime - prev.total;
                        long idleDiff = idleTime - prev.idle;

                        if (totalDiff > 0)
                        {
                            percent = (1.0 - ((double)idleDiff / (double)totalDiff)) * 100.0;
                            if (percent < 0.0)
                            {
                                percent = 0.0;
                            }
                            else if (percent > 100.0)
                            {
                                percent = 100.0;
                            }
                        }
                    }

                    _prevLinuxCpuTimes[cpuId] = (idleTime, totalTime);

                    if (cpuId == "cpu")
                    {
                        totalCpu = Math.Round(percent, 1);
                    }
                    else
                    {
                        coreList.Add(Math.Round(percent, 1));
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LINUX_CPU_ERROR] Gagal membaca /proc/stat: {ex.Message}");
            }
        }

        /// <summary>
        /// Membaca kapasitas total memori RAM fisik pada Linux dari file virtual /proc/meminfo
        /// </summary>
        private ulong GetLinuxTotalRamBytes()
        {
            try
            {
                if (File.Exists("/proc/meminfo"))
                {
                    string[] lines = File.ReadAllLines("/proc/meminfo");
                    foreach (string line in lines)
                    {
                        if (line.StartsWith("MemTotal:", StringComparison.OrdinalIgnoreCase))
                        {
                            string[] parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length >= 2)
                            {
                                if (ulong.TryParse(parts[1], out ulong totalKb))
                                {
                                    return totalKb * 1024;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LINUX_RAM_TOTAL_ERROR] {ex.Message}");
            }
            return 0;
        }

        /// <summary>
        /// Mengumpulkan metrik penggunaan RAM saat ini di Linux (/proc/meminfo)
        /// </summary>
        private void CollectLinuxMemoryMetrics(LiveMetricsSnapshot snapshot)
        {
            try
            {
                if (!File.Exists("/proc/meminfo"))
                {
                    snapshot.RamTotalBytes = TotalPhysicalMemoryBytes;
                    snapshot.RamUsagePercent = 0.0;
                    return;
                }

                ulong memTotalKb = 0;
                ulong memAvailableKb = 0;
                ulong memFreeKb = 0;

                string[] lines = File.ReadAllLines("/proc/meminfo");
                foreach (string line in lines)
                {
                    if (line.StartsWith("MemTotal:", StringComparison.OrdinalIgnoreCase))
                    {
                        string[] parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2)
                        {
                            ulong.TryParse(parts[1], out memTotalKb);
                        }
                    }
                    else if (line.StartsWith("MemAvailable:", StringComparison.OrdinalIgnoreCase))
                    {
                        string[] parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2)
                        {
                            ulong.TryParse(parts[1], out memAvailableKb);
                        }
                    }
                    else if (line.StartsWith("MemFree:", StringComparison.OrdinalIgnoreCase))
                    {
                        string[] parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2)
                        {
                            ulong.TryParse(parts[1], out memFreeKb);
                        }
                    }
                }

                ulong totalBytes = memTotalKb * 1024;
                ulong availBytes = 0;
                if (memAvailableKb > 0)
                {
                    availBytes = memAvailableKb * 1024;
                }
                else
                {
                    // Fallback jika MemAvailable belum didukung kernel lama Linux
                    availBytes = memFreeKb * 1024;
                }

                ulong usedBytes = 0;
                if (totalBytes >= availBytes)
                {
                    usedBytes = totalBytes - availBytes;
                }

                double usagePercent = 0.0;
                if (totalBytes > 0)
                {
                    usagePercent = ((double)usedBytes / (double)totalBytes) * 100.0;
                }

                if (usagePercent < 0.0)
                {
                    usagePercent = 0.0;
                }
                else if (usagePercent > 100.0)
                {
                    usagePercent = 100.0;
                }

                snapshot.RamTotalBytes = totalBytes;
                snapshot.RamAvailableBytes = availBytes;
                snapshot.RamUsedBytes = usedBytes;
                snapshot.RamUsagePercent = Math.Round(usagePercent, 1);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LINUX_RAM_ERROR] {ex.Message}");
                snapshot.RamTotalBytes = TotalPhysicalMemoryBytes;
                snapshot.RamUsagePercent = 0.0;
            }
        }

        /// <summary>
        /// Membaca persentase penggunaan disk pada sistem Linux
        /// </summary>
        private double GetLinuxDiskPercent()
        {
            try
            {
                string currentDir = AppContext.BaseDirectory;
                var drive = new DriveInfo(currentDir);
                if (drive.IsReady && drive.TotalSize > 0)
                {
                    long used = drive.TotalSize - drive.AvailableFreeSpace;
                    double pct = ((double)used / (double)drive.TotalSize) * 100.0;
                    return Math.Round(pct, 1);
                }
            }
            catch
            {
                // Abaikan kesalahan pembacaan drive Linux
            }
            return 0.0;
        }

        public void Dispose()
        {
            _cpuTotalCounter?.Dispose();
            foreach (var counter in _cpuCoreCounters)
            {
                counter.Dispose();
            }
            _cpuCoreCounters.Clear();

            _appDiskCounter?.Dispose();
        }
    }
}
