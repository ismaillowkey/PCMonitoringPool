using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PCMonitoringCPURam.Hubs;
using PCMonitoringCPURam.Models;

namespace PCMonitoringCPURam.Services
{
    /// <summary>
    /// Background Hosted Service untuk pengumpulan berkala metrik live (per detik)
    /// dan penyimpanan riwayat ke SQLite (per 1 menit)
    /// </summary>
    public class MetricsBackgroundService : BackgroundService
    {
        private readonly SystemMetricsCollector _collector;
        private readonly MetricsDatabase _database;
        private readonly IHubContext<MetricsHub> _hubContext;
        private readonly ILogger<MetricsBackgroundService> _logger;

        private LiveMetricsSnapshot _latestSnapshot = new LiveMetricsSnapshot();
        private DateTime _lastDatabaseSaveTime = DateTime.MinValue;
        private DateTime _lastTopProcessesBroadcastTime = DateTime.MinValue;
        private DateTime _lastIisSaveTime = DateTime.MinValue;

        // Ambang batas persentase CPU yang memicu auto-save seketika (default 100%)
        private readonly double _cpuSpikeThreshold;

        // Status apakah CPU sedang berada di atas / sama dengan ambang batas.
        // Dipakai untuk mendeteksi "rising edge" (lonjakan baru) sehingga setiap lonjakan
        // CPU memicu 1x penyimpanan langsung, bukan hanya lonjakan pertama dalam satu menit.
        private bool _isCpuSpikeActive = false;

        // Waktu auto-save seketika terakhir (untuk menerapkan cooldown anti-banjir data).
        private DateTime _lastCpuSpikeSaveTime = DateTime.MinValue;

        // Jeda minimum antar auto-save sekitera agar CPU yang berkedip di sekitar ambang batas
        // tidak menyebabkan pembanjiran baris di database.
        private const double CpuSpikeSaveCooldownSeconds = 10.0;

        /// <summary>
        /// Ambang batas persentase CPU yang dipakai untuk auto-save seketika.
        /// </summary>
        public double CpuSpikeThreshold
        {
            get
            {
                return _cpuSpikeThreshold;
            }
        }

        public LiveMetricsSnapshot LatestSnapshot
        {
            get
            {
                return _latestSnapshot;
            }
        }

        public MetricsBackgroundService(
            SystemMetricsCollector collector,
            MetricsDatabase database,
            IHubContext<MetricsHub> hubContext,
            ILogger<MetricsBackgroundService> logger,
            IConfiguration configuration)
        {
            _collector = collector;
            _database = database;
            _hubContext = hubContext;
            _logger = logger;

            // Ambang batas CPU untuk auto-save seketika.
            // Default 100% (sesuai permintaan awal). Dapat dikonfigurasi lewat appsettings.json
            // pada bagian "MetricThresholds:CpuSpikePercent" bila ingin memicu lebih awal, misal 95.
            _cpuSpikeThreshold = ReadCpuSpikeThreshold(configuration);
        }

        /// <summary>
        /// Membaca nilai ambang batas CPU dari konfigurasi aplikasi.
        /// Nilai tidak valid (di luar rentang 1-100) diabaikan dan digunakan nilai default 100.
        /// </summary>
        private static double ReadCpuSpikeThreshold(IConfiguration configuration)
        {
            const double defaultThreshold = 100.0;

            if (configuration == null)
            {
                return defaultThreshold;
            }

            string? rawValue = configuration["MetricThresholds:CpuSpikePercent"];
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                return defaultThreshold;
            }

            if (double.TryParse(rawValue, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double parsedValue))
            {
                if (parsedValue >= 1.0 && parsedValue <= 100.0)
                {
                    return parsedValue;
                }
            }

            return defaultThreshold;
        }

