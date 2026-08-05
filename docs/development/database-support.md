# Database Support

- Status: Implementation note
- Last updated: 2026-08-05

## Purpose

本文记录 [ADR-0004](../adr/0004-relational-database-portability.md) 的当前实现状态。它区分“代码可以配置或生成迁移”与“已经在真实数据库上通过完整契约测试”，避免把计划能力描述为已验证能力。

## Current Matrix

| Database | EF Core Provider | Migration assembly | Current verification |
|---|---|---|---|
| SQLite | `Microsoft.EntityFrameworkCore.Sqlite` | `StructaDoc.Migrations.Sqlite` | 已使用临时文件数据库验证迁移、Document / Parse Run CRUD 和乐观并发冲突 |
| PostgreSQL | `Npgsql.EntityFrameworkCore.PostgreSQL` | `StructaDoc.Migrations.PostgreSql` | Provider 配置和初始迁移已编译；真实数据库契约测试待实现 |
| MySQL | `Microting.EntityFrameworkCore.MySql` | `StructaDoc.Migrations.MySql` | Provider 配置和 MySQL 8.4 方言初始迁移已编译；真实数据库契约测试待实现 |
| MariaDB | `Microting.EntityFrameworkCore.MySql` | `StructaDoc.Migrations.MariaDb` | Provider 配置和 MariaDB 11.4 方言初始迁移已编译；真实数据库契约测试待实现 |

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

## Migration Workflow

仓库使用本地 `dotnet-ef` 工具清单，先执行：

```bash
dotnet tool restore
```

每次共享模型变化都必须为四个迁移项目分别生成和审查迁移。设计时 Factory 只用于生成迁移，其中的占位连接字符串不得用于运行应用，也不包含有效凭据。

生产环境可以关闭 `ApplyMigrationsOnStartup`，改用同一应用镜像提供的迁移命令；在该命令入口实现前，部署文档不得声称已支持这种模式。

## Next Verification

下一步使用真实 PostgreSQL、MySQL 和 MariaDB 容器运行共享契约测试，至少覆盖：

- 从空库应用迁移并验证无待处理迁移；
- Document / Parse Run CRUD 与外键约束；
- 并发版本冲突；
- 原子任务抢占、续租和过期恢复；
- UTC 时间、排序、分页及字符串大小写行为；
- 从上一发布迁移升级。
