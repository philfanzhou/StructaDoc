# Provider Config 与 Parse Run 创建

本文记录当前实现；目标状态机和 Provider 抽象仍分别以 [`parse-job-lifecycle.md`](../specifications/parse-job-lifecycle.md) 和 [ADR-0002](../adr/0002-parser-provider-abstraction.md) 为准。

## Provider Config 持久化

Provider 配置分成逻辑配置和不可变版本：

- `provider_configs` 保存名称、Provider 类型、启用状态、默认标记和当前版本 ID；
- `provider_config_versions` 保存版本号、Base URL、model、backend 和加密凭据；
- 创建配置生成版本 1，任意更新都生成新版本，旧版本不被覆盖；
- Provider 类型创建后不可修改；需要切换类型时应创建新的逻辑配置；
- 全局最多一个启用的默认配置；停用配置时不能继续标记为默认；
- 当前不提供删除接口，从而保证已存在 Parse Run 引用的版本仍可读取。

支持的类型标识为 `mineru-cloud` 和 `mineru-local`。Base URL 必须是无 user-info 和 fragment 的绝对 HTTP(S) URL。这里的格式校验不替代执行 Provider 请求前的 DNS、地址范围和重定向 SSRF 防护。

凭据通过用途隔离的 ASP.NET Core Data Protection protector 加密后写入数据库。HTTP 列表、创建和更新响应只返回 `hasCredential`，不返回明文或密文。更新请求中省略 `credential` 会沿用上一版本的密文；`clearCredential: true` 显式清除；两者同时提供会返回 `400`。`Authentication:DataProtectionKeysPath` 必须使用受限权限的持久部署卷或平台 Secret，且备份必须与数据库备份配套。

管理端点只允许管理员 Cookie 会话访问，写操作要求 antiforgery token：

| Method | Path | 行为 |
|---|---|---|
| `GET` | `/api/v1/admin/provider-configs` | 列出每个逻辑配置的当前版本 |
| `POST` | `/api/v1/admin/provider-configs` | 创建逻辑配置和版本 1 |
| `PUT` | `/api/v1/admin/provider-configs/{id}` | 创建并切换到新版本 |

## Parse Run 创建

`POST /api/v1/documents/{documentId}/parse-runs` 要求管理员会话或 `parses:write`。管理员请求还要求 antiforgery token。请求可指定 `providerConfigId`；省略时使用当前启用的默认配置。没有可用配置时返回 `503`，显式 ID 不存在时返回 `404`。

成功创建会持久化 `queued` 状态、Document ID、Provider 类型、逻辑配置 ID、不可变版本 ID、非敏感 options JSON、源/计划提交媒体类型、最大尝试次数、调用主体和时间。默认最大尝试次数为 3，可请求 1–10；options 必须是最多 16 KiB 的 JSON object，并拒绝任何层级中名为 credential、password、secret、token、API key 或 authorization 的字段。当前阶段尚未执行 Provider 能力协商，因此 `submittedMediaType` 初始等于源媒体类型，未来 Worker 决定需要 LibreOffice 转换时会记录独立转换信息。

调用方可发送单个、最多 256 个可见 ASCII 字符的 `Idempotency-Key`。幂等范围是认证主体、Document 和 Parse Run 创建操作：首次创建返回 `201`；重复请求返回原记录、`200` 和 `Idempotency-Replayed: true`，不会因默认 Provider 后续改变而创建新任务。不提供该 Header 时，每次请求都会创建独立 Parse Run。

`GET /api/v1/parse-runs/{id}` 要求管理员会话或 `parses:read`，返回稳定状态、阶段、配置版本快照、非敏感 options、媒体类型、尝试次数、脱敏错误和时间字段。响应不暴露 Worker lease、内部并发版本、调用主体或 Provider 外部任务 ID。

## 尚未实现

- MinerU Cloud / Local HTTP 适配和连接测试；
- Provider 能力协商、LibreOffice 回退和实际 Worker 执行；
- Provider 配置与 Parse Run 的管理网页和审计日志；
- 解析取消、结果 Blocks/Assets/Artifacts 和成功提交；
- 面向生产的 Provider 出站 SSRF 策略与多实例凭据 key-ring 部署验证。
