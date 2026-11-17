# NoSL 打不了 Dread：重建世界的架構缺陷（研究，台灣時間 2026-11-17 11:20）

起因（用戶，2026-11-17，在 NoSL 設計的 session）：「nosl打不了dread,sl卻打得了，本身就是架構上有個巨大缺陷，專門開個session研究一下」。

這是研究，沒有改任何程式或網路。跑過的只有本機的小量確認（第 6 節）。
每一點都標明是**量到的**還是**從程式碼讀出來的推論**。程式碼都是現在的 HEAD（11-17 的 Stone Calendar 修正之後），遊戲 v0.113.1。

## 0. 一頁看完

- **缺陷，一句話**：NoSL 的世界不是「真實的狀態，把看不見的部分重抽」，而是「一場新開的戰鬥，照觀察重新畫一遍」。
  觀察沒寫到的、或寫了但倒推不回去的狀態，全部丟失或被猜；觀察因此成了遊戲狀態的第二份定義，
  遊戲每多一樣東西，重建就得跟著補一次。Dread 只是最新的一樣。
- **失敗在哪（量到的）**：NoSL 的 Dread 局在第一場戰鬥的第一個決策就失敗，兩條路（Rust 引擎、C# 重播）都一樣，
  停在重建後的比對：`not built as observed: .mode: want "Dread", got none; .dominion: want "LAGAVULIN_MATRIARCH_DOMINION", got none`。
  引擎本身吃得下 Dread（SL 的 Dread 對照 0 差異）；擋住的是兩條路共用的重建。
- **就算比對放行也會打錯（推論）**：遊戲在戰鬥中讀模式 30 處（14 個怪物和遭遇，主要是 Boss 的數字和招式），
  領域在戰鬥中途也會出手（召喚的怪物），葉子的戰鬥網路也讀不到 `mode:`、`dominion:`。
- **不只 Dread（量到的）**：標準模式的 NoSL 局，把回合開始的觀察拿去重建，16.6 % 和真實的不一樣，1.8 % 不一樣在戰鬥網路直接讀的數字
  （例如第 50 層的 The Tower，傷害數字真的 150、建出來的 3），全部安靜地通過了比對。
- **建議**：(c) 一個世界給兩個環境：NoSL 從 SL 同一份真實狀態的匯出開始，在引擎裡把看不見的部分重抽，
  不再從觀察重建。Dread 和第 2 節表裡「看得到卻沒重建」的東西一次解決，SL 和 NoSL 是同一個結構。
  代價是「不偷看」的保證從資料的邊界移到一段程式（重抽的清單），要靠測試守住（4.4）。
- **對 NoSL 重訓計畫**：Dread 的 NoSL 局要等 (c) 的第 1、2 步（或 (a)）才能進資料集；
  搜尋葉子和訓練樣本讀到的東西不一致，在 (c) 之後消失，在 (a) 之後仍在（第 5 節）。

## 1. 實際的失敗

### 1.1 量到的

本機各跑一局 NoSL 的 Dread（IRONCLAD，A10，`--mode dread`，種子 1，`eval/champions/nosl.json`，4 個 worker）：

| 路徑 | 結果 |
|---|---|
| 引擎（`--combat-world engine`，預設） | 第 2 層 SEAPUNK_WEAK 的第一個戰鬥決策失敗，從 `Serve.Sample`（`PimcSampleRequest`）丟出 |
| 重播（`--combat-world replay`） | 同一個決策失敗，從 `Serve.Search`（`PimcWorldRequest`）丟出 |

兩邊的訊息都是 `not built as observed: .mode: want "Dread", got none; .dominion: want "LAGAVULIN_MATRIARCH_DOMINION", got none`，
丟在 `SimWorlds.NoSL`（`src/Sts2Ai.SimWorker/Simulation/SimWorlds.cs` 第 45 行）。每個 worker 收到請求就失敗、退出、被重開，
一個決策 8 次（`worker restarts: 8`），這一局記為 failed，0 個遊戲錯誤。

### 1.2 一個決策走過的路（讀程式碼）

**引擎路徑**（模擬器的 run、測試、mod 的 brain 都走這條）：

1. `RunSession.CombatDecision`（`RunSession.cs`）做出 `CombatDecision`，裡面有觀察，**也有真實戰鬥的匯出**
   （`() => LiveCombat.Export(...)`，不分環境都給）。
2. `Dispatcher.Fight` → `EnginePimc.Route` → `TurnPlan.Decide` → `EnginePimc.Decide`：只拿觀察和敵人意圖的紀錄，
   向 worker 池送 8 個 `PimcSampleRequest`（`EnginePimc.cs`）。匯出沒有用到。
