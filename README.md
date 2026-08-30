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

## Related Repos
All 14 repos: `job-platform-shared, *-auth-svc, *-job-svc, *-search-svc, *-app-svc, *-profile-svc, *-notif-svc, *-gateway, *-web, *-mobile, *-crawler, *-ai-svc, *-infra, *-docs`.

## Deploy (Render Free jp-notif — TM4 Khoa)
- Service: `jp-notif` `https://jp-notif.onrender.com` `5006`
- Hook: `RENDER_DEPLOY_HOOK_NOTIF`
