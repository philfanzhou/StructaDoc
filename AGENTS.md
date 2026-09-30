# StructaDoc 协作规范

StructaDoc 是一个可自托管的文档摄取与结构化解析服务，负责异步解析、结果规范化、
结构化持久化和版本化 API 输出。

## 维护方式与规则优先级

- 本文件是仓库内约束的唯一事实来源。
- Codex 直接读取本文件；Claude Code 通过根目录 `CLAUDE.md` 导入本文件。
- 本文件与 `.github` 中的 task/PR 模板共同落实统一项目规范；修改项目交付政策时同步更新对应贡献者文档。
- 修改通用规范时只改本文件。除非某条规则确实只适用于单个工具，否则不要把规则正文写进
  `CLAUDE.md` 或其他工具入口文件，以免内容漂移或互相冲突。
- `README.md` 定义产品定位、公开边界和项目入口。
- `docs/README.md` 是设计决策与规范的索引。
- 已接受的 ADR 定义难以逆转的架构决策。
- `docs/specifications/` 定义跨组件共享的目标契约。

## 文档语言

- **仓库内的流程与约束文档用中文**：本文件、`.github/ISSUE_TEMPLATE/` 下的 issue 模板、
  `.github/pull_request_template.md`。
- **提交到 GitHub 的 issue 和 PR，正文一律用中文。** issue 标题用中文；PR 标题沿用英文
  conventional commit 格式（`feat:` / `fix:` / `docs:` 等），因为 squash 合并后它就是
  `main` 上对应 commit 的标题。
- **review 全程用中文**：PR 行内意见、review summary、回复，以及向用户汇报 review 发现的
  问题，一律使用中文。引用的代码、标识符、编译器诊断码和命令行保持原样。
- **产品与技术文档使用英文**：`README.md`、`docs/`、代码注释、日志、异常消息和面向 API
  使用者的文字属于公开或工程交付物，应保持英文。最终用户界面文字遵循产品本地化策略。
- 代码标识符和 commit message 保持英文。

## 分支与合并策略

- `main` 只接受通过 Pull Request 合并的改动。禁止在本地直接提交到 `main`，也禁止直接向
  `origin/main` 推送；即使账号权限或远端配置允许，也不得绕过 PR。
- PR 必须完成适用的验证并满足分支保护要求后才能合并。仓库使用 squash merge，PR 标题即为
  `main` 上的 commit 标题。
- `main` 必须启用要求 PR 的远端分支保护，并对管理员生效。不得使用管理员 bypass、强制推送或
  临时关闭保护来规避本节规则。

## 修改前必读

1. 阅读 `README.md`，确认任务位于 StructaDoc 的产品边界内。
2. 阅读 `docs/README.md`，定位相关 ADR、规范和实现说明。
3. 修改公开结构化数据时，阅读 `docs/specifications/canonical-document-model.md`。
4. 修改 Parse Run、Worker、重试或恢复逻辑时，阅读
   `docs/specifications/parse-job-lifecycle.md`。

如果预期入口不存在，应查明实际结构并如实报告。不得虚构文件、行为或完成状态。

## 事实模型

- 功能实现前，`README.md`、已接受的 ADR 和规范描述目标行为（to-be）。
- 代码、测试、配置、迁移和部署文件描述当前实现事实（as-is）。
- 规范与实现冲突时，应判断是实现未完成、文档过期还是需求已经变化。
- 不得把计划中的端点、数据表、部署命令或测试描述为已经可用。

## 产品边界

- StructaDoc 负责文档摄取、异步解析、结果规范化、结构化持久化和版本化 API 输出。
- StructaDoc 不包含全文搜索、向量搜索、embedding、RAG 或领域数据生成。
- 消费者通过公共 API 使用服务，不直接访问 StructaDoc 的数据库或对象存储。
- 交互式浏览器会话和应用 API 客户端凭据必须相互独立。
- 解析 Provider 由管理员配置；普通上传者和 API 客户端不能修改 Provider 配置。

