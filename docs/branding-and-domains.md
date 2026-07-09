# Branding, marketing site, custom domains & email

Everything user-facing currently uses the **placeholder brand `Acme Trading`** and
**`example.com`** placeholder domains, so nothing here is committed to a final
name. This doc is the checklist to (a) rename to the real brand and (b) stand up
the marketing site, custom domains and the `support@` sender.

> Internal identifiers were deliberately **not** renamed — C# namespaces
> (`SwingTrader.*`), project/assembly names, Azure resource names (`swingtrader-*`)
> and the repo stay as-is. They're never shown to users, and renaming them would
> mean re-provisioning every Azure resource for no visible benefit.

---

## 1. Final brand rename (when the name is settled)

User-facing text only. It's a single find/replace of the placeholder token:

- Replace **`Acme Trading`** → your real brand across `SwingTrader.Angular/src/**`,
  the email subjects/body in `SwingTrader.Agents/**`, `SwingTrader.Infrastructure/**`,
  `SwingTrader.Api/**`, `SwingTrader.Functions/**`, and `marketing/**`.
- The email sender display name falls back to `"Acme Trading"` in
  `EmailService.cs`; either update that literal or set the `Email:FromName`
  Key Vault secret (preferred — no redeploy).
- In `marketing/index.html` + `styles.css`, replace `Acme Trading` and swap the
  `example.com` links (`app.example.com`, `support@example.com`).

## 2. Marketing site (Azure Static Web App)

The site lives in `marketing/` (plain static HTML/CSS — no build step).

1. Provision the SWA: deploy infra with `deployMarketing=true`
   (`az deployment group create ... -p deployMarketing=true`).
2. Get its deployment token: **Portal → the Static Web App → Manage deployment
   token**, or `az staticwebapp secrets list -n swingtrader-marketing-prod --query properties.apiKey -o tsv`.
3. Add it as the repo secret **`MARKETING_SWA_TOKEN`**.
4. Push any change under `marketing/` (or run the **Deploy Marketing** workflow) —
   it publishes to the SWA and gives you a `*.azurestaticapps.net` URL to preview.

## 3. DNS zone + nameserver delegation

1. Deploy infra with `rootDomain=<yourdomain>` (e.g. `example.com`). This creates
   the Azure DNS zone plus the `app` CNAME + `asuid.app` TXT records (the Container
   App target is known immediately).
2. Read the four Azure nameservers from the deployment output `dnsNameServers`
   (or the zone in the portal).
3. **At your domain registrar, set the nameservers to those four Azure NS.**
   Wait for propagation (minutes–hours). Everything below needs this done first.

## 4. Custom domains (phase 3 — after DNS is delegated)

### app.\<domain\> → Container App
Already half-wired: once DNS is delegated and the `app` CNAME + `asuid.app` TXT
resolve, redeploy infra with `bindCustomDomains=true`. This issues a free managed
cert and binds `app.<domain>` (SNI) to the Container App.

### \<domain\> + www → marketing SWA
1. Redeploy infra with `bindCustomDomains=true`. The `www` CNAME + the SWA `www`
   custom domain validate together (cname-delegation).
2. The **apex** needs two values Azure only gives you after the apex custom-domain
   resource is created:
   - the **inbound IP** → pass as `swaApexIp`
   - the **TXT validation token** → pass as `swaApexValidationToken`
   Grab them from **Portal → Static Web App → Custom domains → add `<domain>`**
   (or `az staticwebapp hostname show`), then redeploy infra with those two params
   set. The apex `A` + validation `TXT` records are then published and validation
   completes.

> The apex is inherently two-pass (add domain → read token/IP → publish records);
> that's why those two values are params rather than derived.

## 5. Email — send from `support@<domain>` (Microsoft 365)

Email is entirely Key-Vault-config-driven (`EmailConfig`), so switching the
sender is a secrets change, not a code change. Set these Key Vault secrets
(`swingtrader-kv-prod`):

| Secret | Value |
|---|---|
| `Email--SmtpHost` | `smtp.office365.com` |
| `Email--SmtpPort` | `587` |
| `Email--FromAddress` | `support@<domain>` |
| `Email--FromName` | your brand (e.g. `Acme Trading`) |
| `Email--Username` | `support@<domain>` |
| `Email--Password` | the mailbox password **or an app password** |

Microsoft 365 gotchas:
- **SMTP AUTH must be enabled** for the mailbox (Microsoft 365 admin → Active users
  → the mailbox → Mail → Manage email apps → tick **Authenticated SMTP**). It's
  off by default under Security Defaults.
- If the mailbox has MFA, a plain password won't work over SMTP — create an
  **app password** and use that, or (cleaner long-term) move to Microsoft Graph
  `sendMail` with an app registration. The current SMTP client works fine with
  SMTP AUTH + app password; Graph would be a follow-up if you want to drop basic auth.
- `From` must match the authenticated mailbox (`support@`), which it does.

No code redeploy is needed for the sender switch — the API/Functions read these
secrets at runtime.
