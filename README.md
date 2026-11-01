# Vigiloom

以 .NET 10 重寫的集中監控平台。架構採模組化中央平台，Blazor Web App（Interactive Auto）＋Web API，Web／Worker 可分開執行；資料存取方向為 EF Core 10＋SQL Server。端點 Agent 與 Updater 各自獨立。

目前完成 Solution／專案相依與測試骨架；登入、設備／Ping、服務觀測、公開頁、通知、Agent 採集與自動更新功能尚待需求實作。舊 Pingvalue／NSpeedTest 原始碼及截圖已由本次重寫移除，歷史版本仍在 Git 紀錄中。

## 開發環境

安裝 global.json 指定的 .NET SDK 10.0.403（或同 feature band 的更新 patch）。開啟根目錄 `Vigiloom.slnx`；使用支援 .NET 10／slnx 的 IDE，或使用 CLI。

```powershell
dotnet restore Vigiloom.slnx
dotnet build Vigiloom.slnx --configuration Release --no-restore
dotnet test Vigiloom.slnx --configuration Release --no-build
```

全部測試專案使用 MSTest 4.0.2／VSTest。尚無業務實作的對應測試專案保留為空，測試執行顯示無可用測試；不放空方法或假成功案例。現有有效測試檢查架構、Web prerender／HTTP、SQL provider 註冊與背景 Host 生命週期。

## 啟動

```powershell
dotnet dev-certs https --trust
dotnet run --project src/Hosts/Vigiloom.Web
```

預設開發網址：[https://localhost:7043](https://localhost:7043)，純程序存活檢查 `/health/live`；Development 提供 `/openapi/v1.json`。這些不代表 SQL、採集或業務服務健康。正式環境不公開 OpenAPI。

三個背景 Host 可各自執行；目前只啟動 Generic Host，沒有採集／排程／更新工作，也不會安裝 Windows Service／systemd：

```powershell
dotnet run --project src/Hosts/Vigiloom.Worker
dotnet run --project src/Agent/Vigiloom.Agent
dotnet run --project src/Updater/Vigiloom.Updater
```

目前啟動不需要 SQL Server。後續模組建立自己的 DbContext、schema 與 migration，再以 `AddModuleSqlServer<TContext>` 註冊每次操作使用的 factory；連線與機密由部署配置或 user-secrets 管理，不提交到 repo。不在 Host 啟動時自動執行 migration。

Windows 本機開發與測試都用 LocalDB，共用同一個 instance（預設 MSSQLLocalDB），使用不同資料庫：開發資料保留，測試的唯一臨時資料庫每次結束清除。測試不得使用或清理開發／正式資料庫，也不能停止／刪除共用 instance。詳細規則見 [開發／測試資料庫隔離](docs/architecture/development-and-test-databases.md)，fixture 與清理工具留待資料存取需求實作。

## 專案分工

| 位置 | 責任 |
|---|---|
| src/Hosts | Web（SSR／Server＋API）、Web.Client（Auto／WebAssembly 元件）、Worker |
| src/Modules | IdentityAccess、Resources、NetworkMonitoring、AgentManagement、ServiceObservability、SecurityAnalysis、Publishing、AlertingOperations |
| src/Shared | BuildingBlocks 與瀏覽器／端點可共用的 Contracts |
| src/Infrastructure | 共用 EF Core／SQL Server 技術支援 |
| src/Agent | Agent Host、Core、Windows／Linux 適配邊界 |
| src/Updater | 獨立 Updater Host 與 Core |
| tests | 每個後端專案的 MSTest 專案，加上架構檢查；Web.Client 由 Web 測試參考鏈涵蓋建置 |

共 20 個產品專案、20 個 MSTest 專案。模組一個專案起步，不預先拆四層。跨模組只能依明確公開契約協作，Client 不引用 EF／中央模組。

## Blazor 模式

使用 Auto、按頁面啟用互動。首次可由 Server 互動，WebAssembly 快取後的後續造訪可在瀏覽器互動；不會在下載完成時切換已開啟的 Server 元件。公開／簡單頁面可保留 SSR，互動元件放在 Web.Client。

瀏覽器互動透過授權 API 取得 DTO，資料庫／機密留在後端。30～500 個 Agent 的接收負載，與人員瀏覽器的 Server circuit 數量分開規劃。Auto 不代表零伺服器負擔。

## 開發規範與需求

- 先閱讀 [AGENTS.md](AGENTS.md)。
- [ADR-0001：識別碼／主鍵／SQL 索引政策](docs/architecture/ADR-0001-identifiers-and-keys.md) 已採用，新增模型或訊息前必讀。
- [ADR-0002：專案與程序邊界、Blazor Auto](docs/architecture/ADR-0002-modular-platform-and-hosts.md) 列出相依方向、模組責任與驗證限制。
- [Vigiloom Project](https://github.com/users/ChengYen-Tang/projects/5) 為完整需求與 Task 來源；UI 依已選風格逐需求透過 Product Design 設計，再以 Blazor 實作。
- 不強制將應用服務綁主機；主選單需含網路測速；LINE 在登入後由 Administrator 設定；AI Agent 分析維持擱置。
