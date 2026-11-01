# Vigiloom

**你的設備、伺服器與應用服務，都在同一個地方掌握。**

[![CI](https://github.com/ChengYen-Tang/Vigiloom/actions/workflows/ci.yml/badge.svg?branch=master)](https://github.com/ChengYen-Tang/Vigiloom/actions/workflows/ci.yml)

Vigiloom 是一個開源的集中監控平台。從網路設備、伺服器到應用服務，你可以在同一個網頁介面查看運作狀態、追查異常、接收通知，並向使用者分享服務狀態。

> **開發中：** 以下介紹 Vigiloom 完整的產品規劃；目前尚未提供完整功能或正式版本。

![Vigiloom 監控總覽的設計示意圖](docs/design/Vigiloom-UI-Concept-2-Revision-2.png)

*介面設計示意，畫面中的資料與功能並非目前產品的實際狀態。*

## 核心功能

### 網路設備與連線監控

追蹤路由器、交換器、伺服器等設備是否可達，查看 Ping 延遲、狀態變化和歷史趨勢。只需要確認連線狀態的設備無須安裝 Agent；網路測速與歷史結果則協助你了解連線品質。

### 伺服器監控與資產盤點

透過 Windows／Linux Agent 掌握 CPU、記憶體、磁碟、程序和作業系統服務，並查看軟體與修補狀態、主機日誌及檔案完整性。從中央介面管理 Agent 設定、版本、更新與回復。

### 應用服務可觀測性

結合 OpenTelemetry、Collector 和 Health Checks，查看服務健康狀態、指標、日誌、追蹤與歷史變化。新服務先經管理員確認，再正式加入監控；應用服務擁有獨立身分，不必綁定特定主機。

### 安全監控與漏洞追蹤

從軟體與修補盤點辨識已知漏洞，查看安全設定偏離，並結合安全情報與規則偵測可疑行為。支援 Sigma 規則、IOC 與選配的 YARA 深度感測，協助你從事件與證據釐清問題。

### 異常事件與通知

集中查看異常事件與時間線，追蹤狀態變化及相關證據。管理員可透過介面設定通知方式，包括 LINE，讓需要處理的問題及時送到相關人員手上。

### 狀態頁與資訊分享

選擇要公開的設備和服務，透過狀態頁呈現簡易狀態與歷史；內部設備和進階資訊則保留給登入使用者。公開範本讓你選擇適合分享的資訊，公開設備預設不顯示 IP。

### 群組、權限與整合

依地點、環境或系統建立群組，將同一設備或服務加入多個群組，快速找到相關資源。內建 Administrator 與 Read only 角色，並提供 Web API，讓其他系統與工具整合。

## 建置專案

安裝 [global.json](global.json) 指定的 .NET SDK 後，在專案目錄執行：

```sh
dotnet build Vigiloom.slnx --configuration Release
```

要在本機啟動平台及執行測試，請參考[開發環境快速開始](docs/getting-started.md)。
