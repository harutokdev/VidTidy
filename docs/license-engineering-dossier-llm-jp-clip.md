# Local Media AI — License Engineering Dossier

**対象:** `llm-jp/llm-jp-clip-vit-base-patch16`  
**作成日:** 2026-08-18 JST  
**作成者:** Manus AI  
**目的:** Local Media AI の開発用Production Candidateに含まれるモデル、adapter、Portable runtime、および由来情報を、法務レビューに渡せる技術的な事実として整理する。本書はライセンスまたは法務上の結論を代行せず、配布可否を承認しない。

> **最終ステータス: LEGAL REVIEW REQUIRED**
>
> 本資料は技術証跡であり、商用配布許可、法務クリア、権利非侵害、学習データの再利用可否を保証しない。配布、再配布、SaaS提供、顧客環境への同梱の前に、法務担当者が本体、学習データ、翻訳工程、依存物、NOTICEおよび配布形態を独自に確認する必要がある。

## 1. 評価対象と技術的な位置付け

Local Media AIは本モデルを**Development Production Candidate**として、ローカルの映像フレーム・自然言語検索に技術統合している。技術利用の状態は **Development Candidate / Approved for Technical Use** である一方、商用配布状態は明示的に **Commercial Distribution Review Required** として分離されている。したがって、製品内の表示・最終報告では「Commercial Distribution Approved」および「legal cleared」という表現を使用しない。

| 項目 | 記録値 |
|---|---|
| Model ID | `llm-jp/llm-jp-clip-vit-base-patch16` |
| 固定revision | `e3b65fb4a871bacde924b3613dae0eccb7835e95` |
| Profile ID | `llm-jp-clip-development-production-candidate-v1` |
| Adapter | `openclip_local` |
| Adapter directory | `llm-jp-clip-vit-base-patch16-openclip-adapter` |
| Processor version | `llm-jp-openclip-2.31-local-adapter-v1` |
| embedding dimension | 512 |
| model card上のパラメータ数 | 248M |
| モデルcardのlicense表示 | Apache License 2.0 |
| 技術状態 | Development Candidate; Approved for Technical Use |
| 配布状態 | **LEGAL REVIEW REQUIRED** |

公式モデルカードは、本モデルをOpenCLIP上で学習した日本語CLIPモデルとして説明し、テキストencoderをllm-jp-tokenizerを用いたRoBERTa base、画像encoderをViT-B/16としている。[1]

## 2. ローカルartifactのprovenanceと完全性

本モデルはPortable ModelsRoot内に明示的に配置し、推論実行時のネットワーク利用を許可していない。`provenance.json`にはsource URL、fixed revision、license、準備日時、合計容量、artifact manifest hashを保存している。

| integrity項目 | 実測・記録値 |
|---|---:|
| artifact合計容量 | 1,002,232,538 bytes |
| artifact manifest SHA-256 | `2395666bdf22a47ac096a687b24a99ac718cbd10f1e0e30c1a247b51495dc701` |
| 主weight file | `open_clip_model.safetensors` |
| 主weight file容量 | 995,797,396 bytes |
| 同梱設定・tokenizer | `open_clip_config.json`、`tokenizer.json`、`tokenizer_config.json`、`special_tokens_map.json` |
| model license file | `LICENSE` |
| source | [Hugging Face model card][1] |
| `runtime_network_allowed` | `false` |

モデルfile、adapter、runtimeは、cache identityにmodel ID、revision、adapter、processor version、sampling version、dtype、dimension、入力identityおよびtimestampを含めて区別する。これはライセンス判断ではなく、異なるmodel artifactまたは実行profileの結果を混在させないための技術的な再現性対策である。

## 3. モデルcardが記載する学習データとGemma翻訳

モデルcardの記載によれば、学習データは`relaion2B-en-research-safe-japanese-translation`であり、ReLAION-5Bの英語subsetである`relaion2B-en-research-safe`を日本語へ翻訳したものと説明されている。[1] 画像download成功率70%により、同cardでは学習datasetを約14.5億sample、9 epoch、延べ約130億sampleとして記載している。[1]

| 層 | モデルcard上の記載 | 技術資料上の扱い |
|---|---|---|
| image-text training data | `relaion2B-en-research-safe-japanese-translation` | 学習データ由来・取得元・再配布条件は法務確認対象 |
| 原dataset reference | ReLAION-5B英語subsetの`relaion2B-en-research-safe` | URL dataset、画像、caption等に関する権利の自動解決を意味しない |
| translation model | `gemma-2-9b-it` | 翻訳工程に使用したというモデル作者の記載 |
| モデル作者の解釈 | Gemmaを翻訳専用で使い、Gemma TermsのModel Derivatives定義には該当しないためGemma licenseの継承は不要と結論した旨 | **作者の解釈としてのみ記録する。Local Media AIまたは本書が追認・代行する法務判断ではない。** |

