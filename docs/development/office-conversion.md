# Office 转 PDF

本文记录当前 LibreOffice 转换适配器、执行器接入和恢复边界。部署形态以 [ADR-0003](../adr/0003-technology-and-single-image-deployment.md) 为准，转换 Artifact 语义以 [`canonical-document-model.md`](../specifications/canonical-document-model.md) 为准。

## 当前执行策略

`ParseRunExecutor` 先读取固定 Provider 配置版本的能力快照。Provider 原生支持源媒体类型时直接提交原文件；只有原格式不受支持、Provider 支持 PDF 且已注册转换器支持该 Office 格式时，才生成 PDF 回退。当前 LibreOffice 适配器登记 DOC、DOCX、XLS、XLSX、PPT 和 PPTX 到 PDF，不把 PDF 或任意未知格式交给 LibreOffice 猜测。

转换使用 `converting` Stage，并按以下顺序持久化：

1. 从原始 Document 存储流写入本次转换的隔离工作目录，同时复核输入大小；
2. 由 .NET 直接启动 LibreOffice headless，输出 PDF；
3. 把 PDF 流式写入 `parse-runs/{parseRunId}/conversions/{artifactId}.pdf` 形式的随机不可变存储键；
4. 在当前租约和并发版本下原子保存转换快照、把 `submittedMediaType` 切换为 `application/pdf`，并进入 `preparing-source`；
5. Provider 提交和最终 Parse Bundle 都复用该快照，成功结果包含同一 ID 的 `normalized-pdf` Artifact。

原始 Document 从不被覆盖。转换快照保存转换器类型和实际版本、源/输出媒体类型、PDF Artifact ID、大小、SHA-256 和内部存储引用，不保存临时目录或命令行路径。Artifact metadata 只保存非敏感转换器与格式信息。

如果进程在 PDF 落盘后、转换快照提交前退出，该随机对象不会被错误复用，下一次尝试会生成新对象；旧对象留给后续孤儿清理。快照一旦提交，租约恢复和 Provider checkpoint 恢复都会复用同一个 PDF，不再次运行 LibreOffice。

## 子进程与资源边界

每次转换创建独立工作目录、输出目录和 LibreOffice User Profile。启动使用 `ProcessStartInfo.ArgumentList` 且 `UseShellExecute=false`，用户文件名和正文不参与 Shell 命令。参数包含 headless、无默认文档、无恢复以及独立 `UserInstallation`。

适配器实施以下限制：

- 全局转换并发信号量；
- 输入和输出字节上限；
- 转换超时与定期工作目录大小检查；
- 超时或取消时终止对应进程树；
- 非零退出码、缺失输出、空或过大输出以及非 `%PDF-` 签名均拒绝；
- 标准输出和错误最多各捕获 16 KiB，且当前不写入日志或错误响应；
- 成功、失败和取消后都清理本次随机工作目录。

这些应用级限制不能替代容器 CPU、内存、进程数和文件系统配额；正式镜像仍应配置相应运行时限制。

## 配置

Host 从 `LibreOffice` 配置段读取：

| Key | Default | Meaning |
|---|---:|---|
| `Enabled` | `true` | 是否允许 LibreOffice 格式回退 |
| `ExecutablePath` | `libreoffice` | 直接启动的可执行文件路径或名称 |
| `TemporaryPath` | `./data/temp/libreoffice` | 隔离转换工作目录的父目录 |
| `MaxConcurrency` | `1` | 单 Host 同时持有的转换数量 |
| `Timeout` | 3 minutes | 单次 LibreOffice 进程上限 |
| `ResourceInspectionInterval` | 250 ms | 临时目录用量检查间隔 |
| `MaxInputBytes` | 100 MiB | 转换输入上限 |
| `MaxOutputBytes` | 200 MiB | PDF 输出上限 |
| `MaxTemporaryBytes` | 512 MiB | 输入、输出和 Profile 的合计临时磁盘上限 |

环境变量使用双下划线，例如 `LibreOffice__ExecutablePath=/usr/bin/libreoffice`。`Worker:ExecutionEnabled` 仍默认关闭；只启用转换配置不会自动抢占或发送文档。

## 当前验证和剩余工作

自动化测试覆盖格式选择、独立 Profile 参数、输入限制、无效 PDF、目录清理、租约约束的转换快照、执行器 PDF 提交、Canonical Artifact 提交和恢复复用。服务端数据库契约包含同一转换快照条件更新和最终 Artifact 提交，但当前机器没有容器运行时，因此 PostgreSQL、MySQL 和 MariaDB 的真实执行仍待验证。

当前仓库已提供安装 LibreOffice no-GUI 组件与常用字体的运行时 Dockerfile 和 SQLite Compose 入口，见 [`single-container.md`](../deployment/single-container.md)。本机没有容器引擎，因此尚未用真实 DOC/XLS/PPT 样本对目标镜像中的 LibreOffice 版本执行集成测试；Dockerfile 和静态契约存在不等于镜像构建已经验证通过。
