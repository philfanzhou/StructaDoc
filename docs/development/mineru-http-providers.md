# MinerU HTTP Provider 适配

本文记录 MinerU Cloud 与 MinerU Local HTTP 适配器的当前实现。抽象职责见 [ADR-0002](../adr/0002-parser-provider-abstraction.md)，任务恢复约束见 [`parse-job-lifecycle.md`](../specifications/parse-job-lifecycle.md)。协议依据为 2026-08-05 查阅的 [MinerU Cloud API 文档](https://mineru.net/doc/docs/) 和 [MinerU Local 官方 API/CLI 文档](https://github.com/opendatalab/MinerU/blob/master/docs/en/usage/quick_usage.md)。上游协议变化必须由独立适配器和契约测试吸收，不能进入公共 API。

## MinerU Cloud

`MinerUCloudParseProvider` 使用 Cloud 的签名批量上传协议处理单个 StructaDoc Parse Run：

1. `POST /api/v4/file-urls/batch`，为一个文件申请 batch ID 和签名上传 URL；
2. 使用不带 Provider Token 和 Content-Type 的 `PUT` 流式上传原文件；
3. `GET /api/v4/extract-results/batch/{batchId}` 查询唯一文件的状态；
4. `done` 后从 `full_zip_url` 流式打开结果 ZIP。

Cloud Token 是必需配置，只加入发往管理员配置 Base URL 的 API 请求。Token 不会加入签名上传或结果 CDN 请求。签名 URL 不写入异常、日志、数据库或 Canonical 元数据。Cloud Base URL 必须使用 HTTPS，且不能包含 query；签名上传和结果 URL 也必须是无 user-info、无 fragment 的 HTTPS 地址。HTTP 客户端关闭自动重定向，避免 Token 或请求载荷被无意转发。

当前能力快照为单文件最多 200 MiB、600 页，支持 PDF、DOC/DOCX、PPT/PPTX、HTML 和文档列出的图片类型；不声明 XLS/XLSX Cloud 原生支持。Cloud 当前没有接入可用的取消端点，因此取消能力为 `false`。

## MinerU Local

`MinerULocalParseProvider` 面向当前官方 protocol version 2 的异步接口：

1. `POST /tasks` 使用 `multipart/form-data` 流式上传一个文件，并要求 ZIP、Markdown、middle JSON、model output、content list 和 images；
2. `GET /tasks/{taskId}` 把 `pending / processing / completed / failed` 映射为 Provider 内部状态；
3. `GET /tasks/{taskId}/result` 以响应流返回 ZIP。

Local Base URL 可以是 HTTP，以支持同一受信网络或同一主机上的自托管服务；可选 credential 作为 Bearer Token 加到 Local 请求，便于接入受保护的反向代理。Local 声明 PDF、常见图片、DOC/DOCX、PPT/PPTX 和 XLS/XLSX 支持；官方接口没有给出稳定的统一文件大小或页数上限，因此这两个能力值不在适配器中臆造。当前官方接口没有单任务取消端点。

Local 官方 ZIP 使用 `{document}/{method}/{document}.md`、`*_middle.json`、`*_content_list.json` 和嵌套 `images/`。归一化器同时支持这种目录和 Cloud 的根目录 `full.md`，并在唯一候选、路径清单复核和 Asset 相对路径消歧后才读取。

## Parse Run options

当前两个适配器只接受下列非敏感 JSON 属性；未知属性、重复属性、错误类型，以及 Cloud 请求中的 Local-only 属性会在出站请求前以 `mineru-options-invalid` 拒绝：

| Property | Type | Meaning |
|---|---|---|
| `ocr` | boolean | Cloud `is_ocr`；Local 为 `true` 且未指定 `parseMethod` 时选择 `ocr` |
| `formula` | boolean | 公式识别，默认 `true` |
| `table` | boolean | 表格识别，默认 `true` |
| `language` | string | OCR 语言，默认 `ch` |
| `parseMethod` | `auto` / `txt` / `ocr` | Local 解析方式 |
| `effort` | `medium` / `high` | Local hybrid effort |
| `imageAnalysis` | boolean | Local 图片/图表分析 |
| `startPage` | non-negative integer | 0-based 起始页 |
| `endPage` | non-negative integer | 0-based 结束页且不得小于起始页 |

Local backend 来自不可变 Provider 配置的 `backend`；Cloud model version 来自配置的 `model`，未配置时使用 `pipeline`。运行时 options 不能覆盖 endpoint、credential、backend 或 model。

## HTTP 和错误边界

- 所有响应 JSON 最多读取 1 MiB，不把响应正文放入日志或异常；
- Provider API 的 `400/422` 分类为输入错误，`401/403` 分类为配置错误，`408/429/5xx` 和网络超时分类为瞬时错误；不携带 Token 的签名传输 `401/403` 不误报为凭据配置错误；
- 外部任务失败只返回稳定错误码和通用脱敏消息；
- 结果响应所有权随 `ProviderResultContent` 转移，调用方释放结果时同时释放网络流和 `HttpResponseMessage`；
- 外部 task ID 只作为转义后的单个 URL path segment 使用；源文件名必须是安全的单段名称。

## 尚未启用和剩余风险

适配器已注册到 Host DI，但维护 Worker 仍不会抢占并执行新的 Parse Run。真实执行启用前还必须完成：

- 把租约心跳、Provider 调用、结果接收、归一化和 Canonical 提交编排为可恢复执行器；
- 明确 Cloud “已申请 batch ID、上传响应结果未知”时的持久化 checkpoint，不能把这类情况当作普通瞬时错误盲目创建新 batch；
- 为 Cloud 返回的跨主机签名 URL增加可配置的目标策略和连接级 DNS/IP 固定，当前仅强制 HTTPS、禁止 user-info/fragment 和自动重定向，尚未宣称完整 SSRF 防护；
- 使用部署目标的真实 MinerU 版本和样本执行集成测试；
- 接入取消请求和执行尝试明细。

因此，本实现使协议适配层可独立测试和继续集成，但尚不代表生产任务执行已经开启。
