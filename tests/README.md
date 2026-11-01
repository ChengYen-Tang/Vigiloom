# 測試骨架

全部測試專案使用 MSTest 4.0.2／VSTest，繼承 tests/Directory.Build.props，版本由根目錄 Directory.Packages.props 管理。禁止另外加入 xUnit／NUnit／TUnit；架構測試會檢查。

每個後端專案有直接 ProjectReference 的測試專案；Web.Client 隨 Web 主機編譯，相關 HTTP／prerender 測試先在 Web.Tests。實際功能到位後補元件／瀏覽器測試，仍以 MSTest 為框架。

目前有效測試：
- Architecture：Solution 成員、專案相依／循環、Client 邊界與 MSTest 規範。
- Infrastructure：SQL Server DbContextFactory 註冊與輸入檢查；不連真實 SQL，不驗證 schema。
- Web：prerender、liveness、Development OpenAPI、Production 隱藏 OpenAPI、API 404 Problem Details。
- Worker／Agent／Updater：一般 Host 程序的啟動／停止；不安裝服務，不蒐集或更新。

其他專案保留測試位置；沒有產品邏輯時不塞空測試，執行會顯示無可用測試。這不表示功能或整體覆蓋率已驗證。

```powershell
dotnet test Vigiloom.slnx --configuration Release --no-build
```

所有 SQL schema／索引／約束的後續測試必須使用真實 SQL Server；不可把本輪 provider 註冊測試、EF InMemory 或 SQLite 視為 ADR-0001 驗證。OS 矩陣、Auto 首次／快取後瀏覽器互動、容量、更新故障演練也尚待後續需求。

## 開發／測試資料庫

遵循 [開發／測試資料庫隔離規範](../docs/architecture/development-and-test-databases.md)：Windows 本機開發與測試都用同一個 LocalDB instance（預設 MSSQLLocalDB），使用不同資料庫。開發資料持續保留；每個資料庫整合測試建立唯一臨時 DB，結束後完整刪除。測試不能 fallback 到開發／正式連線，清理只可處理本 run 明確建立的 DB，不能停止／刪除共用 instance 或影響其他 DB。

使用 MSTest 非同步 Cleanup／finally 釋放 context、連線與交易，再刪除 DB 並驗證不存在；失敗必須回報。取消時採獨立清理 token；testhost 強制中止的殘留需依擁有權 manifest 補清。標記 Integration／Database 的資料庫測試與純單元測試區分。

目前尚無 LocalDB fixture、清理工具或測試資料庫；具體實作與清理故障測試列於 TSK-BASE-02／TSK-QA-02。Linux／CI 可用獨立 SQL Server，遵守相同隔離規則。
