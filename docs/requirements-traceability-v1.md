# Architecture Baseline v1 / Vertical Slice v1 要件トレーサビリティ

本書は `pasted_content.txt` と `manus_media_manager_architecture_v1_prompt-2.txt` を上位仕様として、既存実装の監査結果と今回の最小改修範囲を対応付ける。実測結果は最終報告書で置き換えるまで暫定とし、未検証事項を合格扱いしない。

## 1. 実装境界

本改修は **AI機能の追加ではない**。対象は、既存のFolder Registration、SQLite永続Job、Scan、ffprobe metadata、ffmpeg thumbnail、Cache artifact、WPF一覧・検索・Details、Crash Recoveryを、指定された安全性・復旧性・検証条件まで完成させることである。ASR、embedding、semantic search、顔・人物解析、削除、移動、リネーム、クラウド同期、telemetry、モデル/FFmpeg自動ダウンロードは対象外とする。

| 基準 | 実装方針 |
|---|---|
| 原本保護 | 原本はread-only入力として扱い、thumbnailなどの成果物はTempRootからCacheRootへatomic publishする。テストでSHA-256/size/mtimeを前後比較する。 |
| Durable State | SQLite schema、Job、Analysis、ArtifactはC#のみが所有して書き込む。Python Workerはプロトコル境界に限定し、SQLiteを直接操作しない。 |
| Identity | `MediaFileId` と `MediaContentId` を分離し、選択・再照合にrow indexやpathだけを使わない。QuickFingerprintは候補抽出であり、Full SHA-256と同一視しない。 |
| HDD安全性 | Scan、metadata、thumbnailの各stageに保守的な設定可能上限を設け、無制限parallelismを禁止する。 |
| 可観測性 | ErrorCode、永続Job/Event、JSONLローカルログ、UI Jobs/Errorsで主要失敗を追跡可能にする。 |

## 2. MUST / Definition of Done 対応表

| ID | 上位仕様の受入条件 | 既存証拠 | 今回の扱い | 検証方法 |
|---|---|---|---|---|
| D-01 | Windows向け.NET 10 WPF solutionがbuildできる | `LocalMediaAI.sln` とWPF App | 維持 | Windows clean build |
| D-02 | schema v1 / migration / WAL / FK | `SqliteLibraryRepository`、`SqliteJobStore` | 維持 | empty DB migration、PRAGMA、integrity check |
| D-03 | LibraryRootと永続Scan Job | Core + SQLite job store | 維持 | folder scan integration |
| D-04 | MediaFile / MediaContent登録と再scan重複防止 | explicit SQL reconcile | 維持・renameテスト追加 | rescan、same-volume identity test |
| D-05 | ffprobe metadata永続化 | `FfmpegAdapter` / pipeline | 維持・失敗テスト追加 | normal/corrupt media test |
| D-06 | CacheRoot thumbnail / artifact | temp→cache publish | 維持・temp cleanup test追加 | normal、incomplete temp recovery |
| D-07 | DataGrid / Details / Jobs / Errors | WPF `MainWindow` / ViewModel | failure placeholderを追加 | XAML静的監査、failure state test |
| D-08 | stale-safe async search | RequestId + CancellationToken gate | 維持 | unit test |
| D-09 | RootOfflineとFileMissing分離 | root availability / absence reconciliation | 維持 | offline root integration |
| D-10 | Running Job restart recovery | `RecoverInterruptedJobsAsync` | 維持 | fault injection integration |
| D-11 | 原本の不変 | file hash test | 維持 | vertical slice + recovery test |
| D-12 | FFmpeg未配置・壊れたmediaでapp全体が落ちない | error pathは実装済み | 自動テストを追加 | pipeline integration |
| D-13 | C#がDB write所有 | C# repository only | 維持 | Worker source/protocol静的監査 |
| D-14 | Worker protocolとHealth/Version | proto/skeleton/token policy | 既存設計を検証し、実host不可なら理由を明記 | Python compile / protocol source audit |
| D-15 | 10,000行で全件materializeしない | DB paging / 10k test | 維持・現行値を実測 | performance sanity test |
| D-16 | bounded concurrency | `ResourceCostEstimate`のみ、runner直列 | **実装する** | stage別options unit/integration |
| D-17 | decode失敗時placeholder + error state | error stateのみ | **実装する** | UI binding / error integration |
| D-18 | AccessDenied error handling | scanner/pipelineのcatch | 可能な範囲を自動注入し、OS権限E2Eは未測定と明記 | exception mapping test |
| D-19 | portable self-contained directory | publish script / portable.flag | 維持 | script / portable startup test |
| D-20 | docs、ADR、schema、limitations、実測 | 既存docs | traceability・report・limitationを更新 | final audit |

## 3. 最小改修順序

| 順序 | 変更 | 理由 | 受入基準 |
|---|---|---|---|
| 1 | Coreにstage別の`PipelineConcurrencyOptions`を追加する。 | 上位仕様§11の設定可能かつboundedなadmissionを明示する。 | 各上限は1以上で、既定値はHDD安全な1。 |
| 2 | Pipeline runnerへstage semaphoreを導入する。 | metadataとthumbnailがそれぞれの独立上限を超えないようにする。 | acquisition/releaseが例外・cancelでも対になる。 |
| 3 | Thumbnail失敗時のUI placeholderをApp resourceで表示する。 | 仕様§12.3のplaceholderを、原本・cacheへ副作用を作らず満たす。 | artifactがないときにplaceholder、ErrorCodeはDetails/Jobsで可視。 |
| 4 | media tool failure、temp artifact cleanup、rename、AccessDenied mappingのテストを追加する。 | 仕様§21の自動検証条件を閉じる。 | テストが環境独立かつ原本非破壊。 |
| 5 | Worker/portable実測、full build/test、final audit/reportを実行する。 | 未実施事項をPASSにしない。 | 実測値と未測定を明記。 |

## 4. 判断記録

既存のrunnerは単一job直列であり、無制限parallelismではない。しかし上位仕様§11はstageごとの設定可能なconcurrencyを最低限要求するため、単に「直列だから安全」とは判定しない。既定値1のstage別設定を導入し、設計上の将来拡張性と現在のHDD安全性を同時に確保する。

また、一般CA warningはbuild failureではなく、Architecture Baselineの機能受入条件にもwarning 0は明記されていない。したがって本改修では機能安全性に直結する未達を優先する。ただし、検証時の警告件数を実測として残し、最終判定の条件として隠さず報告する。

## 5. 参照

| 番号 | 文書 |
|---|---|
| [1] | `manus_media_manager_architecture_v1_prompt-2.txt` §11、§12、§21、§29〜§31 |
| [2] | `pasted_content.txt` §1〜§7 |
| [3] | `docs/known-limitations-v1.md`（改修前の未達台帳） |
| [4] | `src/LocalMediaAI.Core/Domain.cs`、`src/LocalMediaAI.Infrastructure/MediaPipeline.cs`、`tests/LocalMediaAI.IntegrationTests/IntegrationTests.cs` |
