using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PCMonitoringCPURam.Models;

namespace PCMonitoringCPURam.Services
{
    /// <summary>
    /// Layanan manajemen basis data SQLite untuk menyimpan dan membaca riwayat metrik sistem
    /// </summary>
    public class MetricsDatabase
    {
        private static readonly object _dbLock = new object();
        private readonly string _connectionString;

        public MetricsDatabase(string? dbPath = null)
        {
            string finalDbPath = "monitoring.db";
            if (!string.IsNullOrWhiteSpace(dbPath))
            {
                finalDbPath = dbPath;
            }

            // Pastikan path selalu mengarah ke direktori aplikasi (bukan System32 saat di IIS)
            if (!Path.IsPathRooted(finalDbPath))
            {
                finalDbPath = Path.Combine(AppContext.BaseDirectory, finalDbPath);
            }

            _connectionString = $"Data Source={finalDbPath};";
            InitializeDatabase();
        }

        /// <summary>
        /// Membuat tabel dan indeks bila belum tersedia
        /// </summary>
        private void InitializeDatabase()
        {
            lock (_dbLock)
            {
                using (var connection = new SqliteConnection(_connectionString))
                {
                    connection.Open();

                    string createTableQuery = @"
                        PRAGMA journal_mode = WAL;
                        PRAGMA busy_timeout = 5000;
                        CREATE TABLE IF NOT EXISTS MetricsHistory (
                            Id INTEGER PRIMARY KEY AUTOINCREMENT,
                            Timestamp TEXT NOT NULL,
                            CpuTotal REAL NOT NULL,
                            CpuCoresJson TEXT NOT NULL,
                            RamUsagePercent REAL NOT NULL,
                            DiskUsagePercent REAL NOT NULL,
                            NetworkUsagePercent REAL NOT NULL
                        );
                        CREATE INDEX IF NOT EXISTS idx_metrics_timestamp ON MetricsHistory(Timestamp);

                        CREATE TABLE IF NOT EXISTS IisMetricsHistory (
                            Id INTEGER PRIMARY KEY AUTOINCREMENT,
                            Timestamp TEXT NOT NULL,
                            WebsiteName TEXT NOT NULL,
                            ProcessId INTEGER NOT NULL,
                            CpuPercent REAL NOT NULL,
                            MemoryMb REAL NOT NULL
                        );
                        CREATE INDEX IF NOT EXISTS idx_iis_timestamp ON IisMetricsHistory(Timestamp);
                        CREATE INDEX IF NOT EXISTS idx_iis_website ON IisMetricsHistory(WebsiteName);

                        CREATE TABLE IF NOT EXISTS CpuHighProcessLog (
                            Id INTEGER PRIMARY KEY AUTOINCREMENT,
                            Timestamp TEXT NOT NULL,
                            CpuTotal REAL NOT NULL,
                            Process1 TEXT NOT NULL DEFAULT '',
                            Process2 TEXT NOT NULL DEFAULT '',
                            Process3 TEXT NOT NULL DEFAULT '',
                            Process4 TEXT NOT NULL DEFAULT '',
                            Process5 TEXT NOT NULL DEFAULT ''
                        );
                        CREATE INDEX IF NOT EXISTS idx_cpu_high_timestamp ON CpuHighProcessLog(Timestamp);
                    ";

                    using (var command = new SqliteCommand(createTableQuery, connection))
                    {
                        command.ExecuteNonQuery();
                    }
                }
            }
        }

