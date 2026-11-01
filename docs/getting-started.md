# 開發環境快速開始

目前提供的是開發環境啟動方式，尚未提供完整的一鍵安裝流程。以下範例使用 Windows LocalDB；其他 SQL Server 環境請參考[資料庫 Migration 說明](architecture/sql-server-migrations.md)。

1. 安裝 [global.json](../global.json) 指定的 .NET SDK、SQL Server LocalDB，以及用來建立資料庫的 `sqlcmd`。資料庫相容層級須為 150 以上。
2. 建立獨立的開發資料庫，並執行 Migration：

   ```powershell
   sqlcmd -S "(localdb)\MSSQLLocalDB" -E -b -C -Q "IF DB_ID('Vigiloom_Development') IS NULL CREATE DATABASE [Vigiloom_Development]"
   $env:VIGILOOM_MIGRATION_SQL_CONNECTION = 'Server=(localdb)\MSSQLLocalDB;Database=Vigiloom_Development;Integrated Security=true;Encrypt=false'
   dotnet run --project src/Hosts/Vigiloom.Migrator --configuration Release
   ```

3. 設定首次啟用密鑰，啟動 Web：

   ```powershell
   # 換成至少 32 個字元的隨機密鑰，請勿提交至版本控制。
   $env:Vigiloom__SetupToken = '<你的首次啟用密鑰>'
   dotnet run --project src/Hosts/Vigiloom.Web
   ```

   開啟 [首次設定頁面](https://localhost:7043/account/setup)，輸入密鑰並建立管理員。完成後請從執行環境移除啟用密鑰，並重新啟動 Web。若瀏覽器尚未信任本機開發憑證，可先執行 `dotnet dev-certs https --trust`。

4. 在另一個終端機啟動背景 Worker：

   ```powershell
   $env:DOTNET_ENVIRONMENT = 'Development'
   dotnet run --project src/Hosts/Vigiloom.Worker
   ```

登入後即可開啟設備管理頁面；開發環境的 API 文件位於 [OpenAPI 文件](https://localhost:7043/openapi/v1.json)。正式環境的連線、憑證與部署設定請參考[帳號與存取說明](architecture/identity-and-access.md)。

## 建置與測試

```powershell
dotnet restore Vigiloom.slnx
dotnet build Vigiloom.slnx --configuration Release --no-restore
pwsh tests/Vigiloom.Web.Tests/bin/Release/net10.0/playwright.ps1 install chromium
dotnet test Vigiloom.slnx --configuration Release --no-build --no-restore
```

瀏覽器測試需要 PowerShell 7 和 Playwright Chromium；Linux 的 Chromium 相依套件請參考[瀏覽器測試說明](design/browser-testing.md)。測試使用 MSTest。Windows 資料庫測試使用獨立的 LocalDB 臨時資料庫，不使用開發資料庫；Linux 需要明確設定隔離的 SQL Server 測試環境。環境設定與清理方式見 [SQL Server 測試說明](../tests/Vigiloom.Testing.SqlServer/README.md)。

## 延伸文件

- [CI 與測試環境](architecture/continuous-integration.md)
- [資料庫與 Migration](architecture/sql-server-migrations.md)
- [帳號、授權與首次設定](architecture/identity-and-access.md)
- [平台架構](architecture/ADR-0002-modular-platform-and-hosts.md)
