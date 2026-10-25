using System;
using System.Collections.Generic;
using System.Globalization;
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

        // Batas maksimal jumlah baris pada tabel log CPU tinggi.
        // Data terlama otomatis dihapus setiap kali ada insert baru.
        private const int MaxCpuHighLogRows = 100;

        // Ambang batas persentase CPU yang membedakan baris "CPU naik" (>= ambang)
        // dengan baris "CPU turun" (< ambang) pada filter tabel log CPU tinggi.
        // Nilai ini WAJIB sama dengan ambang yang dipakai MetricsBackgroundService saat
        // memutuskan kapan baris log dibuat. Diisi dari konfigurasi "MetricThresholds:CpuSpikePercent"
        // pada saat aplikasi start, sehingga filter "high" dan "low" selalu konsisten.
        public double CpuHighThresholdPercent { get; set; } = 100.0;

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

                        CREATE TABLE IF NOT EXISTS CpuHighProcessStats (
                            ProcessName TEXT NOT NULL PRIMARY KEY,
                            Rank1Count INTEGER NOT NULL DEFAULT 0,
                            TotalCount INTEGER NOT NULL DEFAULT 0,
                            LastSeen TEXT NOT NULL
                        );
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
        /// dan otomatis membersihkan data yang lebih tua dari 7 hari.
        /// Setiap kolom Process1 s.d. Process5 berisi label berformat "namaproses(cpu%)",
        /// contoh: "chrome(12.5%)".
        /// Method ini dipakai untuk dua jenis baris pada tabel yang sama:
        /// baris "CPU naik" (CPU mencapai ambang batas) dan baris "CPU turun" (CPU turun
        /// di bawah ambang batas).
        /// </summary>
        /// <param name="cpuTotal">Persentase CPU total saat baris dicatat.</param>
        /// <param name="topProcesses">Daftar process dengan pemakaian CPU tertinggi.</param>
        /// <param name="updateProcessStats">
        /// Bernilai <c>true</c> untuk baris "CPU naik" agar statistik frekuensi kemunculan process
        /// ikut diperbarui. Bernilai <c>false</c> untuk baris "CPU turun" agar satu kejadian
        /// tidak terhitung dua kali pada statistik.
        /// </param>
        public void SaveCpuHighLog(double cpuTotal, List<ProcessMetricItem> topProcesses, bool updateProcessStats = true)
        {
            if (topProcesses == null || topProcesses.Count == 0)
            {
                return;
            }

            // Ambil maksimal 5 process teratas
            var top5 = topProcesses.Take(5).ToList();

            // Siapkan label process berformat "namaproses(cpu%)" untuk kolom Process1 s.d. Process5,
            // posisi yang kosong diisi string kosong
            string[] processLabels = new string[5];

            // Nama process tanpa embel-embel persentase, dipakai khusus untuk statistik frekuensi kemunculan
            string[] processNames = new string[5];

            for (int i = 0; i < processLabels.Length; i++)
            {
                if (i < top5.Count)
                {
                    processNames[i] = top5[i].Name ?? string.Empty;
                    processLabels[i] = FormatProcessLabel(processNames[i], top5[i].CpuPercent);
                }
                else
                {
                    processNames[i] = string.Empty;
                    processLabels[i] = string.Empty;
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
                            command.Parameters.AddWithValue("@Process1", processLabels[0]);
                            command.Parameters.AddWithValue("@Process2", processLabels[1]);
                            command.Parameters.AddWithValue("@Process3", processLabels[2]);
                            command.Parameters.AddWithValue("@Process4", processLabels[3]);
                            command.Parameters.AddWithValue("@Process5", processLabels[4]);

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

                        // Batasi jumlah baris log maksimal 100 baris (data terlama dihapus)
                        string trimQuery = @"
                            DELETE FROM CpuHighProcessLog
                            WHERE Id NOT IN (
                                SELECT Id FROM CpuHighProcessLog
                                ORDER BY Id DESC
                                LIMIT @MaxRows
                            );
                        ";

                        using (var trimCommand = new SqliteCommand(trimQuery, connection))
                        {
                            trimCommand.Parameters.AddWithValue("@MaxRows", MaxCpuHighLogRows);
                            trimCommand.ExecuteNonQuery();
                        }

                        // Akumulasi statistik frekuensi kemunculan tiap process.
                        // Baris "CPU turun" dilewati agar statistik tidak dobel untuk satu kejadian.
                        if (updateProcessStats)
                        {
                            UpdateProcessStats(connection, processNames, DateTime.UtcNow);
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
        /// Membentuk label process untuk log CPU tinggi dengan format "namaproses(cpu%)",
        /// contoh: "chrome(12.5%)". Nama process kosong dikembalikan sebagai string kosong.
        /// Persentase selalu memakai satu angka desimal dan pemisah desimal titik (invariant culture)
        /// agar tampilan konsisten di semua konfigurasi regional Windows.
        /// </summary>
        private static string FormatProcessLabel(string processName, double cpuPercent)
        {
            if (string.IsNullOrWhiteSpace(processName))
            {
                return string.Empty;
            }

            double safePercent = cpuPercent;
            if (safePercent < 0.0)
            {
                safePercent = 0.0;
            }
            else if (safePercent > 100.0)
            {
                safePercent = 100.0;
            }

            string percentText = safePercent.ToString("0.0", CultureInfo.InvariantCulture);
            return processName + "(" + percentText + "%)";
        }

        /// <summary>
        /// Memperbarui statistik frekuensi kemunculan process pada log CPU tinggi.
        /// Process di peringkat #1 menambah Rank1Count, seluruh process #1 s/d #5 menambah TotalCount.
        /// </summary>
        private static void UpdateProcessStats(SqliteConnection connection, string[] processNames, DateTime timestamp)
        {
            string nowIso = timestamp.ToString("o");

            // Satu kejadian bisa memuat beberapa instance process yang sama
            // (misalnya 3x chrome.exe). Statistik harus dihitung per NAMA unik per kejadian,
            // supaya TotalCount tidak menggelembung dan posisi #1 tetap dihitung sekali.
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < processNames.Length; i++)
            {
                string processName = processNames[i];
                if (string.IsNullOrWhiteSpace(processName))
                {
                    continue;
                }

                if (!seenNames.Add(processName))
                {
                    // Nama ini sudah dihitung pada peringkat yang lebih tinggi di kejadian yang sama
                    continue;
                }

                int rank1Increment = (i == 0) ? 1 : 0;

                string upsertQuery = @"
                    INSERT INTO CpuHighProcessStats (ProcessName, Rank1Count, TotalCount, LastSeen)
                    VALUES (@ProcessName, @Rank1Increment, 1, @LastSeen)
                    ON CONFLICT(ProcessName) DO UPDATE SET
                        Rank1Count = Rank1Count + @Rank1Increment,
                        TotalCount = TotalCount + 1,
                        LastSeen = @LastSeen;
                ";

                using (var command = new SqliteCommand(upsertQuery, connection))
                {
                    command.Parameters.AddWithValue("@ProcessName", processName);
                    command.Parameters.AddWithValue("@Rank1Increment", rank1Increment);
                    command.Parameters.AddWithValue("@LastSeen", nowIso);
                    command.ExecuteNonQuery();
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
        /// Mengambil satu halaman data log CPU tinggi untuk ditampilkan dengan pagination.
        /// </summary>
        /// <param name="page">Nomor halaman yang diminta (mulai dari 1).</param>
        /// <param name="pageSize">Jumlah baris per halaman.</param>
        /// <param name="cpuFilter">
        /// Filter baris log: "all" untuk semua baris, "high" untuk baris saat CPU mencapai 100%,
        /// "low" untuk baris saat CPU turun di bawah 100%.
        /// </param>
        public PagedCpuHighLogResponse GetCpuHighLogsPaged(int page = 1, int pageSize = 10, string cpuFilter = "all")
        {
            var response = new PagedCpuHighLogResponse();

            if (page <= 0)
            {
                page = 1;
            }

            if (pageSize <= 0)
            {
                pageSize = 10;
            }

            // Normalisasi filter tabel log: hanya "high" dan "low" yang dikenali.
            // Nilai lain (termasuk null) diperlakukan sebagai "all" sehingga semua baris tampil.
            string normalizedFilter = "all";
            if (string.Equals(cpuFilter, "high", StringComparison.OrdinalIgnoreCase))
            {
                normalizedFilter = "high";
            }
            else if (string.Equals(cpuFilter, "low", StringComparison.OrdinalIgnoreCase))
            {
                normalizedFilter = "low";
            }

            // Baris "CPU naik" tersimpan dengan CpuTotal >= 100, sedangkan baris "CPU turun"
            // tersimpan dengan CpuTotal < 100. Filter cukup memakai batas tersebut, sehingga
            // tidak diperlukan kolom penanda tambahan pada tabel.
            bool isFilterActive = normalizedFilter != "all";
            string whereClause = string.Empty;
            if (normalizedFilter == "high")
            {
                whereClause = "WHERE CpuTotal >= @CpuBoundary";
            }
            else if (normalizedFilter == "low")
            {
                whereClause = "WHERE CpuTotal < @CpuBoundary";
            }

            lock (_dbLock)
            {
                using (var connection = new SqliteConnection(_connectionString))
                {
                    connection.Open();

                    // Hitung total baris sesuai filter yang sedang dipilih
                    int totalCount = 0;
                    string countQuery = "SELECT COUNT(*) FROM CpuHighProcessLog " + whereClause + ";";
                    using (var countCommand = new SqliteCommand(countQuery, connection))
                    {
                        if (isFilterActive)
                        {
                            countCommand.Parameters.AddWithValue("@CpuBoundary", CpuHighThresholdPercent);
                        }

                        object? result = countCommand.ExecuteScalar();
                        if (result != null)
                        {
                            totalCount = Convert.ToInt32(result);
                        }
                    }

                    int totalPages = totalCount / pageSize;
                    if (totalCount % pageSize != 0)
                    {
                        totalPages++;
                    }

                    // Jika halaman diminta melewati jumlah halaman, ambil halaman terakhir
                    if (totalPages > 0 && page > totalPages)
                    {
                        page = totalPages;
                    }

                    int offset = (page - 1) * pageSize;

                    // Kolom NextTimestamp diambil lewat subquery ke baris yang lebih baru (Id lebih besar).
                    // Nilainya dipakai untuk menghitung berapa lama baris ini bertahan, yaitu sejak
                    // waktu baris ini sampai kejadian berikutnya. Baris paling baru tidak punya
                    // kejadian berikutnya, sehingga durasinya dihitung sampai waktu sekarang.
                    // Subquery tetap membaca seluruh tabel (tanpa filter), supaya durasi tidak
                    // berubah-ubah saat filter "high"/"low" sedang aktif.
                    string selectQuery = @"
                        SELECT Id, Timestamp, CpuTotal, Process1, Process2, Process3, Process4, Process5,
                               (SELECT n.Timestamp FROM CpuHighProcessLog n
                                WHERE n.Id > CpuHighProcessLog.Id
                                ORDER BY n.Id ASC LIMIT 1) AS NextTimestamp
                        FROM CpuHighProcessLog
                        " + whereClause + @"
                        ORDER BY Id DESC
                        LIMIT @Limit OFFSET @Offset;
                    ";

                    var items = new List<CpuHighLogRecord>();
                    using (var command = new SqliteCommand(selectQuery, connection))
                    {
                        command.Parameters.AddWithValue("@Limit", pageSize);
                        command.Parameters.AddWithValue("@Offset", offset);

                        if (isFilterActive)
                        {
                            command.Parameters.AddWithValue("@CpuBoundary", CpuHighThresholdPercent);
                        }

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

                                // Kolom ke-9 adalah timestamp kejadian berikutnya (bisa NULL).
                                // Bila NULL berarti baris ini yang paling baru, sehingga durasinya
                                // dihitung sampai waktu sekarang dan ditandai sedang berjalan.
                                DateTime nextTimestamp;
                                record.IsLatestEvent = false;
                                if (!reader.IsDBNull(8))
                                {
                                    string nextTimestampString = reader.GetString(8);
                                    DateTime parsedNextTimestamp;
                                    if (DateTime.TryParse(nextTimestampString, out parsedNextTimestamp))
                                    {
                                        nextTimestamp = parsedNextTimestamp;
                                    }
                                    else
                                    {
                                        nextTimestamp = DateTime.UtcNow;
                                    }
                                }
                                else
                                {
                                    nextTimestamp = DateTime.UtcNow;
                                    record.IsLatestEvent = true;
                                }

                                TimeSpan duration = nextTimestamp - record.Timestamp;
                                if (duration.TotalMinutes < 0)
                                {
                                    duration = TimeSpan.Zero;
                                }

                                record.DurationMinutes = duration.TotalMinutes;

                                items.Add(record);
                            }
                        }
                    }

                    response.Items = items;
                    response.TotalCount = totalCount;
                    response.Page = page;
                    response.PageSize = pageSize;
                    response.TotalPages = totalPages;
                }
            }

            return response;
        }

        /// <summary>
        /// Mengambil statistik process yang paling sering muncul saat CPU tinggi.
        /// Top1 = process yang sering menduduki peringkat #1
        /// TopAny = process yang sering masuk daftar #1 s/d #5
        /// </summary>
        public CpuHighStatsResponse GetCpuHighStats(int topLimit = 10)
        {
            var response = new CpuHighStatsResponse();

            if (topLimit <= 0)
            {
                topLimit = 10;
            }

            lock (_dbLock)
            {
                using (var connection = new SqliteConnection(_connectionString))
                {
                    connection.Open();

                    // Ambil seluruh statistik untuk dihitung di memori
                    var allStats = new List<CpuHighProcessStatItem>();
                    string selectQuery = @"
                        SELECT ProcessName, Rank1Count, TotalCount, LastSeen
                        FROM CpuHighProcessStats;
                    ";

                    using (var command = new SqliteCommand(selectQuery, connection))
                    {
                        using (var reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                var stat = new CpuHighProcessStatItem();
                                stat.ProcessName = reader.GetString(0);
                                stat.Rank1Count = reader.GetInt32(1);
                                stat.TotalCount = reader.GetInt32(2);

                                string lastSeenString = reader.GetString(3);
                                DateTime parsedLastSeen;
                                if (DateTime.TryParse(lastSeenString, out parsedLastSeen))
                                {
                                    stat.LastSeen = parsedLastSeen;
                                }
                                else
                                {
                                    stat.LastSeen = DateTime.UtcNow;
                                }

                                allStats.Add(stat);
                            }
                        }
                    }

                    // Total kejadian CPU tinggi yang tercatat.
                    // Tabel menyimpan 2 baris per kejadian (1 baris "CPU naik" + 1 baris "CPU turun"),
                    // sehingga yang dihitung hanya baris "CPU naik" agar angka kejadian tidak dobel.
                    using (var countCommand = new SqliteCommand(
                        "SELECT COUNT(*) FROM CpuHighProcessLog WHERE CpuTotal >= @CpuBoundary;", connection))
                    {
                        countCommand.Parameters.AddWithValue("@CpuBoundary", CpuHighThresholdPercent);
                        object? result = countCommand.ExecuteScalar();
                        if (result != null)
                        {
                            response.TotalEvents = Convert.ToInt32(result);
                        }
                    }

                    // Top 1: process yang paling sering menduduki peringkat #1
                    response.Top1 = allStats
                        .Where(x => x.Rank1Count > 0)
                        .OrderByDescending(x => x.Rank1Count)
                        .ThenByDescending(x => x.TotalCount)
                        .ThenBy(x => x.ProcessName)
                        .Take(topLimit)
                        .ToList();

                    // Top Any: process yang paling sering masuk daftar #1 s/d #5
                    response.TopAny = allStats
                        .OrderByDescending(x => x.TotalCount)
                        .ThenByDescending(x => x.Rank1Count)
                        .ThenBy(x => x.ProcessName)
                        .Take(topLimit)
                        .ToList();
                }
            }

            return response;
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
