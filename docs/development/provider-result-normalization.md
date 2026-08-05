# Provider 结果归一化

本文记录已验证 Provider ZIP 到 Canonical Parse Bundle 的首个实现边界。ZIP 接收和安全预检见 [`provider-result-intake.md`](./provider-result-intake.md)，统一字段和提交不变量分别见 [`canonical-document-model.md`](../specifications/canonical-document-model.md) 与 [`canonical-result-persistence.md`](./canonical-result-persistence.md)。

## 归一化契约

`IProviderResultNormalizer` 通过 `Supports(providerType)` 声明适配范围，并接收 Parse Run ID、Provider 类型、已验证 `StoredProviderArchive` 及非敏感 model/backend 快照。首个 `MinerUResultNormalizer` 同时支持 `mineru-cloud` 和 `mineru-local`，但只承诺下述已观察输出结构，不把 MinerU 字段暴露为公共契约。

归一化前会重新打开固定 Archive 存储对象，并逐项对照接收阶段生成的清单：条目数、NFC 路径、目录标记、展开大小和压缩大小必须一致。存储流不可 seek 时，按已验证 Archive 大小精确复制到 delete-on-close 临时文件。归一化器从不把 Provider 路径直接拼接到宿主文件系统路径。

## 当前 MinerU 输出识别

| ZIP 内容 | 识别规则 | Canonical 结果 |
|---|---|---|
| `full.md` | 根目录精确名称，必需且必须为非空 UTF-8 | `markdown` Artifact |
| `content_list.json` | 根目录精确名称；否则唯一 `*_content_list.json`；否则唯一子目录同名文件 | `content-list` Artifact，并映射 Blocks |
| `content_list_v2.json` | 与 content list 相同优先级，可选 | 第二个 `content-list` Artifact |
| `layout.json` | 精确、唯一后缀、唯一子目录同名文件，可选 | `layout` Artifact |
| `model.json` | 精确、唯一后缀、唯一子目录同名文件，可选 | `model-output` Artifact |
| `images/**` | 已验清单下的非空文件 | Asset；当前接受 PNG、JPEG、GIF、WebP 文件签名 |
| 原始 ZIP | 接收阶段固定对象 | `provider-archive` Artifact |

同一优先级存在多个候选时返回稳定歧义错误，不依赖 ZIP 条目顺序选择“第一个”。JSON 必须是合法 UTF-8 JSON；`content_list` 根节点必须是数组。`content_list` 缺失时仍可生成只包含 Markdown 和原始 Archive 的合法 Bundle，以兼容只输出 Markdown 的 Provider 版本。

## Block 映射

- 数组顺序直接成为从 0 开始的全局连续 `sequence`；
- MinerU `page_id` 按已观察的 0-based 语义映射为 1-based Page；缺失时 Page 为 `null`，非法或负数页号拒绝整包；
- `text/content/body` 依次作为内容来源；对象或数组使用其紧凑 JSON 文本；
- `text`、`table`、`equation/formula`、`image/figure`、`code`、header/footer 等映射为注册的 Canonical 类型，未知类型映射为 `unknown` 并尽量保留安全 subtype；
- `text_level > 0` 映射为 `title` 和 `heading-{level}` subtype；
- `text_format`、公式和 HTML table body 映射为 `contentFormat`；
- 0–1 bbox 原样使用，0–1000 bbox 除以 1000；无法可靠识别的坐标不生成 bbox；
- 0–1 `score` 映射为 confidence；
- `img_path` 只按已验证 Archive 相对路径关联对应 Asset，不作为宿主路径使用。

原始 JSON 已作为 Artifact 保留，因此当前 Block 不复制整块 Provider JSON 到 `providerData`，避免把 `img_path`、内部路径或未来未知敏感字段进入普通公共响应。

## 幂等存储和身份

派生产物使用固定逻辑键：

- `parse-runs/{parseRunId}/artifacts/*.json|*.md`；
- `parse-runs/{parseRunId}/assets/{entryPathHash}.{extension}`。

相同键和相同内容由 `IFileStorage` 幂等复用，不同内容返回冲突且不覆盖原对象。Block、Asset 和 Artifact UUID 由 Parse Run ID 与稳定逻辑来源确定性生成，因此进程在落盘后、Bundle 提交前崩溃，恢复重跑仍产生相同 Bundle 指纹。失败后已经原子写入的固定派生对象会保留供恢复复用；它们不会在失败路径被不安全地删除。

## 配置

| Key | Default | Meaning |
|---|---:|---|
| `ProviderResultNormalization:MaxMarkdownBytes` | 64 MiB | 单个 Markdown 条目的读取和存储上限 |
| `ProviderResultNormalization:MaxJsonBytes` | 64 MiB | 单个 JSON 条目的读取和存储上限 |
| `ProviderResultNormalization:MaxAssetBytes` | 256 MiB | 单个图片 Asset 的流式存储上限 |
| `ProviderResultNormalization:TemporaryPath` | 系统临时目录下的 `structadoc-provider-normalization` | Archive 回读流不可 seek 时的临时文件目录 |

此外仍受接收阶段 ZIP 上限和 Parse Bundle 的 Pages、Blocks、Assets、Artifacts 及内容聚合上限约束。

## 当前未实现

- MinerU HTTP 适配器和真实 Worker 的下载、接收、归一化、提交编排；
- 用实际生产样本扩展更多 MinerU 目录版本、图片媒体类型和结构字段；
- Markdown 内图片链接的公共下载/导出重写策略；
- 解析 Assets/Artifacts/Blocks 的公共读取与受控下载端点。
