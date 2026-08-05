# Database Support

- Status: Implementation note
- Last updated: 2026-08-05

## Purpose

本文记录 [ADR-0004](../adr/0004-relational-database-portability.md) 的当前实现状态。它区分“代码可以配置或生成迁移”与“已经在真实数据库上通过完整契约测试”，避免把计划能力描述为已验证能力。

## Current Matrix

| Database | EF Core Provider | Migration assembly | Current verification |
|---|---|---|---|
| SQLite | `Microsoft.EntityFrameworkCore.Sqlite` | `StructaDoc.Migrations.Sqlite` | 已使用临时文件数据库验证迁移、Document 查询与键集分页、Parse Run/认证数据 CRUD、乐观并发、外部任务恢复与租约状态机，以及 Canonical Bundle 幂等成功事务 |
| PostgreSQL | `Npgsql.EntityFrameworkCore.PostgreSQL` | `StructaDoc.Migrations.PostgreSql` | Provider、Canonical 结果迁移和容器契约测试已编译；本机缺少容器运行时，真实执行待验证 |
| MySQL | `Microting.EntityFrameworkCore.MySql` | `StructaDoc.Migrations.MySql` | MySQL 8.4 方言、Canonical 结果迁移和容器契约测试已编译；本机缺少容器运行时，真实执行待验证 |
| MariaDB | `Microting.EntityFrameworkCore.MySql` | `StructaDoc.Migrations.MariaDb` | MariaDB 11.4 方言、Canonical 结果迁移和容器契约测试已编译；本机缺少容器运行时，真实执行待验证 |

在真实数据库迁移、CRUD、并发抢占、租约恢复和升级测试全部通过前，PostgreSQL、MySQL 和 MariaDB 不标记为发布支持。MySQL 与 MariaDB 即使使用同一 Provider，也保留独立迁移和测试目标。

## Provider Choice

共享模型使用 EF Core 10。SQLite 使用 Microsoft Provider，PostgreSQL 使用 Npgsql。

