# 用户工作台与 OIDC

StructaDoc 的 Web 界面是面向用户的产品，而不只是管理员后台。登录用户可以上传和筛选自己的文档、创建解析任务、查看规范化结果、下载原文和导出结果，也可以把文档权限授予另一个 OIDC 主体。管理员在同一个应用中额外看到 Provider Config 和 API Client 管理区。

## 身份边界

- 外部交互用户使用标准 OIDC Authorization Code + PKCE 登录。
- 用户的稳定身份键是 `(issuer, subject)`，不使用邮箱、用户名或某个 Identity Provider 的私有用户 ID。
- `Authority`、Client、Scope、Claim/Role 映射全部通过 `Oidc` 配置节注入。SignaCore 可作为兼容 OIDC Provider 接入，但 StructaDoc 不引用或绑定 SignaCore 代码。
- 本地管理员 Cookie 继续保留，职责是首次引导和 Identity Provider 故障时的 break-glass 管理。
- API Client 继续使用独立 API Key 和 scope，不复用浏览器 Cookie。

文档创建时会记录 OIDC owner。Owner 拥有完整文档权限；共享授权以目标 `(issuer, subject)` 和 `read/write/parse/export/delete/share` 权限集合保存。管理员和有对应 scope 的服务客户端维持全局服务访问能力。

```json
{
  "Oidc": {
    "Enabled": true,
    "Authority": "https://identity.example.com",
    "ClientId": "structadoc",
    "ClientSecret": "from-secret-store",
    "RequireHttpsMetadata": true,
    "Scopes": ["openid", "profile", "email"],
    "RoleClaimType": "role",
    "AdministratorRole": "structadoc-admin"
  }
}
```

生产环境的 Client Secret 必须由环境变量或 Secret 管理设施注入。
