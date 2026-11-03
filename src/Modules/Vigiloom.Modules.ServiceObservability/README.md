# ServiceObservability

獨立邏輯服務／instance、三分鐘候選、核准後的 Logs／Metrics／Traces、Health Checks 與歷史健康。服務不強制綁主機。

本模組已包含應用來源憑證、三分鐘最小候選、管理員核准與交易內接收授權契約；正式 OTLP 接收、Health Checks、資料儲存及其跨需求驗收仍待對應需求完成。服務與設備維持獨立身分。

- 相依方向：BuildingBlocks／Contracts／Infrastructure；不直接參考其他模組實作或 Host。
- 跨模組使用明確公開契約；不可直接更新其他模組資料表。
- 本模組擁有 `service` schema、`ServiceObservabilityDbContext` 與 migration；不能和其他模組共同遷移同一張表。資料字典見 [資料擁有權與 ERD](../../../docs/architecture/data-ownership-and-erd.md)。
- 新增資料或訊息前先讀 [ADR-0001](../../../docs/architecture/ADR-0001-identifiers-and-keys.md)。
- UI／API 只暴露明確 DTO，不直接暴露 EF entity。
- 對應測試：`tests/Vigiloom.Modules.ServiceObservability.Tests`，統一 MSTest。
- 身分、容量、核准與後續接收端交易限制見 [服務發現與核准](../../../docs/architecture/service-discovery.md)。
