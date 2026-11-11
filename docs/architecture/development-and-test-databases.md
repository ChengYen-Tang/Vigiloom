# 開發與測試資料庫隔離

狀態：已採用的開發／測試規範（2026-11-01）。使用者確認開發與測試都用 LocalDB，共用同一個 instance，以不同資料庫隔離；測試結束須清理乾淨。此規範不修改 ADR-0001 的 key／schema 政策，也不替正式環境選定 SQL Server 版本／edition。

## 環境分工

| 用途 | Windows 本機建議預設 | 資料生命週期 | 自動清理 |
|---|---|---|---|
| 開發／手動操作 UI | (localdb)\\MSSQLLocalDB；資料庫：Vigiloom_Development | 持續保留，跨次啟動使用 | 測試程序不得刪除或重設 |
| 資料庫整合測試 | 同一個 (localdb)\\MSSQLLocalDB；獨立測試資料庫：Vigiloom_Test_<RunId>_<CaseId> | 預設每個測試建立；符合下述條件者可在同一 run 重用，結束刪除 | 僅限本次測試明確建立並登記的資料庫 |
| 正式環境 | 專用 SQL Server，由部署配置決定 | 依正式保留／備份政策 | 測試程序不得連入或清理 |

本機共用預設 instance (localdb)\\MSSQLLocalDB，不另建 VigiloomDev／VigiloomTests instance。開發與測試有各自的連線設定，Data Source 相同、Initial Catalog 不同；測試 fixture 明確指定臨時資料庫，不能只靠 ASPNETCORE_ENVIRONMENT 字串判斷可否刪除。Web／Worker Development 設定提供開發連線範例；fixture 建立並清理臨時資料庫。

LocalDB 屬 SQL Server Express 的本機開發引擎，適合 Windows 本機的 SQL Server 行為測試。Linux／未安裝 LocalDB 的 CI 可改用獨立 SQL Server 測試服務或容器，維持同樣的隔離與清理規則，不改用 EF InMemory／SQLite 取代 SQL schema 驗證。可選的 SQL 版本／相容層級必須明列；LocalDB 測試不能代替正式 edition／拓樸的效能與部署驗證。

## 測試分類與連線

- 純單元測試不連資料庫，使用必要的應用介面替身。
- 連 LocalDB／SQL Server 的測試標記 MSTest TestCategory("Integration") 及 TestCategory("Database")，與純單元測試區分；不要為了分類把現有無資料庫的 provider 註冊測試假標成 Database。
- 測試連線由測試 fixture 的明確設定取得，不自動讀取或 fallback 到產品的 Development／Production 連線。開發連線由 Development 專用設定／user-secrets 管理；正式機密不提交。
- 預設每個測試（包含每個 DataRow）建立唯一資料庫，例如 Vigiloom_Test_<RunId>_<CaseId>；RunId／CaseId 由 fixture 產生，不從使用者輸入拼接。
- 不同 run／testhost 不得共用可變資料庫或固定名稱。同一 run 內經明確挑選、只改資料且不改 schema 的測試，可獨占租用已初始化資料庫；每次使用後必須還原完整初始狀態，重置失敗不得再借出。migration、schema 與有不同資料庫設定的測試仍使用獨立資料庫。共用資料庫仍須有單一清理擁有者與可復原的 manifest。
- migration／schema 測試執行正式模組的 migrations，不能只以 EnsureCreated() 宣稱 migration 已驗證。此文件未新增任何產品模型或 migration。
- 本機缺 LocalDB 時要明確指出前置未滿足；CI 中宣告必跑的 Database 測試不可被靜默略過而標記已驗證。

## 清理契約

1. fixture 建立資料庫前先登記共用 LocalDB instance、測試專用資料庫名稱、run／case 與擁有權；初始化中途失敗也能找到需清理的資源。
2. 每次測試結束透過 MSTest 非同步 TestCleanup／IAsyncDisposable／finally 進行清理，涵蓋成功、斷言失敗、初始化部分完成及一般例外；不能只把清理寫在測試方法最後。
3. 先結束背景工作、交易、reader，釋放 DbContext 與 SqlConnection。臨時 DB 測試連線預設關閉 pooling；若情境必須開啟，只清理該測試連線的 pool，不使用 ClearAllPools 干擾並行測試。
4. 使用獨立且有界的清理取消／超時設定，不能沿用已取消的測試 CancellationToken 而直接跳過清理。
5. 從該 LocalDB instance 的 master 管理連線，只刪除本次登記的臨時資料庫，再驗證資料庫不存在；master 本身是禁止刪除的系統資料庫。清理失敗必須回報為失敗並保留原測試及清理錯誤，不可吞掉例外或只印 log。
6. 正常 DROP DATABASE 後檢查本次建立的資料檔與暫存目錄；若有殘留，只能根據 manifest 的確切擁有權與已驗證路徑處理，不以全機搜尋／萬用字元清除 .mdf／.ldf。
7. assembly／runner 結束時再檢查本 run 的登記清單，確認臨時資料庫、資料檔與 fixture 暫存資源清除；測試報告可保留在 artifacts，不能把測試資料庫當報告保留。

刪除前必須同時檢查允許的 LocalDB Data Source、測試用途、由 fixture 產生的精確資料庫名稱與該 run 擁有權。不只看名稱前綴；拒絕 master／model／msdb／tempdb、Vigiloom_Development、其他開發與正式目標。instance 由開發與測試共用，清理不得停止、刪除或重建 instance，也不能影響同 instance 的其他資料庫。

## testhost 崩潰／中止時

程序被強制終止或斷電時，程序內 Cleanup 無法保證執行。需由外層 runner／後續明確清理工具依 manifest 補清，確認擁有者 run 已結束且無其他 testhost 使用；只處理自身登記資源。不能在下次啟動時掃前綴就直接刪除所有舊資料庫。

LocalDB fixture 與手動補清入口見 [SQL Server 測試輔助工具](../../tests/Vigiloom.Testing.SqlServer/README.md)。正常／失敗／並行／中止復原由 REQ-02-T03 驗證，模組 migration／schema 由 REQ-02-T02 驗證；Windows LocalDB 與 Linux SQL Server 2022 均已執行資料庫測試。

## 需要驗收的情境

- 正常測試與斷言失敗後均無臨時 DB／資料檔殘留。
- 建立 DB 後的初始化失敗、取消與清理失敗可觀察且可補救。
- 多 run／多 testhost／DataRow 並行不污染彼此；清理不關閉其他測試的 pool。
- 誤傳開發／正式／系統資料庫連線時，在建立／寫入／刪除前拒絕。
- 模擬 runner 中止後，明確擁有權的殘留可補清，仍存活的 run 與開發資料不受影響。
- LocalDB 引擎版本與相容層級有記錄；真實 SQL metadata 按 ADR-0001 驗證。

來源：[SQL Server Express LocalDB](https://learn.microsoft.com/en-us/sql/database-engine/configure-windows/sql-server-express-localdb?view=sql-server-ver17)、[MSTest lifecycle](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-mstest-writing-tests-lifecycle)。沿用目前 MSTest 4.0.2 可用的生命週期，不假定後續版本的 fixture 功能已可使用。