3. worker：`Serve.Sample` → `SimWorlds.NoSL` → `CombatBuilder.Setup(observation, buildSeed, history)` → `CombatSession.Start`：
   - `RunState.CreateForTest([player], acts, null, GameMode.Standard, ...)`（`CombatSession.cs` 第 148 行）：模式固定是標準，
     所以 run 的 `RunModels` 沒有 `DreadRunModel`，`RunState.Dominion` 是空的。
   - `RunManager.EnterRoomDebug(...)` 開房間；遊戲的 `CombatRoom` 用 `runState.Dominion` 建 `CombatState`，所以戰鬥的 `Dominion` 也是空的。
   - `CombatBuilder.Apply` 把觀察畫上去（敵人、能力、牌、遺物、藥水、球、箭、旋律……），`Resample` 重抽看不見的部分。
   - `CombatBuilder.Differences(觀察, 建出來的觀察)`：除了 `NotBuilt` 的九個欄位，逐欄比對。`mode`、`dominion` 不一樣 → 丟例外。
4. 正常的話，worker 把建好的戰鬥匯出（`LiveCombat.Bytes`），主程序用 `EngineWorld.Sample` 交給引擎，每個世界一個執行緒搜。
   Dread 的局到不了這一步：**引擎根本沒看到它**。

**重播路徑**：`PooledPimc.Decide` → `PimcWorldRequest` → `Serve.Search` → 同一個 `SimWorlds.NoSL`，同一處失敗；
正常的話世界留在 worker 裡用 C# 重播來搜。

**mod**（推論，沒跑）：NoSL 的 mod 只送觀察（`Assist.cs`：`export` 只在 `sl` 時寫），brain 走同一個 `EnginePimc`，
所以真實遊戲裡 NoSL 的 Dread 也在同一處失敗。

**引擎不是問題（量到的，之前的紀錄）**：SL 的 Dread 對照（`tmp/dread-parity`：12 局 Dread、六個角色、121 場戰鬥、
3,505 個決策、3,472 次匯入）引擎和遊戲 0 差異，五個 Dread Boss 和 Knowledge Demon 第一回合的選擇都有走到。
引擎的 schema 帶 `RunState.GameMode`、`RunState.Dominion`、`RunModels`、`CombatState.dominion`（`engine/port.toml` 的 `[fields]`），
`observe.rs` 從戰鬥狀態寫 `mode`、`dominion`。

### 1.3 比對放行的話，會不會打對？不會（讀程式碼的推論）

比對是現在唯一擋住的東西，但它擋住的是真正的錯。把模式和領域從比對拿掉，建出來的仍是一場標準模式、沒有領域的戰鬥：

1. **遊戲在戰鬥中讀模式**：`AscensionHelper.IsDread()` = `RunManager.Instance.IsDread()` = `State.GameMode == GameMode.Dread`。
   怪物和遭遇裡有 30 處，在 14 個檔：Queen（狀態機的第一招不同、隊友加的力量 1 → 2）、Knowledge Demon（Disintegration 6/7/8 → 8/9/10）、
   Aeonglass（Wither 多一張、放進抽牌堆而不是棄牌堆）、Test Subject、Lagavulin Matriarch、Ceremonial Beast、The Insatiable、
   Vantom、Waterfall Giant、Soul Fysh（多塞一張 Beckon 到手上）、Rocket、Crusher、Torch Head Amalgam、The Kin（多一個位置）。
   - 這些多半在 **Boss 戰**。Boss 戰沒有領域，但模式仍是 Dread，所以 Boss 戰一樣過不了比對。
   - 畫在意圖上的數字（攻擊傷害）在標準模式下對不上，`Moves` 找不到相同意圖的招，比對還是會失敗；
     不畫在意圖上的（加的力量、Slippery 的層數、Wither 放哪一堆、手上多的 Beckon）就**安靜地錯**：搜尋看到的是標準模式的未來。
2. **領域在戰鬥中途出手**：`DominionModel.AfterCreatureAddedToCombat` 對每個中途加入的敵人呼叫 `AfterEnemiesEnteredCombat`。
   Ceremonial Beast 的領域給 Ritual 1、Kaiser Crab 的給 Plating 7、Test Subject 的給 Strength 3、Waterfall Giant 的給 Explosive（3 到 7，小嘍囉除外）、
   Vantom 的多給 Slippery 1。沒有領域的世界裡，召喚出來的怪物什麼都沒有，搜尋會低估召喚戰。
3. **開場的效果沒問題**：進房間時的效果（Aeonglass、Insatiable、Lagavulin Matriarch 的領域能力，Kin 的 Weak + Frail，Queen 的 You Are Mine，
   Waterfall Giant 的 Explosive，Vantom 的 Slippery，Soul Fysh 塞進抽牌堆的 Beckon）留下的是能力和牌，觀察看得到，
   重建照觀察掛上去（`Powers`、`Cards`），之後由能力自己動作。Knowledge Demon 的領域只在第一回合抽牌前出手，之後也只剩能力。
