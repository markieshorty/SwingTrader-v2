using System.Text.Json;
using SwingTrader.Agents.Backtesting;
using SwingTrader.Agents.Sharing;
using SwingTrader.Core.Enums;
using SwingTrader.Core.Interfaces;
using SwingTrader.Core.Models;
using SwingTrader.Infrastructure.Services;

namespace SwingTrader.Api.Endpoints;

// Strategy sharing (docs: admin "Share Strategy" tab + recipient "Shared
// Strategies" page). The send side is admin-gated; the receive side is
// account-scoped with Owner-only mutation. Evidence is structurally tied:
// send is blocked unless the sender's CURRENT live-settings fingerprint has a
// passing out-of-sample validation AND a Monte Carlo run stamped with the
// same fingerprint - so the verdicts quoted in the email were provably
// produced by the exact settings being shared.
public static class StrategyShareEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ---- shapes stored in StrategyShare.EvidenceJson (camelCase) ----
    public record ShareEvidence(SimEvidence? Sim, ValidateEvidence? Validate, MonteCarloEvidence? MonteCarlo);
    public record SimEvidence(int RunId, DateTime? CompletedAt, decimal TotalReturnPct, decimal SpyReturnPct,
        decimal WinRate, int Trades, decimal MaxDrawdownPct);
    public record ValidateEvidence(int RunId, DateTime? CompletedAt, bool HeldUp, string Verdict,
        decimal HoldoutAdjustedExpectancyPct, decimal BaselineHoldoutAdjustedExpectancyPct);
    public record MonteCarloEvidence(int RunId, DateTime? CompletedAt, string Verdict,
        decimal MedianTotalReturnPct, decimal P5TotalReturnPct, decimal P95MaxDrawdownPct,
        decimal ProbabilityOfLossPct, decimal ProbabilityBeatingSpyPct);

    public record SendShareRequest(List<int> RecipientAccountIds, string? Message, string AppBaseUrl);

    public static IEndpointRouteBuilder MapStrategyShareAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin/strategy-share").RequireAuthorization("Admin");

        // Evidence + eligibility for the admin tab: the admin's OWN account's
        // live fingerprint, whether validate/MC evidence exists for it, other
        // accounts' owners as candidate recipients, and the sent history.
        admin.MapGet("/status", async (
            IStrategyShareService shareService,
            IBacktestRunRepository runs,
            IAdminRepository adminRepo,
            IStrategyShareRepository shares,
            IAccountContext ctx,
            CancellationToken ct) =>
        {
            var fingerprint = await shareService.ComputeLiveFingerprintAsync(ctx.AccountId, ct);
            var evidence = await LookupEvidenceAsync(runs, ctx.AccountId, fingerprint, ct);

            var users = await adminRepo.GetUsersAsync(ct);
            var recipients = users
                .Where(u => u.Role == AccountRole.Owner && u.AccountId != ctx.AccountId && !u.IsSuspended)
                .Select(u => new { accountId = u.AccountId, displayName = u.DisplayName, email = u.Email })
                .ToList();

            var history = (await shares.ListForSenderAsync(ctx.AccountId, ct)).Select(s => new
            {
                id = s.Id,
                recipientAccountId = s.AccountId,
                recipientName = users.FirstOrDefault(u => u.AccountId == s.AccountId && u.Role == AccountRole.Owner)?.DisplayName
                    ?? $"Account #{s.AccountId}",
                sentAt = s.SentAt,
                status = s.Status,
                appliedAt = s.AppliedAt,
                revertedAt = s.RevertedAt,
            }).ToList();

            return Results.Ok(new
            {
                fingerprint,
                sim = evidence.Sim,
                validate = evidence.Validate,
                monteCarlo = evidence.MonteCarlo,
                canSend = evidence.Sim is not null && evidence.Validate is { HeldUp: true } && evidence.MonteCarlo is not null,
                recipients,
                history,
            });
        });

        // Freeze a snapshot of the admin's live settings and send it to the
        // selected owners, with an email quoting the tied evidence. Evidence
        // is RE-CHECKED server-side against the just-computed fingerprint so a
        // settings tweak between page load and click can't smuggle unevidenced
        // settings out.
        admin.MapPost("/send", async (
            SendShareRequest req,
            IStrategyShareService shareService,
            IBacktestRunRepository runs,
            IAdminRepository adminRepo,
            IStrategyShareRepository shares,
            IUserRepository userRepo,
            IEmailService email,
            IAdminLogRepository adminLog,
            IAccountContext ctx,
            HttpContext http,
            ILogger<StrategyShare> logger,
            CancellationToken ct) =>
        {
            if (req.RecipientAccountIds is not { Count: > 0 })
                return Results.BadRequest(new { message = "Pick at least one recipient." });

            var fingerprint = await shareService.ComputeLiveFingerprintAsync(ctx.AccountId, ct);
            var evidence = await LookupEvidenceAsync(runs, ctx.AccountId, fingerprint, ct);
            if (evidence.Sim is null || evidence.Validate is not { HeldUp: true } || evidence.MonteCarlo is null)
                return Results.BadRequest(new
                {
                    message = "Your current live settings don't have the full tied evidence yet — run an A/B " +
                              "simulation, an out-of-sample validation (and pass it) and a Monte Carlo run from " +
                              "the Strategy Lab first. Any settings change invalidates earlier runs.",
                });

            var snapshot = await shareService.BuildSnapshotAsync(ctx.AccountId, ct);
            var snapshotJson = JsonSerializer.Serialize(snapshot, StrategyShareService.SnapshotJsonOptions);
            var evidenceJson = JsonSerializer.Serialize(evidence, Json);

            var senderUsers = await userRepo.ListByAccountAsync(ctx.AccountId, ct);
            var senderName = senderUsers.FirstOrDefault(u => u.Role == AccountRole.Owner)?.DisplayName ?? "An admin";

            var allUsers = await adminRepo.GetUsersAsync(ct);
            var sent = new List<object>();
            foreach (var accountId in req.RecipientAccountIds.Distinct())
            {
                if (accountId == ctx.AccountId) continue;
                var owner = allUsers.FirstOrDefault(u => u.AccountId == accountId && u.Role == AccountRole.Owner);
                if (owner is null) continue;

                var share = await shares.AddAsync(new StrategyShare
                {
                    AccountId = accountId,
                    SenderAccountId = ctx.AccountId,
                    SenderName = senderName,
                    Message = string.IsNullOrWhiteSpace(req.Message) ? null : req.Message.Trim(),
                    SnapshotJson = snapshotJson,
                    ConfigFingerprint = fingerprint,
                    EvidenceJson = evidenceJson,
                }, ct);

                try
                {
                    await email.SendSimpleEmailAsync(
                        [owner.Email],
                        BuildEmailMarkdown(senderName, req.Message, evidence, req.AppBaseUrl),
                        $"{senderName} has shared a strategy with you on SwingTrader");
                }
                catch (Exception ex)
                {
                    // The share still exists in-app; email is best-effort.
                    logger.LogWarning(ex, "Strategy-share email to account {AccountId} failed (share #{ShareId} created)",
                        accountId, share.Id);
                }

                sent.Add(new { shareId = share.Id, accountId, recipient = owner.DisplayName });
            }

            await adminLog.LogAsync(new AdminActionLog
            {
                AdminUserId = http.User.FindFirst("sub")?.Value ?? "unknown",
                TargetUserId = string.Join(",", req.RecipientAccountIds),
                Action = "StrategyShareSend",
                Details = $"fingerprint {fingerprint[..12]}…, {sent.Count} recipient(s)",
            }, ct);

            return Results.Ok(new { success = true, sent });
        });

        return app;
    }

    public static RouteGroupBuilder MapStrategyShareEndpoints(this RouteGroupBuilder api)
    {
        // Shares received by the caller's account - the Shared Strategies page.
        api.MapGet("/strategy-shares", async (
            IStrategyShareRepository shares, IAccountContext ctx, CancellationToken ct) =>
        {
            var list = (await shares.ListForRecipientAsync(ctx.AccountId, ct)).Select(s => new
            {
                id = s.Id,
                senderName = s.SenderName,
                message = s.Message,
                sentAt = s.SentAt,
                status = s.Status,
                appliedAt = s.AppliedAt,
                dismissedAt = s.DismissedAt,
                revertedAt = s.RevertedAt,
                canRevert = s.Status == "Applied" && s.BackupJson != null && s.RevertedAt == null,
                evidence = JsonSerializer.Deserialize<ShareEvidence>(s.EvidenceJson, Json),
                snapshot = JsonSerializer.Deserialize<StrategySnapshot>(s.SnapshotJson, StrategyShareService.SnapshotJsonOptions),
            });
            return Results.Ok(list.ToList());
        });

        // Nav badge: undecided shares only.
        api.MapGet("/strategy-shares/pending-count", async (
            IStrategyShareRepository shares, IAccountContext ctx, CancellationToken ct) =>
            Results.Ok(new
            {
                count = await shares.CountPendingForRecipientAsync(ctx.AccountId, ct),
                total = await shares.CountAllForRecipientAsync(ctx.AccountId, ct),
            }));

        // Apply: auto-backup of the recipient's own settings first, then the
        // full overwrite (weights + books + tactics) through the refinement
        // audit trail. Owner-only - this rewrites live trading behaviour.
        api.MapPost("/strategy-shares/{id:int}/apply", async (
            int id, IStrategyShareRepository shares, IStrategyShareService shareService,
            IAccountContext ctx, CancellationToken ct) =>
        {
            if (ctx.Role != AccountRole.Owner)
                return Results.Forbid();
            var share = await shares.GetByIdAsync(ctx.AccountId, id, ct);
            if (share is null) return Results.NotFound();

            var backup = await shareService.BuildSnapshotAsync(ctx.AccountId, ct);
            share.BackupJson = JsonSerializer.Serialize(backup, StrategyShareService.SnapshotJsonOptions);

            var snapshot = JsonSerializer.Deserialize<StrategySnapshot>(share.SnapshotJson, StrategyShareService.SnapshotJsonOptions)
                ?? throw new InvalidOperationException("Share snapshot failed to deserialize.");
            await shareService.ApplySnapshotAsync(ctx.AccountId, snapshot,
                $"Applied shared strategy from {share.SenderName} (share #{share.Id}, sent {share.SentAt:yyyy-MM-dd}).", ct);

            share.Status = "Applied";
            share.AppliedAt = DateTime.UtcNow;
            share.RevertedAt = null;
            await shares.UpdateAsync(share, ct);
            return Results.Ok(new { success = true });
        });

        api.MapPost("/strategy-shares/{id:int}/dismiss", async (
            int id, IStrategyShareRepository shares, IAccountContext ctx, CancellationToken ct) =>
        {
            if (ctx.Role != AccountRole.Owner)
                return Results.Forbid();
            var share = await shares.GetByIdAsync(ctx.AccountId, id, ct);
            if (share is null) return Results.NotFound();
            if (share.Status != "Sent")
                return Results.BadRequest(new { message = $"Share is {share.Status}, not Sent." });

            share.Status = "Dismissed";
            share.DismissedAt = DateTime.UtcNow;
            await shares.UpdateAsync(share, ct);
            return Results.Ok(new { success = true });
        });

        // One-click restore of the settings captured just before this share
        // was applied.
        api.MapPost("/strategy-shares/{id:int}/revert", async (
            int id, IStrategyShareRepository shares, IStrategyShareService shareService,
            IAccountContext ctx, CancellationToken ct) =>
        {
            if (ctx.Role != AccountRole.Owner)
                return Results.Forbid();
            var share = await shares.GetByIdAsync(ctx.AccountId, id, ct);
            if (share is null) return Results.NotFound();
            if (share.Status != "Applied" || share.BackupJson is null)
                return Results.BadRequest(new { message = "Nothing to restore — this share hasn't been applied." });
            if (share.RevertedAt is not null)
                return Results.BadRequest(new { message = "Already restored." });

            var backup = JsonSerializer.Deserialize<StrategySnapshot>(share.BackupJson, StrategyShareService.SnapshotJsonOptions)
                ?? throw new InvalidOperationException("Backup snapshot failed to deserialize.");
            await shareService.ApplySnapshotAsync(ctx.AccountId, backup,
                $"Restored own settings from before applying {share.SenderName}'s share #{share.Id}.", ct);

            share.RevertedAt = DateTime.UtcNow;
            await shares.UpdateAsync(share, ct);
            return Results.Ok(new { success = true });
        });

        return api;
    }

    private static async Task<ShareEvidence> LookupEvidenceAsync(
        IBacktestRunRepository runs, int accountId, string fingerprint, CancellationToken ct)
    {
        // Full-window A/B simulation of the live config (candidates[0] = the
        // user column) - the headline "what did it return" leg of the trio.
        SimEvidence? sim = null;
        var abRun = await runs.GetLatestCompletedByFingerprintAsync(accountId, "ab", fingerprint, ct);
        if (abRun?.ResultJson is not null)
        {
            using var doc = JsonDocument.Parse(abRun.ResultJson);
            if (doc.RootElement.TryGetProperty("candidates", out var cands)
                && cands.ValueKind == JsonValueKind.Array && cands.GetArrayLength() > 0
                && cands[0].TryGetProperty("result", out var r))
            {
                sim = new SimEvidence(abRun.Id, abRun.CompletedAt,
                    r.GetProperty("totalReturnPct").GetDecimal(),
                    r.GetProperty("spyReturnPct").GetDecimal(),
                    r.GetProperty("winRate").GetDecimal(),
                    r.GetProperty("trades").GetInt32(),
                    r.GetProperty("maxDrawdownPct").GetDecimal());
            }
        }

        ValidateEvidence? validate = null;
        var vRun = await runs.GetLatestCompletedByFingerprintAsync(accountId, "validate", fingerprint, ct);
        if (vRun?.ResultJson is not null
            && JsonSerializer.Deserialize<ValidateResult>(vRun.ResultJson, Json) is { Validation: { } v })
        {
            validate = new ValidateEvidence(vRun.Id, vRun.CompletedAt, v.HeldUp, v.Verdict,
                v.HoldoutAdjustedExpectancyPct, v.BaselineHoldoutAdjustedExpectancyPct);
        }

        MonteCarloEvidence? mc = null;
        var mRun = await runs.GetLatestCompletedByFingerprintAsync(accountId, "montecarlo", fingerprint, ct);
        if (mRun?.ResultJson is not null
            && JsonSerializer.Deserialize<MonteCarloResult>(mRun.ResultJson, Json) is { } m)
        {
            mc = new MonteCarloEvidence(mRun.Id, mRun.CompletedAt, m.Verdict,
                m.MedianTotalReturnPct, m.P5TotalReturnPct, m.P95MaxDrawdownPct,
                m.ProbabilityOfLossPct, m.ProbabilityBeatingSpyPct);
        }

        return new ShareEvidence(sim, validate, mc);
    }

    private static string BuildEmailMarkdown(string senderName, string? message, ShareEvidence evidence, string appBaseUrl)
    {
        var sim = evidence.Sim!;
        var v = evidence.Validate!;
        var m = evidence.MonteCarlo!;
        var note = string.IsNullOrWhiteSpace(message) ? "" : $"\n> {message.Trim()}\n";
        return $"""
            ## {senderName} has shared a strategy with you
            {note}
            The shared settings cover strategy weights and thresholds, every regime risk book, and all per-setup tactics. They were snapshotted at send time and won't change if {senderName} keeps tuning.

            **Evidence tied to these exact settings:**

            - **Historic simulation:** {sim.TotalReturnPct:F1}% total return over the full backtest window (vs SPY {sim.SpyReturnPct:F1}%), {sim.Trades} trades, {sim.WinRate:P1} win rate, {sim.MaxDrawdownPct:F1}% max drawdown
            - **Out-of-sample validation:** {(v.HeldUp ? "PASSED" : "did not hold up")} — {v.Verdict}
            - **Monte Carlo robustness:** {m.Verdict} (median return {m.MedianTotalReturnPct:F1}%, 5th-percentile {m.P5TotalReturnPct:F1}%, chance of loss {m.ProbabilityOfLossPct:F1}%)

            Review the details and apply it from your [Shared Strategies page]({appBaseUrl.TrimEnd('/')}/shared-strategies). Applying takes an automatic backup of your current settings, so you can restore them with one click. Past simulated performance doesn't guarantee future results.
            """;
    }
}
