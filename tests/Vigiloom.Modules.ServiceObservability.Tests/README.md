# Vigiloom.Modules.ServiceObservability.Tests

MSTest 測試專案。測試目標：`Vigiloom.Modules.ServiceObservability`。

測試涵蓋候選時間窗與 Private 預設、真實 SQL migration／唯一鍵、來源憑證、容量與核准交易，以及 RCSI 開關下來源撤銷與接收授權鎖的交錯。需要 Windows LocalDB 或設定隔離 SQL Server；fixture 清理規範見 [SQL Server 測試說明](../Vigiloom.Testing.SqlServer/README.md)。

Web 專案另有來源 HTTP／權限、管理頁及瀏覽器測試。這些測試不代表真實 OTLP 接收、Collector 信任映射或其他需求的跨功能驗收已完成。
