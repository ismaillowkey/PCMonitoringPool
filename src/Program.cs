using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PCMonitoringCPURam.Hubs;
using PCMonitoringCPURam.Models;
using PCMonitoringCPURam.Services;

var builder = WebApplication.CreateBuilder(args);

// Daftarkan layanan SignalR untuk transmisi data real-time
builder.Services.AddSignalR();

// Daftarkan komponen pengumpul metrik dan basis data sebagai Singleton
builder.Services.AddSingleton<SystemMetricsCollector>();
builder.Services.AddSingleton<MetricsDatabase>();

// Daftarkan background service monitoring
builder.Services.AddSingleton<MetricsBackgroundService>();
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<MetricsBackgroundService>());

var app = builder.Build();

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

app.Run();
// Updated for Top 20 Processes Monitor
