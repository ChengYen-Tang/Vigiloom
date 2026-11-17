# Vigiloom.Updater.Tests

MSTest 涵蓋 Updater 服務生命週期、復原狀態與安裝包檢查。

Linux 的服務整合測試需在具有 systemd 的隔離測試環境，以 root 執行，並提供 Linux x64 self-contained 的 Agent 與 Updater publish 目錄：

```bash
sudo bash tests/Vigiloom.Updater.Tests/linux-repair-integration.sh <agent-publish-dir> <updater-publish-dir>
```

測試會拒絕已有 Vigiloom 安裝、帳號或服務的環境；結束時會移除本次建立的服務、帳號和資料。它使用離線的測試身分與信任根，不代表正式註冊或套件簽章驗收。
