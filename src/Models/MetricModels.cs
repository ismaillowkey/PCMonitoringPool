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
    /// Menyimpan tanggal & jam kejadian serta 5 nama process dengan CPU tertinggi
    /// </summary>
    public class CpuHighLogRecord
    {
        public long Id { get; set; }
        public DateTime Timestamp { get; set; }

        // Persentase CPU total pada saat kejadian
        public double CpuTotal { get; set; }

        // 5 nama process dengan pemakaian CPU tertinggi saat kejadian
        public string Process1 { get; set; } = string.Empty;
        public string Process2 { get; set; } = string.Empty;
        public string Process3 { get; set; } = string.Empty;
        public string Process4 { get; set; } = string.Empty;
        public string Process5 { get; set; } = string.Empty;
    }
}
