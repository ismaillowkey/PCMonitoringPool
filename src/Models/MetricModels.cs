using System;
using System.Collections.Generic;

namespace PCMonitoringCPURam.Models
{
    /// <summary>
    /// Metrik untuk satu unit disk tertentu (misal: _Total, 0 C:, 1 D:)
    /// </summary>
    public class DiskMetricItem
    {
        public string Name { get; set; } = string.Empty;
        public double UsagePercent { get; set; }
    }

    /// <summary>
    /// Metrik untuk satu adapter jaringan tertentu
    /// </summary>
    public class NetworkMetricItem
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public double UsagePercent { get; set; }
        public double BytesPerSec { get; set; }
    }

    /// <summary>
    /// Model data snapshot metrik sistem real-time
    /// </summary>
    public class LiveMetricsSnapshot
    {
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        // Metrik CPU
        public double CpuTotalPercent { get; set; }
        public List<double> CpuCores { get; set; } = new List<double>();
        public int CoreCount { get; set; }

        // Metrik RAM
        public double RamUsagePercent { get; set; }
        public ulong RamTotalBytes { get; set; }
        public ulong RamUsedBytes { get; set; }
        public ulong RamAvailableBytes { get; set; }

        // Metrik Disk (Khusus disk fisik tempat aplikasi ini berjalan)
        public string AppDiskName { get; set; } = string.Empty;
        public double DiskUsagePercent { get; set; }
        public List<DiskMetricItem> Disks { get; set; } = new List<DiskMetricItem>();

        // Metrik Network (Total dan per-adapter)
        public double NetworkUsagePercent { get; set; }
        public double NetworkBytesPerSec { get; set; }
        public List<NetworkMetricItem> NetworkAdapters { get; set; } = new List<NetworkMetricItem>();

        // Metrik Website / Application Pool IIS (w3wp.exe)
        public List<IisWebsiteMetricItem> IisWebsites { get; set; } = new List<IisWebsiteMetricItem>();
    }

    /// <summary>
    /// Model data histori metrik yang disimpan di SQLite
    /// </summary>
    public class MetricHistoryRecord
    {
        public long Id { get; set; }
        public DateTime Timestamp { get; set; }
        public double CpuTotal { get; set; }
        public List<double> CpuCores { get; set; } = new List<double>();
        public double RamUsagePercent { get; set; }
        public double DiskUsagePercent { get; set; }
        public double NetworkUsagePercent { get; set; }
    }

    /// <summary>
    /// Informasi statis spesifikasi sistem komputer
    /// </summary>
    public class SystemInfo
    {
        public string MachineName { get; set; } = Environment.MachineName;
        public string OsDescription { get; set; } = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
        public int ProcessorCount { get; set; } = Environment.ProcessorCount;
        public ulong TotalMemoryBytes { get; set; }

        /// <summary>
        /// Ambang batas CPU (persen) yang dipakai saat baris log dibuat.
        /// Dikirim ke frontend supaya tampilan bisa membedakan baris "CPU naik"
        /// (>= ambang) dan baris "CPU turun" (&lt; ambang) tanpa duplikasi konfigurasi.
        /// </summary>
        public double CpuHighThresholdPercent { get; set; } = 100.0;
    }

    /// <summary>
    /// Model data pemakaian resource untuk satu program / proses
    /// </summary>
    public class ProcessMetricItem
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public double CpuPercent { get; set; }
        public double MemoryMb { get; set; }
    }

    /// <summary>
    /// Model data pemakaian CPU dan RAM untuk satu website IIS
    /// </summary>
    public class IisWebsiteMetricItem
    {
        public string WebsiteName { get; set; } = string.Empty;
        public int ProcessId { get; set; }
        public double CpuPercent { get; set; }
        public double MemoryMb { get; set; }
    }

    /// <summary>
    /// Respons top 20 program pemakaian CPU dan RAM tertinggi
    /// </summary>
    public class TopProcessesResponse
    {
        public List<ProcessMetricItem> TopCpu { get; set; } = new List<ProcessMetricItem>();
        public List<ProcessMetricItem> TopRam { get; set; } = new List<ProcessMetricItem>();
    }

    /// <summary>
    /// Model data log proses saat CPU mencapai ambang batas tinggi (misal 100%).
    /// Menyimpan tanggal &amp; jam kejadian serta 5 process dengan CPU tertinggi.
    /// Tiap kolom process berisi label berformat "namaproses(cpu%)", contoh: "chrome(12.5%)".
    /// </summary>
    public class CpuHighLogRecord
    {
        public long Id { get; set; }
        public DateTime Timestamp { get; set; }

        // Persentase CPU total pada saat kejadian
        public double CpuTotal { get; set; }

        // 5 process dengan pemakaian CPU tertinggi saat kejadian, format "namaproses(cpu%)"
        public string Process1 { get; set; } = string.Empty;
        public string Process2 { get; set; } = string.Empty;
        public string Process3 { get; set; } = string.Empty;
        public string Process4 { get; set; } = string.Empty;
        public string Process5 { get; set; } = string.Empty;
    }

    /// <summary>
    /// Statistik frekuensi kemunculan satu process pada log CPU tinggi
    /// </summary>
    public class CpuHighProcessStatItem
    {
        // Nama process
        public string ProcessName { get; set; } = string.Empty;

        // Berapa kali process ini occupying peringkat #1
        public int Rank1Count { get; set; }

        // Berapa kali process ini masuk daftar #1 s/d #5
        public int TotalCount { get; set; }

        // Waktu kemunculan terakhir
        public DateTime LastSeen { get; set; }
    }

    /// <summary>
    /// Respons statistik process yang paling sering muncul saat CPU tinggi
    /// </summary>
    public class CpuHighStatsResponse
    {
        // Process yang paling sering menduduki peringkat #1
        public List<CpuHighProcessStatItem> Top1 { get; set; } = new List<CpuHighProcessStatItem>();

        // Process yang paling sering masuk daftar #1 s/d #5
        public List<CpuHighProcessStatItem> TopAny { get; set; } = new List<CpuHighProcessStatItem>();

        // Total jumlah kejadian CPU tinggi yang tercatat
        public int TotalEvents { get; set; }
    }

    /// <summary>
    /// Respons halaman data log CPU tinggi (untuk pagination)
    /// </summary>
    public class PagedCpuHighLogResponse
    {
        public List<CpuHighLogRecord> Items { get; set; } = new List<CpuHighLogRecord>();
        public int TotalCount { get; set; }
        public int Page { get; set; }
        public int PageSize { get; set; }
        public int TotalPages { get; set; }
    }
}
