# Infrastructure

提供共用 EF Core 10／SQL Server 技術基礎，不擁有所有模組資料。AddModuleSqlServer<TContext> 使用 IDbContextFactory<TContext>，供每次操作建立短生命週期 context。

本輪未建立產品 entity、DbContext、schema、migration、連線字串或預設管理帳號；Host 不會啟動時自動遷移。模組各自擁有 context／schema／migration，SQL 版本／edition 與真實 SQL schema 驗證由後續需求處理。只驗證 provider 註冊不代表驗證資料庫 schema 或吞吐。
