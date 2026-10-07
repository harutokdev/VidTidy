# Database Schema v1

SQLite schema versionは`schema_migration`のversion 1で開始する。migrationは単一transactionで適用し、より新しいschemaを検出した場合は実行を停止して既存DBを変更しない。

| Table | 主要key | 役割 |
|---|---|---|
| schema_migration | version | 明示migration履歴 |
| app_setting | key | 将来設定のdurable保存先 |
| library_root | library_root_id, normalized_path unique | 登録Root、Online/Offline、最終Scan |
| library_scan | scan_id, library_root_id | Scan run統計と完了境界 |
| media_content | media_content_id | pathから分離したprovisional/exact content identity |
| media_file | media_file_id, root/path unique | location、File ID、fingerprint、presence |
| analysis | analysis_id | processor/config/input/versionを伴う解析provenance |
| artifact | artifact_id | durable/cache/temp成果物のDB追跡 |
| media_metadata | media_file_id | FFprobe由来の正規化metadata |
| job | job_id | state/phase/reason/retry/heartbeat/progress |
| job_event | job_event_id, job_id | UI追跡用の不可変イベント |

## 必須index

`media_file(library_root_id, normalized_path)`、`media_file(volume_identity,file_identity)`、`media_file(media_content_id)`、`analysis(media_content_id,analysis_type,status)`、`library_scan(library_root_id,started_at DESC)`、`job(state,not_before)`、`job_event(job_id,occurred_at DESC)`を作成する。検索と一覧はSQL filter/sort/pagingへ委譲し、UIが全件をViewModel化しない。

## SQLite設定と回復

全connectionで`foreign_keys=ON`、`busy_timeout=5000`、`journal_mode=WAL`、`synchronous=FULL`を設定する。DB writerはC#側に集約し、transactionはmigration、scan reconciliation、job acquireのような短い単位に限定する。idle時またはpipeline完了時に`PRAGMA wal_checkpoint(TRUNCATE)`を実行する。`PRAGMA quick_check`は結合試験とCrash Recovery試験で確認する。

DB backup/restoreはv1ではアプリ外のファイルコピー運用とし、コピー前にアプリを終了してWAL checkpointを行う。破壊的migrationとproduction DB reset commandは提供しない。
