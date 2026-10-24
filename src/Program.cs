using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using PCMonitoringCPURam.Hubs;
using PCMonitoringCPURam.Models;
using PCMonitoringCPURam.Services;

WebApplicationOptions webAppOptions;

// Jika aplikasi dijalankan sebagai Windows Service, pastikan ContentRootPath mengarah ke direktori aplikasi
// (bukan C:\Windows\System32) agar file wwwroot dan appsettings.json terbaca dengan benar
if (WindowsServiceHelpers.IsWindowsService())
{
    webAppOptions = new WebApplicationOptions
    {
        Args = args,
        ContentRootPath = AppContext.BaseDirectory
    };
}
else
{
    webAppOptions = new WebApplicationOptions
    {
        Args = args
    };
}

// Tentukan port HTTP aplikasi:
// 1) port.conf bila ada & valid & port-nya bebas, 2) port default di C# (5055) bila terjadi error
PortSelection portSelection = PortConfiguration.Resolve(webAppOptions.ContentRootPath);

var builder = WebApplication.CreateBuilder(webAppOptions);

// Terapkan port hasil resolusi ke Kestrel (menimpa nilai "Urls" pada appsettings.json bila ada)
builder.WebHost.UseUrls($"http://localhost:{portSelection.Port}");

// Aktifkan dukungan eksekusi sebagai Windows Service
builder.Host.UseWindowsService();

// Daftarkan layanan SignalR untuk transmisi data real-time
builder.Services.AddSignalR();

// Daftarkan komponen pengumpul metrik dan basis data sebagai Singleton
builder.Services.AddSingleton<SystemMetricsCollector>();
builder.Services.AddSingleton<MetricsDatabase>();

// Daftarkan background service monitoring
builder.Services.AddSingleton<MetricsBackgroundService>();
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<MetricsBackgroundService>());

var app = builder.Build();

// Catat port efektif ke log (terlihat di console maupun Windows Event Log saat berjalan sebagai service)
app.Logger.LogInformation("[PORT] {PortInfo}", portSelection.Describe());

// Konfirmasi alamat yang benar-benar diikat Kestrel setelah server aktif
app.Lifetime.ApplicationStarted.Register(() =>
{
    try
    {
        app.Logger.LogInformation("[PORT] Server aktif di {Urls}", string.Join(", ", app.Urls));
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning("[PORT] Gagal membaca alamat server: {Message}", ex.Message);
    }
});

// Aktifkan serving file statis (index.html, JS, CSS) dari wwwroot
app.UseDefaultFiles();
app.UseStaticFiles();

// Pemetaan endpoint SignalR Hub
app.MapHub<MetricsHub>("/hubs/metrics");

app.MapGet("/api/metrics/info", (SystemMetricsCollector collector) =>
{
    var info = new SystemInfo
    {
        MachineName = Environment.MachineName,
        OsDescription = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
        ProcessorCount = collector.CoreCount,
        TotalMemoryBytes = collector.TotalPhysicalMemoryBytes
    };

    return Results.Ok(info);
});

// Endpoint API: Snapshot metrik live saat ini
app.MapGet("/api/metrics/current", (MetricsBackgroundService bgService) =>
{
    var currentMetrics = bgService.LatestSnapshot;
    return Results.Ok(currentMetrics);
});

// Endpoint API: Riwayat metrik dari database SQLite
app.MapGet("/api/metrics/history", (MetricsDatabase db, string? range) =>
{
    try
    {
        var historyData = db.GetHistoryByPreset(range);
        return Results.Ok(historyData);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[API_HISTORY_ERROR] {ex.Message}");
        return Results.Problem(ex.Message);
    }
});

// Endpoint API: Top 20 program CPU high usage dan RAM high usage
app.MapGet("/api/metrics/top-processes", (SystemMetricsCollector collector) =>
{
    try
    {
        var topProcesses = collector.GetTopProcesses();
        return Results.Ok(topProcesses);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[API_TOP_PROCESSES_ERROR] {ex.Message}");
        return Results.Problem(ex.Message);
    }
});

// Endpoint API: Log 5 process CPU tertinggi pada saat CPU mencapai ambang batas tinggi (dengan pagination)
app.MapGet("/api/metrics/cpu-high-logs", (MetricsDatabase db, int? page, int? pageSize) =>
{
    try
    {
        int currentPage = page ?? 1;
        int currentPageSize = pageSize ?? 10;

        if (currentPage <= 0)
        {
            currentPage = 1;
        }

        if (currentPageSize <= 0 || currentPageSize > 100)
        {
            currentPageSize = 10;
        }

        var pagedLogs = db.GetCpuHighLogsPaged(currentPage, currentPageSize);
        return Results.Ok(pagedLogs);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[API_CPU_HIGH_LOGS_ERROR] {ex.Message}");
        return Results.Problem(ex.Message);
    }
});

// Endpoint API: Statistik process yang paling sering muncul saat CPU tinggi
app.MapGet("/api/metrics/cpu-high-stats", (MetricsDatabase db, int? top) =>
{
    try
    {
        int topLimit = top ?? 10;
        if (topLimit <= 0 || topLimit > 50)
        {
            topLimit = 10;
        }

        var stats = db.GetCpuHighStats(topLimit);
        return Results.Ok(stats);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[API_CPU_HIGH_STATS_ERROR] {ex.Message}");
        return Results.Problem(ex.Message);
    }
});

app.Run();
// Updated for Top 20 Processes Monitor
