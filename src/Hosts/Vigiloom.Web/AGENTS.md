# Vigiloom.Web

- Blazor Web App：Auto，按頁面啟用，預設 prerender；不是全站 Interactive Server。
- Server 專案負責 SSR、API、靜態資產與 composition root；可在兩端執行的互動元件放在同層 Vigiloom.Web.Client。
- Routes／HeadOutlet 不設全站互動；頁面按需求使用 InteractiveAuto。純公開狀態／簡單表單可用 SSR。
- Client 不引用 DbContext、EF entity、OS 型別、密鑰或中央模組。瀏覽器透過 API 取得 DTO。
- 互動元件依賴的服務需在兩端可解析；Server adapter 仍呼叫相同應用服務與授權政策，不盲目向自己 HTTP 或無條件轉送 Cookie。
- API 後端授權不能由 AuthorizeView／隱藏按鈕取代。未來內建 Identity 採 Guid key，遵循根目錄 ADR-0001；登入／Account 頁使用 SSR，不任意加互動模式。
- prerender 可能重複初始化；測試首次 Server 與快取後 WebAssembly，不假設 HttpContext 或瀏覽器 JS 在每階段都可用。
- 正式產品 UI 依根目錄 AGENTS.md 的既定第二張風格，做到需求時透過 Product Design 設計與驗證。目前只有啟動標題，未實作產品 UI。
- 使用 MSTest；測試在 tests/Vigiloom.Web.Tests。WebApplicationFactory 測試不替代真實瀏覽器的兩種互動模式驗證。
