using Azure.Core;
using Azure.Messaging.ServiceBus.Administration;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using SwingTrader.Api.Contracts;
using SwingTrader.Core.Interfaces;

namespace SwingTrader.Api.Services;

// Composes the admin monitoring dashboard from three sources: the database
// (always available), Service Bus queue runtime properties, and App Insights
// logs. Each external source is isolated in its own try/catch and reports its
// own availability, so a not-yet-granted managed-identity role (Monitoring
// Reader / Service Bus Data Owner) leaves that one card empty rather than
// 500-ing the whole page.
public class MonitoringService(
    IMonitoringRepository monitoringRepo,
    IAdminRepository adminRepo,
    IServiceProvider services,
    IConfiguration config,
    ILogger<MonitoringService> logger)
{
    // A worker hasn't checked in for this long => flagged stale. The most
    // infrequent worker (weekly watchlist) still heartbeats via the daily
    // Monitor/readiness cadence, so 90 minutes comfortably clears normal gaps.
    private const int WorkerStaleMinutes = 90;

    public async Task<MonitoringDashboard> GetDashboardAsync(CancellationToken ct)
    {
        var db = await monitoringRepo.GetSnapshotAsync(ct);
        var failures = await adminRepo.GetJobFailuresAsync(TimeSpan.FromHours(48), ct);

        var now = DateTime.UtcNow;
        var workers = db.Workers.Select(w =>
        {
            var mins = (now - w.LastHeartbeatAt).TotalMinutes;
            return new MonitoringWorkerView(w.Name, w.LastResult, w.LastHeartbeatAt, Math.Round(mins, 1), mins > WorkerStaleMinutes, w.Message);
        }).ToList();

        var queues = await GetQueueHealthAsync(ct);
        var insights = await GetInsightsAsync(ct);

        return new MonitoringDashboard(now, workers, queues, db.Jobs, failures, insights, db.SystemEvents, db.Trading);
    }

    private async Task<QueueHealthSection> GetQueueHealthAsync(CancellationToken ct)
    {
        var admin = services.GetService<ServiceBusAdministrationClient>();
        if (admin is null)
            return new QueueHealthSection(false, "Service Bus admin client not configured.", [], 0);

        try
        {
            var depths = new List<QueueDepth>();
            await foreach (var q in admin.GetQueuesRuntimePropertiesAsync(ct))
                depths.Add(new QueueDepth(q.Name, q.ActiveMessageCount, q.DeadLetterMessageCount, q.ScheduledMessageCount));

            // Dead-lettered first - that's the "something is stuck" signal.
            depths = depths.OrderByDescending(d => d.DeadLettered).ThenBy(d => d.Name).ToList();
            return new QueueHealthSection(true, null, depths, depths.Sum(d => d.DeadLettered));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Service Bus queue health unavailable");
            return new QueueHealthSection(false, ex.Message, [], 0);
        }
    }

    private async Task<InsightsSection> GetInsightsAsync(CancellationToken ct)
    {
        var client = services.GetService<LogsQueryClient>();
        var resourceId = config["ApplicationInsights:ResourceId"];
        if (client is null || string.IsNullOrWhiteSpace(resourceId))
            return new InsightsSection(false, "App Insights query not configured.", 0, 0, 0, 0, 0, []);

        try
        {
            var rid = new ResourceIdentifier(resourceId);
            var span = new QueryTimeRange(TimeSpan.FromHours(24));

            var (requests, failedReq) = await RunPairAsync(client, rid, span,
                "requests | summarize total = count(), failed = countif(success == false)", "total", "failed", ct);
            var serverExceptions = await RunScalarAsync(client, rid, span, "exceptions | count", ct);
            var dependencyFailures = await RunScalarAsync(client, rid, span, "dependencies | where success == false | count", ct);
            var claude429 = await RunScalarAsync(client, rid, span,
                "union traces, exceptions | where message has '429' or outerMessage has '429' | " +
                "where message has 'Claude' or outerMessage has 'Claude' or operation_Name has 'esearch' or operation_Name has 'atchlist' | count", ct);

            var topExceptions = await RunNamedCountsAsync(client, rid, span,
                "exceptions | summarize c = count() by type | top 5 by c", "type", "c", ct);

            var failPct = requests > 0 ? Math.Round((double)failedReq / requests * 100, 2) : 0;
            return new InsightsSection(true, null, requests, failPct, serverExceptions, dependencyFailures, claude429, topExceptions);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "App Insights telemetry unavailable");
            return new InsightsSection(false, ex.Message, 0, 0, 0, 0, 0, []);
        }
    }

    private static async Task<long> RunScalarAsync(LogsQueryClient client, ResourceIdentifier rid, QueryTimeRange span, string kql, CancellationToken ct)
    {
        var result = await client.QueryResourceAsync(rid, kql, span, cancellationToken: ct);
        var row = result.Value.Table.Rows.FirstOrDefault();
        return row is null ? 0 : Convert.ToInt64(row[0]);
    }

    private static async Task<(long, long)> RunPairAsync(LogsQueryClient client, ResourceIdentifier rid, QueryTimeRange span, string kql, string colA, string colB, CancellationToken ct)
    {
        var result = await client.QueryResourceAsync(rid, kql, span, cancellationToken: ct);
        var row = result.Value.Table.Rows.FirstOrDefault();
        if (row is null) return (0, 0);
        return (Convert.ToInt64(row[colA] ?? 0L), Convert.ToInt64(row[colB] ?? 0L));
    }

    private static async Task<IReadOnlyList<NamedCount>> RunNamedCountsAsync(LogsQueryClient client, ResourceIdentifier rid, QueryTimeRange span, string kql, string nameCol, string countCol, CancellationToken ct)
    {
        var result = await client.QueryResourceAsync(rid, kql, span, cancellationToken: ct);
        return result.Value.Table.Rows
            .Select(r => new NamedCount(r[nameCol]?.ToString() ?? "(unknown)", Convert.ToInt64(r[countCol] ?? 0L)))
            .ToList();
    }
}