4. **網路**：搜尋的葉子讀的是世界自己寫的觀察（`EngineWorld.Observe`、`CombatSession.Observation`），上面沒有 `mode:`、`dominion:`，
   所以戰鬥網路在根節點讀 Dread，在每一個葉子讀標準模式。V 那一半是對的：`World.Run()` 用真實的 `RunView`，只換遺物、藥水、血量、金幣。
5. **`DreadRunModel`** 在戰鬥裡沒有鉤子（`AfterActEntered`、`AfterRunLoaded`、`BeforeRoomEntered`），但建出來的戰鬥自己也會「進房間」：
   若建的是 Dread 的 run，`BeforeRoomEntered` 會讀這個 run 的幕的 Boss（`RemoveDominionIfRoomIsBoss`、`ApplyCurrentDominion`），
   而建出來的 run 的幕沒有排房間。這是 4.1 的 (a) 要多處理的一點（推論，沒試）。

## 2. 缺陷：重建的世界少了什麼

SL 的世界是真實狀態本身（模擬器裡把真實戰鬥匯出、交給引擎；mod 也送同一份匯出）。
NoSL 的世界是 `CombatBuilder` 的產物：照觀察開一場新戰鬥，再一欄一欄蓋上去。
所以問題不是「NoSL 少了 Dread」，而是「NoSL 的世界只含觀察寫得出、重建倒得回去的東西」。

### 2.1 量到的：標準模式也一樣（不只 Dread）

本機六局標準模式的 NoSL（每個角色 A0，種子 7，引擎），全部打到結束、0 次失敗。

- 觀察裡帶 `NotBuilt` 欄位的：3,150 個戰鬥決策裡 2,469 個（78.4 %），778 個回合開始裡 571 個（73.4 %），幾乎都是 `calc`。
  這只說明這些欄位常出現，不說明建出來的不同。
- 把每個回合開始（778 個）和其他決策每五個取一個（458 個）拿去重建（`sim combat --from`，和 NoSL 用的是同一個 `CombatBuilder`），
  建出來的觀察和真實的逐欄比，比對跳過的欄位也比：

| | 回合開始 778 | 回合中 458 |
|---|---|---|
| 有任何不同 | 129（16.6 %） | 117（25.5 %） |
| 不同在比對跳過的欄位（`NotBuilt`） | 124（15.9 %） | 109（23.8 %） |
| 不同在戰鬥網路直接讀的欄位（`calc`、`detail`） | 14（1.8 %） | 31（6.8 %） |
| 不同在躺著的敵人的意圖（比對不看死掉的敵人的意圖） | 5（0.6 %） | 8（1.7 %） |

例子：

- **The Tower**（第 50 層）：`calc` 真的 150，建出來的 3。建出來的 run 只有 1 層（2.2 表裡的樓層那一列，量到了）。
- **Supermassive**：`calc` 真的 77，建出來的 5（從這一場的歷史算的）。
- **Bygone Effigy 的 Slow**：`detail` 真的 11，建出來的 1；**Mythic Form** 的 `detail` 建出來沒有。
- **Adaptive Strike**：真的費用改變是整場的（沒有 `lasting`），建出來的當成只到本回合（`lasting` 2）。
- **躺著的 Decimillipede 節段**（血量 0，等復活）：真的沒有意圖或是 Heal，建出來的是 Attack。戰鬥網路讀躺著的敵人的意圖。

讀法（推論）：

- `lasting` 戰鬥網路不讀，但它決定費用的改變在回合結束時會不會消失；搜尋的葉子在下一回合開始，讀的就是那時的費用。
  所以 `lasting` 不同，葉子的輸入也會不同。回合開始的 124 個裡大部分是它（手上的牌 607 處）。
- 這些都是今天就有的、和 Dread 無關的差距，而且都安靜地通過了比對，六局 0 失敗。

### 2.2 表：重建的世界少了什麼

類別：**甲** = 玩家看得到或記得，但觀察沒有；**乙** = 觀察有，但重建沒設；**丙** = 真的看不見，NoSL 該重抽，不能照抄。
「網路／搜尋」欄：網路讀不讀它、搜尋的世界用不用它（遊戲邏輯會讀它）。數字是從引擎的 schema 數的（遊戲整個戰鬥狀態，2,081 個類別）。

