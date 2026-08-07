# 结果 API、导出与资源生命周期

公共 API 只返回 StructaDoc 的规范化 DTO：Parse Run、Page、Block、Asset 和 Artifact 元数据。Provider 原始 JSON、内部 `StorageRef`、checkpoint 和凭据不会出现在公共响应中。二进制内容只能通过鉴权下载端点读取。

```text
GET    /api/v1/documents/{documentId}/parse-runs
GET    /api/v1/parse-runs/{parseRunId}
GET    /api/v1/parse-runs/{parseRunId}/pages
GET    /api/v1/parse-runs/{parseRunId}/blocks
GET    /api/v1/parse-runs/{parseRunId}/assets
GET    /api/v1/parse-runs/{parseRunId}/assets/{assetId}/content
GET    /api/v1/parse-runs/{parseRunId}/artifacts
GET    /api/v1/parse-runs/{parseRunId}/artifacts/{artifactId}/content
GET    /api/v1/parse-runs/{parseRunId}/markdown
GET    /api/v1/parse-runs/{parseRunId}/exports/{markdown|html|zip|pdf}
```

Block 使用 `afterSequence` 做稳定游标分页；公共 Block DTO 不包含 `ProviderDataJson` 或原始 source locator。HTML 导出由规范化 Markdown 生成，ZIP 包含 Markdown 和受控 Assets，PDF 导出使用规范化 PDF Artifact。

删除不是一次跨数据库和对象存储的脆弱同步操作：API 先在事务中把目标标为 `deletion-pending`，并把完整对象引用快照写入唯一 Cleanup Job；后台 Worker 幂等删除原文、转换 PDF、Provider Archive、分段 PDF、分段 Archive、Assets 和 Artifacts；全部对象成功后才删除关系数据并把 Job 标为 `completed`。失败进入带退避的 `retry-wait`，崩溃遗留的 `running` Job 也会恢复。

非最终状态的 Parse Run 不能删除；包含活动 Parse Run 的 Document 也不能删除。这避免清理和执行 Worker 竞争同一资源。
