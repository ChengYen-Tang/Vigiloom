# Ping 執行與狀態契約

REQ-09-T01，遵循 ADR-0001／0002 與監測執行規格。使用 .NET 10 內建 `System.Net.NetworkInformation.Ping.SendPingAsync`，不加入過時 System.Net.Ping NuGet 套件；[官方 API](https://learn.microsoft.com/en-us/dotnet/api/system.net.networkinformation.ping.sendpingasync?view=net-10.0) 支援逾時與取消。Linux 使用空 payload，權限／工具不可用作監測器錯誤，不當設備不可達。

- 每個設備的主要及額外目標分別量測，保留設定目標、解析後位址、位置、實際時間、結果及成功延遲。DNS 每次解析一次，依地址族及位址位元組穩定選擇第一個地址；只代表該次選中的 IP。
- 每輪每目標發送一次，不以重試增加取樣；預設 30 秒、2 秒逾時、3 敗／2 成功，管理員可由設定頁修改。監測器／DNS 錯誤不增加不可達計數，且切斷連續成功／失敗序列。首次資料及未達成功門檻維持未知。過期缺口會切斷先前的連續計數並將判定基線重設未知，再處理新結果，不能靠過期的成功證據恢復可達。
- NetworkMonitoring 擁有 Ping settings、目標狀態／租約、樣本及狀態事件。設定為 UUID v7 資源；目標狀態是 `(DeviceId, Target)` 複合 key，樣本／事件為 bigint clustered record。資源仍由 Resources 擁有。
- Resources 透過伺服器端契約提供啟用設備快照；提交時在共享 SQL 交易內取得設備狀態／目標版本鎖。NetworkMonitoring 不直接更新或查詢 Resources 資料表。伺服器端交易協作契約使用標準 DbConnection／DbTransaction，不作瀏覽器資料或 HTTP DTO。
- SQL 原子租約、fencing 代次及預期設備版本保護排程與提交。停用、封存、改址或過期租約的舊執行不得提交；DB 不可用不假稱持久成功。Worker 停止時取消量測，保留可回收租約，不補造漏掉的歷史。
- 每個 Worker 有界併發，無每設備常駐 Task。下一輪依實際完成時間及間隔安排，租約過期可重新取得。過期判定為大於 `max(90秒, 2×間隔)`，不以舊成功值顯示目前正常。
- Ping 狀態與 Agent／應用服務獨立；多目標分別呈現，不把任一成功等同整個設備所有路徑成功。歷史圖表與彙總由 REQ-10 擁有；本需求提供最新結果與狀態變更紀錄，供 REQ-15 使用。
