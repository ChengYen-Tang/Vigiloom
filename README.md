# Vigiloom

**從同一個介面掌握設備、伺服器與應用服務的監控平台。**

Vigiloom 正以 .NET 建立一個集中式監控平台，讓管理者透過網頁管理監控對象、檢視狀態，並以 API 與其他系統整合。設備與應用服務是不同的監控對象：設備可以只監控 Ping，也可以進一步透過 Agent 蒐集主機資訊；應用服務不必綁定特定主機。

[![CI](https://github.com/ChengYen-Tang/Vigiloom/actions/workflows/ci.yml/badge.svg?branch=master)](https://github.com/ChengYen-Tang/Vigiloom/actions/workflows/ci.yml) · [需求看板](https://github.com/users/ChengYen-Tang/projects/5)

> **開發中**：目前已具備登入、設備資料管理、API 和背景工作基礎。Ping 實際量測、Agent 採集、應用服務觀測與公開狀態頁等功能仍在開發，尚不適合當作完整監控產品部署。

## 目前可以使用

- **帳號與權限**：使用內建帳號密碼登入，提供 Administrator 和 Read only 角色，可啟用驗證器雙重驗證。
- **設備管理**：新增及編輯 IPv4、IPv6 或 DNS 目標，選擇「僅 Ping」或「深入監控」模式，管理多個目標、啟停與封存。模式選擇目前不會啟動 Ping 或 Agent 採集。
- **Web API**：管理員可建立具有讀取或寫入權限的 API 憑證，供外部系統存取目前已實作的設備 API。
- **背景工作**：Worker 使用 Hangfire 與 SQL Server 執行背景工作；管理員可在登入後查看工作狀態及進階管理介面。

## 接下來的方向

Vigiloom 的目標是將下列功能整合到同一個操作介面，實際進度以[需求看板](https://github.com/users/ChengYen-Tang/projects/5)為準：

- 對設備執行 Ping，呈現延遲、歷史紀錄與狀態變化。
- 以 Windows／Linux Agent 蒐集伺服器資訊，支援安裝、版本管理與更新。
- 接收應用服務的觀測與健康資訊，提供服務狀態及歷史檢視。
- 依公開／私人設定呈現資訊，並提供適合外部使用者的公開狀態頁。
- 提供設備與服務群組、網路測速，以及包含 LINE 在內的通知方式。

這些項目目前是規劃中的產品能力，不代表已完成或已支援正式部署。

## 開發者快速開始

目前提供的是開發環境啟動方式，尚未提供完整的一鍵安裝流程。以下範例使用 Windows LocalDB；其他 SQL Server 環境請參考[資料庫 Migration 說明](docs/architecture/sql-server-migrations.md)。

1. 安裝 [global.json](global.json) 指定的 .NET SDK、SQL Server LocalDB，以及用來建立資料庫的 `sqlcmd`。資料庫相容層級須為 150 以上。
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

登入後即可開啟設備管理頁面；開發環境的 API 文件位於 [OpenAPI 文件](https://localhost:7043/openapi/v1.json)。正式環境的連線、憑證與部署設定請參考[帳號與存取說明](docs/architecture/identity-and-access.md)。

## 建置與測試

```powershell
dotnet restore Vigiloom.slnx
dotnet build Vigiloom.slnx --configuration Release --no-restore
dotnet test Vigiloom.slnx --configuration Release --no-build --no-restore
```

測試使用 MSTest。Windows 資料庫測試使用獨立的 LocalDB 臨時資料庫，不使用開發資料庫；Linux 需要明確設定隔離的 SQL Server 測試環境。環境設定與清理方式見 [SQL Server 測試說明](tests/Vigiloom.Testing.SqlServer/README.md)。

## 技術與文件

中央平台採模組化架構，使用 .NET 10、Blazor Web App、ASP.NET Core Web API、EF Core 與 SQL Server；Web 和 Worker 可以分開執行。Agent 與 Updater 為獨立程式，目前仍在開發中。

- [CI 與測試環境](docs/architecture/continuous-integration.md)
- [資料庫與 Migration](docs/architecture/sql-server-migrations.md)
- [帳號、授權與首次設定](docs/architecture/identity-and-access.md)
- [平台架構](docs/architecture/ADR-0002-modular-platform-and-hosts.md)