        /// <summary>
        /// Menyimpan data metrik per menit ke SQLite dan otomatis membersihkan data yang lebih tua dari 7 hari
        /// </summary>
        public void SaveMetric(LiveMetricsSnapshot snapshot)
        {
            lock (_dbLock)
            {
                using (var connection = new SqliteConnection(_connectionString))
                {
                    connection.Open();

                // 1. Simpan snapshot metrik ke tabel
                string insertQuery = @"
                    INSERT INTO MetricsHistory (
                        Timestamp, 
                        CpuTotal, 
                        CpuCoresJson, 
                        RamUsagePercent, 
                        DiskUsagePercent, 
                        NetworkUsagePercent
                    ) VALUES (
                        @Timestamp, 
                        @CpuTotal, 
                        @CpuCoresJson, 
                        @RamUsagePercent, 
                        @DiskUsagePercent, 
                        @NetworkUsagePercent
                    );
                ";

                using (var command = new SqliteCommand(insertQuery, connection))
                {
                    string coresJson = JsonSerializer.Serialize(snapshot.CpuCores);

                    command.Parameters.AddWithValue("@Timestamp", snapshot.Timestamp.ToString("o"));
                    command.Parameters.AddWithValue("@CpuTotal", snapshot.CpuTotalPercent);
                    command.Parameters.AddWithValue("@CpuCoresJson", coresJson);
                    command.Parameters.AddWithValue("@RamUsagePercent", snapshot.RamUsagePercent);
                    command.Parameters.AddWithValue("@DiskUsagePercent", snapshot.DiskUsagePercent);
                    command.Parameters.AddWithValue("@NetworkUsagePercent", snapshot.NetworkUsagePercent);

                    command.ExecuteNonQuery();
                }

                // 2. Pembersihan data: Maksimal simpan 7 hari ke belakang
                DateTime cutoffDate = DateTime.UtcNow.AddDays(-7);
                string deleteQuery = "DELETE FROM MetricsHistory WHERE Timestamp < @CutoffDate;";

                using (var deleteCommand = new SqliteCommand(deleteQuery, connection))
                {
                    deleteCommand.Parameters.AddWithValue("@CutoffDate", cutoffDate.ToString("o"));
                    deleteCommand.ExecuteNonQuery();
                }
            }
        }
    }

