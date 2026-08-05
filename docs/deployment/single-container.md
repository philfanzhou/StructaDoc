# 单容器部署

本文记录当前 SQLite 单容器部署入口。镜像边界以 [ADR-0003](../adr/0003-technology-and-single-image-deployment.md) 为准；PostgreSQL、MySQL 和 MariaDB 的外部数据库拓扑将在各数据库真实契约验证后补充。

## 镜像内容

根目录 `Dockerfile` 使用两阶段构建：

1. 官方 .NET 10 SDK Noble 镜像恢复并发布 `StructaDoc.Host`；
2. 官方 ASP.NET Core 10 Noble 运行时镜像安装 LibreOffice Writer、Calc、Impress 的 no-GUI 组件和常用拉丁/CJK 字体，再复制发布结果。

最终镜像只包含 ASP.NET Core Runtime、Host、四套迁移程序集、LibreOffice no-GUI 组件、字体、CA 证书和健康检查使用的 curl。它不包含 .NET SDK、Node.js、npm、Python、UNO Python bridge、FastAPI 或第二个常驻服务。当前管理网页尚未实现，因此本轮没有虚构 Node 构建阶段；网页项目出现后应增加构建阶段，并只把静态产物复制到同一个最终镜像。

镜像显式使用 Ubuntu 24.04 Noble，因为 .NET 10 官方镜像不发布 Debian 变体。它没有安装 Ubuntu 的 `libreoffice-nogui` 元包：该元包依赖 `python3-uno`，与最终运行时不包含 Python 的决策冲突。Dockerfile 改为显式安装转换 DOC/DOCX、XLS/XLSX 和 PPT/PPTX 所需的 no-GUI 组件，并在构建时验证 `python3`、`node` 和 `npm` 不存在。

## 构建

普通构建默认使用 MCR、Ubuntu 和 NuGet 官方源。在仓库根目录执行：

```bash
docker build --tag structadoc:local .
```

仓库同时提供 Bash 和 PowerShell 构建入口。默认 `auto` 模式会在调用 Docker 之前，以五秒超时探测 NuGet 和 Ubuntu 官方源；两者都可访问时保持官方源，只要其中一个不可访问就切换国内下载源。该判断只反映构建机当时的连通性，不查询 IP 地理位置，也不在 Dockerfile 中隐藏网络分支：

