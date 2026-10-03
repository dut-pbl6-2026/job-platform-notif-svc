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

`PBL6-35` MUST `NOTIF-01` — Email notification delivery triggered by Kafka application-events. `Port 5007` `net10.0` `YARP gateway`. Owner TM2 W4. DB `job_platform_notif` (Postgres — idempotency log only).

## Architecture — clean Api/Core/Infrastructure

```
src/Notif.Api            → Web API (Program.cs, /health, /api/notifications/history)
src/Notif.Core           → Domain (NotificationLog entity, INotificationService, EmailTemplates)
src/Notif.Infrastructure → Data (NotifDbContext, Migrations) + Services (SmtpEmailSender, LoggerEmailSender) + Workers (ApplicationEventsConsumer)
tests/Notif.Tests        → xunit (ApplicationEventsConsumerRetryTests, EmailTemplatesTests)
NotifService.sln         → mise run build/test
```

Dependency: `Api → Infrastructure → Core → SharedKernel` (`PackageReference JobPlatform.SharedKernel 0.2.0` via `local-feed` + `nuget.config`, never `ProjectReference` per `master-plan.md:132`). `MAINT-01` clean arch.

## SRS mapping (NOTIF-01)

- `ApplicationEventsConsumer` — Consumes `application-events` topic (group `notif-svc`):
  - `application.submitted` → email recruiter template (NOTIF-01-03).
  - `application.status_changed` / `application.updated` → email applicant template (NOTIF-01-01).
  - Unknown or poison messages: `MessageOutcome.Skip` (offset committed, no retry loop).
  - Idempotency: unique index on `(ApplicationId, EventType, StatusSnapshot)` in `notification_logs`. Concurrent delivery handled via Postgres `23505` unique-violation catch.
- `GET /api/notifications/history` (NOTIF-01-06) — Paginated delivery log (PII). Requires `X-Internal-Token == NOTIF_HISTORY_TOKEN` when configured.
- `GET /health` — `{"status":"ok","service":"notification"}`.

## Recipient resolution (STUB — follow-up required)

`ResolveRecipient` in `ApplicationEventsConsumer` currently sends all emails to a configured fallback address (`NOTIF_DEFAULT_RECIPIENT` → `SMTP_FROM`). Real recipient lookup from auth/profile service is a follow-up task (PBL6-35 acceptance criterion). **Do not remove the `[STUB]` warning log until real lookup is implemented.**

## Email templates

- `EmailTemplates.SubmittedToRecruiter` — Uses `NOTIF_BASE_URL` env var (injected via `IConfiguration`) for absolute application links. Defaults to `""` (relative) in dev.
- `EmailTemplates.StatusChangedToApplicant` — Plain status update to applicant.

## No hard-coding (STRICT — apply to every file you touch)

**NEVER** embed literal values for any of the following in source code (`.cs`, `.json`, `.yaml`, `.toml`, …):

| Category | Examples of forbidden literals |
|----------|--------------------------------|
| Connection strings | `Host=localhost;Port=5432;Database=job_platform_notif;...` |
| Ports / URLs | `http://localhost:5007`, `5007` |
| Secrets / passwords | plain-text SMTP credentials |
| Email addresses | `recruiter@job-platform.local` (except `appsettings.Development.json`) |

**Always** read from `IConfiguration` / environment variables. Dev-only fallbacks in `appsettings.Development.json` only.

- Required env vars: `DATABASE_URL_NOTIF`, `KAFKA_BOOTSTRAP_SERVERS`, `KAFKA_TOPIC_APPLICATION_EVENTS`, `KAFKA_GROUP_NOTIF`, `SMTP_HOST`, `SMTP_PORT`, `SMTP_USER`, `SMTP_PASS`, `SMTP_FROM`, `NOTIF_DEFAULT_RECIPIENT`, `NOTIF_HISTORY_TOKEN`, `NOTIF_BASE_URL`.

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
mise run run  # http://localhost:5007/health → {"status":"ok","service":"notification"}
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
