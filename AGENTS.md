# StructaDoc Agent Contract

## 1. Authority

- 用户当前请求优先于本文件的默认工作流。
- 本文件是仓库级协作和变更规则。
- `README.md` 定义产品定位、公开边界和项目入口。
- `docs/README.md` 是设计决策和规格的导航入口。
- 已接受的 ADR 定义难以逆转的架构决策。
- `docs/specifications/` 定义跨组件共享的目标契约。

## 2. Read Before Change

1. 先读 `README.md`，确认任务是否属于 StructaDoc 的产品边界。
2. 再读 `docs/README.md`，定位相关 ADR 和规格。
3. 涉及公共结构化数据时，必须读 `docs/specifications/canonical-document-model.md`。
4. 涉及解析任务、Worker、重试或恢复时，必须读 `docs/specifications/parse-job-lifecycle.md`。
5. 最后读取目标代码、测试、配置和部署文件，再进行修改。

入口缺失时应按实际目录定位并报告，不得编造文件、行为或完成状态。

## 3. Truth Model

- 在尚未实现的阶段，README、已接受 ADR 和规格描述目标行为（to-be）。
- 代码、测试、配置、迁移和部署文件描述当前实现事实（as-is）。
- 规格与实现冲突时，必须先判断是实现未完成、文档过期还是需求变化。
- 不得把计划中的端点、数据表、部署命令或测试写成已经可用。

## 4. Product Boundary

- StructaDoc 负责文档摄取、异步解析、结果归一化、结构化存储和版本化 API 输出。
- StructaDoc 不内置全文检索、向量检索、Embedding、RAG 或特定领域数据生成。
- 调用方通过公共 API 消费结构化数据，不直接读写 StructaDoc 数据库或对象存储。
- 管理员网页会话与应用 API Client 凭据必须分离。
- 管理员负责配置解析 Provider；普通上传者和 API Client 不得修改 Provider 配置。

超出边界的功能必须先由用户确认，并通过新 ADR 修改边界后再实现。

## 5. Architecture Rules

- MinerU Cloud、MinerU Local 和未来解析器必须通过 Provider 适配层接入。
- 公共 API 不得直接暴露某个 Provider 的任务协议或以其原始 JSON 作为稳定契约。
- 所有 Provider 结果必须归一化为 `canonical-document-model.md` 定义的结构。
- 每次 Parse Run 必须保存 Provider、配置版本和解析参数快照。
- Provider 配置版本不可变；仍被非最终 Parse Run 引用的版本不得删除或失效。
- 外部 Provider 的临时任务状态不是 StructaDoc 的权威状态。
- 原始上传文件必须保留；转换后的 PDF 是独立 Artifact，不覆盖原文件。
- Provider 原生支持源格式时优先原生提交，不支持时才使用镜像内置的 LibreOffice 转换能力回退。
- 大型文件和原始解析产物存入本地文件系统或 S3 兼容对象存储；数据库保存业务元数据、结构化字段和存储引用。
- 第一阶段使用 PostgreSQL 持久化解析任务；Worker 必须原子抢占、维护租约并支持崩溃恢复。

## 6. Public Contract and Compatibility

- 公共 HTTP API 必须使用版本化路径。
- 对公共 DTO、状态值、Block 类型和坐标语义的破坏性修改必须升级契约版本。
- 同一主版本内优先使用新增可选字段演进。
- API Client 必须能够忽略未知字段和未知 Block 类型。
- Provider 原始字段只能放入明确标记为非稳定的扩展区域或 Raw Artifact。
- 数据库和对象存储的内部引用不得作为公共 API 字段暴露。

## 7. Security

- 不得提交真实 Token、密码、连接字符串或私有文档样本。
- Provider Token 和存储凭据不得返回浏览器或写入日志。
- 数据库中的 Provider 凭据必须加密，主密钥由环境变量或部署平台 Secret 注入。
- 在线 Provider 会产生外部数据传输，管理界面和文档必须明确说明。
- 上传校验不能只信任客户端 MIME 类型；必须限制大小、处理时间、内存和临时磁盘。
- 外部 URL、预签名 URL 和回调地址必须考虑 SSRF、有效期和最小权限。

## 8. Documentation Impact

以下变化必须在同一变更中同步权威文档：

- 产品边界或明确非目标；
- 公共 API、状态机或统一数据模型；
- Provider 接口和能力语义；
- 数据主责、Artifact 保留和删除规则；
- 认证、凭据、外部数据传输或安全边界；
- 部署依赖和关键运维方式。

内部重构、格式化、局部重命名和不改变行为的测试补充默认不需要新建设计文档。

## 9. Coding and Dependencies

- 沿用仓库已选技术栈、目录、测试工具和依赖。
- 新增依赖前先确认现有代码和平台能力没有等价方案。
- 行为变化和缺陷修复必须有与风险相称的自动化测试。
- 数据库结构变化必须使用可审查、可重复执行的迁移，不依赖仅在启动时执行的零散 DDL。
- Provider 特定代码不得反向污染 Domain 或公共 DTO。
- 日志、异常和注释使用英文；面向最终用户的界面文本可按产品本地化策略处理。

## 10. Verification and Safety

- 只读分析任务不得实施变更。
- 保留并绕开无关用户修改，不得擅自回退、覆盖、提交或推送。
- 删除、移动或批量改写前验证精确目标。
- 运行了哪些构建和测试、哪些未运行、失败原因是什么，都必须如实说明。
- 提交前检查文档链接、格式、敏感信息和仓库状态。
