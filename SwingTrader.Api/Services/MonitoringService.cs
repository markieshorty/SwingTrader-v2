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
    // Per-worker staleness thresholds keyed to each worker's real cadence, so
    // "stale" means "missed its expected run", not merely "idle". Each worker
    // only heartbeats when it runs: Monitor fires every ~5 min, the daily agents
    // once a day, and Watchlist weekly. A flat threshold flagged every daily/
    // weekly worker as stale between runs (a false alarm); these are generous
    // multiples of each real cadence so only a genuinely missed run trips it.
    private static readonly Dictionary<string, int> StaleThresholdMinutes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Monitor"] = 20,        // ~5-min cadence during market hours
        ["Execution"] = 1560,    // daily (~26h)
        ["Research"] = 1560,     // daily
        ["Report"] = 1560,       // daily
        ["Refinement"] = 1560,   // daily
        ["Risk"] = 1560,         // daily
        ["Readiness"] = 1560,    // daily
        ["Watchlist"] = 11520,   // weekly (~8 days)
    };

    // Unknown worker names fall back to the daily threshold.
    private const int DefaultStaleThresholdMinutes = 1560;

    public async Task<MonitoringDashboard> GetDashboardAsync(CancellationToken ct)
    {
        var db = await monitoringRepo.GetSnapshotAsync(ct);
        var failures = await adminRepo.GetJobFailuresAsync(TimeSpan.FromHours(48), ct);

        var now = DateTime.UtcNow;
        var workers = db.Workers.Select(w =>
        {
            var mins = (now - w.LastHeartbeatAt).TotalMinutes;
            var threshold = StaleThresholdMinutes.GetValueOrDefault(w.Name, DefaultStaleThresholdMinutes);
            return new MonitoringWorkerView(w.Name, w.LastResult, w.LastHeartbeatAt, Math.Round(mins, 1), mins > threshold, w.Message);
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

    // Drill-down list for a single App Insights metric tile. Kinds mirror the
    // clickable tiles on the dashboard: "exceptions", "dependencies", "claude429".
    public async Task<InsightsDetailSection> GetInsightsDetailAsync(string kind, CancellationToken ct)
    {
        var client = services.GetService<LogsQueryClient>();
        var resourceId = config["ApplicationInsights:ResourceId"];
        if (client is null || string.IsNullOrWhiteSpace(resourceId))
            return new InsightsDetailSection(false, "App Insights query not configured.", kind, []);

        var kql = kind switch
        {
            "dependencies" =>
                "dependencies | where success == false | order by timestamp desc | take 200 " +
                "| project timestamp, category = coalesce(type, target), title = name, detail = strcat(tostring(resultCode), ' ', tostring(data)), operation = operation_Name, location = ''",
            "claude429" =>
                "union traces, exceptions | where message has '429' or outerMessage has '429' " +
                "| where message has 'Claude' or outerMessage has 'Claude' or operation_Name has 'esearch' or operation_Name has 'atchlist' " +
                "| order by timestamp desc | take 200 " +
                "| project timestamp, category = itemType, title = coalesce(message, outerMessage), detail = '', operation = operation_Name, location = ''",
            // default = exceptions. Walk the parsed stack of each exception and
            // surface the top application-code frame as "file.cs:line" (the
            // origin), falling back to the reported method when no SwingTrader
            // frame carries a line number.
            _ =>
                "exceptions | extend _d = todynamic(details) | mv-expand frame = _d[0].parsedStack " +
                "| extend fa = tostring(frame['assembly']), fline = tolong(frame['line']), ffile = tostring(frame['fileName']), fmethod = tostring(frame['method']) " +
                "| summarize timestamp = take_any(timestamp), category = take_any(type), title = take_any(outerMessage), detail = take_any(innermostMessage), operation = take_any(operation_Name), " +
                "appLoc = take_anyif(strcat(replace_string(ffile, '/src/', ''), ':', tostring(fline)), fa startswith 'SwingTrader' and fline > 0), topMethod = take_any(method) by itemId " +
                "| extend location = coalesce(appLoc, topMethod) " +
                "| project timestamp, category, title, detail, operation, location " +
                "| order by timestamp desc | take 200",
        };

        try
        {
            var rid = new ResourceIdentifier(resourceId);
            var span = new QueryTimeRange(TimeSpan.FromHours(24));
            var result = await client.QueryResourceAsync(rid, kql, span, cancellationToken: ct);
            var events = result.Value.Table.Rows.Select(r => new InsightsEvent(
                r["timestamp"] is DateTimeOffset ts ? ts : DateTimeOffset.TryParse(r["timestamp"]?.ToString(), out var p) ? p : default,
                r["category"]?.ToString() ?? "(unknown)",
                r["title"]?.ToString() ?? string.Empty,
                string.IsNullOrWhiteSpace(r["detail"]?.ToString()) ? null : r["detail"]!.ToString(),
                r["operation"]?.ToString(),
                string.IsNullOrWhiteSpace(r["location"]?.ToString()) ? null : r["location"]!.ToString())).ToList();
            return new InsightsDetailSection(true, null, kind, events);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "App Insights detail query ({Kind}) unavailable", kind);
            return new InsightsDetailSection(false, ex.Message, kind, []);
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
