# job-platform-notif-svc

.NET Web API — part of **Vietnam Job Platform** (`pbl6`) microservices suite under [`dut-pbl6-2026`](https://github.com/dut-pbl6-2026).

- **Org:** `dut-pbl6-2026`
- **Tech:** .NET Web API
- **Branch flow:** `feature/* → main` (see [job-platform-docs/.github/git-strategy.md](https://github.com/dut-pbl6-2026/job-platform-docs/blob/main/.github/git-strategy.md))
- **Docs:** Master plan `docs/master-plan.md`, Jira `PBL6` on `skid.atlassian.net`
- **TM ownership:** Auth/Gateway/Infra TM1 Hoai, Job/Search/Crawler/AI TM2 Thanh, Web TM3 Chi Bao, Mobile TM4 Khoa

## Getting Started
```bash
git clone https://github.com/dut-pbl6-2026/job-platform-notif-svc.git
cd job-platform-notif-svc
# see .github/git-strategy.md for branching, <type>(<scope>): <subject> commits
```

## Health, logging & code quality
- `GET /health` - static liveness (`{"status":"ok","service":"..."}`, used by Render `healthCheckPath`).
- `GET /health/ready` - readiness with short-timeout dependency checks: database (503 when unreachable), Kafka (degraded, only when `KAFKA_BOOTSTRAP_SERVERS` is set), SMTP (degraded, only when `SMTP_HOST` is set).
- Logging: Serilog, compact JSON on the console outside Development, request logging, `Service` + `CorrelationId` properties. `X-Correlation-Id` is reused if sent (and well-formed), otherwise generated, and always echoed in the response. Headers and bodies are never logged.
- SonarCloud (optional): CI runs `dotnet-sonarscanner` only when the `SONAR_TOKEN` secret is available (skipped on fork PRs). Optional repo variables `SONAR_ORGANIZATION` (default `dut-pbl6-2026`) and `SONAR_PROJECT_KEY` (default `dut-pbl6-2026_job-platform-notif-svc`).

## Related Repos
All 14 repos: `job-platform-shared, *-auth-svc, *-job-svc, *-search-svc, *-app-svc, *-profile-svc, *-notif-svc, *-gateway, *-web, *-mobile, *-crawler, *-ai-svc, *-infra, *-docs`.

## Deploy (Render Free jp-notif — TM4 Khoa)
- Service: `jp-notif` `https://jp-notif.onrender.com` `5006`
- Hook: `RENDER_DEPLOY_HOOK_NOTIF`
