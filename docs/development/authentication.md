# Authentication

- Status: Implementation note
- Last updated: 2026-08-05

## Current Boundary

认证实现遵守 [ADR-0005](../adr/0005-authentication-and-api-clients.md)：管理员浏览器会话和应用 API Client 是两种独立主体。

| Subject | Credential | Stored data | Revocation |
|---|---|---|---|
| Administrator | HttpOnly、SameSite Strict Cookie | 可升级密码哈希、security stamp、启用状态 | 停用账户或更换 stamp 后下一次请求失效 |
| API Client | `Authorization: ApiKey <credential>` | Client UUID、Secret SHA-256、scope、启用/撤销状态 | 停用或写入撤销时间后下一次请求失效 |

Cookie/API Key 验证都会查询权威数据库。当前没有认证缓存，因此撤销不依赖缓存过期时间。

## Bootstrap Administrator

首次部署可以通过 Secret 注入：

```text
Authentication__BootstrapAdministratorEmail
Authentication__BootstrapAdministratorPassword
Authentication__BootstrapAdministratorDisplayName
```

Email 和密码必须同时配置，密码长度为 12–1024 个字符。启动迁移完成后，如果相同规范化 Email 不存在，Host 创建管理员；已有账户的密码、启用状态和 security stamp 不会被 bootstrap 配置覆盖。完成首次创建后应从部署配置删除 bootstrap 密码。

当前尚未实现管理员创建、密码修改和密码恢复 API。

## Administrator Session Flow

1. `GET /api/v1/admin/antiforgery`，保存响应 Cookie 和 `requestToken`。
2. `POST /api/v1/admin/session`，发送 JSON Email/Password，并在 `X-CSRF-TOKEN` Header 中发送 token。
3. 登录成功后重新获取 antiforgery token，因为主体已经从匿名用户变为管理员。
4. 后续 Cookie 写操作发送新的 `X-CSRF-TOKEN`。
5. `DELETE /api/v1/admin/session` 退出，也需要 antiforgery token。

认证失败统一返回 `401`，不区分账户不存在、停用或密码错误。API 端点不会重定向到 HTML 登录页。

登录端点按 `RemoteIpAddress` 使用固定窗口限流，默认每个来源 IP 每分钟 10 次，超限返回 `429`。可通过 `Authentication:LoginPermitLimit` 和 `Authentication:LoginRateLimitWindow` 调整。反向代理部署必须先配置并限制可信代理转发头，否则来源地址只代表直接连接的代理；多实例部署中的限额当前按实例计算。

## API Client Credential

Credential 格式包含版本、公开 Client UUID 和 256-bit 随机 Secret。数据库只保存 Secret SHA-256；完整 Credential 不能恢复，也不得写入日志。创建和轮换响应使用 `Cache-Control: no-store`，并且是唯一返回完整 Credential 的位置。

已登记 scope：

- `documents:read`
- `documents:write`
- `parses:read`
- `parses:write`

当前 Document 上传要求 `documents:write`；列表、详情和原文件下载要求 `documents:read`。Parse Run 创建要求 `parses:write`，状态读取要求 `parses:read`。四个 scope 相互独立。管理员主体由独立策略授权，不需要伪造 API Client scope。Provider 配置管理只允许管理员 Cookie 会话访问，不向 API Client 开放。

## API Client Administration

以下端点只允许管理员 Cookie 会话访问；所有写操作都要求 antiforgery token：

| Method | Path | Behavior |
|---|---|---|
| `GET` | `/api/v1/admin/api-clients` | 按创建时间倒序列出 Client，不返回 Credential 或 Secret 哈希 |
| `POST` | `/api/v1/admin/api-clients` | 创建 Client，返回只显示一次的 Credential |
| `PUT` | `/api/v1/admin/api-clients/{id}` | 修改显示名称和 scope，立即影响后续请求 |
| `POST` | `/api/v1/admin/api-clients/{id}/rotate` | 生成新 Credential，旧 Credential 立即失效 |
| `DELETE` | `/api/v1/admin/api-clients/{id}` | 不可逆撤销；重复撤销保持幂等 |

名称会去除首尾空白，scope 会去重并按固定顺序保存。未知 scope 返回 `400`。轮换和修改使用并发版本比较；Client 被撤销或同时发生其他变更时返回 `409`。撤销是终态，不能通过轮换重新启用；需要恢复调用方时应创建新的 API Client。

## Data Protection

`Authentication:DataProtectionKeysPath` 默认是 `./data/keys`。该目录保存 Cookie、antiforgery 和 Provider 凭据加密使用的 ASP.NET Core Data Protection key ring，必须放入持久卷并限制文件权限。删除或更换 key ring 会使现有管理员会话与 antiforgery token 失效，并使已保存的 Provider 凭据无法解密。

多实例 Host 必须共享同一 key ring。当前只实现文件系统持久化；在无法安全共享文件卷的环境中，管理员会话暂不具备多实例发布支持，API Key 不受该限制。

## Remaining Work

- 管理员创建、停用、密码变更和安全审计；
- API Client 管理网页和持久化安全审计；
- 登录失败审计和可配置锁定策略；
- 生产 HTTPS、反向代理和 Cookie Secure 部署验证；
- 可选 OIDC 管理员登录；
- 多实例外部 Data Protection key ring 方案。
