# File Storage

- Status: Implementation note
- Last updated: 2026-08-05

## Current Scope

StructaDoc 当前通过 Application 层的 `IFileStorage` 隔离业务逻辑与具体存储实现。第一阶段已经提供本地文件系统实现；S3 兼容实现仍是目标能力，尚未实现。

原始文件使用服务端生成的 Document UUID 构造内部 `storageRef`，格式不属于公共契约。用户文件名只作为清理后的展示值保存，不能参与物理路径拼接，API 响应也不返回 `storageRef`。

本地写入流程：

1. 在存储根目录内的 staging 区创建随机临时文件；
2. 分块写入，同时执行大小限制和 SHA-256 计算；
3. 空文件、超限文件或写入失败时删除临时文件；
4. 写入完成后原子移动到服务端生成的逻辑位置；目标已有相同大小和 SHA-256 时幂等返回，不同内容返回冲突且不覆盖；
5. 从已保存对象重新读取并检测实际文档类型；
6. Document 数据库记录提交失败时尽力删除对应对象。

幂等写入也用于 Parse Run 的固定 Provider 结果逻辑键，使崩溃后的同内容重放能够复用对象。该语义必须由未来 S3 实现通过条件写入和元数据复核保持一致，不能降级为无条件覆盖。

进程在文件移动后、数据库提交前崩溃仍可能产生孤儿对象。后续必须实现基于数据库引用的孤儿扫描；在此之前部署文档不会声称存储与数据库具备跨介质事务。

## Supported Detection

当前服务端检测支持：

| Format | Detection |
|---|---|
| PDF | `%PDF-` 文件签名 |
| DOC / XLS / PPT | OLE Compound File 签名，并结合原始扩展名区分格式 |
| DOCX / XLSX / PPTX | ZIP 包、`[Content_Types].xml` 及对应 Open XML 主部件 |

客户端提交的 MIME 类型不作为权威值。任意 ZIP 不会被当作 Office 文档；包含 VBA Project 的宏文档当前拒绝上传，避免把宏格式错误标记为无宏 Open XML 格式。后续如需支持 DOCM、XLSM 或 PPTM，必须增加独立媒体类型、安全策略和测试。

## Upload Endpoint

开发期端点：

```text
POST /api/v1/documents
Content-Type: multipart/form-data
file=<exactly one file>
```

成功返回 `201` 和不含内部存储引用的 Document 摘要。空文件、无效文件名和错误表单返回 `400`，超过 `Documents:MaxUploadBytes` 返回 `413`，不支持或无法识别的格式返回 `415`。

`Documents:UploadApiEnabled` 默认是 `true`。端点要求已登录管理员，或具有 `documents:write` scope 的 API Client。管理员 Cookie 上传还必须携带 antiforgery token；API Key 请求不需要该 token。保存的 `createdBy` 会记录主体类型和不透明主体 ID。

## Configuration

| Key | Default | Meaning |
|---|---:|---|
| `Documents:UploadApiEnabled` | `true` | 是否映射受授权策略保护的上传端点 |
| `Documents:MaxUploadBytes` | `104857600` | 单文件大小上限 |
| `Storage:Provider` | `Local` | 当前文件存储实现 |
| `Storage:RootPath` | `./data/storage` | 本地存储根目录，应映射到持久卷 |

`/health/ready` 会创建并删除一个小型探测文件，以验证本地存储 staging 目录可写；失败时应用不进入就绪状态。

## Remaining Work

- 管理员和 API Client 管理界面（API Client 管理端点已实现）；
- S3 兼容对象存储实现及就绪检查；
- Document 删除 API；详情、键集分页列表和受控下载已经实现；
- 孤儿对象扫描与删除重试；
- 恶意文档扫描策略和更完整的文件结构验证；
- 存储配额、保留策略、备份与恢复说明。
