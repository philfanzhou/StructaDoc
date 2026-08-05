# Canonical 结果持久化

本文记录当前 Parse Bundle 验证和成功提交实现。字段语义以 [`canonical-document-model.md`](../specifications/canonical-document-model.md) 为准，状态竞争以 [`parse-job-lifecycle.md`](../specifications/parse-job-lifecycle.md) 为准。

## 持久化结构

当前关系模型增加：

- `parse_pages`：以 Parse Run ID 和正整数页码作为复合主键；
- `parse_blocks`：保存全局连续 sequence、可选页码、类型、内容、归一化 bbox、置信度和 Asset 引用；
- `parse_assets`：保存图片等二进制资源的内部存储引用、大小和 SHA-256；
- `parse_artifacts`：保存 Markdown、Provider Archive、Content List、Layout 等产物元数据；
- `parse_runs.result_schema_version`、`result_sha256` 和 `provider_metadata_json`：保存成功 Bundle 的版本、幂等指纹和脱敏 Provider 元数据。

Asset 与 Artifact 的 `storageRef` 只属于内部模型。Artifact 使用 `(parseRunId, type, name)` 区分同类型分片；Asset 展示名允许重复，身份由 UUID 决定。Block 的复合外键保证 Page 和 Asset 必须属于同一个 Parse Run。

## Bundle 验证

第一版只接受 schema `1.0`，并验证：

- Page 编号为正数且唯一；Block sequence 从零开始、连续并按列表顺序排列；
- Block 页码和 Asset ID 引用同一 Bundle；
- bbox 为有限的 0–1 坐标且满足边界顺序，confidence 位于 0–1；
- 类型、subtype、content format 和 Artifact type 使用小写 token；
- Asset / Artifact UUID、媒体类型、正数大小、小写 SHA-256 和相对 POSIX 存储引用合法；
- Provider metadata、source locator、Artifact metadata 和 provider data 是受大小限制的 JSON object；
- JSON 扩展拒绝凭据字段、内部路径字段和带查询参数的 HTTP(S) URL。

单项和聚合限制当前包括 10,000 Pages、100,000 Blocks、10,000 Assets、10,000 Artifacts、单 Block 最多 4 MiB 字符、全部 Block 内容最多 64 MiB 字符，以及全部 JSON 扩展最多 64 MiB。限制属于内部防护边界；后续真实样本若证明不合适，可以在不改变公共字段语义的情况下调整。

## 成功提交

`IParseBundleCommitStore` 的顺序是：

1. 复制集合形成不可变的本次提交快照并验证 Bundle；
2. 流式读取每个唯一 `storageRef`，复核实际大小和 SHA-256；
3. 计算流式序列化的 Bundle SHA-256 指纹；
4. 开启数据库事务并再次确认 Parse Run 仍为 `running`、租约持有者/并发版本匹配且租约未过期；
5. 写入全部 Canonical 结果行，并在同一事务把 Parse Run 标记为 `succeeded`；
6. 清除租约和旧错误，写入 `completedAt`、schema、指纹和 Provider metadata。

相同指纹对已成功任务返回 `AlreadyCommitted`；不同指纹返回冲突。数据库唯一键冲突、已存在部分结果、过期租约或并发取消都不会留下目标 Parse Run 的部分结果行，也不会覆盖已有状态。

## 当前限制

- Provider ZIP 解包、路径穿越/解压炸弹防护和 MinerU 结果归一化尚未实现；
- Raw Artifact 是否包含 Provider 返回的敏感正文必须在下载与清理阶段额外检查，不能只靠元数据验证；
- 结果表目前只有内部持久化模型，尚未提供 Blocks、Assets 和 Artifacts 公共读取端点；
- SQLite 已执行真实事务测试；PostgreSQL、MySQL 和 MariaDB 的同一契约已编译进容器测试，但需要可用容器运行时后才能标记真实验证通过。
