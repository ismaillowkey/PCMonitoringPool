using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
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
            ILogger<MetricsBackgroundService> logger)
        {
            _collector = collector;
            _database = database;
            _hubContext = hubContext;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Layanan Monitoring Metrik Sistem telah dimulai.");

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

                    // 4. Simpan ke SQLite setiap 1 menit (60 detik) untuk metrik sistem utama
                    double secondsSinceLastSave = (now - _lastDatabaseSaveTime).TotalSeconds;

                    if (secondsSinceLastSave >= 60.0)
                    {
                        _database.SaveMetric(snapshot);
                        _lastDatabaseSaveTime = now;
                        _logger.LogInformation("Snapshot metrik berhasil disimpan ke SQLite pada {Time}", now.ToString("yyyy-MM-dd HH:mm:ss"));
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
