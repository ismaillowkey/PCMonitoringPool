using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using PCMonitoringCPURam.Models;
using PCMonitoringCPURam.Services;

namespace PCMonitoringCPURam.Hubs
{
    /// <summary>
    /// Hub SignalR untuk menyiarkan metrik real-time ke antarmuka web
    /// </summary>
    public class MetricsHub : Hub
    {
        private readonly SystemMetricsCollector _collector;
        private readonly MetricsDatabase _database;

        public MetricsHub(SystemMetricsCollector collector, MetricsDatabase database)
        {
            _collector = collector;
            _database = database;
        }

        public override async Task OnConnectedAsync()
        {
            await base.OnConnectedAsync();

            // Kirimkan snapshot proses CPU dan RAM saat client baru saja terhubung
            try
            {
                var initialTopProcesses = _collector.GetTopProcesses();
                await Clients.Caller.SendAsync("ReceiveTopProcesses", initialTopProcesses);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HUB_TOP_PROC_INIT_ERROR] {ex.Message}");
            }
        }

        /// <summary>
        /// Mengambil top 20 program secara on-demand lewat WebSocket SignalR
        /// </summary>
        public TopProcessesResponse RequestTopProcesses()
        {
            return _collector.GetTopProcesses();
        }

        /// <summary>
        /// Mengambil riwayat metrik secara on-demand lewat WebSocket SignalR
        /// </summary>
        public List<MetricHistoryRecord> RequestHistory(string? range)
        {
            return _database.GetHistoryByPreset(range);
        }
    }
}
