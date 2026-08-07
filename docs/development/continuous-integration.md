# 持续集成

仓库的 [CI 工作流](../../.github/workflows/ci.yml) 在 push、pull request 和手动触发时运行。它用于补足开发机可能缺少 Docker、服务端数据库或浏览器运行环境的验证，不改变 StructaDoc 的产品边界，也不启用全文检索、LLM 等扩展能力。

## 验证范围

工作流包含三个相互独立的 Job：

1. `build-and-test` 使用 .NET 10 与 Node.js 24，还原并构建前后端，执行 npm 安全审计和解决方案测试。需要 Docker 的数据库契约在该 Job 中保持跳过。
2. `database-contracts` 设置 `STRUCTADOC_RUN_DATABASE_CONTRACT_TESTS=1`，由 Testcontainers 分别启动 PostgreSQL 17、MySQL 8.4 和 MariaDB 11.4，执行相同的迁移和 Parse Run 租约契约。
3. `container-and-browser` 构建真实生产 Dockerfile，以只读根文件系统、全部 capability 移除和临时测试管理员启动镜像，验证健康检查与系统信息端点，然后用 Chromium 完成管理员登录、PDF 上传、用户工作台和管理区访问。

数据库测试结果、浏览器 HTML 报告、成功页面截图、失败 trace/video，以及容器日志都会作为 Actions artifact 保留。测试管理员凭据仅存在于隔离 runner 的环境变量中，不是生产凭据，也不需要配置仓库 Secret。

## 本地复现

不依赖 Docker 的基线：

```bash
cd web
npm ci
npm run build
npm run test:e2e -- --list
cd ..
dotnet test StructaDoc.slnx
```

有 Docker 时运行三种数据库契约：

```bash
STRUCTADOC_RUN_DATABASE_CONTRACT_TESTS=1 \
dotnet test tests/StructaDoc.DatabaseContractTests/StructaDoc.DatabaseContractTests.csproj
```

浏览器测试默认访问 `http://127.0.0.1:8080`，也可以通过 `STRUCTADOC_E2E_BASE_URL` 指向已经启动的测试实例。管理员邮箱和密码分别通过 `STRUCTADOC_E2E_ADMIN_EMAIL`、`STRUCTADOC_E2E_ADMIN_PASSWORD` 注入。

## 验证状态

工作流文件和 Playwright 测试可以在本地完成静态发现与构建验证；容器、服务端数据库和 Chromium 的最终结论只以 GitHub Actions 的实际运行结果为准。工作流尚未运行时，不应把这些项描述为已经通过。
