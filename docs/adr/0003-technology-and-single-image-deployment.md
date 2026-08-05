# ADR-0003：采用 .NET 10 和包含 LibreOffice 的单一应用镜像

- Status: Accepted
- Date: 2026-08-05

## Context

StructaDoc 是一个低频写入、以读取结构化结果为主的自托管服务。文档上传和 Office 转换的吞吐量预计远低于结果读取量，第一阶段更重视部署简单、升级一致和较低的日常维护成本，而不是分别扩缩 API、Worker 和转换器。

项目仍然需要清晰隔离 HTTP、任务执行、Provider、存储和转换职责，但这种代码边界不要求第一阶段把每项职责部署为独立容器。尤其是 Office 转 PDF 只需要受控调用 LibreOffice headless；为此额外维护 Python、FastAPI、内部 HTTP 协议和第二个常驻进程没有足够收益。

StructaDoc 同时需要长期维护的公共 DTO、PostgreSQL 事务、持久化任务恢复、流式文件处理、认证和管理网页托管。.NET 10 已经是正式发布的 LTS 版本，适合作为新项目基线。.NET 8 的支持周期接近结束，不再作为首个实现的目标框架。

## Decision

### 1. 技术基线

- API、后台任务、Provider 和基础设施代码使用 .NET 10 与 ASP.NET Core 10。
- PostgreSQL 是业务数据和持久化任务的权威数据库。
- 管理网页使用 Vue 3、TypeScript 和 Vite，并在镜像构建阶段生成静态文件。
- 前端静态文件由 ASP.NET Core Host 提供，不部署独立 Web Server 或前端容器。
- 默认 JSON、HTTP、日志、健康检查和可观测性优先使用 .NET 平台内置能力；新增第三方依赖前需要证明现有能力不足。

### 2. 单一应用镜像和单一主进程

第一阶段只发布一个 StructaDoc 应用镜像。最终运行时镜像包含：

- ASP.NET Core Runtime；
- StructaDoc Host 及其依赖程序集；
- 已构建的管理网页静态文件；
- LibreOffice headless 和受支持文档所需字体。

容器中只有 StructaDoc Host 是常驻主进程。Host 同时承载：

- 管理网页和 HTTP API；
- 持久化 Parse Run 的后台执行器；
- Provider 适配器；
- 本地 LibreOffice 转换适配器。

Worker 是独立的逻辑组件，但第一阶段作为 `BackgroundService` 运行在 Host 内，而不是单独发布可执行程序或镜像。任务仍必须通过 PostgreSQL 原子抢占、租约和心跳执行，不能依赖进程内队列；因此未来可以让同一镜像按全部功能、仅 API 或仅 Worker 的模式启动，而无需改变领域模型或公共 API。

### 3. 内置 Office 转换

Provider 原生支持源格式时仍优先提交原文件。只有 Provider 不支持源格式时，Worker 才通过本地 LibreOffice 转换适配器生成 PDF。

转换适配器由 .NET 直接启动 LibreOffice 子进程，不在默认镜像中运行 Python、FastAPI、Uvicorn、进程监督器或内部转换 HTTP 服务。实现必须：

- 为每次转换创建独立工作目录和 LibreOffice User Profile；
- 使用参数列表启动进程，不把用户输入拼接为 Shell 命令；
- 限制转换并发、执行时间、输入大小、输出大小和临时磁盘占用；
- 超时或取消时终止对应进程树；
- 检查退出码、输出文件存在性和 PDF 内容类型；
- 在成功、失败和取消后清理临时目录；
- 不在日志中记录文档正文、内部路径或敏感文件名信息。

转换后的 PDF 作为 `normalized-pdf` Artifact 保存，不覆盖原始文件。Artifact 和 Parse Run 记录源格式、实际提交格式、LibreOffice 版本、大小和哈希，保证结果可追溯。

### 4. 构建和运行时边界

应用镜像采用多阶段构建：

1. Node.js 构建管理网页；
2. .NET SDK 构建并发布 Host；
3. 最终运行时阶段安装 ASP.NET Core Runtime、LibreOffice 和字体，并复制前两阶段产物。

Node.js、.NET SDK 和 Python 不进入最终运行时镜像。

### 5. 外部状态依赖

“单一应用镜像”不表示把数据库也嵌入应用容器：

- PostgreSQL 使用独立实例或官方数据库容器；
- 默认文件存储可以使用挂载卷；
- S3 兼容对象存储是可选部署能力；
- 不在 StructaDoc 镜像中启动或管理 PostgreSQL。

最小自托管拓扑是一个 StructaDoc 应用容器加一个 PostgreSQL 实例；如果已有外部 PostgreSQL，则只需部署一个 StructaDoc 容器。

## Consequences

### Positive

- 前端、API、Worker 和转换能力通过一个版本化镜像交付和升级。
- 不需要维护 Python 运行时、内部转换 HTTP 协议或额外常驻进程。
- 低频转换不会为默认部署引入独立服务发现、健康检查和网络故障面。
- 代码仍保留模块边界，未来可以使用同一镜像拆分 API 与 Worker 运行模式。
- PostgreSQL 任务租约使单 Host 和多实例部署遵守同一套可靠性语义。

### Trade-offs

- LibreOffice 和字体会显著增大最终镜像。
- API、Worker 和转换器默认共享同一容器的 CPU、内存和故障域。
- 不能单独升级或扩容 LibreOffice；如果未来转换量显著增长，需要用新 ADR 重新评估部署边界。
- 构建 LibreOffice 层可能较慢，需要通过稳定基础层和构建缓存控制构建时间。

## Rejected Alternatives

### 独立 Python doc-converter 容器

拒绝作为默认部署。Python 服务只是在 HTTP 层封装 LibreOffice 子进程，会增加运行时、进程、协议和运维成本，而当前预期转换频率不足以证明这些成本合理。

### 在同一容器中同时运行 .NET Host 和 Python Web 服务

拒绝。它表面上只有一个镜像，实际仍需要管理多个常驻进程、内部端口、退出顺序和健康状态，没有实现单一运行时和单一主进程的维护目标。

### 第一阶段分别发布 API、Worker 和转换器镜像

拒绝作为默认形态。逻辑边界会保留，但当前负载没有证明独立部署和扩缩的复杂度是必要的。

### 把 PostgreSQL 打包进应用容器

拒绝。数据库备份、恢复、升级、持久卷和生命周期必须独立于应用镜像管理。

### 使用 Go 作为核心实现语言

Go 在镜像体积、启动速度和单二进制交付方面有优势，但 StructaDoc 的主要复杂度是长期演进的数据契约、认证、PostgreSQL 事务和持久化任务，而不是 CPU 密集计算。结合现有 .NET 领域经验和 .NET 10 LTS 的平台能力，Go 的运行时优势不足以抵消重建工程惯例和降低开发效率的成本。
