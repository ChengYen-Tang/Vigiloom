# AgentManagement

端點註冊、心跳、能力、版本／設定指派、正式套件庫與部署更新狀態。

此專案是一個模組邊界，目前不含業務實作。後續按實際需求在同一專案建立 Domain／Application／Infrastructure 等資料夾，不預先拆四層空專案。

- 相依方向：BuildingBlocks／Contracts／Infrastructure；不直接參考其他模組實作或 Host。
- 跨模組使用明確公開契約；不可直接更新其他模組資料表。
- 本模組擁有自己的 DbContext、schema 與 migration；不能和其他模組共同遷移同一張表。這些模型尚未建立。
- 新增資料或訊息前先讀 [ADR-0001](../../../docs/architecture/ADR-0001-identifiers-and-keys.md)。
- UI／API 只暴露明確 DTO，不直接暴露 EF entity。
- 對應測試：`tests/Vigiloom.Modules.AgentManagement.Tests`，統一 MSTest。
