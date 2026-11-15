# AGENTS — job-platform-notif-svc

> Notification microservice (PBL6-35, SRS NOTIF-01). Consumes `application-events` from Kafka and delivers idempotent email notifications via SMTP/MailHog. Git: `job-platform-docs/.github/git-strategy.md` (`feature/* → main`).

## Mise activation

Activate `mise` for bare `dotnet`/`infisical` without `mise exec`:

| Shell | Add to config file | Activate |
|-------|--------------------|----------|
| `bash` | `~/.bashrc` or `~/.bash_profile` | `eval "$(mise activate bash)"` |
| `zsh` | `~/.zshrc` | `eval "$(mise activate zsh)"` |
| `fish` | `~/.config/fish/config.fish` | `mise activate fish \| source` |
| `PowerShell` | `$PROFILE` | `mise activate pwsh \| Out-String \| Invoke-Expression` |

Agent uses `mise exec -- dotnet ...` / `mise exec -- infisical ...` due to non-interactive shell without `mise activate`; humans just use `dotnet` / `infisical` after `mise install`.

## Scope

`PBL6-35` MUST `NOTIF-01` — Email notification delivery triggered by Kafka application-events. `Port 5006` `net10.0` `YARP gateway`. Owner TM2 W4. DB `job_platform_notif` (Postgres — idempotency log only).

## Architecture — clean Api/Core/Infrastructure

```
src/Notif.Api            → Web API (Program.cs, /health, /api/notifications/history)
src/Notif.Core           → Domain (NotificationLog entity, INotificationService, EmailTemplates)
src/Notif.Infrastructure → Data (NotifDbContext, Migrations) + Services (SmtpEmailSender, LoggerEmailSender) + Workers (ApplicationEventsConsumer)
tests/Notif.Tests        → xunit (ApplicationEventsConsumerRetryTests, EmailTemplatesTests)
NotifService.sln         → mise run build/test
```

Schema migration is a separate deploy step: `dotnet Notif.Api.dll migrate` (`Bootstrap/MigrateCommand.cs`, compose service `notif-migrate`). The web host never migrates on startup.

Dependency: `Api → Infrastructure → Core → SharedKernel` (`PackageReference JobPlatform.SharedKernel 0.3.1` via the GitHub Packages feed in `nuget.config`, never `ProjectReference` per `master-plan.md:132`). `MAINT-01` clean arch.

## SRS mapping (NOTIF-01)

- `ApplicationEventsConsumer` — Consumes `application-events` topic (group `notif-svc`, the default for `KAFKA_GROUP_NOTIF`):
  - `application.submitted` → recruiter template (NOTIF-01-03) is currently skipped: the event has no recruiter user id (see Recipient resolution).
  - `application.status_changed` / `application.updated` → applicant email (NOTIF-01-01), address from auth-svc by `ApplicantId`.
  - Unknown or poison messages: `MessageOutcome.Skip` (offset committed, no retry loop).
  - Idempotency: unique index on `(ApplicationId, EventType, IdempotencyKey)` in `notification_logs`; application notifications default the key to the status snapshot, while `JobEventsConsumer` uses the envelope event id. Concurrent delivery handled via Postgres `23505` unique-violation catch.
