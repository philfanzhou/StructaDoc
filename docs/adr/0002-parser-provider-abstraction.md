# ADR-0002：使用 Provider 适配解析服务并归一化结果

- Status: Accepted
- Date: 2026-08-05

## Context

StructaDoc 第一阶段需要同时支持 MinerU 在线托管服务和本地部署的 `mineru-api`。两者在认证、文件传递、任务提交、状态查询、结果下载、格式支持、限制和生命周期方面并不相同，接口也可能随 MinerU 版本继续变化。

如果公共 API、数据库或任务 Worker 直接依赖某一种 MinerU 响应格式，调用方会被迫理解 Provider 细节，切换在线与本地服务也会变成破坏性变更。

## Decision

### 1. Provider 适配层

每种解析服务通过独立 Provider 适配器接入。Provider 在概念上需要承担以下能力：

- 验证管理员配置并测试连接；
- 报告支持的文件格式、模型、选项和限制；
- 提交文档解析任务；
- 查询外部任务状态和进度；
- 获取最终结果和原始产物；
- 在上游支持时尝试取消任务；
- 将错误分类为可重试、配置错误、输入错误或永久失败。

第一阶段包含：

- MinerU Cloud Provider；
- MinerU Local Provider。

### 2. StructaDoc 状态是权威状态

外部 Provider 的任务状态只是一项集成事实。StructaDoc Parse Run 是面向管理员和调用方的权威任务记录。

- 外部任务 ID 必须与 Parse Run ID 分离保存。
- 外部任务短期保留或服务重启不能删除 StructaDoc 已保存的结果。
- Worker 在重启后优先使用已保存的外部任务 ID 恢复轮询，而不是重复提交。

### 3. 统一结果模型

Provider 输出必须转换成 [`canonical-document-model.md`](../specifications/canonical-document-model.md) 定义的统一结构。

- 公共 API 只承诺 StructaDoc 字段的语义。
- Provider 原始 ZIP、JSON 和未知字段作为 Raw Artifact 或非稳定扩展保留。
- Provider 特有字段不得成为所有调用方必须处理的字段。

### 4. 配置由管理员控制

- 普通上传者和 API Client 不能修改 Provider 配置。
- Provider Token 不返回浏览器，也不写入日志。
- 每次 Parse Run 保存 Provider 类型、配置 ID、配置版本和解析参数快照。
- Provider 配置版本不可变；修改配置创建新版本，而不是原位覆盖旧版本。
- 被停用的配置版本不能创建新任务，但在仍被非最终 Parse Run 引用时必须保留并可供 Worker 解密使用。
- 修改默认 Provider 不改变已经创建的 Parse Run。

### 5. 能力驱动的文件处理

Worker 根据 Provider 报告的能力决定如何提交文件：

1. Provider 原生支持源格式时优先提交原文件。
2. 不支持时，通过内置 LibreOffice 转换能力生成 PDF 后提交；默认部署形态见 [ADR-0003](./0003-technology-and-single-image-deployment.md)。
3. 原文件始终保留，转换文件作为独立 Artifact。
4. Parse Run 记录源格式、实际提交格式和转换信息。

## Consequences

### Positive

- 在线和本地 MinerU 可以在不改变公共 API 的情况下切换。
- MinerU 接口升级的影响被限制在对应 Provider 中。
- 未来可以增加其他解析器，而不污染 Domain 和调用方契约。
- 原始产物得到保留，出现归一化问题时可以追溯和重新处理。

### Trade-offs

- 需要维护 Provider 能力模型和结果归一化测试样本。
- 不同 Provider 的结果不可能完全等价，统一模型必须允许缺失字段和未知类型。
- Provider 原始数据与统一数据会占用额外存储空间。
- 取消外部任务只能是 best-effort，具体效果取决于 Provider 能力。

## Rejected Alternatives

### 直接把 MinerU 响应作为公共 API

拒绝。它会把调用方绑定到 MinerU 的版本、模式和部署方式。

### 在线和本地 MinerU 共用一个硬编码客户端

拒绝。两者协议和生命周期不同，共用客户端会产生大量条件分支并模糊错误语义。

### 所有 Office 文件一律先转 PDF

拒绝作为默认策略。原生提交可以保留更多文档语义；转换只作为 Provider 不支持源格式时的回退。