        /// <summary>
        /// Menyimpan log data metrik per website IIS setiap 20 detik
        /// dan membersihkan otomatis data yang lebih tua dari 3 hari (retensi 3 hari)
        /// </summary>
        public void SaveIisMetrics(List<IisWebsiteMetricItem> items)
        {
            if (items == null)
            {
                return;
            }

            if (items.Count == 0)
            {
                return;
            }

            lock (_dbLock)
            {
                try
                {
                    using (var connection = new SqliteConnection(_connectionString))
                    {
                        connection.Open();

                        using (var transaction = connection.BeginTransaction())
                        {
                            // 1. Masukkan seluruh data pemakaian website IIS
                            string insertQuery = @"
                                INSERT INTO IisMetricsHistory (
                                    Timestamp, 
                                    WebsiteName, 
                                    ProcessId, 
                                    CpuPercent, 
                                    MemoryMb
                                ) VALUES (
                                    @Timestamp, 
                                    @WebsiteName, 
                                    @ProcessId, 
                                    @CpuPercent, 
                                    @MemoryMb
                                );
                            ";

                            string nowIso = DateTime.UtcNow.ToString("o");

                            using (var command = new SqliteCommand(insertQuery, connection, transaction))
                            {
                                var pTimestamp = command.Parameters.Add("@Timestamp", SqliteType.Text);
                                var pWebsiteName = command.Parameters.Add("@WebsiteName", SqliteType.Text);
                                var pProcessId = command.Parameters.Add("@ProcessId", SqliteType.Integer);
                                var pCpuPercent = command.Parameters.Add("@CpuPercent", SqliteType.Real);
                                var pMemoryMb = command.Parameters.Add("@MemoryMb", SqliteType.Real);

                                for (int i = 0; i < items.Count; i++)
                                {
                                    var item = items[i];
                                    pTimestamp.Value = nowIso;
                                    pWebsiteName.Value = item.WebsiteName;
                                    pProcessId.Value = item.ProcessId;
                                    pCpuPercent.Value = item.CpuPercent;
                                    pMemoryMb.Value = item.MemoryMb;

                                    command.ExecuteNonQuery();
                                }
                            }

                            // 2. Pembersihan otomatis log: Retensi tepat 3 hari ke belakang sesuai permintaan
                            DateTime cutoffDate = DateTime.UtcNow.AddDays(-3);
                            string deleteQuery = "DELETE FROM IisMetricsHistory WHERE Timestamp < @CutoffDate;";

                            using (var deleteCommand = new SqliteCommand(deleteQuery, connection, transaction))
                            {
                                deleteCommand.Parameters.AddWithValue("@CutoffDate", cutoffDate.ToString("o"));
                                deleteCommand.ExecuteNonQuery();
                            }

                            transaction.Commit();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[IIS_METRICS_DB_ERROR] Gagal menyimpan log IIS: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Menyimpan log 5 process dengan CPU tertinggi pada saat CPU mencapai ambang batas tinggi
        /// dan otomatis membersihkan data yang lebih tua dari 7 hari
        /// </summary>
        public void SaveCpuHighLog(double cpuTotal, List<ProcessMetricItem> topProcesses)
        {
            if (topProcesses == null || topProcesses.Count == 0)
            {
                return;
            }

            // Ambil maksimal 5 process teratas
            var top5 = topProcesses.Take(5).ToList();

            // Siapkan nama process, posisi yang kosong diisi string kosong
            string[] processNames = new string[5];
            for (int i = 0; i < processNames.Length; i++)
            {
                if (i < top5.Count)
                {
                    processNames[i] = top5[i].Name ?? string.Empty;
                }
                else
                {
                    processNames[i] = string.Empty;
                }
            }

            lock (_dbLock)
            {
                try
                {
                    using (var connection = new SqliteConnection(_connectionString))
                    {
                        connection.Open();

                        string insertQuery = @"
                            INSERT INTO CpuHighProcessLog (
                                Timestamp, CpuTotal,
                                Process1, Process2, Process3, Process4, Process5
                            ) VALUES (
                                @Timestamp, @CpuTotal,
                                @Process1, @Process2, @Process3, @Process4, @Process5
                            );
                        ";

                        using (var command = new SqliteCommand(insertQuery, connection))
                        {
                            command.Parameters.AddWithValue("@Timestamp", DateTime.UtcNow.ToString("o"));
                            command.Parameters.AddWithValue("@CpuTotal", cpuTotal);
                            command.Parameters.AddWithValue("@Process1", processNames[0]);
                            command.Parameters.AddWithValue("@Process2", processNames[1]);
                            command.Parameters.AddWithValue("@Process3", processNames[2]);
                            command.Parameters.AddWithValue("@Process4", processNames[3]);
                            command.Parameters.AddWithValue("@Process5", processNames[4]);

                            command.ExecuteNonQuery();
                        }

                        // Pembersihan otomatis log: retensi 7 hari ke belakang
                        DateTime cutoffDate = DateTime.UtcNow.AddDays(-7);
                        string deleteQuery = "DELETE FROM CpuHighProcessLog WHERE Timestamp < @CutoffDate;";

                        using (var deleteCommand = new SqliteCommand(deleteQuery, connection))
                        {
                            deleteCommand.Parameters.AddWithValue("@CutoffDate", cutoffDate.ToString("o"));
                            deleteCommand.ExecuteNonQuery();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[CPU_HIGH_LOG_DB_ERROR] Gagal menyimpan log CPU tinggi: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Mengambil daftar log proses CPU tinggi terbaru (terbaru di paling atas)
        /// </summary>
        public List<CpuHighLogRecord> GetCpuHighLogs(int limit = 100)
        {
            var records = new List<CpuHighLogRecord>();

            if (limit <= 0)
            {
                limit = 100;
            }

            lock (_dbLock)
            {
                using (var connection = new SqliteConnection(_connectionString))
                {
                    connection.Open();

                    string selectQuery = @"
                        SELECT Id, Timestamp, CpuTotal, Process1, Process2, Process3, Process4, Process5
                        FROM CpuHighProcessLog
                        ORDER BY Id DESC
                        LIMIT @Limit;
                    ";

                    using (var command = new SqliteCommand(selectQuery, connection))
                    {
                        command.Parameters.AddWithValue("@Limit", limit);

                        using (var reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                var record = new CpuHighLogRecord();
                                record.Id = reader.GetInt64(0);

                                string timestampString = reader.GetString(1);
                                DateTime parsedTimestamp;
                                if (DateTime.TryParse(timestampString, out parsedTimestamp))
                                {
                                    record.Timestamp = parsedTimestamp;
                                }
                                else
                                {
                                    record.Timestamp = DateTime.UtcNow;
                                }

                                record.CpuTotal = reader.GetDouble(2);
                                record.Process1 = reader.GetString(3);
                                record.Process2 = reader.GetString(4);
                                record.Process3 = reader.GetString(5);
                                record.Process4 = reader.GetString(6);
                                record.Process5 = reader.GetString(7);

                                records.Add(record);
                            }
                        }
                    }
                }
            }

            return records;
        }

        /// <summary>
        /// Mengambil riwayat metrik berdasarkan rentang waktu tertentu
        /// </summary>
        public List<MetricHistoryRecord> GetHistory(DateTime fromTime, DateTime toTime)
        {
            var records = new List<MetricHistoryRecord>();

            lock (_dbLock)
            {
                using (var connection = new SqliteConnection(_connectionString))
                {
                    connection.Open();

                string selectQuery = @"
                    SELECT Id, Timestamp, CpuTotal, CpuCoresJson, RamUsagePercent, DiskUsagePercent, NetworkUsagePercent
                    FROM MetricsHistory
                    WHERE Timestamp >= @FromTime AND Timestamp <= @ToTime
                    ORDER BY Timestamp ASC;
                ";

                using (var command = new SqliteCommand(selectQuery, connection))
                {
                    command.Parameters.AddWithValue("@FromTime", fromTime.ToString("o"));
                    command.Parameters.AddWithValue("@ToTime", toTime.ToString("o"));

                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var record = new MetricHistoryRecord();
                            record.Id = reader.GetInt64(0);

                            string timestampString = reader.GetString(1);
                            DateTime parsedTimestamp;
                            if (DateTime.TryParse(timestampString, out parsedTimestamp))
                            {
                                record.Timestamp = parsedTimestamp;
                            }
                            else
                            {
                                record.Timestamp = DateTime.UtcNow;
                            }

                            record.CpuTotal = reader.GetDouble(2);

                            string coresJson = reader.GetString(3);
                            if (!string.IsNullOrEmpty(coresJson))
                            {
                                try
                                {
                                    var cores = JsonSerializer.Deserialize<List<double>>(coresJson);
                                    if (cores != null)
                                    {
                                        record.CpuCores = cores;
                                    }
                                }
                                catch
                                {
                                    record.CpuCores = new List<double>();
                                }
                            }

                            record.RamUsagePercent = reader.GetDouble(4);
                            record.DiskUsagePercent = reader.GetDouble(5);
                            record.NetworkUsagePercent = reader.GetDouble(6);

                            records.Add(record);
                        }
                    }
                }
            }
        }

        return records;
    }

        /// <summary>
        /// Mengambil histori berdasarkan filter rentang preset (1h, 6h, 24h, 7d)
        /// </summary>
        public List<MetricHistoryRecord> GetHistoryByPreset(string? preset)
        {
            DateTime nowUtc = DateTime.UtcNow;
            DateTime fromUtc;

            // Evaluasi rentang waktu secara eksplisit tanpa operator ternary bersarang
            if (string.Equals(preset, "1h", StringComparison.OrdinalIgnoreCase))
            {
                fromUtc = nowUtc.AddHours(-1);
            }
            else if (string.Equals(preset, "6h", StringComparison.OrdinalIgnoreCase))
            {
                fromUtc = nowUtc.AddHours(-6);
            }
            else if (string.Equals(preset, "24h", StringComparison.OrdinalIgnoreCase))
            {
                fromUtc = nowUtc.AddHours(-24);
            }
            else if (string.Equals(preset, "3d", StringComparison.OrdinalIgnoreCase))
            {
                fromUtc = nowUtc.AddDays(-3);
            }
            else if (string.Equals(preset, "7d", StringComparison.OrdinalIgnoreCase))
            {
                fromUtc = nowUtc.AddDays(-7);
            }
            else
            {
                // Default bila parameter tidak dikenali atau kosong: ambil 24 jam terakhir
                fromUtc = nowUtc.AddHours(-24);
            }

            return GetHistory(fromUtc, nowUtc);
        }
    }
}
