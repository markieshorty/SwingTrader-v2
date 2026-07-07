-- ============================================================
-- SwingTrader — Momentum Health / Probation Phase data migration
-- Run after: dotnet ef database update (AddMomentumHealthProbationPhase)
-- Safe to run multiple times (idempotent) — only touches NULL/default-
-- looking values, never overwrites a value an account owner already set.
-- ============================================================

BEGIN TRANSACTION;

-- ── 1. AccountRiskProfiles: backfill MinHoldDays / MomentumHealthThreshold ──
--
-- The EF migration's column defaults already backfill these for existing
-- rows, so this is a defensive re-run rather than a required step.

UPDATE AccountRiskProfiles
SET
    MinHoldDays = CASE WHEN MinHoldDays <= 0 THEN 3 ELSE MinHoldDays END,
    MomentumHealthThreshold = CASE WHEN MomentumHealthThreshold <= 0 THEN 0.35 ELSE MomentumHealthThreshold END
WHERE
    MinHoldDays <= 0
    OR MomentumHealthThreshold <= 0;

-- ── 2. Cross-field sanity check: MinHoldDays must be < MaxHoldDays ──────────
--
-- Should never trigger with the 3/whatever-existing-MaxHoldDays defaults,
-- but a profile edited directly in the DB (rather than through the API,
-- which enforces AccountRiskProfile.Validate()) could violate it.

UPDATE AccountRiskProfiles
SET MinHoldDays = MaxHoldDays - 1
WHERE MinHoldDays >= MaxHoldDays;

-- Verify — expect 0 rows:
SELECT AccountId, MinHoldDays, MaxHoldDays, MomentumHealthThreshold
FROM AccountRiskProfiles
WHERE MinHoldDays >= MaxHoldDays OR MinHoldDays <= 0 OR MomentumHealthThreshold <= 0;

-- ── 3. Trades: confirm Phase defaulted correctly for existing rows ─────────
--
-- All existing trades (open or closed) should have Phase = 0 (Probation)
-- from the migration's column default — the next Research run will
-- evaluate any still-open ones on their real DaysHeld. This is informational
-- only; nothing to fix here under normal circumstances.

SELECT
    CASE Phase WHEN 0 THEN 'Probation' WHEN 1 THEN 'Confirmed' WHEN 2 THEN 'Exiting' END AS PhaseName,
    Status,
    COUNT(*) AS TradeCount
FROM Trades
GROUP BY Phase, Status
ORDER BY Phase, Status;

COMMIT TRANSACTION;

-- ============================================================
-- Migration complete. Review the SELECT outputs above:
--   Step 2 query should return 0 rows.
--   Step 3 query is informational — just confirms row counts by phase/status.
-- ============================================================
