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
| [ADR-0006](./adr/0006-user-workspace-and-oidc.md) | Accepted | 用户工作区、泛化 OIDC 认证和资源级授权 |
| [ADR-0007](./adr/0007-resource-lifecycle-and-cleanup.md) | Accepted | 文档所有权、共享和可靠删除清理 |

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
| [Office Conversion](./development/office-conversion.md) | LibreOffice 受限子进程、转换快照、恢复和 normalized-pdf Artifact |
| [MinerU HTTP Providers](./development/mineru-http-providers.md) | Cloud 签名上传、Local multipart、状态/结果流和当前安全边界 |
| [Provider Result Intake](./development/provider-result-intake.md) | Provider ZIP 的幂等落盘、资源限制、安全校验和当前归一化边界 |
| [Provider Result Normalization](./development/provider-result-normalization.md) | 已验证 MinerU ZIP 的条目识别、派生存储、确定性 ID 和 Canonical 映射 |
| [Canonical Result Persistence](./development/canonical-result-persistence.md) | Parse Bundle 验证、存储复核、幂等成功事务和当前限制 |
| [Continuous Integration](./development/continuous-integration.md) | 常规测试、真实数据库契约、生产容器和浏览器工作流 |

## 部署

| 文档 | 说明 |
|---|---|
| [Single Container](./deployment/single-container.md) | 当前 Host、LibreOffice、字体与 SQLite 持久卷的单容器构建和运行边界 |

## 文档原则

- README 负责介绍和导航，不承载完整字段级契约。
- ADR 只记录已经做出的架构决定及其后果。
- 规格记录需要被多个组件共同遵守的目标行为。
- 具体数据库表、端点请求响应和部署命令应与首个实现同步产生。
- 规划、实现和验证状态必须明确区分。
- 业务持久化支持 SQLite、PostgreSQL、MySQL 和 MariaDB；数据库差异不得改变领域模型、公共 API 或任务生命周期语义。
