# Vigiloom 開發 Agent 指引

## HTTP 與資料庫資源生命週期

外部 HTTP 呼叫使用 `IHttpClientFactory` 或有明確理由、生命週期受控且可重用連線的等效做法；設定合理的逾時、取消與回應釋放，避免每次請求建立獨立連線集區。不得把使用者憑證或要求專屬狀態放進共用 handler／預設標頭；需用 Cookie 時評估 factory handler 的 CookieContainer 共用與輪替行為。

資料存取沿用既有的 `IDbContextFactory<TContext>` 註冊；每個操作建立並及時釋放 context，不跨並行操作共用，不將 context 保留於長生命週期服務。`DbContext` pooling 與底層 SQL 連線 pooling 是不同機制：是否啟用 context pool 先驗證 context／相依服務可安全重用、狀態完整重置與實際效益，不能為了 pooling 洩漏狀態或無條件改用 pooled factory。連線、交易和 reader 均須正確釋放，依需求驗證取消、失敗與並行情境。

## 已合併分支清理

PR 合併後，確認目標分支已包含變更，再清理由本次工作建立且沒有未合併提交的本機與遠端分支；先保護工作區現有修改，遠端刪除時確認分支未被更新。不刪除他人的分支或尚未完成的工作。

## 識別碼與資料庫：先讀再做

新增或修改 entity、migration、API 識別碼、Agent 訊息或遙測接收以前，必須閱讀並遵循 [ADR-0001](docs/architecture/ADR-0001-identifiers-and-keys.md)。這是已採用的決策，不是自行選擇 generator 的參考清單。

- 可獨立管理資源：UUID v7 的 Guid Id／uniqueidentifier，nonclustered PK；另有 bigint IDENTITY RowId 作 unique clustered index。
- 高流量樣本／紀錄：bigint RowId clustered PK；資源 FK 是 Guid。純關聯表用已定義複合 key。
- 統一呼叫 Guid.CreateVersion7()；資源 Id 明確 ValueGeneratedNever，不依賴 EF／SQL 預設 GUID 生成，也不自換 Guid.NewGuid、NEWID、NEWSEQUENTIALID。
- RowId 不作公開或跨模組身分；歷史依 UTC 時間欄位查詢，不能靠 Guid 排序。
- 自有訊息／批次識別重送不變；OTel TraceId／SpanId／外部 instance ID 保留原格式。
- 以真實 SQL Server 驗證 schema、約束與索引；效能未量測前不宣稱 UUID v7 在 SQL Server 已解決順序寫入。
- 改政策需先提出理由與 ADR 修訂，不在產品程式裡靜默改鍵型別。

## HTTP 與資料庫資源管理

- HTTP 使用 `IHttpClientFactory` 管理 handler／連線生命週期；依用途設定逾時、取消及連線更新，正確釋放 response、stream 與其他持有資源。不為每次請求建立獨立連線池。
- EF Core 以 `IDbContextFactory<TContext>` 依操作建立並釋放 context，不跨並行操作共用。只有確認 context 無不安全的跨次使用狀態時才啟用 pooled factory；SQL 連線池和 EF context pool 是不同機制。
- 對涉及重用與並行的變更，驗證釋放及狀態隔離；涉及 SQL 實際連線或寫入時，以隔離的真實 SQL Server／LocalDB 補充驗證。

## 既有工作邊界

使用者已於 2026-11-01 授權移除舊方案，建立全新 Solution、專案／測試骨架及相依關係。使用者已授權持續開發已討論且未明確擱置的產品需求，依看板前置條件安排，不必每完成一項需求就重新請求授權。
既定方向為 .NET 10／Blazor／Web API／EF Core 10＋SQL Server；完整需求與 Task 以 [Vigiloom Project](https://github.com/users/ChengYen-Tang/projects/5) 為準。

架構邊界先讀 [ADR-0002](docs/architecture/ADR-0002-modular-platform-and-hosts.md)：模組化中央平台、可分程序的 Web／Worker、獨立 Agent／Updater。Blazor 使用 Auto、按頁面啟用；瀏覽器元件在 Web.Client，只參考 Contracts，不引用後端模組或 EF。
測試專案一律 MSTest；目前使用範本的 MSTest 4.0.2／VSTest。共用設定在 tests/Directory.Build.props，套件版本在 Directory.Packages.props；不加入 xUnit／NUnit／TUnit，不建立假成功的佔位測試。

資料庫測試先讀 [開發／測試資料庫隔離規範](docs/architecture/development-and-test-databases.md)。Windows 本機開發與測試都用同一個 LocalDB instance（預設 MSSQLLocalDB），使用不同資料庫與連線設定；開發 DB 持續保留，測試每次建立唯一臨時 DB。成功／失敗均清理並驗證刪除；中止殘留由明確擁有權 manifest 補清。清理不得操作開發／正式 DB、刪除系統 DB，或停止／刪除共用 LocalDB instance；只可透過 master 管理連線處理自己登記的臨時 DB。純單元測試不連 DB；LocalDB／SQL Server 測試標記 Integration／Database。fixture 與補清方式見 tests/Vigiloom.Testing.SqlServer/README.md；正式 migration／schema 驗證仍由 REQ-02-T02 完成。

UI 風格已選第二輪第 2 張（Vigiloom-UI-Concept-2-Revision-2.png）。做到各 UI 需求時，開發 Agent 依該需求透過 Product Design 設計與驗證，再以 Blazor 實作，沿用共用風格；不以全站原型完成為前置。
設備／主機與應用服務各有獨立身分、欄位和狀態；服務不強制綁主機。選單含網路測速。LINE 在登入後由 Administrator 從設定頁管理。AI Agent 分析維持擱置。
