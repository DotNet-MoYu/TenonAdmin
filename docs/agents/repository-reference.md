# TenonAdmin repository reference

Read the relevant section when working on backend architecture, frontend integration, local environment setup, or CI. Product-wide contracts and task routing live in [AGENTS.md](../../AGENTS.md).

## Commands

Backend (run from repo root; solution is `.slnx`, not `.sln`):
```bash
dotnet build backend/TenonAdmin.slnx -c Release
dotnet test  backend/TenonAdmin.slnx                       # xUnit + WebApplicationFactory, defaults to SQLite
dotnet test  backend/TenonAdmin.slnx --filter "FullyQualifiedName~DataScopeTests"   # single test/class
dotnet run   --project backend/samples/MinimalHost         # zero-config run on http://localhost:5100
```
Tests against MySQL (matches the CI matrix leg) via env vars:
```bash
TENON_TEST_DBTYPE=MySql TENON_TEST_MYSQL="Server=127.0.0.1;Port=3306;User ID=root;Password=root;AllowPublicKeyRetrieval=true;SSL Mode=None;" dotnet test backend/TenonAdmin.slnx
```
SQL Server 测试不要直接串行执行整套 `dotnet test`：CI 通过 `TENON_TEST_SQLSERVER_TEMPLATE=1` 先初始化一次模板库，普通 WebApplicationFactory 测试从模板备份恢复独立测试库，避免重复 CodeFirst；需要验证 CodeFirst、生产闸门或同库重启的测试仍走原始路径。按 CI 的方式用 `scripts/test-backend-shard.py --shard 1..4 --count 4` 并行跑四片；CI 已默认采用该分片路径。

Frontend (run from `web/`):
```bash
npm run dev          # Vite on :5173, proxies /api and /openapi to backend :5100 (override: TENON_API_TARGET)
npm run build        # vue-tsc --noEmit && vite build
npm run lint         # oxlint (lint:fix to autofix)
npm run typecheck    # vue-tsc --noEmit
npm run gen:api      # regenerate src/api/schema.d.ts; override the backend with TENON_API_TARGET
```

Contract drift check (run once per clone to activate the tracked pre-push hook):
```bash
git config core.hooksPath .githooks
node scripts/check-contract-drift.mjs
```
The check starts its own Development MinimalHost, regenerates both frontend schemas, and compares them with `HEAD`.

Full local env: `dev.bat` starts backend + frontend in separate windows (installs web deps on first run); `stop.bat` stops them.

Package versions are **centrally managed** — add/bump deps in `backend/Directory.Packages.props` (`<PackageVersion>`), not in individual `.csproj` files. Shared build/NuGet metadata lives in `backend/Directory.Build.props`.

## Backend architecture

**Package layering** (dependencies point downward only; this ordering is the load-bearing constraint):

```
TenonAdmin.Core        contracts only: interfaces (I*Provider, I*Service surface), Options, Result<T>, ErrorCode, AdminException. No SqlSugar, no ASP.NET.
   ↑
TenonAdmin.SqlSugar    data layer: ISqlSugarClient singleton (SqlSugarScope), IRepository<>, entity base classes, CodeFirst DatabaseInitializer, seed runner.
   ↑
TenonAdmin.Services    domain: entities (Sys*), *Service implementations, RBAC/data-scope providers, event bus. Entities live HERE, not in SqlSugar.
   ↑
TenonAdmin.AspNetCore  host integration: AddTenonAdmin/MapTenonAdmin, JWT, [RolePermission]/[ActiveSession] filters, built-in Controllers, envelope/exception/oplog filters.

TenonAdmin             meta-package: references AspNetCore only; consumers install this one to pull the whole stack.
```

Each layer's DI wiring is a `*Setup.cs` extension (`SqlSugarSetup`, `ServicesSetup`, `TenonAdminSetup`). `AddTenonAdmin` (in `TenonAdminSetup.cs`, the composition root) binds config, then calls down the chain.

