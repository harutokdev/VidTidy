# Test Plan v1

## 自動Unit

`LocalMediaAI.Core.Tests`はJob state transition、canonical config hash/cache key、SearchRequestGateによるstale result rejectionを検証する。`LocalMediaAI.Infrastructure.Tests`はempty DB migration、quick_check、rescanでのstable MediaFileId、QuickFingerprintの決定性、Running jobのrecovery、Offline rootでのMissing誤判定防止を検証する。

## Integration

`LocalMediaAI.IntegrationTests`はfault injectionとして`Running`を残してrunnerを停止した状態を作り、再初期化で`RetryWaiting + AppRestartRecovery`へ再queueされること、DB quick_check、fixture原本のsize/mtime/SHA-256不変を確認する。10,000件のdummy media rowを投入し、SQL search/pageの返却件数がlimitを越えないことを確認する。

## 手動E2E

1. RuntimeRootのffmpeg/ffprobeを配置し、短いFFmpeg生成fixtureをRootへ置く。
2. WPFでRootを登録し、Scanを開始する。
3. MediaFile/MediaContent、metadata、thumbnail artifact、DataGrid/Details/Jobsを確認する。
4. ffprobe/ffmpegを未配置にしてもアプリ全体が起動し、当該Jobだけがactionable errorとなることを確認する。
5. ScanまたはThumbnail中にrunnerを強制終了し、再起動後のjob recovery、DB integrity、原本不変、duplicate row/artifact非増殖を確認する。

## 原本保護

fixture原本について、処理前後のSHA-256、byte length、mtimeを記録する。Cache/Temp/Dataにのみ書込みが発生し、Library Root内にthumbnail、sidecar、log、DBが作成されないことを確認する。
