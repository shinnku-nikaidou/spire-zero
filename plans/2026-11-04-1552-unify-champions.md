# 冠軍檔不再分角色（2026-11-04 15:52 台灣時間）

**起因：** v9-1 的 gate 開工作時，16 個非 Silent 的工作全部失敗：冠軍檔只放在 `eval/silent/champions/`，而 `box.py`
只把「這個角色自己的」冠軍資料夾送到主機上（`box.py:311`）。網路從 v9-0 起已經五個角色共用一份，冠軍也該只有一份。

## 哪些還要分角色，哪些不用

| 東西 | 現在 | 之後 | 理由 |
|---|---|---|---|
| 網路權重 | `weights/all/` 共用 | 不變 | 已經統一 |
| 冠軍檔 | `eval/silent/champions/{sl,nosl}.json`，選項裡寫死 `character: SILENT`、`asc: 10` | `eval/champions/{sl,nosl}.json`，五個角色共用；只記網路和玩法，不記角色和晉階 | 冠軍是「哪兩個網路、怎麼玩」，跟角色無關；角色和晉階由每個工作自己給（`--character`、`--asc`），v9g0 和這次 gate 本來就這樣給 |
| 冠軍的報告 | 一份 Silent A10 的報告 | 冠軍檔裡按角色記：`reports: {SILENT: …, DEFECT: …}`，沒有的角色先空著 | 報告是一個角色在一個晉階的成績，不同角色不能互比 |
| 報告、排行榜 | `eval/CHAR/reports/`、`leaderboard.md` | 不變 | 成績本來就分角色 |
| 對局資料 | `data/CHAR/...` | 不變 | 一局只有一個角色，工作也按角色開 |
| 帳本 | 五份 `eval/CHAR/ledger.md`；種子區塊表抄五份，網路表只在 Silent 那份 | **待你決定**：合成一份 `eval/ledger.md`（一張網路表、一張種子區塊表、self-play 批次每列加角色欄），或維持五份 | 網路和種子區塊已經是全角色共用，五份大半是重複 |

## 步驟

1. **冠軍檔搬家並去掉角色：** `eval/champions/sl.json`、`nosl.json`，`options` 拿掉 `character`、`asc`，`report` 換成按角色的 `reports`（現在只有 SILENT 一份）。刪 `eval/silent/champions/`。
2. **模擬器（C#）：**
   - `ChampionFile` 改成 `Reports`（按角色）；`Layout` 加冠軍的路徑。
   - `gate` 用 `--character` 那一份報告配對，`--promote` 改寫共用冠軍檔、只更新那個角色的報告，選項不再寫入角色和晉階（`ChampionOptions.Promoted`）。
   - `leaderboard` 讀共用冠軍檔，標出這個角色的報告。
   - 測試跟著改。
3. **`box.py`：** 送 `eval/champions` 取代 `eval/{char}/champions`。
4. **`--champion` 的使用者：** `sl-selfplay` 和 `fleet` 的 skill、mod 的 `brain` 啟動方式、`dist-admin.py`（如有）、記憶檔 `per-character-brains`。
5. **（若你選合併）帳本合成一份。**
6. **文件（docs/）：** 冠軍與基線、閘門、名詞表、環境、指標、mod 等頁提到 `eval/CHARACTER/champions/` 的地方。依規矩，要先問你由誰寫。
7. **驗證：**
   - C# 和 learn 的測試全過。
   - 本機各角色一局：`--champion eval/champions/sl.json --character X --asc 0` 和把選項逐項寫出來的跑法，紀錄要完全相同。
   - 確認 `box.py` 打包的內容裡有冠軍檔：一個非 Silent 角色的小 box，一兩局就好。

**不影響正在跑的 gate：** 它跑的是 5a85b58，選項已逐項寫出。gate 若通過，晉級時直接寫新的共用冠軍檔。

**估計：** 約 1–1.5 小時（不含文件）。
