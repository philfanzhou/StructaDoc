# Document Reading

- Status: Implementation note
- Last updated: 2026-08-05

## Authorization

Document 读取端点允许管理员 Cookie 会话，或具有 `documents:read` scope 的 API Client。`documents:write` 不隐含读取权限。GET 请求不需要 antiforgery token。

## Endpoints

| Method | Path | Behavior |
|---|---|---|
| `GET` | `/api/v1/documents?limit=50&cursor=...` | 按创建时间和 ID 倒序分页 |
| `GET` | `/api/v1/documents/{id}` | 返回单个 Document 元数据 |
| `GET` | `/api/v1/documents/{id}/content` | 下载不可变的原始文件 |

列表默认返回 50 项，`limit` 范围是 1–200。响应的 `nextCursor` 是不透明值；调用方只能原样传入下一次请求，不应解析、构造或长期保存。没有下一页时为 `null`。

分页使用 `(createdAt, id)` 键集而不是 offset，并由数据库复合索引支持。翻页期间新上传的文档不会造成已读取项重复；分页不是数据库快照，新文档应在调用方下一次从第一页读取时出现。

列表和详情只返回公共 Document ID、清理后的原文件名、检测后的媒体类型与扩展名、大小、SHA-256 和创建时间。内部 `storageRef`、上传主体和内部 metadata 不属于当前公共响应。

## Content Download

原文件响应具有以下语义：

- `Content-Disposition: attachment`，文件名由服务端安全编码；
- 检测后的 `Content-Type` 和 `X-Content-Type-Options: nosniff`；
- `Content-Security-Policy: sandbox`；
- SHA-256 生成的强 ETag，支持 `If-None-Match` 返回 `304`；
- 支持单个或框架允许的字节 Range 请求及 `206 Partial Content`；
- `Cache-Control: private, max-age=0, must-revalidate`，允许调用方私有缓存，但每次复用前验证权限和内容版本。

数据库中不存在 Document 时返回 `404`。Document 存在但存储对象缺失时返回不包含内部路径的通用 `503`，同时在服务端记录 Document ID。当前本地文件实现支持 Range；未来 S3 实现必须保持相同 HTTP 契约，不能先把整个对象缓冲到内存。

## Remaining Work

- Document 删除、审计及数据库/对象存储补偿；
- 列表过滤、搜索条件和相应的游标版本化；
- S3 兼容对象存储的流式 Range 读取；
- 解析状态、产物和 Block/Asset 读取端点。