| 東西 | 玩家看得到嗎 | 觀察有嗎 | 重建有設嗎 | 網路／搜尋 | 類別 |
|---|---|---|---|---|---|
| 遊戲模式 `mode`（Dread） | 看得到 | 有 | 沒有（固定標準） | 兩個網路的 context；遊戲 30 處 | 乙 |
| 領域 `dominion` | 看得到（頂欄的 Boss 圖示） | 有 | 沒有 | 戰鬥網路的 context；中途召喚 | 乙 |
| `RunModels`（`DreadRunModel`；Blitz 的） | 規則 | 隨模式 | 沒有 | 戰鬥中沒有鉤子 | 乙 |
| `lasting`、`stars_lasting`（暫時的費用改變結束後的費用） | 看得到 | 有 | 沒有：手上的牌當成「本回合或打出前」，其他堆當成「整場」 | 網路不讀；搜尋裡回合結束後的費用可能不同 | 乙 |
| `replay`（打一次重播幾次） | 看得到（牌面） | 有 | 沒有 | 戰鬥網路讀 | 乙 |
| `spent`（附魔的一場一次已用） | 看得到（灰色圖示） | 有 | 沒有 | 戰鬥網路讀；搜尋裡會再觸發一次 | 乙 |
| `calc`（牌面從戰鬥狀態算出的數字） | 看得到 | 有 | 間接：從建出來的狀態算，歷史那部分沒有 | 戰鬥網路讀；搜尋用它出傷害 | 乙 |
| `extra`（Momentum 累積的傷害） | 看得到 | 有 | 沒有 | 戰鬥網路讀 | 乙 |
| `detail`（能力的數字偏離它自己的） | 看得到（提示） | 有 | 沒有 | 戰鬥網路讀 | 乙 |
| `stolen`（Swipe 偷走的牌） | 看得到 | 有 | 沒有 | 戰鬥網路讀；搜尋裡牌還不還得回來 | 乙 |
| `this_turn`（只限本回合的 Retain、Sly） | 看得到 | 有 | 沒有 | 戰鬥網路讀；回合結束留不留牌 | 乙 |
| 躺著等復活的敵人排好的招 | 看得到（意圖） | 有 | 沒有：比對不看死掉的敵人的意圖 | 戰鬥網路讀；搜尋裡牠復活時做什麼 | 乙 |
| 戰鬥歷史：打過的牌、受的傷、抽／棄／消耗、施加的能力（18 種紀錄） | 看過，記得 | 只有本回合的旋律 | 只補旋律，和讓能力、遺物數字對上的出牌（用一張假牌） | 網路只讀到結果（`calc` 之類）；遊戲 31 張牌、14 個能力、5 個遺物、8 個命令讀它 | 甲 |
| 怪物的出招紀錄 | 看過 | 不在觀察裡，在意圖紀錄（`EnemyTurns`） | 照意圖配對（`MoveLogs`），配不上的留新開的 | 戰鬥網路讀前 3 回合的意圖；怪物照紀錄選下一招 | 甲（部分） |
| 怪物的私有欄位：49 類、78 欄（醒了沒、第二階段、召喚倒數、Knowledge Demon 的詛咒次數、Waterfall Giant 的水壓） | 多半從牠做過的事看得出 | 沒有 | int 欄只在意圖對不上時試 0 到 999；bool 不動 | 怪物 AI 讀 | 甲 |
| 遺物的私有欄位：75 類、121 欄（計數器，「本回合」「上回合」「本場」的旗標） | 計數器看得到，其他多半記得 | 計數器、狀態、`spent` | 計數器用 knob 解；其他是新開的值 | 兩個網路讀計數器和狀態 | 甲、乙 |
| 能力的私有欄位：14 類，加上 40 類內部資料（`Data`） | 部分看得到 | 數字、層數、`detail` | 數字有設；其他用 knob 解或留新開的 | 能力自己讀 | 甲 |
| 牌的私有欄位：20 類（Rampage 的加傷、Up My Sleeve 打過幾次、Genetic Algorithm） | 數字看得到 | `vars` | `vars` 設回去 | 戰鬥網路讀傷害、格擋、`grown` | 大致有 |
| 樓層 `TotalFloor`（The Tower 的傷害乘上它） | 看得到 | 戰鬥觀察沒有（`RunView` 有） | 建出來的 run 是 1 層（`EnterRoomDebug` 記一筆；2.1 量到） | The Tower 的 `calc` 和傷害 | 甲 |
| 牌組 `Player.Deck` | 看得到 | 戰鬥觀察沒有（`RunView` 有） | 把戰鬥中的牌堆當牌組 | 遊戲裡讀牌組的遺物和牌；V 的葉子用真的（`EngineWorld.Run` 保留牌組） | 甲 |
| 解鎖狀態 `UnlockState` | 看得到（自己的存檔） | 沒有 | 全解鎖 | 隨機生成牌的池子（20 個牌和能力的檔讀它） | 甲，只在 mod 有差（模擬器全是全解鎖） |
| 自訂規則 `Modifiers` | 看得到 | 沒有 | 沒有 | | 甲，模擬器不玩 |
| 抽牌堆的順序 | 看不見；剛放上去的看得到 | 排過序的多重集合 | 洗亂（`Resample`） | | 丙；知道位置的那幾張是甲，今天丟了 |
| 手牌以外的牌的 id | 看不見（id 是洗牌後的位置） | 沒有 | 重新編號 | | 丙 |
| 亂數流：run 的 12 條、玩家的、每隻怪物的 `_rng`、遭遇的 `_rng` | 看不見 | 沒有 | 重抽 | | 丙 |
| run 的種子 `RunRngSet.seed`（中途召喚的怪物用它加位置和 id 定自己的亂數） | 看不見 | 沒有 | 建出來的 run 用建造種子 | | 丙 |
| 意圖相同的招裡排好的那一招 | 看不見 | 只有意圖 | 均勻抽；紀錄讓哪招更可能沒有建模 | | 丙 |
| 機率和遺物袋（`Odds`、`RelicGrabBag`） | 看不見 | 沒有 | 新開的 | 戰鬥內容沒讀到（搜過牌、能力、藥水、怪物、遭遇） | 丙，戰鬥中用不到 |

