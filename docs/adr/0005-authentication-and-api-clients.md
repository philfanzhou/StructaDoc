# ADR-0005：分离管理员 Cookie 会话与 API Client 密钥认证

- Status: Accepted
- Date: 2026-08-05

## Context

StructaDoc 同时服务管理网页和其他应用。浏览器管理员需要登录、维护 Provider 和管理文档；应用调用方需要稳定的机器凭据和最小权限范围。让两类调用方共享 Cookie、管理员密码或不区分用途的 Token，会扩大凭据泄漏和越权风险。

项目还要求单镜像自托管、SQLite/PostgreSQL/MySQL/MariaDB 可移植，以及尽量使用 .NET 平台能力。第一阶段不需要完整的注册、社交登录、多租户或邮件找回流程。

## Decision

### Administrator Session

- 本地管理员保存在业务数据库的独立 `admin_users` 表中。
- 密码使用 ASP.NET Core `PasswordHasher<TUser>` 保存可升级的单向哈希，不自行设计密码算法。
- 管理网页使用独立 Cookie scheme；Cookie 为 HttpOnly、SameSite Strict，并具有有限会话寿命。
- Cookie principal 包含管理员 ID 和 security stamp。每次授权请求检查数据库中的启用状态和 stamp，使停用账户或变更安全信息可以撤销现有会话。
- 浏览器 Cookie 发起的写操作必须验证 antiforgery token。
- 第一个管理员可以通过只从环境变量或部署 Secret 注入的 bootstrap 配置创建；已有账户不会被 bootstrap 密码覆盖。

### API Client

- 应用调用方保存在独立 `api_clients` 表中，不复用管理员账户或 Cookie。
- API Key 由公开 Client UUID 和至少 256 bit 随机 Secret 组成。完整 Key 只在创建时显示；数据库只保存 Secret 的 SHA-256。
- 每次请求使用固定时间比较验证 Secret，并检查 Client 是否启用或撤销。
- API Client 权限使用明确 scope，例如 `documents:read`、`documents:write`、`parses:read` 和 `parses:write`。
- API Key 通过 `Authorization: ApiKey <credential>` 发送，不进入 URL、Cookie 或日志。

### Authorization

- 管理员和 API Client 使用不同 subject type claim 与 authentication scheme。
- 管理员可以执行管理策略允许的操作；API Client 必须具有端点要求的 scope。
- Document 上传允许已登录管理员，或具有 `documents:write` 的 API Client。API Key 请求不需要 antiforgery token，因为浏览器不会自动附带该凭据。

### Data Protection

管理员 Cookie 和 antiforgery token 使用 ASP.NET Core Data Protection。Key ring 持久化到配置路径并设置固定应用名。单实例部署应把该路径放入持久卷；多实例部署必须共享相同 key ring 或在后续 ADR 中采用外部密钥管理方案。

## Consequences

### Positive

- 浏览器和机器凭据的生命周期、传输方式与权限可以独立演进。
- API Key 数据库泄漏不会直接暴露高熵 Secret。
- 使用 ASP.NET Core Cookie、PasswordHasher、Authorization 和 Antiforgery，减少自定义安全协议。
- 认证表与业务模型共用四套 EF Core 迁移，保持数据库可移植性。

### Trade-offs

- Cookie 请求需要前端处理 antiforgery token。
- 每次 Cookie/API Key 请求查询数据库以支持及时撤销；后续若增加缓存，必须保留有界撤销延迟。
- 本地 bootstrap 适合首个自托管版本，但成熟部署仍需要管理员管理、密码变更、审计和可选 OIDC。
- 多实例管理员会话依赖共享 Data Protection key ring。

## Rejected Alternatives

### 管理员和 API Client 共用 API Key

拒绝。浏览器会话、人工账户和机器凭据具有不同风险、撤销和权限需求。

### 把完整 API Key 加密后保存

拒绝。服务只需要验证调用方，不需要恢复原 Secret；高熵 Secret 的单向哈希减少数据库泄漏影响。

### 第一阶段引入完整 ASP.NET Core Identity 表集

暂不采用。当前只有本地管理员登录，不需要角色、外部登录、用户 Token 等完整存储模型。保留使用平台 PasswordHasher 和 Cookie middleware，未来需求增长时可以通过新迁移演进。
