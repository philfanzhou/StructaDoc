# Provider 执行边界

本文记录当前 Provider 执行契约的实现范围。Provider 的架构职责以 [ADR-0002](../adr/0002-parser-provider-abstraction.md) 为准，任务状态和恢复规则以 [`parse-job-lifecycle.md`](../specifications/parse-job-lifecycle.md) 为准。

## 当前内部契约

`IParseProvider` 隔离具体 MinerU 协议，并定义以下异步能力：

- 返回原生支持的媒体类型、文件大小/页数限制和取消能力；
- 以 Parse Run ID、不可变 Provider 配置、非敏感 options 和可重复打开的源文件流提交任务；
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

返回值包含 Document 元数据、内部存储引用、非敏感 options、已有外部任务 ID，以及从固定版本加载的 Base URL、model、backend 和解密凭据。它不会改读管理员后来切换的当前版本，也不要求旧版本仍处于启用状态。

## 当前未启用执行的原因

Host 的维护 Worker 仍只做过期抢占和重试恢复。当前不会解析 `queued` 任务，也没有注册 MinerU Cloud / Local 的 HTTP 实现。启用真实提交前还需要同一执行链具备：

1. 外部任务 ID 的租约约束持久化和运行中租约恢复；
2. 处理期间心跳续租和丢失租约后的取消传播；
3. Raw Artifact 的受限流式保存、ZIP 安全检查和 Provider 结果归一化；
4. Canonical Pages、Blocks、Assets、Artifacts 与 `succeeded` 的幂等最终事务。

在这些条件完成前提交真实外部任务会产生无法可靠恢复或无法安全发布结果的中间状态，因此本轮只建立可测试的边界，不把占位适配器注册为可用 Provider。

## 已验证行为

SQLite 测试覆盖：媒体类型能力匹配、凭据默认脱敏、重复 Provider 类型拒绝、按类型解析适配器，以及执行上下文固定读取旧配置版本并拒绝过期并发令牌。该边界只使用 EF Core 可移植查询；服务端数据库仍需在容器契约测试中实际验证。