**三類各說一句**：

- **甲**是「玩家知道、但觀察沒寫」。NoSL 的原則是「不偷看」，不是「失憶」：一個記性完美的玩家知道這些。
  今天的重建丟了它們，或用 knob 去猜（`Solve`、`Moves`、`HandCosts` 都是在找「哪個內部值能讓畫出來的數字對上」，多對一，猜不準就留新開的值）。
- **乙**是「觀察有、重建倒不回去」。`NotBuilt` 的九個欄位就是承認「這些倒不回去，比對時跳過」；Dread 的兩個欄位沒放進去，所以變成失敗。
- **丙**才是 NoSL 真正該重抽的東西。這一份清單很短，而且 `CombatSession.Resample` 已經把它寫出來了（除了 run 的種子，因為今天用不到）。

### 2.3 這個缺陷的形狀

- 觀察本來是「網路讀什麼」和「什麼不能偷看」的界線，重建把它也當成「世界是什麼」的唯一來源。三件事綁在一起：
  網路要多讀一樣東西，就得先教會重建倒推它；遊戲多一樣狀態（模式、領域、新角色的箭和旋律），重建也要跟著補。
- 它一直在補：`CombatBuilder.cs` 從 10-26 到現在 28 次提交，11-16 到 11-17 兩天裡 4 次是 NoSL 測試失敗之後的修正
  （Velvet Choker、Hardened Shell / Coda / 箭、Stone Calendar）。
- 引擎已經有一條更直接的路：SL 把真實戰鬥整份匯出交給引擎（`EngineExport` 沿著 schema 走遍 2,081 個類別的每個欄位，
  schema 漏列任何欄位就拒絕載入）。NoSL 的引擎路徑卻是「先在 C# 從觀察重建，再匯出這個重建出來的戰鬥」，等於把引擎接在重建後面。

## 3. 為什麼有兩種造世界的方法

### 3.1 歷史（讀 git 紀錄）

- `CombatBuilder` 是 10-26 的第一批程式（「build sim combats from observations, with real-game uids」），那時唯一能從真實遊戲拿到的就是觀察：
  它的說明寫的是「such as one the mod sends from the real game」。
- 匯出（`EngineExport`）是 11-14 跟引擎一起來的，SL 的 mod 從那之後送匯出。NoSL 的引擎路徑是 11-17 做的，留著重建、把重建的結果匯出。
- 所以兩種方法是歷史留下來的，不是設計上需要。

### 3.2 SL 怎麼拿到它的世界

- 模擬器：`RunSession.CombatDecision` 給 `LiveCombat.Export` → `EngineExport.Write`（從 6 個靜態根沿 schema 走） → `EnginePlanner.Plan` →
  `EngineWorld.Import`（`sz_world_import`，`sample = 0`）。
- mod：`Assist.cs` 在出觀察的同一個 frame 寫匯出（`_export.Value.Write(...)`），放進 `DecisionRequest.Export`；`BrainServer` 交給同一個 `EnginePlanner`。
- 成本（量到的，之前的紀錄）：Dread 對照 3,472 次匯出，每次 203 KB、0.97 ms；另一份全程紀錄 24 次，每次 216.6 KB、匯出 4.11 ms、匯入 2.11 ms。

### 3.3 NoSL 從同一份匯出開始要什麼

一個重抽的步驟（「determinize」），把 2.2 的丙類換掉，其他照抄。它今天已經以 C# 的形式存在（`CombatSession.Resample`），要搬到引擎裡、做在匯入之後：

