# Provider 结果接收

本文记录 Provider ZIP 结果进入 StructaDoc 存储前后的安全与幂等边界。任务状态与恢复遵循 [`parse-job-lifecycle.md`](../specifications/parse-job-lifecycle.md)，最终 Artifact 和 Parse Bundle 语义遵循 [`canonical-document-model.md`](../specifications/canonical-document-model.md)。

## 接收顺序

`IProviderResultIntake.StoreArchiveAsync` 当前只接收 ZIP 类型结果：

1. 接受 `application/zip`、`application/x-zip-compressed`，以及实际具有 ZIP 签名的 `application/octet-stream`；
2. 将 Provider 流写入 `parse-runs/{parseRunId}/provider/result.zip` 固定内部逻辑键，同时限制压缩字节数并计算 SHA-256；
3. 相同逻辑键和相同内容幂等复用；已有不同内容时返回稳定冲突，不覆盖原结果；
4. 从存储重新读取对象，验证 ZIP 签名并实际流过每个文件条目；
5. 返回只包含规范化相对路径、压缩/展开大小和目录标记的内存清单。

这一层保留原 ZIP，但不会把 ZIP 条目直接解压到正式存储。已实现的 MinerU 归一化器只从已验证清单选择已知条目，并为提取出的 Asset 和 Artifact 使用独立逻辑键；详见 [`provider-result-normalization.md`](./provider-result-normalization.md)。

恢复 Worker 可以先调用 `TryLoadArchiveAsync`，从固定逻辑键重新计算大小和 SHA-256 并重复全部 ZIP 校验；对象存在且合法时不需要再次依赖 Provider 下载。Raw Archive 的内部展示名固定为 `provider-result.zip`，不使用上游文件名参与 Bundle 身份。

## 安全限制

当前校验拒绝：

- 绝对路径、反斜杠、盘符、空路径段、`.` / `..`、控制字符和超长 UTF-8 路径；
- Unicode NFC 规范化并忽略大小写后重复的跨平台路径；
- Unix 链接/特殊文件和 Windows reparse point；
- 超过配置的压缩包大小、中央目录大小、条目数、单条目大小、总展开大小或单条目压缩比；
- ZIP 中央目录声明大小与实际流式读取大小不一致；
- 空包、损坏包、非 ZIP 内容、ZIP64、多卷 ZIP 和当前运行库无法读取的条目。

错误使用稳定、脱敏的 `ProviderResultIntakeException.ErrorCode` 和 Provider failure category；错误消息不包含恶意条目名或上游响应正文。安全或结构失败会删除本次固定结果对象；租约取消和瞬时存储 I/O 则保留已原子写入的对象，供恢复 Worker 重新校验。

## 配置

| Key | Default | Meaning |
|---|---:|---|
| `ProviderResults:MaxArchiveBytes` | 512 MiB | ZIP 压缩包最大字节数 |
| `ProviderResults:MaxEntryCount` | 20,000 | 最大目录与文件条目总数 |
| `ProviderResults:MaxEntryBytes` | 256 MiB | 单文件实际展开上限 |
| `ProviderResults:MaxExpandedBytes` | 2 GiB | 全包实际展开总上限 |
| `ProviderResults:MaxCompressionRatio` | 200 | 单文件最大展开/压缩比 |
| `ProviderResults:MaxEntryPathBytes` | 2,048 | 单条目路径的 UTF-8 字节上限 |
| `ProviderResults:MaxCentralDirectoryBytes` | 64 MiB | 在构造 ZIP 条目对象前允许扫描的中央目录总大小 |
| `ProviderResults:TemporaryPath` | 系统临时目录下的 `structadoc-provider-results` | 存储回读流不可 seek 时的临时文件目录 |

临时文件使用随机名称和 delete-on-close，并再次按已存对象大小精确限制复制。后续启用多任务并发前仍需把 Worker 并发数和容器临时磁盘配额纳入整体资源预算。

## 当前未实现

- 用更多生产样本扩展 MinerU Cloud / Local ZIP 目录和 JSON 版本识别；
- 实际 Worker 对下载、验包、归一化和成功提交的阶段编排。