- `JobEventsConsumer` — Consumes `job-events` topic (group `notif-svc-job`, the default for `KAFKA_GROUP_JOB_NOTIF`; **must stay distinct** from the application consumer's `notif-svc` group) for admin approval emails. **Gated by `NOTIF_JOB_EVENTS_ENABLED` (off by default)**: `JobUpdatedEvent` carries no recruiter address and no `previousStatus` transition yet, so enabling it in production needs the PBL6-35 recipient lookup plus the job-svc contract change. When enabled it only mails on a real transition (`Active` from another status = approved, `Rejected` from another status = rejected); an edit of an already-live job is silent. Missing `previousStatus`/`rejectionReason` are read from the raw envelope so the shared contract can grow without a package bump. Idempotency is per Kafka event (envelope `eventId`), so redelivery is deduped but a later `Rejected`/`Active` transition for the same job still emails.
- `GET /api/notifications/history` (NOTIF-01-06) — Paginated delivery log (PII). Requires `X-Internal-Token == NOTIF_HISTORY_TOKEN` (constant-time). `NOTIF_HISTORY_TOKEN` is mandatory outside Development: startup fails without it. Anonymous access exists only in Development (explicit opt-in via environment).
- `GET /health` — `{"status":"ok","service":"notification"}`.

## Telegram job alert bot (PBL6-63, SRS TELE-01)

- `POST /api/notifications/telegram/webhook` — Telegram webhook. Requires header `X-Telegram-Bot-Api-Secret-Token == TELEGRAM_WEBHOOK_SECRET` (constant-time compare). Missing or wrong secret, or the bot not configured → `401`, nothing processed. Any authenticated update → `200`.
- Commands: `/start`, `/help` (welcome and command list), `/subscribe`, `/unsubscribe`. In groups, `/cmd@BotName` is accepted. Other `/…` commands get the help text; plain text is ignored.
- Subscriptions: table `telegram_subscriptions` (`ChatId` bigint, unique; `IsActive`). Unsubscribe deactivates the row; re-subscribe reactivates it.
- `TelegramBroadcaster.BroadcastAsync(text)` sends plain text to every active chat and returns `{Sent, Failed}`. A failed chat is counted and skipped. Nothing calls it yet; wiring it to job events is a follow-up.
- Bot API calls go through `TelegramBotClient` (plain `HttpClient`, not `IHttpClientFactory`, so the token-bearing URL is not logged).
- Gateway: `/api/notifications/**` requires JWT, and Telegram cannot send one. The gateway needs an anonymous route for `/api/notifications/telegram/webhook` (follow-up in `job-platform-gateway`).

Config (all from env, never committed):

| Key | Required | Notes |
|-----|----------|-------|
| `TELEGRAM_BOT_TOKEN` | to enable | Token from BotFather. Unset → bot off. |
| `TELEGRAM_WEBHOOK_SECRET` | with token | 1–256 chars of `A-Z a-z 0-9 _ -`. Startup fails if missing. |
| `TELEGRAM_API_BASE_URL` | with token | Absolute URL, e.g. `https://api.telegram.org`. Startup fails if missing. |

Register the webhook once (placeholders, do not commit real values):

```bash
curl -X POST "<TELEGRAM_API_BASE_URL>/bot<TELEGRAM_BOT_TOKEN>/setWebhook" \
  -d "url=<PUBLIC_BASE_URL>/api/notifications/telegram/webhook" \
  -d "secret_token=<TELEGRAM_WEBHOOK_SECRET>"
```

Not implemented: keyword subscriptions, multiple subscriptions per chat, `/list`, `/language`, Vietnamese messages, rate-limit (429) handling, and automatic job alerts.

## Recipient resolution (applicant emails)

`ApplicationEventsConsumer` resolves `application.status_changed` / `application.updated` to the applicant's email by calling auth-svc `GET /api/internal/users/{ApplicantId}/email` directly on the internal network (not via the gateway). Bearer auth with `INTERNAL_SERVICE_TOKEN`; response `{ userId, email }`.

- Found: mail is sent to the returned address.
- 404: warning with the user id only, `Skip`. Nothing is sent.
- Unavailable (transport, timeout, 5xx, 408, 429, bad body): retried in-process with bounded backoff (3 attempts), then `Retry` (Kafka redelivery). A 401/403 is not retried; it logs an error and returns `Retry`.
- There is no fallback address. `NOTIF_DEFAULT_RECIPIENT` is not used for applicant mail.
- `application.submitted` is **skipped** with a warning: the recruiter email needs a recruiter user id, which the event does not carry. Re-enable once the contract carries it.

`JobEventsConsumer` (gated by `NOTIF_JOB_EVENTS_ENABLED`) still uses `NOTIF_DEFAULT_RECIPIENT` / `SMTP_FROM`. It stays off until its recruiter lookup exists.

## Email templates

- `EmailTemplates.SubmittedToRecruiter` — Uses `NOTIF_BASE_URL` env var (injected via `IConfiguration`) for absolute application links. Defaults to `""` (relative) in dev.
- `EmailTemplates.StatusChangedToApplicant` — Plain status update to applicant.

## No hard-coding (STRICT — apply to every file you touch)

**NEVER** embed literal values for any of the following in source code (`.cs`, `.json`, `.yaml`, `.toml`, …):

| Category | Examples of forbidden literals |
|----------|--------------------------------|
| Connection strings | `Host=localhost;Port=5432;Database=job_platform_notif;...` |
| Ports / URLs | `http://localhost:5006`, `5006` |
| Secrets / passwords | plain-text SMTP credentials |
| Email addresses | `recruiter@job-platform.local` (except `appsettings.Development.json`) |

**Always** read from `IConfiguration` / environment variables. Dev-only fallbacks in `appsettings.Development.json` only.

- Required env vars: `DATABASE_URL_NOTIF`, `KAFKA_BOOTSTRAP_SERVERS`, `KAFKA_TOPIC_APPLICATION_EVENTS`, `KAFKA_GROUP_NOTIF`, `KAFKA_TOPIC_JOB_EVENTS`, `KAFKA_GROUP_JOB_NOTIF`, `NOTIF_JOB_EVENTS_ENABLED`, `SMTP_HOST`, `SMTP_PORT`, `SMTP_USER`, `SMTP_PASS`, `SMTP_FROM`, `NOTIF_DEFAULT_RECIPIENT` (job events only), `NOTIF_HISTORY_TOKEN`, `NOTIF_BASE_URL`, `AUTH_SERVICE_URL` (auth-svc base URL, required outside Development), `INTERNAL_SERVICE_TOKEN` (required outside Development, no default).
- Telegram (optional, PBL6-63): `TELEGRAM_BOT_TOKEN`, `TELEGRAM_WEBHOOK_SECRET`, `TELEGRAM_API_BASE_URL` — see the Telegram section.

## 2026 best practice (NFR `MAINT`)

- `dotnet 10.0.100` `net10.0` `nullable enable` `ImplicitUsings` file-scoped namespace, `ProblemDetails` + `UseExceptionHandler` + `ILogger` JSON `ERROR/WARN/INFO/DEBUG`, `GET /health` per `8-system-architecture.md`.
- `dotnet build --warnaserror` + `dotnet format --verify-no-changes` (mise `build/test/format`), EF `EF10.0.4` + `Npgsql10.0.3`, test coverage `>70%` `MAINT-02`.
- Never commit `.env` (`.gitignore`), `mise run sync-env` single source `../job-platform-infra/envs/.env.dev.example`.

## Workflow

```bash
mise trust && mise install
mise run sync-env && mise run verify
mise run build && mise run test && mise run format
mise run ef-check
mise run run  # http://localhost:5006/health → {"status":"ok","service":"notification"}
```

## Git convention (git-strategy.md)

Branch: `feature/<description>` | `bugfix/<description>` | `hotfix/v<semver>-<desc>` → `main`.

Commits — `<type>(notif): <subject>` (scope always `notif` for this repo):

| Type | Example |
|------|---------|
| `feat` | `feat(notif): add application.submitted email notification` |
| `fix` | `fix(notif): resolve recipient from profile-svc` |
| `refactor` | `refactor(notif): extract email template builder` |
| `test` | `test(notif): add idempotency test for duplicate events` |
| `docs` | `docs(notif): update AGENTS.md with env vars` |
| `chore` | `chore(notif): update SharedKernel package` |
| `ci` | `ci(notif): add Dockerfile and CI workflow` |

PR checklist: Description / How to verify / Checklist `mise run build/test/format/ef-check`.