| 會洩漏的 | 洩漏什麼 | 怎麼重抽 |
|---|---|---|
| 抽牌堆的順序 | 接下來抽到什麼 | 照牌的 key 排序後，用樣本的亂數洗（`Resample` 的做法） |
| 手牌以外的牌的 id | id 是開局洗牌後的位置，等於順序 | 手牌照觀察的 uid，其他重新編號（`NumberCards`） |
| run、玩家的每一條亂數流 | 之後的洗牌、隨機目標、生成的牌和藥水、Snecko 的費用、球、怪物 AI | 每一條換成樣本亂數給的新狀態 |
| 每隻怪物的 `_rng`、遭遇的 `_rng` | 怪物的隨機選招 | 同上 |
| `RunRngSet.seed`、`string_seed`、`PlayerRngSet.seed` | run 的種子本身；中途召喚的怪物用它定亂數（`CombatState` 建怪物：`new Rng(RunState.Rng.Seed + 位置 + 幕 + id)`） | 換成樣本給的值。今天的 `Resample` 不換，因為建出來的 run 本來就不是真的種子；從真實狀態開始就一定要換 |
| 意圖相同的招裡排好的那一招 | 怪物真的下一步 | 在顯示相同意圖的招裡抽（`MovesShowing`），也改掉出招紀錄的最後一筆 |

- 搜過的地方沒找到其他「預先擲好」的東西：戰鬥裡生成牌和藥水的工廠都是當場用亂數流抽，遺物袋在戰鬥內容裡沒有人讀。
  但這只是用名字搜的，不是證明，所以要有 4.4 的測試。
- 引擎要多一個呼叫（例如 `sz_world_determinize(world, seed)`），8 個世界 = 同一份匯入複製 8 份、各用一個種子。
  意圖相同的招要在引擎裡算每一招的意圖，`observe.rs` 已經有這段（給 `next_move` 用的）。

### 3.4 在哪裡跑：模擬器和 mod

- **模擬器的自我對局和測試**：真實狀態本來就在，規則只是「NoSL 不能用看不見的東西」。`CombatDecision` 已經帶著匯出（不分環境），
  所以 NoSL 只要用它：`EngineWorld.Import` + 重抽。不用 worker 建世界。
- **mod（真實遊戲）**：今天 NoSL 只送觀察，是刻意的（`Wire.cs` 對 `Export` 的說明：「It holds the hidden state, so a NoSL host never sends it」；
  `RunHeader.IdOf`：「in NoSL the seed never leaves the host」）。mod 送得出匯出（SL 就在送，同一段程式），選擇是：
  - **(c1) mod 照送匯出，brain 匯入後先重抽再搜**。看不見的狀態會傳到 brain 的程序（用戶自己的電腦、自己的程序，不顯示給任何人），
    但在任何搜尋讀它之前就被換掉。保證從「資料過不來」變成「程式先換掉」。
  - **(c2) mod 在送之前就重抽**。mod 程序沒有載入引擎（`Sts2Ai.Mod.csproj` 只編進 GameBridge 和 Protocol），也不能改正在玩的遊戲，所以得在 C# 對匯出的 JSON 動手，
    抽牌堆、牌的 id、排好的招都是結構性的改動，等於在 mod 裡再寫一次重抽。耦合比 (c1) 高。
  - 我的看法：(c1)。NoSL 的意思是決策不依賴看不見的東西，這可以用 4.4 的測試直接驗；資料在不在同一台電腦的另一個程序裡不是重點。
    但面板上寫著「NoSL 從不偷看」，這是公開的說法，所以這一點由用戶決定（第 7 節）。

## 4. 選項

### 4.1 (a) 最小的修補：建出來的戰鬥帶模式和領域

- **改什麼**：`CombatSession.Start` 用觀察的模式建 run（`CreateForTest` 的 `gameMode`），進房間前設好 run 的領域，
  並給建出來的 run 的幕一個 Boss（`DreadRunModel.BeforeRoomEntered` 會讀；Boss 戰用觀察的遭遇）。兩條路共用重建，所以一次改好兩條。
- **修好什麼**：表裡前三列。Boss 的 Dread 數字和招式、領域對召喚的作用、葉子讀到 `mode:` 和 `dominion:`。
- **代價**：一個檔加一個測試，一次 Dread 的 NoSL smoke。小。
- **留下什麼**：表裡其他所有甲、乙列；下一個遊戲加的狀態（Blitz、新模式、新角色的機制）照樣再失敗一次。
  而且 (c) 做了之後這段會整個刪掉。

### 4.2 (b) 一欄一欄加寬觀察和重建，直到看得到的都不丟

