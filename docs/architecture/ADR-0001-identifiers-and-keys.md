# ADR-0001：識別碼、主鍵與 SQL Server 索引規範

- 狀態：Accepted（2026-11-01）。
- 決策依據：使用者指示先訂定 UUID v7、資料 key 與索引規則，供後續開發 Agent 遵循。
- 適用：Vigiloom 重寫的 .NET 10／EF Core 10／SQL Server 新模型、API、主機 Agent 契約及遙測接收。
- 本次僅訂規格；尚未建立新模型、migration 或執行效能測試。舊 Pingvalue schema 不在本次直接修改。
- 看板：DEC-02；落實於 TSK-ARC-02、TSK-BASE-02、TSK-ARC-03 及相關接收／查詢工作。

## 1. 固定選擇

| 資料類別 | 邏輯／主要 key | SQL Server 實體索引 | 外部識別 |
|---|---|---|---|
| 可獨立管理的資源實體：設備、應用服務、ServiceInstance、群組、帳號／角色、Agent 註冊、公開範本、告警／案件等 | `Id: Guid`，UUID v7；nonclustered primary key | 另外配置 `RowId: long` → `bigint IDENTITY(1,1)`；unique clustered index | API、跨模組契約與資源關聯使用 `Id` |
| 高流量紀錄：Ping 樣本、metrics 點、Log、span 儲存列、健康狀態區間、稽核／投遞紀錄等 | `RowId: long` → `bigint IDENTITY(1,1)`，clustered primary key | 依時間窗／所屬資源建立有界 nonclustered 索引；需要的自然唯一鍵另建 unique constraint | 所屬資源用 UUID v7；不因每列存在就額外建立 UUID |
| 單純多對多關聯：群組成員等 | 複合 key，例如 `(GroupId, DeviceId)` 或 `(GroupId, ServiceId)` | 複合 primary key 明確 clustered；依反向查詢建立必要索引 | 不配置沒有業務用途的第三個 ID |
| 輸入協定識別：OTel TraceId／SpanId、上游 service.instance.id、原生事件位置 | 保留原協定型別與值；與 Vigiloom 內部 key 分開 | 原始欄位與來源範圍按接收契約建立索引 | 不改寫成 UUID v7 |

這是預設且需明確實作的分類，不是讓 Agent 自行在 Guid／int／string 或各種 generator 間挑選。每張表必須在 ERD 指定所屬類別；同一資源的 ID 在資料庫、EF、API 與 Agent 契約保持一致。
資源間 FK 使用 `Guid`／SQL `uniqueidentifier` 指向邏輯 `Id`；`RowId` 只作本表儲存與內部查詢用途，不作跨模組關聯、端點身分或 API 資源路由 ID。
歷史／遙測以資源 ID、時間窗及有界分頁查詢；如需求要永久公開識別單一紀錄，需先把該識別需求寫入表分類與契約，不能直接洩出或跨模組依賴 RowId。
純關聯若之後成為可獨立管理的業務資源，須先更新分類；不能自行偷偷增加第二種外部 ID。

框架保留子表（如 Identity claims／logins／tokens、EF migration metadata）的內建 int／string／複合 key 不強行改造；ERD 明確標記 FrameworkOwned 並保留官方相容契約。Vigiloom 的使用者／角色資源本身仍採 Guid key；此例外不允許把一般業務表任意標成框架表。

## 2. UUID v7 的產生與保存

1. 新建 Vigiloom 資源由應用層的統一 ID generator 呼叫 `Guid.CreateVersion7()`；在加入 EF change tracker 以前賦值。此 generator 是唯一產生政策入口。
2. EF 對資源 `Id` 明確配置 `ValueGeneratedNever()`，不得依賴 SQL Server provider 的預設 sequential GUID generator；它與 UUID v7 並不相同。
3. 不使用 `Guid.NewGuid()`、SQL `NEWID()`／`NEWSEQUENTIALID()` 或未經本 ADR 指定的 COMB／ULID／自訂位元重排替代資源 ID。
4. C# 使用 `Guid`；SQL 使用 16-byte `uniqueidentifier`，不以 `nvarchar(36)` 保存資源 UUID。JSON／URL 輸出標準小寫 D 格式；輸入可接受有效 D 格式後正規化，不把大小寫當不同 ID。
5. 新建資源不得使用 `Guid.Empty`。資料庫 PK／unique 約束必須存在；不能只依應用層假定沒有碰撞。
6. ID 一旦分配且持久化即不變；更新、重送、重啟、修改名稱、IP 或群組時不得重新產生 ID。重建不同資源不重用已刪除資源的 ID。
7. 控制面資源原則上由中央建立；Agent 產生自己的訊息／批次識別，不因此取得任意建立或指定 Device／Service 身分的權限。AgentId 與 DeviceId 分開，綁定依註冊流程管理；API 不接受任意客戶端 ID 越權綁資源。
8. 內建帳號／角色採 Guid key 的 Identity 模型；建立流程顯式使用統一 generator。Read only／Administrator 的角色 ID 只建立並持久化一次，重啟不重建；如用固定 seed，固定值亦須符合本政策。
9. 第三方／舊資料的原始 ID 留在明確的 external 欄位與來源範圍，不以本政策改寫；任何遷移須有另行指定的映射，不覆寫真實資料。