公式モデルcardはGemma Terms of Useを参照するよう求め、上記の解釈を記載している。[1] Gemma Terms自体はGoogleの一次資料であり、利用者は配布形態・実際の依存関係・適用範囲に照らして確認する必要がある。[2] ReLAION由来のdata referenceについても、モデル重みのApache-2.0表記だけで学習データ、URL先画像、caption、translation出力、人格権・肖像権・データ保護等の論点が解決されたとは扱わない。[1] [3]

## 4. Portable runtimeと依存コンポーネント

LLM-jp CLIP用runtimeは、本体applicationのSQLite所有境界を越えないPortable OpenCLIP runtimeとして分離している。Python Workerはloopback gRPCを介してembedding結果を返すだけであり、SQLiteを直接操作しない。

| コンポーネント | 記録version | license記録 | 技術的役割 | 配布前の確認 |
|---|---:|---|---|---|
| `open_clip_torch` | 2.31.0 | MIT | モデルconfig読込、image/text encode | MIT license text・copyright noticeの同梱要否を確認 |
| `timm` | 1.0.15 | Apache-2.0 | vision model utility/dependency | Apache-2.0 license・NOTICE対応を確認 |
| `torch` | 2.5.1+cu121 | BSD-3-Clause | tensor/CUDA推論runtime | BSD-3-Clause text、CUDA配布物と別途bundled dependencyを確認 |
| `grpcio` | Portable runtime同梱版 | runtime metadata/配布物で再確認 | loopback Worker transport | license inventoryにversion・licenseを確定記録 |
| `transformers` | Base runtimeと分離 | runtime metadata/配布物で再確認 | 本candidateのOpenCLIP runtimeでは主推論path外 | 実際にbundledする場合のみlicense inventoryへ確定記録 |
| Local OpenCLIP adapter | `llm-jp-openclip-2.31-local-adapter-v1` | Local project source | LLM-jp artifactとOpenCLIPの接続 | project source copyright・third-party code混入を確認 |

OpenCLIPのupstream repositoryはMIT Licenseを掲げている。[4] 本dossierでは現地Portable runtimeの`dist-info/METADATA`に記録された`open_clip_torch 2.31.0 / MIT`、`timm 1.0.15 / Apache-2.0`、`torch 2.5.1+cu121 / BSD-3-Clause`をinventoryの出発点として記録するが、すべてのtransitive dependencyの網羅的な配布適合性を保証しない。

## 5. Apache-2.0配布時の技術チェックリスト

Apache-2.0の適用を前提とする配布物では、少なくともlicense text、著作権表示、NOTICEの有無、改変表示、同梱componentとの整合を法務・release engineeringが確認する必要がある。[5]

| 配布チェック | 現時点の技術証跡 | release前の必要作業 |
|---|---|---|
| 本モデルのLICENSE | model directoryに`LICENSE`を確認 | 実際のinstaller/packageにlicense textを含めるか確認 |
| NOTICE | model directoryとupstreamを確認対象として明記 | NOTICEが存在するcomponentについて要求どおりに保持・表示 |
| revision固定 | `e3b65fb4...`とmanifest hashを記録 | release artifactが同一revision・hashか再照合 |
| artifact同一性 | manifest SHA-256を記録 | build/installer pipelineでverify、改変時は再記録 |
| dependency inventory | OpenCLIP/timm/torchの一次inventoryを記録 | transitive dependencies、CUDA/NVIDIA配布物、FFmpeg、.NET/NuGetを別inventoryで確定 |
| training-data/translation provenance | model cardの原文・Gemma Terms参照を記録 | 法務がdata/license/terms/re-distributionを評価 |
| 商用表現 | UI・reportにLEGAL REVIEW REQUIREDを維持 | marketing、installer、EULA、distribution channelをレビュー |

## 6. 配布判断に渡すべき未解決事項

本モデルがApache-2.0で公開されていることは、weight artifactについてのモデルcard表記である。[1] しかし、Local Media AIにおける商用同梱・再配布の判断には、モデルcardのみでなく、学習dataの構成・入手可能性・各componentのnotice・GPU runtime・地域別規制・製品のdistribution methodを含む追加確認が必要である。

> **配布判断: LEGAL REVIEW REQUIRED**
>
> 本候補は技術的にはDevelopment Production Candidateとして統合・検証済みである。これは商用配布の承認ではない。法務確認完了前に、本モデルを商用default、commercially cleared、または配布可能確定として扱わない。

## References

[1]: https://huggingface.co/llm-jp/llm-jp-clip-vit-base-patch16 "Model Card for llm-jp/llm-jp-clip-vit-base-patch16"
[2]: https://ai.google.dev/gemma/terms "Gemma Terms of Use"
[3]: https://huggingface.co/datasets/laion/relaion2B-en-research-safe "ReLAION English research-safe dataset reference"
[4]: https://github.com/mlfoundations/open_clip "OpenCLIP upstream repository"
[5]: https://www.apache.org/licenses/LICENSE-2.0 "Apache License, Version 2.0"
