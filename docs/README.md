# StructaDoc 设计文档

本目录保存 StructaDoc 的架构决策和跨组件目标规格。当前项目尚处于启动阶段；文档描述目标行为，不代表对应代码已经实现。

## 权威位置

| 内容 | 权威位置 |
|---|---|
| 产品定位、能力摘要、路线图 | [`README.md`](../README.md) |
| 仓库协作与变更规则 | [`AGENTS.md`](../AGENTS.md) |
| 难以逆转的架构决策 | [`adr/`](./adr/) |
| 跨组件公共契约 | [`specifications/`](./specifications/) |

## 架构决策

| ADR | 状态 | 说明 |
|---|---|---|
| [ADR-0001](./adr/0001-product-boundary.md) | Accepted | 产品边界：只负责文档摄取与结构化解析 |
| [ADR-0002](./adr/0002-parser-provider-abstraction.md) | Accepted | 通过 Provider 适配在线和本地解析服务 |
| [ADR-0003](./adr/0003-technology-and-single-image-deployment.md) | Accepted | 使用 .NET 10 和包含管理网页、Worker、LibreOffice 的单一应用镜像 |
| [ADR-0004](./adr/0004-relational-database-portability.md) | Accepted | 支持 SQLite、PostgreSQL、MySQL / MariaDB，并保持任务可靠性语义一致 |
| [ADR-0005](./adr/0005-authentication-and-api-clients.md) | Accepted | 分离管理员 Cookie 会话、API Client 密钥、scope 与防伪请求 |

## 规格

| 规格 | 说明 |
|---|---|
| [Canonical Document Model](./specifications/canonical-document-model.md) | StructaDoc 对外提供的统一结构化文档模型 |
| [Parse Job Lifecycle](./specifications/parse-job-lifecycle.md) | 持久化解析任务的状态、抢占、重试和恢复语义 |

## 实现说明

| 文档 | 说明 |
|---|---|
| [Database Support](./development/database-support.md) | 当前数据库 Provider、迁移边界、配置和验证状态 |
| [File Storage](./development/file-storage.md) | 当前本地文件存储、上传入口、安全限制和待实现能力 |
| [Authentication](./development/authentication.md) | 管理员会话、API Key、scope、bootstrap 和 antiforgery 实现状态 |
| [Document Reading](./development/document-reading.md) | Document 键集分页、详情、下载、缓存和 Range 语义 |
| [Provider Config and Parse Runs](./development/provider-config-and-parse-runs.md) | Provider 配置版本、凭据保护、Parse Run 创建与状态查询 |
| [Provider Execution](./development/provider-execution.md) | Provider 能力、异步任务、流式结果和租约约束的执行快照边界 |

## 文档原则

- README 负责介绍和导航，不承载完整字段级契约。
- ADR 只记录已经做出的架构决定及其后果。
- 规格记录需要被多个组件共同遵守的目标行为。
- 具体数据库表、端点请求响应和部署命令应与首个实现同步产生。
- 规划、实现和验证状态必须明确区分。
- 业务持久化支持 SQLite、PostgreSQL、MySQL 和 MariaDB；数据库差异不得改变领域模型、公共 API 或任务生命周期语义。
