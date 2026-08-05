# Provider 执行边界

本文记录当前 Provider 执行契约的实现范围。Provider 的架构职责以 [ADR-0002](../adr/0002-parser-provider-abstraction.md) 为准，任务状态和恢复规则以 [`parse-job-lifecycle.md`](../specifications/parse-job-lifecycle.md) 为准。

## 当前内部契约

`IParseProvider` 隔离具体 MinerU 协议，并定义以下异步能力：

- 返回原生支持的媒体类型、文件大小/页数限制和取消能力；
- 以 Parse Run ID、不可变 Provider 配置、非敏感 options 和可重复打开的源文件流准备并提交任务；需要远端预分配的 Provider 会先返回必须持久化的提交 checkpoint；
- 使用独立的外部任务 ID 查询 Provider 状态；
- 以流的形式打开最终结果，避免把 ZIP 或大型 JSON 强制读入内存；
- 在 Provider 支持时尝试取消外部任务。

任务状态使用 Provider 内部枚举表达，不直接成为公共 Parse Run 状态。适配器使用 `ProviderException` 返回稳定错误码、脱敏消息和瞬时/配置/输入/永久/安全分类；不得把上游响应正文、Token 或带签名查询参数的 URL写入异常或日志。

`ProviderCredential` 默认字符串表示固定为 `[redacted]`，只有适配器构造认证请求时才显式读取值。这减少意外结构化日志泄露的风险，但不替代日志审查和内存边界控制。

## 执行快照

`IParseRunExecutionContextStore` 只接受当前 `ParseRunLease`。数据库查询同时验证：

- Parse Run 仍处于 `claimed` 或 `running`；
- `claimedBy`、并发版本和未过期租约完全匹配；
- Parse Run 固定的 Provider Config ID 与 Version ID 对应同一个不可变版本；
- Document 和源存储引用仍存在。

返回值包含 Document 元数据、内部存储引用、非敏感 options、当前 Stage、已有外部任务 ID、解密后的内部提交 checkpoint，以及从固定版本加载的 Base URL、model、backend 和解密凭据。它不会改读管理员后来切换的当前版本，也不要求旧版本仍处于启用状态；checkpoint 的 continuation 不进入字符串表示或公共 DTO。

## 执行状态与心跳

`IParseRunStateStore` 只允许当前运行租约更新 Stage。Local 等原子提交在获得任务 ID 后，从 `submitting` 使用同一个 compare-and-set 写入 ID 并进入 `waiting-provider`。Cloud 先由 `IParseRunSubmissionCheckpointStore` 原子写入外部 ID 与加密 continuation、保持 `submitting`，确认上传后再清除 continuation 并进入 `waiting-provider`；ID 不允许覆盖。租约过期的 `running` 任务只要已有外部任务 ID即可进入自动接管候选，新 Worker 保留原 Stage 和 attempt，复用已有 checkpoint 或继续查询既有任务。

Host 的 `ParseRunLeaseHeartbeat` 把心跳续租与阶段、外部任务 ID 写入串行化在一个会话内，并始终传播最新并发版本。续租失败会取消会话执行 token，使后续 Provider 调用可以停止。它不会取消已经在远端完成的请求，因此所有数据库写入仍必须验证当前租约。

`IProviderResultIntake` 已提供 Provider ZIP 结果的固定逻辑键落盘和只读安全校验。它限制压缩包大小、条目数、单条目/总展开大小、压缩比和路径，并拒绝路径穿越、跨平台重复路径、链接及特殊文件。具体行为见 [`provider-result-intake.md`](./provider-result-intake.md)。

`IProviderResultNormalizer` 和首个 `MinerUResultNormalizer` 已把已观察的 Markdown、content list、layout、model output 与图片映射成 Canonical Parse Bundle。派生存储键和资源 UUID 可在崩溃重试时稳定复现，具体规则见 [`provider-result-normalization.md`](./provider-result-normalization.md)。

`MinerUCloudParseProvider` 和 `MinerULocalParseProvider` 已按两套独立协议实现能力报告、提交、状态查询、流式结果和当前上游取消能力。Cloud 使用可恢复的签名 batch 上传且不向上传/CDN 主机转发 Token；Local 使用 multipart `/tasks`。适配器、options 白名单、错误分类与出站边界见 [`mineru-http-providers.md`](./mineru-http-providers.md)。

## 当前未启用执行的原因

Host 的维护 Worker 仍只做过期抢占和重试恢复。HTTP 适配器已注册，但当前不会解析 `queued` 任务，因此不会产生 MinerU 出站请求。启用真实提交前还需要同一执行链具备：

1. 把 Provider 调用、现有心跳会话、结果接收、归一化和 Canonical 成功事务接入完整执行器；
2. 用部署目标的真实 MinerU 版本和样本补齐协议/输出差异；
3. 取消请求传播和尝试明细记录；
4. 明确管理员配置 Base URL 的部署级出站策略和受信网络边界。

Cloud 的远端预分配中间状态已可恢复，Canonical 结果持久化边界也已实现；但完整编排器和部署集成证据尚未完成，因此当前只注册适配器，不由维护 Worker 调用。

## 已验证行为

SQLite 与本地存储测试覆盖：媒体类型能力匹配、凭据默认脱敏、重复 Provider 类型拒绝、Cloud checkpoint 加密保存/清除与恢复提交、签名上传不转发 Token、私网目标拒绝、Local multipart、Provider 状态/错误映射和结果流所有权、按类型解析适配器、执行上下文固定读取旧配置版本并拒绝过期并发令牌、阶段与外部 ID 条件写入、运行任务单次接管、心跳与状态写入共享最新租约、ZIP 受限接收，以及 Cloud/Local MinerU 条目识别、Canonical 映射和确定性重放。服务端数据库契约也包含 checkpoint、状态写入与接管，但本机缺少容器运行时，仍待实际执行。