- **改什麼**：甲類每一樣加進觀察（C# 的 `Observe.cs` 和 Rust 的 `observe.rs` 要逐位元組一致），重建學會倒推它，最後拿掉 `NotBuilt`。
- **修好什麼**：原則上甲、乙全部。
- **代價**：大，而且沒有盡頭。每一欄要觀察、倒推、比對三份程式。倒推多半是多對一（幾個內部值畫出同一個數字），只能猜；
  戰鬥歷史沒辦法真的倒推（今天的 `Plays` 是用一張假牌假裝打過）；怪物、遺物、能力的私有欄位每個遊戲版本都會變。
  觀察變大，兩個網路的輸入也跟著變。
- **留下什麼**：沒人觀察的內部欄位；每個遊戲版本的新內容。

### 4.3 (c) 一個世界給兩個環境：NoSL 是真實狀態，看不見的部分重抽

- **改什麼**：
  - 引擎：匯入後的重抽（3.3 的清單），一個 C ABI 呼叫。
  - `EnginePimc`：從 `CombatDecision.Export` 匯入，複製 8 份各自重抽，不再送 `PimcSampleRequest`。
  - mod：NoSL 也送匯出（`Assist.cs` 一行條件，`Wire.cs` 的說明）。
  - NoSL 的重播路徑：拿掉（重播留給 SL 的檢查），或在模擬器裡改成「節點的副本 + C# 的 `Resample`」當作對照。我傾向拿掉。
  - 拿掉 NoSL 的重建：`SimWorlds.NoSL`、`PimcSampleRequest`、`PimcWorldRequest`、`PooledPimc`、`CombatBuilder`（和用它的 `sim combat --from`、自我測試的幾段）。
- **修好什麼**：
  - Dread 和任何之後的模式、機制：只要引擎移植了它（SL 本來就需要），NoSL 就有。
  - 表裡所有甲、乙列一次解決：世界就是真的，歷史、私有欄位、樓層、牌組、解鎖狀態都在。
  - **SL 和 NoSL 同一個結構**：同一份匯出、同一個匯入、同一種世界、葉子讀同一種觀察。差別只剩「SL 不重抽，NoSL 重抽丙類」，
    和今天就有的「SL 用 `HiddenState.Full`、NoSL 用 `HiddenState.Key` 分辨狀態」。
  - **網路讀得到全部看得到的東西變便宜**：之後要讓網路多讀一樣東西，只要加進觀察和 token，不用再寫倒推。
  - NoSL 搜尋不再需要 worker 建世界：每個決策少 8 個建世界的請求，每局少一組 worker 程序。
    （量到的事件：11-16 的 NoSL 測試裡，6 G 的小機器跑 3 條 NoSL 局時記憶體不夠。worker 在其中佔多少沒量過。）
- **代價**：
  - Rust 的重抽（3.3 的五樣），一個 C ABI 呼叫，`EnginePimc` 改寫，mod 一行，刪掉 NoSL 的重建。
  - 每個決策一次匯出（量到的：0.97 到 4.11 ms、約 200 KB），取代 8 次在 worker 裡的重建。重建本身的時間沒量。
  - 4.4 的兩個測試。
- **留下什麼**：
  - **保證變成程式**：漏掉一個丙類欄位，就會安靜地偷看。今天的做法是建構上不可能偷看（只有觀察進去），代價是丟了甲、乙。所以 4.4 不能省。
  - 決策停在卡牌選擇（card prompt）的根：引擎在動作中途收不了匯出（SL 也一樣，它在節點的副本上跨過去），
    NoSL 今天在根的選擇給第一個答案（`FirstPicks`），(c) 之後照舊，不變好也不變壞。
  - 抽牌堆裡玩家知道位置的牌（剛放到頂上的）：真實狀態有，但全部重洗就丟了，和今天一樣。要保留得先記下哪幾張是玩家看到放上去的。
  - 意圖相同的招照紀錄加權：和今天一樣沒有建模。

### 4.4 (c) 的保證怎麼驗

1. **只動看不見的部分，決策不能變**：在模擬器裡拿真實戰鬥，做兩份匯出：一份照實，一份先在 C# 對副本做 `Resample`（看得到的不變、看不見的全換）再匯出。
   兩份用同一個搜尋種子跑 NoSL 的決策，必須逐位元相同。跑過整局的每個決策。
2. **重抽之後看得到的一個都不能變**：每個重抽出來的世界寫的觀察，和真實的觀察逐欄相同，**不再有 `NotBuilt` 的例外**。
3. （可選）**找漏網的丙類**：同一個觀察由不同種子走到時，比較兩份匯出；不同、又不在重抽清單裡的欄位，就是候選的洩漏，逐一看。

### 4.5 建議：(c)

理由：

- 用戶要的三件事它都給：一個結構給 SL 和 NoSL；網路讀得到全部看得到的東西（而且之後加輸入不用再寫倒推）；低耦合、簡單
  （刪掉的程式比加的多：`CombatBuilder.cs` 773 行和兩種 worker 請求，換一段重抽；C# 的 `Resample` 是 34 行，引擎裡還要算意圖相同的招）。
