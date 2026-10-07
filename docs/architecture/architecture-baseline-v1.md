# Architecture Baseline v1

## 目的と境界

本版は、ローカル動画を**読み取り専用**で台帳化し、FFprobeメタデータとFFmpegサムネイルをSQLiteへ追跡可能に記録する基盤である。AIモデル、削除、移動、リネーム、クラウド同期、テレメトリは対象外である。原本メディアはどの処理でも出力先にならず、DB・キャッシュ・一時ファイルの破損は原本へ波及しない。

## プロセスと依存方向

```text
WPF App (MVVM, UI thread)
  -> Core (domain records, interfaces, use-case contracts)
  -> Infrastructure (SQLite explicit SQL, scanner, FFmpeg, cache, scheduler)
  -> WorkerProtocol (future loopback gRPC contract)
  -> future Python worker (no SQLite ownership)
```

`Core` はWPF、SQLite、FFmpeg、P/Invokeへ参照を持たない。SQLite DBはLibrary状態、Job状態、metadata、analysis provenanceのdurable sourceであり、thumbnail cacheとtempは再構築可能である。Python WorkerはDB pathを受け取らず、C# Core/InfrastructureのみがDB writerである。

## RootとPortable

`portable.flag` がAppRoot直下に存在する場合、DataRoot、CacheRoot、ModelsRoot、RuntimeRoot、TempRootをAppRoot相対に解決する。各rootは将来設定により別ドライブへ変更可能であり、Cache/ModelsをAppRootへ強制しない。

| Root | v1の用途 | 原本との関係 |
|---|---|---|
| AppRoot | 実行ファイルと設定 | 原本外 |
| DataRoot | SQLite、JSONLログ、durable artifact | 原本外 |
| CacheRoot | thumbnail cache | 原本外 |
| ModelsRoot | 将来のモデル配置のみ | 原本外 |
| RuntimeRoot | FFmpeg/Pythonの明示配置候補 | 原本外 |
| TempRoot | atomic publish前のjob専用一時成果物 | 原本外 |

## メディア安全境界

走査は指定Root内のみを再帰し、reparse pointを既定で追跡しない。パスはcanonical/full pathへ正規化し、後続処理では存在と絶対パスを再確認する。FFprobe/FFmpegは`ProcessStartInfo.ArgumentList`で起動し、shell文字列連結を行わない。入力はread-only、出力はCacheRoot/TempRootだけを許可する。子プロセスにはtimeoutを適用するが、永続Jobの生存確認はheartbeatとphaseで行い、単一の進捗停止時間を失敗判定に用いない。

## Job実行境界

JobはSQLiteへ先に永続化され、`Pending → Running → Completed/Partial/Failed/RetryWaiting`を明示的に遷移する。開始時に`Running`が残っていれば、単純失敗ではなく`RetryWaiting + AppRestartRecovery`へ遷移する。サムネイルはTempRootで生成、非空・hashを検査してからCacheRootにatomic moveし、Analysis/ArtifactをDBへ確定する。

## Worker protocol

`worker/python/local_media_ai_worker.proto` はprotocol version、Health、JobRequest、progress、result、error、cancelを表す。将来のgRPC serverは127.0.0.1へ限定し、起動ごとのランダムtokenを環境継承で渡す。v1 skeletonはAI backendもSQLite writeも持たない。