**Replaceability model** (the whole point — respect it when adding features):
- Built-in services are registered with **`TryAdd*`** so a consumer registering the same interface *before* `AddTenonAdmin()` wins. Never use plain `Add*` for a replaceable service.
- Long service methods are split into small `virtual` steps (template-method) so consumers override one step by subclassing, not by copying the method.
- Consumer business assemblies are wired in via `options.ApplicationAssemblies`: their entities join CodeFirst table creation and their controllers get `AddApplicationPart`-ed. When touching entity scanning or controller registration in `TenonAdminSetup`, keep this path intact — dropping it silently breaks consumer modules (their tables aren't created, their controllers 404).
- The replaceability guarantees are locked by the "六件套" tests (`ReplaceabilityTests`) — treat those as a contract, not ordinary tests.

**Request pipeline** (an authenticated call flows through these, in order):
1. **Auth** — Microsoft JWT Bearer. Claims are unmapped (`sub`, `sid`, `sadm`, `unique_name`). Framework 401 challenges are reshaped into the standard envelope (code 40006).
2. **`[RolePermission]`** (`RolePermissionAttribute`) — permission code IS the normalized route: `{METHOD}:/{route template}` (e.g. `GET:/api/v1/ping`). There are **no permission strings in code** — authorization is granted by checking routes in the role-menu UI. Super admin (`sadm` claim) bypasses. Also validates the session (`sid`) is still active, so force-logout takes effect immediately. Use `[ActiveSession]` for any-logged-in-user endpoints that need no specific permission.
3. **Data scope** — during authorization, the user's effective org data-scope is resolved (cached) into `IDataScopeContext` (an `HttpContext.Items` carrier on the HTTP path — deliberately *not* `AsyncLocal`, which doesn't flow back through auth filters).
4. **Result envelope** — controllers may `return dto` directly; `ResultEnvelopeFilter` wraps bare returns into `Result<T>`. Business errors are thrown as `AdminException` / returned as `ErrorCode` and turned into envelopes by `AdminExceptionFilter`. **Errors are numeric `ErrorCode`s, never localized text** — i18n happens on the frontend by code (`§13.2`).