- (a) 只修 Dread，下一個狀態照樣失敗，而且 (c) 之後會整段刪掉；(b) 沒有盡頭。
- 該抽的清單（丙類）短而且已經寫在 `Resample` 裡；該抄的（甲、乙）長而且一直在變。把短的那份寫成程式、長的那份交給遊戲本身，才划算。

**做的順序**（每一步都能單獨驗）：

1. 引擎：匯入後重抽（3.3 的清單）+ C ABI；4.4 的第 2 個測試（重抽後的觀察和真實的逐欄相同）。
2. `EnginePimc` 從匯出開始；4.4 的第 1 個測試；本機的 NoSL 標準局和 Dread 局各幾局。
3. 刪掉 NoSL 的重建和它的 worker 請求、NoSL 的重播路徑。
4. NoSL 測試（promotion-1-100，A0 和 A10），新舊兩種世界在同一個版本上打，看贏的局數。這是行為改變，不是只修 Dread，所以要測。
5. mod：NoSL 送匯出（等用戶對 (c1) 發話）；mod 的版本由發版的 session 給。

不做 (a) 當過渡：除非用戶要 Dread 的 NoSL 資料早於 (c) 的第 2 步。

## 5. 對 NoSL 重訓計畫（`plans/2026-11-17-0306-nosl-shared-nets.md`）的意思

- **第一個資料集能不能有 NoSL 的 Dread 局**：今天不能（量到的：第一場戰鬥就失敗）。(c) 的第 1、2 步之後可以，mod 那一步不影響自我對局
  （模擬器本來就有匯出）。(a) 之後也可以，但葉子讀到的仍是重建的世界。3.5 那句「NoSL 的列只能是標準模式」在今天是對的，
  (c) 的第 2 步進 main 之後就可以拿掉，`box.py` 也不用拒收。
- **搜尋葉子和訓練樣本的不一致**：戰鬥網路的訓練樣本是日誌裡真實的回合開始（`learn/combat/data.py`），搜尋的葉子是世界自己寫的觀察。
  - 今天和 (a) 之後：葉子在重建的世界裡，2.1 量到的差距照舊。
  - (c) 之後：葉子在真實狀態（看不見的部分重抽）裡，和訓練樣本是同一種東西。只剩抽牌堆的順序這類看不見的部分不同，而它們本來就不在觀察裡。
  - 計畫第 1 步的「先查」（真實和重建的觀察編成輸入差在哪、差多少），2.1 是第一份量測；採用 (c) 的話這一項就不用再查。
- **3.1 的環境標記**：仍然由大腦交給編碼器，不寫在觀察上。(c) 的世界一樣寫自己的觀察，理由不變。
- **順序**：(c) 改的是 NoSL 的世界，不動網路的表和結構，可以和計畫的第 1 步（網路的結構）分開做、分開進 main。
  但它改變 NoSL 的打法，所以計畫 3.6 的 NoSL 測試對照要在 (c) 之後的同一個版本上打。

## 6. 跑過的東西（都在本機，`tmp/compute-timetable.md` 有紀錄）

- NoSL Dread 兩局（引擎、重播），各幾秒就失敗。輸出在 `tmp/nosl-dread/engine`、`tmp/nosl-dread/replay`。
- 標準模式的 NoSL 六局（每個角色 A0 種子 7，引擎），全部打到結束，0 個遊戲錯誤，3,150 個戰鬥決策、778 個回合開始。`tmp/nosl-dread/std`。
- 從這六局的觀察重建戰鬥再比對（`sim combat --from --obs`，1,236 次，4 個同時，每個 4G 上限，11 分鐘）。
  結果 `tmp/nosl-dread/build/result.txt`，腳本 `tmp/nosl-dread/builddiff.py`、`tmp/nosl-dread/freq.py`。

## 7. 等用戶發話

1. 採不採用 (c)。
2. mod 的 NoSL 要不要送匯出（(c1)：看不見的狀態到 brain 的程序，搜尋前換掉；面板上「NoSL 從不偷看」的說法是否仍然成立，由用戶判斷）。
3. 第一個資料集要不要等 (c) 的第 2 步，好讓 NoSL 的 Dread 局和沒有不一致的葉子一起進來。

## 8. 沒能確定的

- (c) 的重抽清單是否完整：只用名字搜了戰鬥內容，沒有證明。4.4 的測試是用來補這一點的。
- 重建本身花多少時間、NoSL 的 worker 佔小機器多少記憶體：沒量。
- (a) 的 Boss 那一點（`DreadRunModel.BeforeRoomEntered` 讀幕的 Boss）：只讀了程式，沒試。
- 1.3 的「安靜地錯」是讀程式碼的推論，沒有在 Dread 的局上量過搜尋因此選了不同的動作。