超出该边界的功能必须先获得用户确认，并新增改变产品边界的 ADR，之后才能实施。

## 架构约束

- MinerU Cloud、MinerU Local 和未来解析器必须通过 Provider 抽象集成。
- 公共 API 不得暴露 Provider 任务协议，也不得把原始 Provider JSON 当作稳定契约。
- 每个 Provider 结果都必须规范化为
  `docs/specifications/canonical-document-model.md` 定义的模型。
- 每个 Parse Run 都必须记录 Provider、配置版本和解析选项快照。
- Provider 配置版本不可变。被非终态 Parse Run 引用的版本不得删除或变为不可用。
- 临时的外部 Provider 状态不是 StructaDoc 的权威状态。
- 原始上传必须保留；转换得到的 PDF 是独立 Artifact，不得替换原文件。
- Provider 支持源格式时提交源文件；否则使用镜像内的 LibreOffice 转换回退。
- 大文件和原始解析结果存入本地或 S3 兼容存储；业务元数据、结构化字段和存储引用存入数据库。
- 业务持久化必须支持 SQLite、PostgreSQL、MySQL 和 MariaDB；Domain、Application 和公共 API
  层不得依赖单一数据库方言。
- Worker 必须在所有受支持数据库上原子领取任务、维护租约并从崩溃中恢复。SQLite 只支持一个
  应用实例；服务端数据库支持多个 Worker 实例。

## 公共契约与兼容性

- 公共 HTTP API 使用带版本号的路径。
- 对公共 DTO、状态值、Block 类型或坐标语义的破坏性变更，必须提升契约主版本。
- 同一主版本内优先增加可选字段。
- API 客户端必须容忍未知字段和未知 Block 类型。
- Provider 原生字段只能出现在明确标记为不稳定的扩展或 Raw Artifact 中。
- 内部数据库引用和存储引用不得出现在公共 API 字段中。

## 安全约束

- 不得提交真实 token、密码、连接字符串或私有文档样本。
- Provider token 和存储凭据不得返回浏览器或写入日志。
- 数据库中的 Provider 凭据必须加密；主密钥通过环境变量或部署 secret 注入。
- 在线 Provider 会把数据传输到外部，UI 和文档必须明确提示。
- 上传校验不得只信任客户端 MIME 类型；必须限制文件大小、处理时间、内存和临时磁盘用量。
- 外部 URL、预签名 URL 和 callback 必须防御 SSRF，并使用短有效期和最小权限。

## 范围纪律

### 实施过程中

- 行为变更和 bug 修复必须配有与风险相称的自动化测试。

## 文档影响

ADR 记录选择、理由、约束和后果。迁移实施细节属于 implementation issue；ADR 不包含
Provider 专用 SQL、编号迁移步骤或测试 checklist。

以下内容发生变化时，必须在同一改动中更新权威文档：

- 产品边界或明确的非目标；
- 公共 API、状态机或 Canonical Model；
- Provider 接口或 capability 语义；
- 数据所有权、Artifact 保留或删除规则；
- 认证、凭据、外部传输或安全边界；
- 部署依赖或关键运维流程。

内部重构、格式化、局部重命名和不改变行为的测试补充通常不需要新的设计文档。

## 编码、依赖与数据库

- 遵循仓库已有的技术栈、目录结构、测试工具和依赖管理方式。
- 增加依赖前，确认现有代码和平台能力不能提供等效方案。
- 数据库变更使用可审查、可重复执行的迁移，不得使用临时启动 DDL。
- 每个受支持数据库都必须有可独立执行的迁移，并运行相同的持久化与生命周期契约测试。
- Provider 专用代码不得泄漏到 Domain 或公共 DTO。

## 验证与安全操作

- 删除、移动或批量改写前必须确认精确目标。
- 提交前检查文档链接、格式、secret 和仓库状态。