**Data layer conventions** (enforced globally in `SqlSugarSetup`, so business code stays clean):
- One `SqlSugarScope` singleton (thread-safe). Global query filters: soft-delete (`ISoftDelete` → `IsDelete == false`) and **data scope** (`IOrgScoped`/`DataEntity` filtered by the current request's resolved org set). The data-scope filter is the signature feature (`§6`).
- AOP auto-fills audit fields on insert/update: snowflake `Id` (when 0), `CreateTime`, `CreateUserId`, `CreateOrgId` (the data-scope anchor — if this isn't filled, org-scoped queries return 0 rows), `UpdateTime`, `UpdateUserId`. Business code sets business fields only.
- Snowflake `WorkerId` comes from `TenonAdmin:Id:WorkerId` (default 0) — **must differ per instance** when horizontally scaled or same-millisecond IDs collide.

**Zero-config bootstrap**: default SQLite (relative paths resolved against ContentRoot), CodeFirst auto-DDL via `DatabaseInitializer` (a hosted service), seed data (`ISeedData` implementations run once, idempotently), and a **random super-admin password printed to the console on first startup**. Switch dialect by changing `TenonAdmin:Database` (DbType + connection string); SQLite/MySQL/SqlServer/PostgreSQL supported. Same-process **extra connections** (multi ConfigId): `TenonAdmin:AdditionalDatabases` — see site guide `site/zh/guide/multi-database.md` (en: `site/guide/multi-database.md`); access via `db.AsTenant().GetConnection(configId)`; `IRepository<>` always hits main.

Config lives under the `TenonAdmin` section of `appsettings.json`, bound to `TenonAdminOptions` (see `Core/Options/*`). `appsettings.Development.json` is gitignored (holds credentials) — copy from the `.example`.

Health/OpenAPI: `/health` (liveness), `/health/ready` (DB+cache), and `/openapi/v1.json` (dev-only, the frontend's contract source).

## Frontend architecture (`web/`)

Vue 3 `<script setup>` + Naive UI + Pinia (persisted) + vue-router + vue-i18n + VueUse. Path alias `@` → `src`.

- **API is contract-generated**: `src/api/schema.d.ts` is generated from the backend's OpenAPI (`npm run gen:api`, backend must be running). `src/api/client.ts` wraps `openapi-fetch` typed against it. Don't hand-edit `schema.d.ts`; regenerate it.
- **Dynamic routing**: `router/routes.ts` holds static routes (login, error, shell); the real menu tree is fetched from the backend after login and injected as dynamic routes (multi-app portal — user picks/switches an app). `useModule().enterInitial()` in the router guard rebuilds them on hard refresh/deep-link, since dynamic routes live only in memory. `v-auth` directive (`directives/auth.ts`) gates buttons by permission.
- **Stores**: `auth` (token/session, routesReady), `user` (profile/login state), `app` (theme/prefs). First visit follows system dark/light (VueUse `usePreferredDark`); after a manual toggle, persistence takes over.
- Login page ships three swappable skins (`views/login/skins/`); theming via `styles/tokens.css` + `theme/`. Design system spec is `web/DESIGN.md`.
- **Shared components live in `web/COMPONENTS.md`** — read it before writing a page (FormContainer, useConfirm, StatusSwitch, dict suite, ProTable, icons); no component-demo menu by design. Update it when adding a shared component.

## Frontend architecture (`web-react/`)

React 19 + Ant Design (antd 6) + `@ant-design/pro-components` + zustand (persisted) + react-router-dom 7 + react-i18next. Path alias `@` → `src`. A second official template that ports `web/` feature-for-feature against the same backend contract — **a parallel template, not a shared library**. Commands run from `web-react/` (`npm run dev` on **5174**, plus `build`/`lint`/`typecheck`/`gen:api`, each its own script); `dev.bat`/`dev.sh` start it next to `web`.

- **Self-contained, zero-shared — the load-bearing constraint**: `web-react/` never imports from `web/`, `web-shared`, or `@shared`. This is a deliberate product decision (fork-and-own: a consumer degits one template), documented with its rationale in `docs/react-template-ledger.md`. **Don't factor common code out of the two templates, don't add a "must bundle both" note anywhere, and expect text/design-tokens to be maintained twice on purpose.**
- **Contract-generated API** (same shape as `web/`): `src/api/schema.d.ts` from the backend OpenAPI (`npm run gen:api`, its own script); `src/api/client.ts` wraps `openapi-fetch`. Don't hand-edit `schema.d.ts`.
- **Dynamic routing**: `useRoutes` over routes derived from the backend menu tree; `<RequireAuth>` guards (login / must-change-password / routes-ready); `<Can code="VERB:/path">` gates buttons by permission (antd's answer to `v-auth`).
- **Stores** (zustand): `user`/`auth`/`app`/`dict`/`tabs`. Selectors must return **primitives or stable references** — a selector returning a new object/closure re-runs every render and loops forever.
- **antd v6, not v5**: renamed props (`variant` not `bordered`, `styles.body` not `bodyStyle`, `styles.container` for Modal padding, …) are silent under `tsc`. Query the offline CLI before writing a component — `antd info/demo/semantic <C> --version 6.x` — and `antd lint <file>` after.
- Login ships the same three swappable skins (`views/login/skins/`); the `<DataTable>` wrapper isolates `pro-components` so CRUD pages depend only on it. The driving log for the whole port is `docs/react-template-ledger.md`.
- **Shared components live in `web-react/COMPONENTS.md`** (self-contained, contracts inline — no per-component README tree) — read it before writing a page; update it when adding a shared component.

## CI

`.github/workflows/backend-ci.yml` runs on pushes/PRs touching `backend/**`, `templates/**`, the workflow itself, or the two sharding scripts (`scripts/test-backend-shard.py`, `scripts/test_backend_shard_test.py`), with build/test, template smoke, and a SQL Server aggregate check:
- **`build-test`** — build + test on a `[sqlite, mysql, sqlserver, postgres]` matrix (`fail-fast: false`, so one red leg doesn't mask the others). SQL Server has four independent runner/container shards; the other dialects keep one full-suite leg each. MySQL/SqlServer/PostgreSQL/Redis service containers start on *every* leg; `TestDb.cs` derives an isolated DB per test from the DB-type env vars. `TENON_TEST_REDIS` is set on all legs, so the Redis contract tests actually run instead of silently skipping. **On push/PR the SqlServer leg runs only a dialect-sensitive subset** (`TEST_FILTER` in the Test step) — the other three legs run the full suite; see the SqlServer subsection below.
- **`sqlserver-check`** — retains the required check name `build-test (sqlserver)` and fails unless the entire build/test matrix succeeds, so one passing shard cannot conceal another failing or cancelled shard.
- **`template-smoke`** — `dotnet new tenon-app` → restore → build, via `templates/smoke-test.ps1` (the same script used for local manual runs). Catches the consumer's very first command breaking while kernel tests are all green.

`docker-smoke.yml` also fires on `backend/**` (and `web/**`), so a backend PR shows two more checks: **`single`** (image comes up, creates tables, seeds, issues a token) and **`multi`** (two replicas behind Caddy — cross-replica force-logout, lockout threshold, cluster-wide rate limit, distinct `WorkerId`, real client IP). Those guarantees only surface with two replicas online, so don't collapse `multi` into `single`.

`backend-release.yml` handles NuGet packaging. Required checks depend on changed paths and triggers; inspect the current workflow files and PR checks.

### The sqlserver leg: dialect-sensitive subset on push/PR, full nightly

The full SqlServer suite ran **40–60 min** (measured 2026-07-20: 2302 / 2466 / 2748 / 3000 / 3124 / 3277 / 3335 / 3514s, all green) vs 3–5 min for the other three legs. It was never hung — just slow — so don't add a short `timeout-minutes` (under ~90 would turn a green leg red), and don't cancel a nightly SqlServer run for "looking stuck".

**Historical diagnosis (2026-07-20; superseded by the measurement below).** `TestDb` gives every test its own database — near-free on SQLite (a file), MySQL (a directory), PostgreSQL (a `template1` copy), brutal on SQL Server. A micro-benchmark against a real SqlServer (network latency 1.5 ms, so not a network artifact) put **85% of the per-database cost in `CodeFirst.InitTables`**: ~20 s/db, of which ~17.6 s is the 23 `CREATE TABLE`/`CREATE INDEX` statements themselves (each auto-committed → its own transaction-log flush), only ~2.4 s is existence-check round-trips, and `CREATE DATABASE`/`DROP` are rounding error. ×~200 databases ≈ the whole leg.

**Historical attempts:** tmpfs on `/var/opt/mssql` and a targeted `ClearPool` were no-ops on the 2026-07-20 instrument. `DBCC CLONEDATABASE` was fragile under repeated cloning of one source. The current SQL Server 2022 CI image was remeasured on 2026-09-08: a compressed `BACKUP` once plus per-test `RESTORE ... WITH MOVE` completed restore work in roughly 0.02–0.05s per database, so the template path is now used for ordinary host tests; CodeFirst-specific tests remain on the original path.

**The original resolution** was to stop rebuilding ~200 SqlServer schemas on every PR. On push/PR the SqlServer leg runs only the SqlServer-*specific* surface (nvarchar Chinese, boolean-predicate global filters, T-SQL DDL/bootstrap, Storageable seed SQL) via `TEST_FILTER` — DB-agnostic business logic is already exercised by the full sqlite/mysql/postgres legs on the same PR. The **full** SqlServer suite runs nightly (the `schedule` trigger), so nothing is permanently uncovered — a test outside the subset regresses into the nightly run, not into a blind spot. Measurement discipline that cost real time to learn: the baseline's own spread (2302–3514s, 1.5×) means **a single CI run cannot resolve anything smaller than a ~50% change** — repeat runs or measure locally.


### SQL Server 分片更新（2026-09-08）

上面的 2026-07-20 刷盘结论属于历史测量。当前 `DatabaseInitializer` 已将 CodeFirst 包在事务里；本地 SQL Server 2022 CU26 的 15 条持久化回归基线中，查询优化累计约 423s，事务日志等待仅 461ms，主要成本是反复建隔离库时 SqlSugar 元数据 SQL 的编译。没有修改数据库耐久性、兼容级别或测试隔离。

当前 CI 用 `scripts/test-backend-shard.py` 先按原 `TEST_FILTER` 调用 VSTest 发现方法，再排序轮询分成四片；Theory 的全部数据行留在同一片。push/PR 的原子集不变，nightly 无过滤发现，四片并集保持全量；新增方法自动参与分片。每片上传 `discovered.txt`、`selected.txt` 和 TRX，保留七天。脚本校验可运行 `python3 -B scripts/test_backend_shard_test.py`。

同一组 15 条真实 SQL Server 测试，本地基线总耗时 511.6s（测试报告 8m25s），四个独立容器并行总耗时 156.3s，均 15/15 且逐测试名称/结果一致。该单次对比减少约 69% 等待时间；分片不减少总计算量，GitHub runner 排队和整套测试分布仍影响最终 CI 时间，不能把这个比例直接当成 GitHub 全套实测。旧 PR filter 的 151 个方法与 nightly 的 1121 个方法已通过真实 VSTest 重新发现验证分片无遗漏、无重复；这项检查是覆盖清单核对，不是全套测试通过的声明。