## 3. SQL Server 與 EF 的索引規格

資源表同時有邏輯 Id 與本機 RowId，是刻意分開識別需求與實體儲存。標準 UUID v7 在 SQL Server 的 uniqueidentifier 比較方式下，不可假定依生成時間遞增，亦不可宣稱它等同 SQL sequential GUID。

EF 配置示意（文件範例，非本次新增的產品程式）：

```csharp
builder.HasKey(x => x.Id).IsClustered(false);
builder.Property(x => x.Id).ValueGeneratedNever();
builder.Property(x => x.Id).HasColumnType("uniqueidentifier");
builder.Property(x => x.RowId).UseIdentityColumn();
builder.HasIndex(x => x.RowId).IsUnique().IsClustered();
```

- 高流量紀錄表以 bigint RowId 作 clustered PK，不再套用上述雙 key 的資源表配置；其 `DeviceId`／`ServiceId` 等 FK 仍為對應資源 Guid。
- 對單一資源的歷史查詢，rowstore 基準索引順序為 `(DeviceId 或 ServiceId, OccurredAt, RowId)`；欄位包含與其他查詢索引依需求列明。不要在同一列用設備／服務的必填一對一關係混淆兩種維度。
- 彙總資料另定自然唯一鍵，例如 `(ServiceId, Resolution, BucketStartUtc, 其他必要維度)`，不可只看時間就把不同維度合併。RowId 不代替去重鍵。
- PK、clustered、unique、FK 與 generator 都必須在 EF model／migration 顯式指定；SQL 預設 PK clustered 不是本政策的替代。
- 非聚簇 UUID 索引仍有成本，bigint 也不是無競爭或無限吞吐保證。先以此 rowstore 基準量測；partition、columnstore、fill factor 或其他實體設計需以實際負載提出 ADR 修訂，不能由 Agent 靜默套用。
- 暫不選定 SQL Server edition／版本或保留期限；這些沿原 Task 決定，但不能讓主鍵選擇因此回到自由發揮。

## 4. 時間、分頁與並行控制

- 資源保存 `CreatedAt`／`UpdatedAt`；遙測保存來源事件時間 `OccurredAt` 與中央接收時間 `ReceivedAt`。缺來源時間時要標示由接收時間代替，不偽造精確發生時間。
- 新系統時間契約統一 C# `DateTimeOffset`、SQL `datetimeoffset(7)`，持久化 offset 為 +00:00；UI 才轉顯示時區。
- 歷史查詢依明確時間與穩定的次序鍵（例如 OccurredAt＋RowId）排序／分頁；API 分頁游標當作不透明值，不成為跨模組永久身分。
- 不以 UUID 時間或 bigint 的增量推斷業務發生先後。時鐘偏移、同毫秒隨機值、斷線補送與交易提交順序都可能不同；不以 SQL ORDER BY Id 取得時間順序。
- 可編輯設定的並行檢查使用明確的 version token（如 SQL rowversion），不能把 RowId 或 UUID v7 的時間當 rowversion。

## 5. Agent／平台自有訊息的識別與去重