        /// <summary>
        /// Mencatat 5 process dengan pemakaian CPU tertinggi pada saat CPU mencapai ambang batas.
        /// Data disimpan ke tabel CpuHighProcessLog dan ditampilkan pada tabel log di antarmuka web.
        /// </summary>
        private void SaveCpuHighProcessLog(double cpuTotalPercent)
        {
            try
            {
                var topProcesses = _collector.GetTopProcesses();

                // Simpan 5 process dengan CPU tertinggi
                _database.SaveCpuHighLog(cpuTotalPercent, topProcesses.TopCpu);

                string top5Names = string.Join(", ", topProcesses.TopCpu.Take(5).Select(x => x.Name));
                _logger.LogInformation(
                    "Log CPU tinggi disimpan: CPU {CpuPercent}% - 5 process tertinggi: {ProcessNames}",
                    cpuTotalPercent, top5Names);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Gagal menyimpan log 5 process CPU tertinggi.");
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Layanan Monitoring Metrik Sistem telah dimulai.");
            _logger.LogInformation("Ambang batas auto-save CPU seketika: {Threshold}%", _cpuSpikeThreshold);

            // Loop utama background service
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // 1. Kumpulkan snapshot metrik terbaru
                    var snapshot = _collector.CollectMetrics();
                    _latestSnapshot = snapshot;

                    // 2. Siarkan data live ke seluruh browser client via SignalR
                    await _hubContext.Clients.All.SendAsync("ReceiveLiveMetrics", snapshot, stoppingToken);

                    DateTime now = DateTime.UtcNow;

                    // 3. Siarkan Top 20 Program CPU High Usage dan High RAM setiap 10 detik via SignalR
                    double secondsSinceLastTopProcesses = (now - _lastTopProcessesBroadcastTime).TotalSeconds;
                    if (secondsSinceLastTopProcesses >= 10.0 || _lastTopProcessesBroadcastTime == DateTime.MinValue)
                    {
                        var topProcesses = _collector.GetTopProcesses();
                        await _hubContext.Clients.All.SendAsync("ReceiveTopProcesses", topProcesses, stoppingToken);
                        _lastTopProcessesBroadcastTime = now;
                    }

                    // 4. Simpan ke SQLite setiap 1 menit (60 detik) untuk metrik sistem utama.
                    //    Auto-save seketika dilakukan saat CPU melonjak melewati ambang batas
                    //    (default 100%) melalui deteksi rising edge, sehingga setiap lonjakan
                    //    CPU baru pasti tersimpan 1x tanpa menunggu jadwal per menit.
                    double secondsSinceLastSave = (now - _lastDatabaseSaveTime).TotalSeconds;
                    bool isRegularSaveDue = secondsSinceLastSave >= 60.0;

                    // Deteksi rising edge: CPU baru saja melewati ambang batas (dari di bawah ke atas).
                    // Inilah yang memicu auto-save seketika pada setiap lonjakan CPU.
                    bool isCpuSpike = snapshot.CpuTotalPercent >= _cpuSpikeThreshold;
                    bool isCpuSpikeRisingEdge = isCpuSpike && !_isCpuSpikeActive;
                    _isCpuSpikeActive = isCpuSpike;

                    if (isRegularSaveDue)
                    {
                        _database.SaveMetric(snapshot);
                        _lastDatabaseSaveTime = now;
                        _logger.LogInformation("Snapshot metrik berhasil disimpan ke SQLite pada {Time}", now.ToString("yyyy-MM-dd HH:mm:ss"));
                    }
                    else if (isCpuSpikeRisingEdge)
                    {
                        // Terapkan cooldown minimal antar auto-save seketika agar CPU yang
                        // berkedip di sekitar ambang batas tidak membanjiri database.
                        double secondsSinceCpuSpikeSave = (now - _lastCpuSpikeSaveTime).TotalSeconds;
                        bool isCooldownElapsed = _lastCpuSpikeSaveTime == DateTime.MinValue
                                                || secondsSinceCpuSpikeSave >= CpuSpikeSaveCooldownSeconds;

                        if (isCooldownElapsed)
                        {
                            // Simpan 1x secara langsung saat CPU mencapai ambang batas.
                            // Syarat "else if" memastikan tidak terjadi dobel-save apabila
                            // jadwal rutin per menit jatuh pada detik yang sama.
                            _database.SaveMetric(snapshot);
                            _lastCpuSpikeSaveTime = now;
                            _logger.LogInformation(
                                "Auto-save snapshot metrik (CPU {CpuPercent}% mencapai ambang {Threshold}%) berhasil disimpan ke SQLite pada {Time}",
                                snapshot.CpuTotalPercent, _cpuSpikeThreshold, now.ToString("yyyy-MM-dd HH:mm:ss"));

                            // Catat 5 process dengan CPU tertinggi pada saat kejadian CPU tinggi
                            SaveCpuHighProcessLog(snapshot.CpuTotalPercent);
                        }
                    }

                    // 5. Simpan ke SQLite setiap 20 detik untuk data metrik website IIS (retensi 3 hari)
                    double secondsSinceLastIisSave = (now - _lastIisSaveTime).TotalSeconds;
                    if (secondsSinceLastIisSave >= 20.0 || _lastIisSaveTime == DateTime.MinValue)
                    {
                        if (snapshot.IisWebsites != null && snapshot.IisWebsites.Count > 0)
                        {
                            _database.SaveIisMetrics(snapshot.IisWebsites);
                        }
                        _lastIisSaveTime = now;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Terjadi kesalahan saat memproses data metrik sistem.");
                }

                // Beri jeda 1 detik sebelum pembacaan live berikutnya
                try
                {
                    await Task.Delay(1000, stoppingToken);
                }
                catch (TaskCanceledException)
                {
                    // Task dibatalkan saat aplikasi berhenti
                    break;
                }
            }

            _logger.LogInformation("Layanan Monitoring Metrik Sistem dihentikan.");
        }
    }
}
