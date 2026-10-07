# Known Limitations v1

Architecture Baseline v1とVertical Slice v1の範囲を超える機能は意図的に実装していない。AIモデル（ASR、embedding、検出、顔クラスタリング）、semantic search、duplicate deletion、移動・リネーム、クラウド同期、ログイン、telemetry、runtime/model自動ダウンロードは対象外である。

| 項目 | 状態 | 影響・扱い |
|---|---|---|
| Python Worker | Protocol、token方針、diagnostic health skeletonのみ | Python 3.14/gRPC hostは同梱・起動していない。C#側はWorkerなしで動作する。 |
| FFmpeg配布 | RuntimeRoot検出とerror handlingを実装 | 実行時自動downloadはしない。portable publish後に`runtime/ffmpeg`へ明示配置する。 |
| AccessDenied | scannerはIgnoreInaccessible、Jobは`MEDIA_ACCESS_DENIED`を保存 | 接続端末の権限モデル上、実際のAccessDenied注入は未測定。 |
| rename identity | Windows Volume + File IDでreconciliationを実装 | 同一volume renameの実機E2Eは未測定。 |
| concurrency | scan / metadata / thumbnailにstage別のbounded admissionを実装 | 既定値は各1でHDD安全な直列処理を維持する。`LOCAL_MEDIA_AI_SCAN_CONCURRENCY`、`LOCAL_MEDIA_AI_METADATA_CONCURRENCY`、`LOCAL_MEDIA_AI_THUMBNAIL_CONCURRENCY`で1〜4に設定可能。無制限値は拒否する。 |
| thumbnail placeholder | CacheRootのサムネイルを一覧で表示し、未生成・decode失敗時はUI placeholderを表示 | placeholderはWPF描画であり、原本にもCacheRootにも追加の副作用を作らない。ErrorCodeはDetails/Jobsで確認する。 |
| logging field | JSONL structured logging、Job Eventを実装 | すべてのイベントへMediaFileId/MediaContentIdを強制付与する相関enricherは次Phase。 |
| build warnings | nullableとNuGet脆弱性警告をエラーに維持 | 最終clean buildで一般CA警告は78件（CA1725 60、CA1707 12、CA1001 4、CA1822 2）。機能検証を妨げないが品質バックログとして解消対象。 |

## 検証済み範囲

実際のFFmpeg 8.1.1をRuntimeRoot相当へ配置し、64×48・2秒のMP4 fixtureに対して、Scan、FFprobe JSON metadata、FFmpeg JPEG thumbnail、SQLite保存、CacheRoot生成、原本SHA-256/size/mtime不変を統合テストで確認した。10,000行のSQL paging/search、restart recovery fault injection、Offline Root保護、WPF起動/portable DB初期化も確認済みである。

## 実行上の注意

Windows上でローカルSDKを隔離配置している検証環境では`PROGRAMFILES(X86)`環境変数が欠けていたため、SDK/NuGet実行時に明示設定した。通常のWindowsログオン環境でこの値が存在する場合には不要である。これはアプリ実行時依存ではなく、開発端末のSDK復元環境に限る。