```bash
bash ./scripts/build-container.sh auto
bash ./scripts/build-container.sh official
bash ./scripts/build-container.sh china
```

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ./scripts/build-container.ps1 -MirrorMode Auto
powershell -NoProfile -ExecutionPolicy Bypass -File ./scripts/build-container.ps1 -MirrorMode Official
powershell -NoProfile -ExecutionPolicy Bypass -File ./scripts/build-container.ps1 -MirrorMode China
```

`ExecutionPolicy Bypass` 只作用于这一次 Windows PowerShell 进程；执行前仍应像审阅其他构建入口一样审阅仓库内脚本。PowerShell 7 且策略允许脚本执行时，也可以使用 `pwsh -File ./scripts/build-container.ps1 -MirrorMode Auto`。

`official` 适合要求来源固定、可复现的 CI 和发布构建。`china` 默认使用华为云 NuGet 镜像、清华大学 Ubuntu 镜像和 Ubuntu Ports 镜像；可以通过以下环境变量替换为组织自己的可信代理或缓存：

- `STRUCTADOC_CN_NUGET_SOURCE`：国内 NuGet V3 service index；
- `STRUCTADOC_CN_APT_MIRROR`：amd64 等架构使用的 Ubuntu 仓库根地址；
- `STRUCTADOC_CN_APT_PORTS_MIRROR`：arm64 等 ports 架构使用的 Ubuntu 仓库根地址。

脚本直接调用 `docker build` 并传入最终选择，因此单独构建镜像不要求设置运行时管理员 Secret。默认产物标签是 `structadoc:local`；Bash 可通过 `STRUCTADOC_IMAGE_TAG` 修改，PowerShell 可使用 `-ImageTag` 参数。Compose 也暴露 `STRUCTADOC_NUGET_SOURCE`、`STRUCTADOC_APT_MIRROR` 和 `STRUCTADOC_APT_PORTS_MIRROR` 这组显式构建参数，所以也可以不使用脚本，先设置变量和管理员 Secret，再运行 `docker compose build`。镜像地址会在 `apt-get update` 和 `dotnet restore` 之前生效，构建日志会打印实际使用的 NuGet 与 APT 地址。

基础镜像的 `FROM` 在容器内命令执行前解析，因此自动探测不能安全地替换基础镜像仓库。默认始终从 `mcr.microsoft.com/dotnet` 拉取微软官方镜像；国内环境应优先为 Docker daemon 配置受信任的 registry mirror。确有内部 MCR 代理时，可显式设置 `STRUCTADOC_DOTNET_REGISTRY`，其值应以同时包含 `sdk` 和 `aspnet` 子仓库的路径结尾，不要包含末尾斜杠。

国内 APT 镜像仍通过 Ubuntu 仓库签名校验软件包，但 NuGet 替代入口和自定义基础镜像代理属于额外供应链信任边界。生产发布应固定来源、保留构建日志，并优先使用组织管理的缓存代理；不要把带凭据的私有源 URL 放进构建参数。

本机当前没有 Docker/Podman，因此本轮已验证 .NET 发布、Dockerfile 静态契约和 Compose 配置内容，但没有把镜像构建成功描述为已验证事实。首次在有容器引擎的 CI 或开发机验证时，至少应检查：

```bash
docker run --rm --entrypoint /usr/bin/libreoffice structadoc:local --headless --version
docker run --rm --entrypoint /bin/sh structadoc:local -c '! command -v python3 && ! command -v node && ! command -v dotnet-sdk'
```

随后应使用不含私有内容的 DOCX、XLSX 和 PPTX 样本执行真实 PDF 转换和字体回归测试。

## SQLite Compose 启动

`compose.yaml` 只启动一个 StructaDoc 应用容器，SQLite、原文件、转换产物和 Data Protection key ring 都写入同一个命名卷的 `/data`。数据库服务器没有被打包进应用镜像。

首次启动前在当前 Shell 设置管理员 bootstrap 凭据：

```bash
export STRUCTADOC_ADMIN_EMAIL='admin@example.com'
export STRUCTADOC_ADMIN_PASSWORD='use-a-secret-manager-or-a-long-random-value'
docker compose up --build --detach
```

PowerShell 使用：

```powershell
$env:STRUCTADOC_ADMIN_EMAIL = 'admin@example.com'
$env:STRUCTADOC_ADMIN_PASSWORD = 'use-a-secret-manager-or-a-long-random-value'
docker compose up --build --detach
```

如果前面已经通过网络选择脚本完成构建，启动时使用 `docker compose up --detach --no-build`，避免再次按当前 Shell 的默认变量重新构建。

示例字符串只说明变量形状，不是默认凭据。生产环境应从部署平台 Secret 注入，不应把真实值写入仓库、Compose 文件或共享的 `.env`。bootstrap 完成并验证管理员可登录后，可以在后续部署中移除这两个环境变量；已有管理员数据保留在数据库中。

默认映射 `http://localhost:8080`，就绪检查是 `/health/ready`。Compose 使用只读根文件系统、移除 Linux capabilities、禁止提权，并给 `/tmp` 提供受限 tmpfs。应用以官方 .NET 镜像内置的非 root UID 运行。

真实 Parse Run 执行仍默认关闭。只有明确设置以下变量，Worker 才会把文档发送到管理员选择的 Provider，并在必要时启动 LibreOffice 子进程：

```bash
export STRUCTADOC_EXECUTION_ENABLED=true
```

## 持久化和权限

镜像声明 `/data` 卷并预创建以下目录：

- `/data/structadoc.db`：SQLite 数据库及 sidecar 文件；
- `/data/storage`：原文件、Provider Archive、Assets 和 Artifacts；
- `/data/keys`：Cookie、antiforgery、Provider credential 和 submission checkpoint 使用的 Data Protection key ring；
- `/data/temp`：LibreOffice、ZIP 接收与归一化的受限临时目录。

命名卷首次创建时继承镜像中的非 root UID 权限。改用 bind mount 时，宿主目录必须预先授予镜像 `APP_UID` 写权限；不要通过改回 root 用户绕过权限问题。备份必须同时覆盖数据库、存储和 key ring，否则恢复后的 Provider 凭据或运行中 Cloud checkpoint 可能无法解密。

## 运行时限制

应用内部已经限制转换并发、时间、输入、输出和临时磁盘，但 Compose 示例不替部署平台决定 CPU 和内存预算。生产部署还应设置内存、CPU、进程数和日志轮转限制，并给 `/data` 配置容量告警。容器收到 `SIGTERM` 后有一分钟优雅停止窗口；远端 Provider 请求仍遵守 Parse Run 租约和恢复语义。