- 平台自有持久訊息使用 `MessageId: Guid`（UUID v7），批次使用 `BatchId: Guid`（UUID v7）。產生一次後與離線佇列一起保存，重試不得換 ID。
- 認證得到的 `ProducerId` 與 `BatchId` 構成接收批次唯一鍵；批次內使用固定 `ItemOrdinal`。ProducerId 不由 payload 任意宣告，批次順序與內容在重送時保持不變。
- 同一批次在同一 SQL 交易中持久化接收紀錄與其資料，提交後才成功確認；唯一約束控制並行重送。同 key 同內容回覆既有成功結果，不再插入；同 key 不同內容視為契約衝突，不覆寫原資料。自有批次的原始 payload 序列化位元組與佇列一起持久化，重試保持不變；接收端以去除傳輸壓縮後的 payload bytes 及 SHA-256 辨別內容，認證／接收時間等接收端欄位不混入該雜湊。
- 自有批次採有界且完整驗證後的整批接收／拒絕，不在未定義部分成功契約時模糊 ack。上限與接收／重送期限由 TSK-ARC-03 確認。
- 去重紀錄回收不得早於已確認的可接受重送窗口及寬限；窗口與過期拒絕規則未定前，不啟用自動回收。不宣稱超出紀錄保存範圍仍具永久 exactly-once 保證。
- 可作資料表的接收紀錄以 bigint RowId clustered PK，加 unique `(ProducerId, BatchId)`；MessageId／BatchId 是協定識別，不強制成為所有樣本的主鍵。

## 6. OpenTelemetry／第三方協定例外

- OTel TraceId 保留 16-byte，SpanId 保留 8-byte；SQL 分別使用 `binary(16)`／`binary(8)` 的原始協定位元組，API 按標準 32／16 位十六進位輸出，不經 Guid byte-order 轉換。
- 外部 `service.instance.id` 保留來源字串，對應到獨立的 Vigiloom ServiceInstance.Id；同名服務、主機 IP 或 Collector 所在主機不能充當內部資源 ID。
- 標準 OTLP 不被強迫加上 Vigiloom BatchId／MessageId，內部 ID 也不覆寫 trace／span ID。每個 receiver 明列它真正具備的原始識別與去重能力。
- TraceId 本身不是單筆 span 的唯一鍵；相同時間／訊息文字／指標值也不能當通用 Log／Metric 去重鍵。無可靠原始識別的 OTLP Logs 預設承認重送可能產生重複，不用內容雜湊直接刪掉可能合法的相同事件。
- 不把自有 Agent 批次的去重承諾套到標準 OTLP 全路徑；第三方來源仍受服務核准、來源驗證與公開資料規則約束。

## 7. 後續實作必須驗證

- 每張新表有上述分類、PK／FK 型別、generator、clustered 與 unique 設定。
- 真正 SQL Server 的 migration／metadata 證明 Id 為 nonclustered PK、RowId 為唯一聚簇索引，不能只用 EF InMemory 或看 C# 屬性。
- Guid.Empty、資源 ID 不變、角色／Agent 重啟、外部 trace bytes round-trip、同批次重試／並行／衝突與 ack 遺失有對應驗證。
- UUID v7 與 SQL Server 排序差異有可重現的驗證；查詢以時間欄位而非 Guid 排序。
- 負載測試量測 UUID secondary index 成本、索引大小、查詢與寫入延遲、順序鍵競爭及去重紀錄成長，未測前不承諾效能數字。
- 涉及這些 key 的 Task 開始時讀取本 ADR。要改政策，先提出具體理由、驗證與 ADR 修訂，不能在實作中自行換 generator 或鍵型別。

## 官方依據

- [.NET Guid.CreateVersion7](https://learn.microsoft.com/en-us/dotnet/api/system.guid.createversion7?view=net-10.0)：時間來源與隨機欄位。
- [EF Core SQL Server value generation](https://learn.microsoft.com/en-us/ef/core/providers/sql-server/value-generation)：provider 的 GUID generator／Identity。
- [SQL Server GUID 比較方式](https://learn.microsoft.com/en-us/dotnet/framework/data/adonet/sql/comparing-guid-and-uniqueidentifier-values)：uniqueidentifier 與一般 Guid 比較次序不同。
- [EF Core SQL Server indexes](https://learn.microsoft.com/en-us/ef/core/providers/sql-server/indexes)：PK／索引 clustered 配置。
- [EF Core generated properties](https://learn.microsoft.com/en-us/ef/core/modeling/generated-properties)：ValueGeneratedNever。
- [SQL Server 索引設計](https://learn.microsoft.com/en-us/sql/relational-databases/sql-server-index-design-guide?view=sql-server-ver17)：實體索引的取捨與量測。
- [OpenTelemetry SpanContext](https://opentelemetry.io/docs/specs/otel/trace/api/#spancontext)：TraceId／SpanId 格式。
- [OTLP duplicate data](https://opentelemetry.io/docs/specs/otlp/#duplicate-data)：重送可能產生重複。
