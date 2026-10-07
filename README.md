# VidTidy

Windows desktop app that finds duplicate and near-duplicate videos in a large local library and helps clean them up safely — fully offline, without touching the originals until you approve. Available in English and Japanese (chosen on first start).

手元のPCにある数百〜数千本の動画から、**完全に同じ動画・よく似た動画・長さが違うコピー**を見つけ、安全に整理するための Windows デスクトップアプリです。すべてローカルで動作し、動画や解析結果を外部へ送信しません。

## できること

- **整理画面**：重複・類似のグループを「空く容量が大きい順」に並べ、残す1本を決めて、残りをまとめてごみ箱へ送る（完全削除はしない）。判断に迷う組は「このまま残す」で除外できる。
- **完全一致の検出**：SHA-256 で中身がまったく同じファイルを見つける。
- **よく似た動画の検出**：動画内の複数の時点から取った場面の指紋（dHash）を比べる。再エンコード・解像度違い・黒い帯（レターボックス/ピラーボックス）付きのコピーも対象。
- **長さが違うコピーの検出**：動画全体の時系列を比べ、頭や終わりを切った版・一部を抜いた版を見つけ、「長い方にだけある部分」（例：最初の 0:03 と 20:00〜30:00）を表示する。
- **日本語・英語に対応**：初回起動時に表示言語を選べる。あとから設定画面で変更できる（次の起動から反映）。仕組みは [docs/localization.md](docs/localization.md)。
- **AI 意味検索（任意）**：日本語 CLIP モデル（llm-jp-clip）で「海」「夜景」のような言葉から場面を探す。別途ランタイムとモデルの準備が必要（後述）。

## 設計で大事にしたこと

- **元の動画を壊さない**：解析は読み取り専用。ファイル操作は「確認 → 最終確認 → ごみ箱へ移動」の段階を踏み、対象が途中で変わったら止まる。
- **HDD にやさしい**：物理ディスクごとに読み取りを1本に絞り、計算（GPU/DB）とは別の列で並行させる。HDD の温度を実測して作業と休憩のサイクルを決め、データ・キャッシュは SSD に置く。
- **プライバシー**：クラウドを使わない。開発中もユーザーの実ライブラリは読まず、生成した動画と、許可を得た少数のサンプルだけで検証した。
- **古い版に戻せる**：後から増えた表は版管理された移行の外に置き、外部キーを付けない。古いビルドでもカタログを開ける。

## 実測（開発環境での記録）

| 項目 | 結果 |
|---|---|
| 解析時間（合成動画400本・63GiB、HDD、コールドキャッシュ） | 74.9分 → **26.5分** |
| サムネイル完了まで（同上） | 34.7分 → **4.2分** |
| HDD への書き込み回数（同上） | 253,295回 → **366回** |
| 長さが違うコピーの検出（生成した評価セット） | 含まれる49組中48組を検出、別の動画31組の誤判定 **0組** |
| 自動テスト | 約870件（単体・結合・WPF UI） |

類似検出の評価方法と結果は [docs/similar-detection-accuracy-2026-10-02.md](docs/similar-detection-accuracy-2026-10-02.md) を参照。解析時間の計測は開発用の PC で、生成した合成動画だけを使って行った。

## 構成

プロジェクトやフォルダーの名前（`LocalMediaAI.*`、データの保存先 `LocalMediaAI`）は、開発中の旧名「Local Media AI」のままです。

| プロジェクト | 役割 |
|---|---|
| `src/LocalMediaAI.Core` | ドメインモデル、判定ロジック（近似重複の判定、時系列の照合、黒帯の除去など） |
| `src/LocalMediaAI.Infrastructure` | SQLite カタログ、スキャン、FFmpeg 呼び出し、ジョブのスケジューラ、保護付きの読み取りセッション |
| `src/LocalMediaAI.App` | WPF（.NET 10）の画面。整理画面は `Organize/` |
| `worker/python` | AI 意味検索用の Python ワーカー（gRPC） |
| `tests/` | xUnit の単体・結合・UI テスト |
| `tools/` | 計測用ツール（類似検出のベンチマーク、スループット計測など） |
| `docs/` | 設計書、ADR、データベース、表示言語の仕組み、類似検出の評価、整理画面の試作（`ui-redesign-prototype/`） |

技術：C# / .NET 10 / WPF / SQLite（Microsoft.Data.Sqlite）/ FFmpeg / Python（gRPC、PyTorch、OpenCLIP）

## ビルドと実行

必要なもの：Windows 10/11 x64、[.NET SDK 10.0.400 以降](https://dotnet.microsoft.com/)、FFmpeg（`ffmpeg.exe` と `ffprobe.exe`）

FFmpeg はリポジトリに含めていません。まずリポジトリ直下の `runtime\ffmpeg\` に `ffmpeg.exe` と `ffprobe.exe` を置いてください（テストはここを使います）。

```powershell
git clone <このリポジトリ>
cd <リポジトリ>
dotnet build LocalMediaAI.sln -c Release
# アプリは実行ファイルの隣の runtime\ffmpeg を使うので、ビルド出力へコピーする
$out = "src\LocalMediaAI.App\bin\Release\net10.0-windows"
New-Item -ItemType Directory "$out\runtime" -Force | Out-Null
Copy-Item runtime\ffmpeg "$out\runtime\" -Recurse -Force
& "$out\LocalMediaAI.App.exe"
```

テスト：

```powershell
dotnet test LocalMediaAI.sln
```

一部のテストは `runtime\ffmpeg` の FFmpeg で動画を生成して使います。AI ワーカーのテスト（`WorkerRuntimeTests` と `WorkerEmbeddingIntegrationTests`、8件）は、下の Python ランタイムとモデルを用意したときだけ通ります。用意していなければ `WORKER_RUNTIME_NOT_FOUND` で失敗します。

データ（カタログ・キャッシュ）は既定で `%LOCALAPPDATA%\LocalMediaAI` に保存されます。実行ファイルの隣に `portable.flag` を置くと、その場所に保存します。

### AI 意味検索を使う場合（任意・上級者向け）

AI 意味検索には、ポータブル Python ランタイム（`runtime\python*`）とモデル（`models\llm-jp-clip-vit-base-patch16` ほか、約1GB）が必要です。どちらもサイズとライセンスの都合でリポジトリに含めていません。準備用のスクリプトは `scripts\` にありますが、完全には自動化されていません。ワーカーがなくても、重複・類似の検出と整理は動きます。

モデルのライセンスと配布に関する整理は [docs/license-engineering-dossier-llm-jp-clip.md](docs/license-engineering-dossier-llm-jp-clip.md) を参照してください。

## 既知の制限

- Windows 専用です。
- AI 意味検索のランタイム準備は手作業が残っています。
- 別のライブラリフォルダー間の移動は、安全確認の都合で未対応です。
- そのほかは [docs/known-limitations-v1.md](docs/known-limitations-v1.md) を参照してください。

## 開発について

個人開発です。要件・安全上の制約・受け入れ条件を自分で決め、実装には AI コーディングエージェント（OpenAI Codex、Claude Code など）を使いました。主な設計・判断・評価は `docs/` にまとめています（開発中の作業記録は含めていません）。

## ライセンス

[MIT License](LICENSE)。FFmpeg、Python ランタイム、AI モデルは含まれておらず、それぞれのライセンスに従います。
