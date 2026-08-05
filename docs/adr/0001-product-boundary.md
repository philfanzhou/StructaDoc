# ADR-0001：StructaDoc 只负责文档摄取与结构化解析

- Status: Accepted
- Date: 2026-08-05

## Context

StructaDoc 的目标是让管理员上传 PDF、Word、Excel、PowerPoint 等文档，通过在线或本地解析服务把文件转换为稳定的结构化数据，并让其他应用通过 API 消费这些数据。

文档解析之后还可能出现全文检索、向量化、知识库构建、RAG、题目生成或其他领域处理。如果这些能力都由 StructaDoc 承担，项目会同时拥有文档解析平台、搜索平台和领域应用的职责，公共数据契约也会被特定调用方需求牵引。

## Decision

StructaDoc 的产品边界固定为：

1. 接收和管理原始文档。
2. 创建并执行持久化异步解析任务。
3. 适配外部或本地文档解析 Provider。
4. 将 Provider 结果归一化为稳定的 Document、Parse Run、Block、Asset 和 Artifact 数据。
5. 保存原文件、结构化结果、原始解析产物和解析历史。
6. 通过版本化 HTTP API 向其他应用提供这些数据。

StructaDoc 不负责：

- 全文检索、向量检索、Embedding 或 RAG 管线；
- 针对题库、词汇、合同、发票等特定领域生成业务实体；
- Office 文档在线编辑；
- 让调用方直接读写 StructaDoc 的数据库或对象存储。

调用方可以在自己的边界内对 StructaDoc 数据进行搜索、向量化、领域提取或其他后处理。

## Consequences

### Positive

- StructaDoc 的公共契约集中在文档结构，不依赖调用方技术选型。
- 默认部署所需组件更少，适合自托管和公共项目使用。
- 不同应用可以对同一解析结果采用不同的检索和领域处理方式。
- Provider、存储和 API 可以独立演进，而不会与某个领域模型耦合。

### Trade-offs

- StructaDoc 本身不提供“上传后立即搜索”的完整知识库体验。
- 调用方需要自行选择检索、向量化和领域处理方案。
- 公共 API 必须提供足够完整的结构、定位和原始产物，避免调用方被迫绕过 API。

## Change Rule

任何把搜索、向量化、RAG 或特定领域实体引入 StructaDoc 核心的提议，都需要新的 ADR 明确修改本决策，不能作为普通功能直接加入。
