# ServiceObservability

獨立邏輯服務／instance、三分鐘候選、核准後的 Logs／Metrics／Traces、Health Checks 與歷史健康。服務不強制綁主機。

此專案是一個模組邊界，目前不含業務實作。後續按實際需求在同一專案建立 Domain／Application／Infrastructure 等資料夾，不預先拆四層空專案。

- 相依方向：BuildingBlocks／Contracts／Infrastructure；不直接參考其他模組實作或 Host。
- 跨模組使用明確公開契約；不可直接更新其他模組資料表。
- 本模組擁有自己的 DbContext、schema 與 migration；不能和其他模組共同遷移同一張表。這些模型尚未建立。
- 新增資料或訊息前先讀 [ADR-0001](../../../docs/architecture/ADR-0001-identifiers-and-keys.md)。
- UI／API 只暴露明確 DTO，不直接暴露 EF entity。
- 對應測試：`tests/Vigiloom.Modules.ServiceObservability.Tests`，統一 MSTest。
