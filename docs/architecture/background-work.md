# 背景工作與交易性 Outbox

REQ-06-T01 的實作契約。Hangfire 使用 LGPL-3.0-or-later 開源授權；分發產物須攜帶第三方授權、保留可替換元件及其相應授權義務，不依賴商業套件或靜默變更授權模式。

## 啟動與排程

- Migrator 在驗證既有非系統資料庫後，明確安裝／升級 `hangfire` framework schema 及平台自有 `scheduling` schema。Web／Worker 不建立資料庫，也不在正常請求中自動升級 schema；缺設定或 schema 不完整應明確失敗。
- Web 註冊 Hangfire client／monitoring API 與同站 Dashboard，不啟動 Hangfire Server。Worker 才執行 Server，並以穩定 ID 註冊分鐘級 Outbox 派送工作。多個 Worker 共同消費，Web 回收不影響執行。
- 循環工作使用 UTC；同 ID 重啟後更新註冊而非建立重複排程。遺失的監測樣本不補造；通知與清理使用持久待辦繼續處理。工作可以取消，停止時不將未完成工作標為成功。
- 訊息處理間隔以 `scheduling.ScheduleSettings` 的 UUID v7 資源保存，SQL rowversion 防止舊頁面覆蓋其他管理員的修改；Hangfire 排程是應用後的執行狀態。跨兩種儲存的更新不宣稱原子：儲存後如套用失敗，UI 顯示待套用，Worker 每 30 秒重新協調並保留最後設定。內建派送排程被移除時亦由協調程序重建。

## Outbox 交易與派送

- 業務模組在自己的 SQL 交易內，透過同一個 `SqlConnection`／`SqlTransaction` 寫入 `scheduling.Outbox`，與業務資料一起 commit 或 rollback。呼叫端產生一次 UUID v7 MessageId 並維持不變；MessageId 唯一約束拒絕重複，RowId 是內部 clustered identity，不作公開身分。
- 只接受已註冊的穩定訊息類型，payload 為有界 JSON；禁止序列化憑證、任意 .NET 型別或 EF 實體。新增處理器需由擁有模組明確註冊，不以任意反射載入類別。
- 派送以有限批次取得到期工作，利用 SQL 原子更新、到期租約與 fencing token 協調 Worker。取得、處理、完成分階段持久化；被佔用工作不阻塞整個批次。過期執行者不可確認或覆寫較新租約；重啟後租約到期可續處理。
- 同資料庫的接收處理器須在派送交易中執行，業務變更、去重紀錄及完成標記共同提交。崩潰前 rollback，崩潰後重試同一 MessageId，不能以記憶體佇列或單次 Hangfire 執行保證耐久。外部 HTTP、訊息系統或通知無法與本地交易原子提交，必須使用接收端冪等鍵或明示至少一次與可能重複，不能宣稱 exactly-once。
- 預設重試最多 10 次，首次至少等待 10 秒、每次加倍，延遲上限 1 小時；到期後由下一次背景檢查派送，並非保證精確在該秒重試。未知類型或重試耗盡進入失敗狀態供管理員診斷，保留 MessageId、次數、時間及安全錯誤代碼，不把含機密的 exception 或 payload 顯示給一般讀取者。不同工作若有業務重試規則可另外設定，不以全域重試覆蓋。
- 訊息處理預設以 90 秒取消、租約 120 秒；處理器及所有 I/O 必須合作遵守 cancellation token。SQL 交易仍使用資料列鎖與完成前期限檢查，不能強制中斷忽略取消的任意程式，也不把這種程式宣稱為可安全逾時的處理器。

## 權限與操作紀錄

- Administrator 在 Vigiloom 管理頁查看近期背景工作與受保護的失敗診斷，並可前往同站 `/admin/jobs`。Read only 與匿名均不得存取 Dashboard 或其 JSON／靜態子路徑。所有操作由伺服器檢查現行身分；原生 Dashboard 的寫入保留 Hangfire 與 ASP.NET Core 防偽驗證。
- Dashboard 觸發、重試、刪除等狀態變更需記錄操作者、UTC 時間、操作、受影響工作識別及結果；不記錄 Token、密碼、完整 payload 或 Cookie。若無法安全持久稽核則拒絕變更，避免只在記憶體紀錄就回報成功。
- 測試以獨立真實 SQL Server／LocalDB 驗證 schema、交易 rollback、並行競爭、失效租約、重啟恢復與冪等；以實際 HTTP 驗證匿名、Read only、Administrator、CSRF 與稽核。不得修改開發資料庫或停止共用 LocalDB instance。

## 開發與部署

部署者先建立目標資料庫，將 `VIGILOOM_MIGRATION_SQL_CONNECTION` 設為該資料庫的連線，再執行 `dotnet run --project src/Hosts/Vigiloom.Migrator`。Migrator 不建立資料庫，也拒絕系統資料庫；它套用模組與排程 migration、初始化內建角色並安裝 Hangfire schema，且會驗證安裝結果。Web 和 Worker 分別以 `ConnectionStrings:Vigiloom` 指向同一目標資料庫；通常環境變數名稱是 `ConnectionStrings__Vigiloom`。日常排程由管理員透過介面管理，不要求終端使用者操作 Hangfire 或修改設定檔。
