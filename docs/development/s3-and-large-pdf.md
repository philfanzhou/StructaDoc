# S3 兼容存储与大 PDF 分段

`Storage:Provider` 支持 `Local` 和 `S3`。S3 实现兼容 AWS S3、MinIO 等 S3-compatible 服务，使用条件写入避免静默覆盖已有不同内容，并以 SHA-256 metadata 验证幂等重放。

```json
{
  "Storage": {
    "Provider": "S3",
    "ServiceUrl": "https://minio.example.com",
    "Region": "us-east-1",
    "Bucket": "structadoc",
    "Prefix": "production",
    "ForcePathStyle": true
  }
}
```

Access Key 和 Secret Key 可以省略以使用 AWS SDK 默认凭据链；显式凭据必须成对从 Secret 注入。Readiness 检查会验证 Bucket 可访问性。

当 PDF（包括 Office 转换生成的 PDF）超过 Provider 声明的 `MaxFileBytes` 或 `MaxPages` 时，执行器按页创建受限分段。每段使用确定性 ID，并持久化页码范围、源对象、SHA-256、提交 checkpoint、外部任务 ID 和阶段状态。

进程重启后会复用已创建的分段、已提交的外部任务和已下载的 Provider Archive。所有分段规范化后，执行器把局部页码转换为原文全局页码，重建连续 Block sequence，合并 Assets 和 Markdown，并一次性提交父 Parse Run 的 Canonical Bundle。单页自身仍超过 Provider 限制时返回明确的永久输入错误。

全文检索、OpenSearch、Embedding、RAG、元数据/LLM 扩展不属于本次实现范围。
