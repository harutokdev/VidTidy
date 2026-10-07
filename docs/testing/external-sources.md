# 外部技術情報の記録

| 用途 | 情報 | 参照 |
|---|---|---|
| .NET SDK導入 | Windows向けSDKにはWPF実行に必要なDesktop Runtimeを含み、CI/非管理者導入には`dotnet-install.ps1`を利用できる。 | [Microsoft Learn: Install .NET on Windows](https://learn.microsoft.com/en-us/dotnet/core/install/windows) / [dotnet-install scripts](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-install-script) |
| NuGet復元 | NuGetのWindows環境パス解決は`PROGRAMFILES(X86)`、`APPDATA`、`LOCALAPPDATA`を参照する。接続端末では`PROGRAMFILES(X86)`が未設定だったため、開発コマンドで明示設定した。 | [NuGetEnvironment source](https://raw.githubusercontent.com/NuGet/NuGet.Client/dev/src/NuGet.Core/NuGet.Common/PathUtil/NuGetEnvironment.cs) |
| SQLite native dependency | `SQLitePCLRaw.lib.e_sqlite3` 2.1.11には高深刻度の既知脆弱性が報告されているため、Microsoft.Data.Sqlite 10.0.11とSQLitePCLRaw.lib.e_sqlite3 3.53.3を明示解決した。 | [NuGet package](https://www.nuget.org/packages/SQLitePCLRaw.lib.e_sqlite3/) / [GHSA-2m69-gcr7-jv3q](https://github.com/advisories/GHSA-2m69-gcr7-jv3q) |