上游 [`Pomelo.EntityFrameworkCore.MySql`](https://github.com/PomeloFoundation/Pomelo.EntityFrameworkCore.MySql) 当前稳定版仍面向 EF Core 9；StructaDoc 的 .NET 10 基线不能为此退回已结束支持的 EF Core 主版本。当前 MySQL / MariaDB 适配使用 MIT 许可的 [`Microting.EntityFrameworkCore.MySql`](https://github.com/microting/Pomelo.EntityFrameworkCore.MySql)，它是面向 EF Core 10 的 Pomelo 分支，并提供不同的 `MySqlServerVersion` 与 `MariaDbServerVersion` 方言配置。

该依赖只存在于 Infrastructure 和迁移边界，不进入 Domain、Application 或公共 DTO。如果上游 Pomelo 发布稳定 EF Core 10 版本，或者当前分支不能通过 StructaDoc 的真实数据库契约测试，可以在不改变公共 API 和领域模型的情况下替换。

SQLite 的 `SQLitePCLRaw` 原生依赖被集中固定到无当前已知 NuGet 漏洞的 3.x 版本。不得通过关闭 NuGet 审计或忽略高危告警恢复到存在已知漏洞的传递版本。

## Configuration

Host 从 `Database` 配置段读取：

| Key | Required | Meaning |
|---|---:|---|
| `Provider` | Yes | `Sqlite`、`PostgreSql`、`MySql` 或 `MariaDb` |
| `ConnectionString` | Yes | 所选数据库的连接字符串 |
| `ServerVersion` | MySQL / MariaDB | 显式数据库版本；不通过启动时连接自动猜测 |
| `ApplyMigrationsOnStartup` | Yes | 是否在 Host 接受请求前应用当前 Provider 的迁移 |

生产凭据必须通过环境变量或部署 Secret 注入。配置文件只提供不含凭据的 SQLite 开发默认值。

SQLite 数据库文件使用本地持久卷；不支持多个容器共享该文件，也不支持放在网络文件系统。服务端数据库连接失败或迁移失败时，Host 不得进入就绪状态。

## Parse Run Lease Store

`IParseRunLeaseStore` 是 Application 层定义的持久化任务边界，Infrastructure 当前提供基于 EF Core 条件更新的可移植实现：

- 按 `nextAttemptAt`、创建时间和 ID 选择到期的 `queued` 候选任务；
- 使用状态、到期时间和并发版本作为 compare-and-set 条件，只有更新一行的 Worker 获得租约；
- 抢占时写入 Worker、租约到期时间、尝试次数和新并发版本；
- 续租要求 Worker 和并发版本仍匹配、旧租约尚未过期；
- `claimed`、没有外部任务 ID 且租约过期的任务可以原子恢复为 `queued`；
- `running`、已有外部任务 ID 且租约过期的任务可以由一个新 Worker 原子接管；接管保留 Stage、外部任务 ID 和当前 attempt，不重新提交或增加尝试次数。

`IParseRunStateStore` 进一步限制租约持有者能够执行的状态转换：

- 当前租约可以把 `claimed` 原子转换为 `running` 并写入初始 Stage；
- 只有当前运行租约能够更新 Stage；原子提交可在写入外部任务 ID 时直接进入 `waiting-provider`；Cloud 两阶段提交先写入外部 ID 和加密 continuation 并保持 `submitting`，上传确认后清除 continuation 再进入等待；
- 外部任务 ID 只能写入一次，且已有外部任务的运行不能回到提交前 Stage；
- 可重试错误在尝试次数未耗尽时进入 `retry-wait`，否则进入最终状态 `failed`；
- 永久错误直接进入 `failed`；
- Host 内置维护 Worker 分批把已到时间的 `retry-wait` 转回 `queued`。

`IParseRunExecutionContextStore` 只为仍持有未过期租约且并发版本匹配的 Worker 返回执行快照。快照从 Parse Run 固定的 Provider Config Version 读取 Base URL、model、backend 和加密凭据，而不是读取逻辑配置的当前版本；因此管理员更新或停用配置不会改变已经创建任务的执行意图。Provider 凭据和提交 continuation 只在该内部边界解密，不进入公共 DTO。

Host 注册的 `ParseRunLeaseHeartbeat` 为一个运行任务创建串行化租约会话。阶段写入、外部任务 ID/提交 checkpoint 写入、执行快照读取和后台续租共享最新并发令牌，避免彼此用旧 token 竞争；续租条件失败或已知租约到期会取消该会话的执行 token。该组件已就绪，但要由后续实际 Parse Run 执行器创建和释放会话。

`IParseBundleCommitStore` 在事务前流式复核所有 Asset 和 Artifact 的大小及 SHA-256，然后使用当前运行租约和并发版本作为成功提交条件。Pages、Blocks、Assets、Artifacts、Bundle 指纹和 `succeeded` 状态在同一事务写入；相同指纹可幂等重放，不同指纹、取消竞争、失效租约或既有部分结果不能覆盖任务状态。

当前维护 Worker 不抢占或执行 `queued` 任务。Provider HTTP 适配器、受限结果下载与归一化以及完整执行器接入前，不得把它描述为完整解析 Worker。

该实现不依赖某个数据库的专有 SQL。后续真实数据库竞争测试若证明有必要，可以在同一接口后为服务端数据库增加 `SKIP LOCKED` 等方言优化，而不改变 Worker 和公共 API。

## Migration Workflow

仓库使用本地 `dotnet-ef` 工具清单，先执行：

```bash
dotnet tool restore
```

每次共享模型变化都必须为四个迁移项目分别生成和审查迁移。设计时 Factory 只用于生成迁移，其中的占位连接字符串不得用于运行应用，也不包含有效凭据。

生产环境可以关闭 `ApplyMigrationsOnStartup`，改用同一应用镜像提供的迁移命令；在该命令入口实现前，部署文档不得声称已支持这种模式。

## Contract Tests

普通测试命令始终运行 SQLite 文件数据库契约测试。PostgreSQL、MySQL 和 MariaDB 测试使用 Testcontainers，默认跳过，避免没有容器运行时的开发机和 CI 被误判为数据库验证通过。

安装并启动 Docker 兼容运行时后，显式运行：

```powershell
$env:STRUCTADOC_RUN_DATABASE_CONTRACT_TESTS = '1'
dotnet test tests/StructaDoc.DatabaseContractTests
```

当前服务端数据库套件从空库应用迁移，并验证无待处理迁移、Document 复合游标分页与详情查询、管理员持久化、API Client scope 更新、Key 轮换、并发版本和撤销，以及 Parse Run 并发抢占、续租令牌失效、未启动任务的租约过期恢复、Stage/外部任务 ID 条件写入、运行中任务无重复接管、失败、重试等待、到期回队列转换和 Canonical Bundle 幂等成功提交。测试成功前不得更新上面的发布支持状态。

## Remaining Verification

下一步需要在有容器运行时的开发机或 CI 中实际执行现有套件，并继续补齐：

- Document / Parse Run CRUD 与外键约束；
- 并发版本冲突；
- UTC 时间、排序、分页及字符串大小写行为；
- 从上一发布迁移升级。
