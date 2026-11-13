# Rust 戰鬥引擎：SL 戰鬥搜尋搬到快引擎上，完整支援 v0.113.0

2026-11-14 02:01（台灣時間）。這份檔案是這件工作的唯一依據，進度寫在最後一節，每完成一步就更新；時間一律取自系統時鐘。

## 1. 用戶定的（2026-11-14）

1. 生成局太慢是現在最大的問題。參考 tfriedel/spire-mentor（MIT，在 `tmp/spire-mentor`），程式碼可以遷移過來，公開時要感謝。
2. **只支援戰鬥模擬**：整局（地圖、事件、商店、獎勵）仍由真 `sts2.dll` 跑。
3. **完整支援我們的 v0.113.0**（六個角色，含愚者）。
4. 用最新版 Rust（1.100.0）。
5. 設計最合理的新架構，寫進 plan，然後**做到完為止**；本機可以隨便用；沒說停就接著做。
6. 其他工作階段正在監視一場訓練：不碰 pushqkzh、hub 上的 step、`learn/`、`weights/`、`eval/champions/`；
   commit 只含自己的檔案，commit 前看 diff 有沒有別人的改動。

7. （02:05 補）**程式碼要重新寫自己的版本，定義好最好的架構，復刻 STS2 戰鬥**（原話：「注意程式碼重新寫自己版本的，定義好
   最好的架構，復刻sts2戰鬥」）。所以不把 spire-mentor 的 crate 原樣帶進來；它只當參考。第 3、4 節依此重寫中。

## 2. 量到的數字（本機 9800X3D，HEAD，v0.113.0）

一局冠軍（Defect A10、medium、4 workers、promotion-1-50 第 0 顆種子，573 個決策）：252 秒，599 CPU 秒。

| 搜尋行程的時間（共 578 秒，117,174 個節點，每節點 4.93 ms） | 佔比 |
|---|---|
| 還原狀態：重讀存檔 9.9 % + 重播 71.6 %（每次還原平均重播 30.4 步、6.6 ms） | 81.5 % |
| 新走的一步 | 7.3 % |
| 建觀測（每次 254 µs） | 5.2 % |
| 隱藏狀態的鍵（每次 109 µs） | 2.0 % |
| 葉節點：戰鬥網路 1.9 %（每次 301 µs，36,922 次）+ 編碼 0.8 % | 2.7 % |
| 其餘（狀態鍵、雜湊、排序、RunView、dispose） | 約 3 % |

搜尋之外：戰鬥外決策在複本上試選項 11.4 秒（1,011 次，每次 11 ms），主行程約 6 秒。

對方引擎在本機（Rust 1.100.0，digest 與它公佈的相同）：隨機打 66,165 場/秒、每步 0.69 µs；複製＋列合法動作＋走一步
1.36 µs。我們的真 DLL：每步約 95 µs、約 440 場/秒。

推算（一局 CPU 秒，由上表各部分相加，不是量出來的）：

| 做法 | 一局 | 倍數 |
|---|---|---|
| 現在 | 599 | 1 |
| 只換世界，仍每個節點建觀測 | 約 76 | 約 8 |
| 鍵由引擎給，觀測只在葉節點與選中的那條線上建 | 約 37 | 約 16 |
| 再加上整批推論 | 約 27 | 約 22 |
| 上限（戰鬥搜尋趨近 0，剩戰鬥外試選項與主行程） | 約 17 | 約 35 |

## 3. 架構（02:11 依第 1 節第 7 點重寫；完整版是 `engine/DESIGN.md`，以它為準）

三條原則：

1. **遊戲自己的程式碼是來源。** 內容（約 1,700 個類別：牌、能力、遺物、怪物、藥水…，8 萬行）不手寫，由我們寫的翻譯器
   `port`（C#，Roslyn）把用戶自己那份遊戲的反編譯原始碼機械翻成 Rust。遊戲改版就重跑一次。
2. **引擎照遊戲自己的模型長**：同樣的類別、欄位、掛鉤、指令、亂數流、id、名字。翻出來的方法意思不變；真遊戲的戰鬥可以逐欄位
   複製進來；觀測照 `Observe.cs` 一項一項讀。
3. **真遊戲是每一步的裁判。** 計畫帶著每一步預期的觀測雜湊在真遊戲上執行，不符就算一次分歧並重新規劃。沒有退路、沒有容忍。

儲存庫裡不放任何由遊戲衍生的東西（生成的 Rust、schema 都不進 git），和 `tmp/gamebin` 一樣由用戶自己的遊戲產生。

探測（Roslyn 編譯整份反編譯原始碼來查型別）：內容類別 1,699 個、方法體 9,058 個，解析不了的識別字 11 個；內容用到的遊戲核心
成員 1,091 個。核心裡翻不動的部分（回合迴圈、動作佇列、等輸入的地方）手寫在 `manual/`；還沒有的成員由 `port` 生成
`todo!` 樁，樁的清單就是剩下的工作。

選擇（戰鬥中途的選牌提示）：一個動作是「動作前的狀態 + 目前給過的答案」的函數；跑到沒有答案的提示就停，世界留下動作前的狀態、
動作、答案、停下時的樣子（給觀測）。下一個答案從頭再跑一次。所以狀態永遠是可以直接複製的純資料。

| 路徑 | 內容 |
|---|---|
| `src/Sts2Ai.Port/` | 翻譯器 |
| `engine/crates/sts2ai-engine/src/rt/` | 手寫的基礎：`Dec`、亂數流、arena 與 handle、`Combat`、選擇機制、LINQ 輔助 |
| `…/src/manual/` | 手寫的遊戲核心，每個 C# 類別一個模組 |
| `…/src/gen/` | 翻譯結果、分派表、狀態結構、樁、`schema.json`（不進 git） |
| `…/src/{world,import,observe,ffi}.rs` | 世界、匯入、觀測、C ABI（`libsts2ai_engine.so`） |
| `src/Sts2Ai.GameBridge/EngineExport.cs` | 真遊戲的戰鬥沿 schema 匯出 |
| `src/Sts2Ai.Engine/` | P/Invoke、`EngineWorld : ICombatWorld`、行程內規劃 |
| `src/Sts2Ai.SimWorker` | `sim parity` 對拍 |

對拍與「完整支援」的定義不變：每個決策點做匯入、走步、續打三項檢查；v0.113.0 的每個 id 都出現過且 0 分歧，六個角色 A0 與 A10
整局實打也 0 分歧。

## 4. 步驟與分工（用戶 02:06：「可以開新的session專門負責寫程式碼」）

這個工作階段負責架構、協調、驗收；寫程式的工作階段共用這個 checkout、各管各的目錄、commit 時只列自己的路徑。

1. **port + 執行核心骨架**（工作階段 engine-port）：`src/Sts2Ai.Port`、`engine/`。里程碑一：全部內容類別翻完、crate 編得過
   （缺的是樁）、有 schema。里程碑二：由匯出狀態建世界，起始牌組打一場能走到底。
2. **對拍工具與接口**（工作階段 engine-bridge）：`sim parity`（先對現有的複本重播世界驗證工具本身）、`Sts2Ai.Engine`。
   schema 出來後接 `EngineExport`。
3. **手寫核心分批**：里程碑二之後，依樁清單把 `manual/` 的類別分給幾個工作階段。
4. **歸零**：第一輪全量對拍 → 依分歧數由大到小修（修翻譯器或手寫核心，不修生成結果）。
5. **接進 SL 規劃**：`CombatDecision` 帶可延後取用的匯出狀態；行程內多執行緒搜尋；同一顆種子兩種世界每個決策相同。
6. **速度**：鍵與動作排序由引擎給、觀測只在葉節點與選中的線上建；量一局 CPU 秒對第 2 節。
7. **佈署**：bundle 帶引擎函式庫，每台自檢；不發 fleet 工作。
8. **收尾**：README、致謝；書要改哪幾章列給用戶，問誰寫。

## 5. 進度

- 02:01 plan 寫好。Rust 已更新到 1.100.0；對方引擎的兩個基準已在本機編譯並跑過。下一步：步驟 1。
- 02:05 用戶改指示：重寫自己的版本（第 1 節第 7 點）。已撤掉複製進 `engine/` 的上游 crate（未 commit）。正在做可行性探測：
  用 Roslyn 把反編譯的內容類別（牌、能力、遺物、怪物…約 1,650 個類別、8 萬行）機械翻成 Rust，手寫的只有執行核心。
- 02:11 架構定案並寫成 `engine/DESIGN.md`；探測專案在 `tmp/port-spike`。開工作階段 engine-port、engine-bridge。
- 02:14 `engine/DESIGN.md` 已 commit、push（02:12）。兩個工作階段已開：engine-port（`local_5c229cc9-c330-400c-8d1f-a17be45107ae`，
  管 `src/Sts2Ai.Port`、`engine/`）、engine-bridge（`local_a06be296-cf6a-473f-8fa9-3018a166fab1`，管 `src/Sts2Ai.Engine`、
  `sim parity`、`Sts2Ai.sln`，之後 `EngineExport.cs`）。它們結束一輪會回報到協調的工作階段；回報只是回報，要看 diff 與實跑再算數。
  下一步（協調）：驗收里程碑一（`engine/gen.sh` + `cargo check`、報告數字）與 `sim parity --world replay` 的 0 分歧；
  然後依樁清單把 `manual/` 分批開工作階段。
- 02:15 用戶：推論維持 ONNX Runtime，不換 tch（「ONNX Runtime 不需要換，你說什麼就是什麼」）。這類技術選擇由協調的工作階段直接定。
- 02:19 用戶：所有計畫檔由 `tmp/plans` 搬到 `plans/` 並進 git，檔名都帶到分鐘（四個舊檔補上建立時間），四處機器位址改成名稱；
  之前這一節寫的時間是估的、比時鐘快，已照檔案與 commit 時間改正。用戶另囑：經常 commit + push，全自動，不必問。
  兩個工作階段已收到新路徑的通知（排隊中，未確認已讀；`tmp/plans` 留了指向 `plans/` 的符號連結，舊路徑照樣寫得到）。
- 03:00 engine-bridge（步驟 2）回報：`sim parity` 與 `src/Sts2Ai.Engine` 已 commit、push（三個 commit）。
  - `sim parity`：真遊戲用帶種子的隨機策略打完一場並記下每個決策，再拿第二世界逐點對：匯入、走步（帶著走的世界，
    分歧後在下一個對得上的決策重建）、續打。輸出每場一行 JSON（`--out`）、總結（依第一個不同的 JSON 路徑、依動作作用的
    牌／藥水分組）、每個 id 的涵蓋表（`--coverage`）。第二世界由 `ISecondWorlds` 工廠給：`--world replay`（現有的 SL
    重播世界）、`--world determinized`（同一個世界但隱藏狀態重抽，用來反過來測工具）。`--workers N` 分給 N 個行程。
  - `--world replay` 全量（6 角色 × A0、A10 × 90 個遭遇 × 4 套配置，seed 1，每個決策都續打）：4,320 場、104,528 個決策，
    匯入 104,528 次、走步 104,528 步、續打 3,528,527 步，**0 分歧、0 場失敗**（12 個行程 21 分鐘）。預設的 2 套配置
    2,160 場、53,444 個決策同樣 0 分歧（14 個行程 5 分 20 秒）。涵蓋：牌 715/717（671 張被打出過）、能力 283/304、
    遺物 316/316、藥水 68/68、怪物 113/122、遭遇 89/90、充能球 5/5、附魔 22/23、苦痛 7/7；沒遇到的是遊戲自己的測試怪、
    DEPRECATED_*、MAD_SCIENCE，與 21 個只在特定招式／牌之後才出現的能力。
  - 工具自測：`--world determinized` 720 場：匯入 0 分歧（17,706 次），走步 4,388、續打 4,899 個分歧，幾乎都在
    `end_turn` 之後的 `hand[].id`，正是「匯出漏了隱藏狀態」該有的樣子。比較邏輯另有 18 個單元測試（假世界注入差異）。
  - `Sts2Ai.Engine`：十二個 C 函式的 P/Invoke、safe handle、`EngineWorld : ICombatWorld`（快照 = 原生世界的複本，跟著
    快照物件被回收；世界互不共用，可各在一條執行緒）。函式庫路徑只有一處：環境變數 `STS2AI_ENGINE_LIB`，否則程式旁邊
    的檔。真函式庫還沒有，所以對一個用 rustc 現編的替身函式庫測（8 個測試；`dotnet test src/Sts2Ai.Tests` 因此需要 rustc）。
  - 沒做：`EngineExport.cs` 與 `--world engine`。`gen/schema.json` 02:59 才出現（format 1），`engine/DESIGN.md` 還沒寫它的
    格式與匯出的 JSON 長相，引擎也還沒有 `import.rs`／`ffi.rs`。格式寫定後由這個工作階段接著做。
  - 要協調決定或知道的事在共享筆記 `engine-bridge.md`：戰敗只比 status；提示中途無法匯入；`sz_world_run` 的格式是
    我假設的；快照靠 GC 釋放；`CombatSession.Start` 在 A10 滿藥水時會讓整個行程之後全失敗（未修，不是我的檔）。
- 03:10 （engine-port）里程碑一完成：`engine/gen.sh --fresh --check` 從乾淨的樹跑完，`cargo check` 通過（port 5 秒，
  整個流程 12 秒）。port 的報告：翻譯類別 1,897 個（內容類別全部：牌 717、能力 308+巢狀、遺物 317、怪物 123、藥水 68、
  遭遇 91、附魔 23、苦難 7、球 5，加核心）；翻出成員 12,071 個、約 22 萬行 Rust；沒翻出的成員 251 個（函式體是 `todo!`，
  原因在 `gen/report.txt`，大宗是 CombatManager、動作佇列、選牌這些本來就要手寫的）；rustc 拒收 0 個；樁 410 個；
  分派函式 501 個；整個丟掉的呈現成員 1,210 個、丟掉的敘述 1,974 條，其中含非呈現呼叫要人看的 6 條。手寫成員目前 0
  （`manual/` 只有骨架）。`rt/` 的 `Dec`（對 .NET 1,437 萬個欄位 0 差異）和亂數流（對 C# 97.7 萬行 0 差異）已就位。
  設計文件依實作改了幾處（控制代碼含 null、不用 Option；泛型的遊戲類別改傳 `Kind`；成員是模組函式；呈現的規則），
  見 `engine/DESIGN.md`。下一步：里程碑二（手寫核心 + world/import/observe/ffi，讓一場戰鬥跑到底）。
- 03:21 （協調）驗收兩份回報，都成立：
  - 里程碑一：在 HEAD 的乾淨匯出（`tmp/builds/verify-1b47cd3a`）重跑 `engine/gen.sh --fresh --check`，數字與回報相同
    （1,897 類別、12,071 成員、251 個沒翻出、410 個樁、0 個被 rustc 拒收），`cargo check` 通過，全程 28 秒。
    翻出的牌對照 C# 讀過，照設計文件的規則。
  - `sim parity`：同一份乾淨建置，`--n 240` 6,158 個決策 0 分歧（exit 0）；`--world determinized --n 48` 匯入 0、
    走步 269、續打 322 個分歧（exit 1），工具確實抓得到隱藏狀態的差異。全量那次的輸出檔與回報一致。
  - 裁定（已發給兩個工作階段）：匯出格式採 engine-bridge 的提案（format 1）；匯入的讀取器由 `port` 生成，不在 Rust
    執行期解讀 schema；`outside` 必須歸零（Rng、RunRngSet、PlayerRngSet、戰鬥讀到的 RunState／Player 欄位都進
    schema，否則在 `port.toml` 標為非狀態），非空的匯出一律拒收；schema 另列每個類別刻意不收的欄位、根物件、遊戲版本
    與自身雜湊；剩下的工作以「由戰鬥入口可達的 `todo!` 與樁」為準（410 個樁多半是戰鬥外的）；內容類別沒翻出的成員
    一律修翻譯器，不手寫。觀測維持設計文件的做法：`observe.rs` 照 `Observe.cs` 逐項手寫，由對拍逐位元組把關。
  - 「完整支援」的定義補一句：遊戲自己的測試怪與 `DEPRECATED_*` 沒有任何打法會遇到，以名單排除。
  - 分工：engine-port 留在關鍵路徑（生成的匯入讀取器 → 手寫核心到一場打到底 → 可達清單 → 翻譯器涵蓋）；
    engine-bridge 接著做樣本傾印（`sim parity` 逐決策倒出匯出、真觀測、合法動作、兩個鍵、RunView、動作，讓整個對拍
    也能在 `cargo test` 離線跑）、修 `CombatSession.Start`，之後是整局對拍與步驟 5 的設計；「讀出狀態」這一塊
    （`observe.rs`、合法動作、隱藏鍵、狀態鍵、RunView、`ffi.rs`、對樣本的 Rust 測試）原本要開第三個工作階段，
    遠端控制下開不了，改由協調工作階段的子代理 engine-state 做，筆記在 `tmp/engine/notes/engine-state.md`。
- 03:32 （engine-bridge）步驟 3 的 C# 這一半與樣本傾印完成，都已推上 main：
  - `EngineExport.cs`：照 `engine/DESIGN.md`「The export」的 format 1 寫（根物件取自 schema 的 `roots`、帶 schema 的
    `hash`、`outside` 照數）。載入 schema 時就地核對這份遊戲：類別、欄位、型別參數、靜態根欄位缺一個就拒收；
    schema 的類別在遊戲裡有、但 `fields` 和 `dropped` 都沒列的實例欄位也拒收。03:28 那份 schema（121 個 arena、
    1,953 個類別）對 v0.113.0 零問題。每份匯出約 110 KB、1.2 毫秒；同一場戰鬥位元組相同。
  - `sim parity --world engine`：每個決策匯出、交給 `EngineWorld.Import`。引擎函式庫還沒建出來，所以現在每次匯入都是
    同一個明確的錯（no engine library at …），真遊戲那一側的走法與 replay 掃描完全相同（匯出不擾動遊戲）。
  - `sim parity --dump DIR`（新）與 `--world none`（只打真遊戲、不比對）：每場戰鬥一個資料夾，`combat.json` 列出逐決策的
    回合、狀態、合法動作、隱藏鍵、狀態鍵、所採動作；每個決策另有 `K.export.json`（提示中途與結束時沒有）、
    `K.observation.json`（ProtocolJson 原位元組）、`K.run.json`（`sz_world_run` 該寫出的位元組）、`K.hidden.txt`
    （隱藏鍵雜湊前的兩段文字）。版面與雜湊規則寫在傾印根目錄的 `README.md`（由指令自己寫出，永遠與程式一致）。
  - 預設樣本在 `tmp/engine/fixtures/`，指令：
    `sim parity --world none --dump tmp/engine/fixtures --encounter NIBBITS_WEAK,CULTISTS_NORMAL,RUBY_RAIDERS_NORMAL,BYGONE_EFFIGY_ELITE,CEREMONIAL_BEAST_BOSS --workers 12`
    （六個角色 × A0、A10 × 5 個遭遇 × 2 套牌組 = 120 場，2,685 筆記錄、2,473 份匯出、318 MB，12 個行程 5 秒）。
    全量預設掃描（2,160 場）約 5.2 萬份匯出、約 5.7 GB。
  - 兩支不靠遊戲也不靠引擎的檢查：`tmp/engine/check_dump.py DIR`（README 的規則：隱藏鍵＝第二段文字的雜湊、狀態鍵＝
    觀測依搜尋的分法雜湊、觀測能原樣寫回；預設樣本 2,685 筆 0 問題，可當 Rust 那邊雜湊的對照實作）與
    `tmp/engine/check_export.py SCHEMA EXPORT...`（匯出對 schema：類別、欄位、參照、根、雜湊；855,285 個物件 0 問題，
    `outside` 全空）。
  - `RunChange`（`sz_world_run` 的六個成員）成為 `Sts2Ai.Engine` 的公開型別；`HiddenState` 另給出雜湊前的文字。
  - 接下來：修 `CombatSession.Start`、整局對拍（先 `--world replay`）、步驟 5 的設計寫進共享筆記再動手。
- 03:48 （engine-bridge）`CombatSession.Start` 修好，整局對拍（`--world replay`）完成，都已推上 main：
  - `CombatSession.Start`：`SetUpTest` 失敗時把 run 清掉，同一行程之後的戰鬥照常開打（實測：關掉檢查讓遊戲自己在
    A10 滿藥水時丟例外，下一場 A0 仍然打完）。藥水裝滿而進階又要收掉一格腰帶時，`Loadout.ApplyTo` 直接報
    「N potions do not fit the belt: ascension A leaves M slots」（`RunSession` 走同一條檢查）。
  - `sim parity --runs N`：每個角色 × 進階各打 N 個整局（戰鬥用掃描的隨機玩家，其他畫面均勻亂選），局裡每一場戰鬥照
    單場的三種比對走，另外多比一項：戰鬥外圍的 run（`ICombatWorld.Run()` 對 `Observe.Run(.., previews: false)`，
    路徑以 `run.` 開頭）。`--world replay` 在整局裡的第二世界是 `NodeWorld`（節點複本載入後重播，就是現在搜尋用的
    那個世界），提示中途也造得出來；`--world engine` 把當下的 RunView 交給 `EngineWorld.Import`。`--dump` 一樣可用
    （資料夾名多一段樓層）。每場戰鬥一行，run 的結局記在它最後一場那行。
  - 隨機玩家用角色自己的血量幾層就死，所以整局要加 `--hp 9999 --max-hp 9999`。結果（種子 1，
    `--runs 4 --hp 9999 --max-hp 9999 --continue 4 --workers 12`）：48 局、959 場戰鬥、44,093 個決策，匯入、走步、
    續打（88,053 步）全部 0 分歧，0 場失敗，12 個行程 113 秒。14 局打通三幕，其餘多半死在首領戰的 80 回合上限
    （第 17／33／49 層）。
  - 途中找到並修掉的是工具自己的問題，不是 `NodeWorld` 的：整局的隨機作答把選牌順序打亂，而動作文字
    （`pick:1,2`）只有排序後的一種寫法，賭徒籌碼、Prepared、Dredge 這類「順序會落到棄牌堆」的提示因此出現分歧
    （54 個決策）。改成照動作的順序作答後歸零。對引擎的意義：`pick` 一律照選項順序套用。
  - 涵蓋：單場掃描沒遇到的 34 個 id 裡，整局補到 `MAD_SCIENCE`（也打出去了）、`CURIOUS_POWER`、`NEMESIS_POWER`。
    仍然沒遇到的 19 個能力：AEONGLASS_DOMINION、AMNESIA、CONCOCT、COORDINATE、COVERED、EXPLOSIVE、FADE、GRAVITY、
    GUARDED、IMITATION_LEARNING、IMPROVEMENT、INSATIABLE_DOMINION、INTERCEPT、LAGAVULIN_MATRIARCH_DOMINION、
    LEADERSHIP、MAGIC_BOMB、NO_ENERGY_GAIN、SOULBOUND、YOU_ARE_MINE（各加 `_POWER`）；要逐一查是多人專用、
    另一種模式才有，還是有單人打得到的來源。其餘沒遇到的仍是測試怪與 `DEPRECATED_*`。
  - 回歸：單場掃描 `--n 480` 12,549 個決策 0 分歧；`--world determinized --n 48` 匯入 0、走步 269（與驗收時同數）。
- 03:51 （協調）驗收 engine-port 的「生成的匯入讀取器」與新 schema（commit b77a130d）：
  - 乾淨匯出的 HEAD（`tmp/builds/verify-5431cd52`）重跑 `gen.sh --fresh --check` 通過：範圍擴到 2,019 個類別、13,061 個成員
    （沒翻出 449、樁 556、手寫成員 6、0 個被 rustc 拒收），schema 雜湊與工作樹的相同。
  - 匯出端吃得下新 schema：我另倒一份小樣本（`tmp/verify/fx`，24 場、396 份匯出），雜湊相符、四個根物件、`outside` 全空。
    engine-bridge 的大樣本（`tmp/engine/fixtures`，120 場、2,685 筆，檢查腳本 0 個問題）是照舊 schema 倒的，已請它重倒。
  - **匯入還不通**：396 份匯出全部被拒（`abstract_intent[0]: no member AttackIntent`）。原因在讀取器：沒有欄位的類別
    （`AttackIntent` 唯一的欄位被列為不收）它也要求匯出裡有那一層；格式規定只寫有欄位或型別參數的層，匯出端是對的。
    已回給 engine-port，連同重現方法（`tmp/verify/imp`）；要它在 crate 裡留一個測試：樣本的每份匯出都能匯入。
  - 另外：工作樹的 crate 此刻編不過（生成碼對 `rt::ops::Exception` 用了 `is_null`），會擋住子代理 engine-state 的
    `cargo check`，已提醒 engine-port 盡量讓工作樹保持可編譯。
  - HashSet／Dictionary 移除過元素後的列舉順序是 format 1 說不出來的已知缺口：請 engine-bridge 在匯出時數「有空槽的集合」，
    全量為 0 就當不存在，否則 format 1 加上槽位與空閒串列。
- 03:52 （協調）驗收整局對拍：乾淨的 HEAD 建置跑 `parity --runs 1 --hp 9999 --max-hp 9999 --continue 4`，12 局、227 場戰鬥、
  9,920 個決策，0 分歧（exit 0），與 engine-bridge 的回報一致。裁定：還沒遇到的 19 個能力逐個查原始碼分兩類，單人打不到的
  以名單排除並寫理由（測試怪、`DEPRECATED_*` 同），打得到的由掃描補上能逼出它的牌組或遭遇。
- 03:54 （協調）engine-bridge 全量掃描新 schema（51,823 份匯出）的兩個數字與裁定：
  - 「有空槽的集合」不是 0：49,890 份匯出裡有（幾乎都是 `CombatManager._card_or_potion_effect_depth`，另有牌的關鍵字集合與
    幾個能力的記帳字典）。空的集合只要有兩個空槽，接下來兩次加入的列舉順序就會反過來，所以缺口是真的。裁定：format 1 裡
    有空槽的 set／dict 改寫成 `{"items","at","free"}`（列舉順序的元素、各自的槽位、空槽依重用順序），沒空槽的照舊寫陣列；
    Rust 端照同樣的槽位與空閒串列建 `rt::Set`／`rt::Dict`，並用真 .NET 集合產生的序列樣本做單元測試。
  - `outside` 還有 17 個參照：`GoldReward` 14、`CardReward` 3，都經 `CombatRoom._extra_rewards`（Gremlin Merc 偷的金幣、
    戰鬥中加的選牌獎勵），出現在 4 場戰鬥。裁定：戰鬥中會建立的獎勵類別是狀態，進 schema；只有獎勵畫面才跑的部分不在範圍內。
    在那之前這 4 場的匯入照規則被拒。
  - engine-port 的順序：先修讀取器（396 份都要能匯入）→ 這兩項 → 手寫核心。
- 04:01 （engine-bridge）協調要的量測、槽位配置、名單，都已推上 main：
  - 量測：全量掃描 51,823 份匯出裡，有已釋放槽位的 HashSet／Dictionary 共 58,916 個，分佈在 49,890 份匯出；絕大多數是
    「東西都移掉了的空集合」（`CombatManager._card_or_potion_effect_depth` 49,683、各能力的 `amounts_for_played_cards` 等），
    還有內容的只有 `CardModel._keywords` 427 個。schema 外的參照 17 個，全部經 `CombatRoom._extra_rewards`
    （GoldReward 14、CardReward 3，4 場戰鬥）；協調裁定獎勵類別進 schema，在那之前這 4 場照規矩拒收。
  - 匯出（format 1 的補充，協調 03:57 裁定）：`set`／`dict` 沒有已釋放槽位時照舊是陣列；有的話寫成
    `{"items": [...], "at": [...], "free": [...]}`（`at` 是每個項目的槽位，`free` 是已釋放槽位、下一個會被拿走的在前）。
    寫出前檢查每個槽位恰好屬於其中一邊，否則失敗；不是 HashSet<T>／Dictionary<K,V> 的 `set`／`dict` 也失敗。
    全量掃描 0 場失敗：狀態裡沒有別種雜湊集合。每個摘要與每行都帶這兩個計數，dump 的 `combat.json` 逐筆標 `freed`。
  - `tmp/engine/fixtures` 仍是舊格式（等 engine-port 的讀取器跟上再換）；新格式的同一批在 `tmp/engine/fixtures-next`
    （2,473 份匯出裡 2,366 份帶物件形式）。
  - `sim hashed --out tmp/engine/hashed.txt`（新指令，不需要遊戲）：.NET 9.0.22 自己的 HashSet<int> 與
    Dictionary<int,int> 在 3,000 條帶種子的操作序列（141,720 步）下，每步之後的項目、槽位、已釋放槽位；給引擎的
    `rt::Set`／`rt::Dict` 直接對 .NET 本身驗。`tmp/engine/check_hashed.py` 用 30 行的模型對過：0 問題
    （一個容易漏的：`IntersectWith` 空集合等於 `Clear`）。
  - 涵蓋報告的名單：`GameIds.Unreached` 列出 30 個「單人標準局打不到」的 id 與原因，報告不再把它們算進「沒遇到」，
    萬一遇到了會指出名單有誤。19 個能力的歸類：只能打在隊友身上的多人專用牌 7 個＋Tank 給隊友的 GUARDED；沒有任何
    來源的 4 個（GRAVITY、LEADERSHIP、MAGIC_BOMB、NO_ENERGY_GAIN）；只有 Dread 模式的首領領域才有的 6 個
    （三個 *_DOMINION、EXPLOSIVE、YOU_ARE_MINE、AMNESIA：這一組算不算由協調裁定）；單人打得到、不排除的只有
    `IMPROVEMENT_POWER`（Mad Science 配 Tinker Time 的 Improvement 附加效果，牌組參數寫不出來，只能靠整局遇到，
    另外 240 局正在跑）。其餘是測試怪與 `DEPRECATED_*`。
  - 套上名單後，4 套牌組的單場掃描加 48 個整局：除了 `IMPROVEMENT_POWER` 每個 id 都遇到了（牌 716/716、能力
    285/286、遺物 316/316、藥水 68/68、怪 113/113、遭遇 89/89），148,621 個決策對 replay 0 分歧。
- 04:04 （協調）這一輪的驗收與裁定：
  - 匯入：engine-port 已修讀取器、收進獎勵類別與集合的空槽（commit 3958b840）。我用工作樹的 crate 匯入剛倒的 1,369 份匯出
    （`tmp/verify/fx`，含 Gremlin Merc 與 Corpse Slugs）：1,333 份成功，36 份被拒，都是同一個原因
    （`CorpseSlug` 的下一招不在重建的招式狀態機裡），已回給 engine-port。
  - 讀出狀態（子代理 engine-state）：`protocol.rs`、`observe.rs`、`legal.rs`、`keys.rs`、`prompt.rs`、`ffi.rs` 與測試已 commit；
    `protocol`／`reflection`／`abi` 三組測試通過（我重跑過）。走真樣本的 `fixtures` 測試還是 0 通過：每次呼叫都被翻譯器的三個
    缺口擋住——`ModHelper.IterateAllCombatStateSubscribers`（樁；沒有 mod 訂閱者，定為空）、`CardModel.GetKeywordsWithSources`
    （`ImmutableHashSet.Empty`）、`MonsterModel.get_Rng`（因一個分支回傳 `Rng.Chaotic` 被整個丟掉；裁定：保留成員，該分支
    panic）。三項已發給 engine-port。它的筆記（`tmp/engine/notes/engine-state.md`）寫了 `World` 的讀取介面與提示要帶的欄位。
  - engine-bridge：匯出已照裁定寫集合的槽位（全量 51,823 份 0 失敗）；`sim hashed` 產生真 .NET 集合的操作序列樣本
    （`tmp/engine/hashed.txt`，141,720 步）；沒遇到的 19 個能力已分類，30 個 id 以名單排除並附理由。裁定：Dread 模式才有的
    那一組（各 Boss 的 dominion 等）維持排除，理由寫「只在 Dread 模式；模擬器打標準局」。排除後只剩 `IMPROVEMENT_POWER`
    還沒遇到（要整局才碰得到，正在多跑 240 局）。樣本 `tmp/engine/fixtures` 請它照現在的 schema 重倒成新格式、只留一份。
- 04:09 （協調）狀態裡的 delegate（engine-port 提出，筆記 `delegates.md`）：
  - 問題：怪物的招式（`MoveState._onPerform`）、狀態機的分支條件、意圖的傷害算式、牌的倍率算式都是 delegate，匯出帶不走。
    現在的做法是匯入後用遊戲自己的程式碼重建（重跑 `GenerateMoveStateMachine`、`CanonicalVars`，依狀態 id／變數名對回去），
    但「當場做出來的招式」重建不了：暈眩的怪物下一招是 `new MoveState("STUNNED", stunMove, ..)`（13 個呼叫點各給各的），
    `IllusionPower` 的復活招同理。所以 Corpse Slugs 從第一次暈眩起的匯出都被拒（剛才那 36 份）。
  - 裁定：採它的提案——delegate 是匯出裡的值（哪個方法、綁在誰身上），閉包抓住的變數是狀態；不再重建。補三點：
    (1) 閉包物件（編譯器做的 display class）當一般物件匯出，保有身分，兩個 delegate 共用同一個閉包時不會被拆開；
    (2) 哪個 lambda 對應 DLL 裡哪個方法，從組件的 IL 順序配對，並逐對檢查簽章與抓住的變數名，對不上就在生成時報錯，不猜；
    (3) 先由 engine-bridge 對全量掃描與整局做一次普查（狀態裡實際出現哪些 delegate、閉包有哪些欄位、抓住的值會不會變），
    用來估工作量，之後當作翻譯器那張表的完整性檢查。
  - 順序：engine-port 先把手寫核心與 `world.rs` 做到戰鬥能從匯入的狀態打下去（里程碑二，用匯得進來的遭遇），再做引擎這邊的
    delegate；engine-bridge 現在做普查與匯出端。在那之前含當場招式的匯出照規則被拒。
  - engine-port 另回報：我提的三個缺口已補（commit 8029dedd）；翻譯器新增一條規則——被呼叫端會改動的集合參數要回到呼叫端
    （C# 的集合是參考），否則每隻怪的狀態機都是空的，共 96 個呼叫點。它自己的小樣本 780 份匯出全部匯得進來。
- 04:22 （協調）engine-bridge 的 delegate 普查（全量 2,160 場；`delegates.md`、`tmp/engine/delegates-combats.tsv`）與裁定：
  - 規模不大：約 440 個不同的方法，316 個是怪物身上有名字的方法；lambda 只掛在 `<>c` 或怪物自己（只抓 `this`）；閉包只有五種
    形狀；沒有多播的資料 delegate；沒有會被 lambda 改寫的區域變數。重建出來的 22,462 個 delegate 與活的完全相同、0 個不同，
    缺口只有三種當場做的下一招（`UNSET_MOVE` 412、`STUNNED` 23、`REVIVE_MOVE` 22）。上一則裁定不變：delegate 當匯出的值，
    一套機制；匯出端的寫法採 engine-bridge 的提案 `{"method","target"}`，閉包當一般物件。
  - **事件不全是呈現**（普查發現）：遊戲規則會在執行期訂閱事件——`Creature.CurrentHpChanged` → Cubex Construct、
    `Creature.Died` → Slumbering Beetle／Soul Nexus／寵物死亡、`PowerApplied／Removed` → Test Subject 等。翻譯器原本把事件
    一律當呈現丟掉，這幾隻怪的規則就不會跑。裁定：有翻譯類別訂閱的事件是狀態（訂閱者清單，照訂閱順序），`+=`／`-=` 改它、
    觸發時依序呼叫；呈現類的訂閱者依 `port.toml` 的丟棄名單略過，名單寫進 schema，兩邊都不認得的類別照舊讓匯出失敗。
  - `AttackCommand` 在決策點仍被參照（約 6,000 個）：由 engine-port 讀程式碼判斷之後還會不會被執行，會才算狀態。
  - engine-bridge 另報：樣本已照 schema 38a937d6 重倒（192 場、4,292 筆）；`IMPROVEMENT_POWER` 已遇到；四套牌組的戰鬥掃描
    4,320 場 105,063 個決策、種子 2 的 240 局 4,981 場 227,110 個決策，對重播世界都是 0 分歧；選牌提示佔整局決策的 2.8 %。
    步驟 5（真遊戲停在提示時怎麼規劃）我給了起始立場，等它的設計筆記。
- 04:25 （engine-bridge）delegate 普查補完：
  - 普查的程式已進 main：`sim parity --delegates FILE`，每次匯出時把走到的物件上所有 delegate 記一筆；配 `--world replay`
    時在每場最後一個決策把世界重做一次，讓遊戲自己的程式碼再做一遍狀態機與牌的變數，逐一和活的比。
  - 48 整局（959 場、42,846 次匯出）的普查和戰鬥掃描一致：資料 delegate 沒有新的（全在戰鬥掃描那 435 組裡），重做後
    8,124 個相同、0 個不同、249 個做不回來（`UNSET_MOVE` 234、`STUNNED` 9、`REVIVE_MOVE` 6）；多一個有規則的事件訂閱：
    `RunManager.RoomExited` → 事件 Punch Off。
  - `AttackCommand` 在決策點是從哪裡被參照的：只從能力的內部資料（Aim 408、Vigor 83、Quietus 20 筆，樣本 3,972 份匯出中
    470 份有），不在動作佇列也不在歷史裡。Vigor 在攻擊結束後不清這個欄位，之後算加傷時還會讀那個已結束指令的來源牌與
    攻擊者；沒找到會再執行它的地方。路徑已交給 engine-port 判讀。
  - 匯出端的 delegate 等 engine-port 在筆記回答四個問題、schema 有 `delegate` 種類、閉包類別與丟棄名單後再寫。接著做步驟 5 的設計。
- 04:27 （協調）子代理 engine-state 完成這一輪（讀出狀態：`protocol.rs`、`observe.rs`、`legal.rs`、`keys.rs`、`prompt.rs`、`ffi.rs`、
  `tests/`，四個 commit；筆記 `tmp/engine/notes/engine-state.md`）。驗收：
  - **第一個「引擎對真遊戲」的數字**：在補上八個缺的生成成員的 scratch 複本（`tmp/engine-state/snap3`，手寫替身只在 scratch，
    不進 git）裡，`tmp/engine/fixtures` 的 3,972 份匯出有 3,277 份通過匯入檢查——觀測逐位元組、合法動作、狀態、RunView、
    狀態鍵、隱藏鍵都與真遊戲相同；其餘 695 份全是匯入被拒或撞到 `todo!`，沒有任何一份是「算出來不一樣」。我重跑過，數字相同。
    它另報整局與針對性樣本合計 6,613 份通過、同樣 0 個差異（這部分我沒重跑）。
  - 在現在的生成碼上是 0 份通過：每筆都停在翻譯器還沒翻出的成員（`CombatHistoryEntry.HappenedThisTurn`、
    `DynamicVarSet.Values`、`PowerModel.GetInternalData<T>`、`FoulPotion.PassesCustomUsabilityCheck`，後面還有五個）或匯入被拒
    （Bygone Effigy 的狀態機 592 份；當場做的下一招：Corpse Slug、Ceremonial Beast、Thieving Hopper、Eye With Teeth）。
  - 只寫了、還沒有任何一次執行走到的：選牌提示那條路、`relics[].melted`、Pael's Tooth 的 `relics[].cards`、提示中與戰鬥結束後
    的合法動作。速度：寫一份觀測 1.6 µs、算狀態鍵 1.7 µs（它量的；C# 現在建一份觀測 254 µs）。
  - 走步與續打 0 通過：引擎的 `apply` 進得了翻譯出來的規則，停在 engine-port 的 `todo!`（`Creature.LogName`、
    `EnergySpentEntry.ctor`、`end_turn` 後玩家為 null 等）。
  - 已轉給 engine-port：上述成員與匯入被拒的清單、commit `world.rs` 並把 `ffi.rs` 的引擎型別換成它、提示要帶的欄位、
    回合上限（FightCap）。已轉給 engine-bridge：模擬器自己的提示計數要隨匯入進引擎（匯出加第七個頂層成員）、
    鍵回傳 0 時查錯誤、補「熔掉的遺物」與「Pael's Tooth 帶牌」兩種樣本、一份常停在選牌提示的樣本。
- 04:40 （協調）步驟 5（SL 規劃接到引擎上）的設計：engine-bridge 寫在它的筆記 `engine-bridge.md`「Step 5」，我接受並裁定三個問題。
  - 設計：同一個 `SlPlanner.Search`，只換 `TurnSearcher`——在兩個動作之間的決策點由主機給出匯出（`CombatDecision` 多一個
    延後取用的 `export`），匯入一次，每個工作複製根世界、套用路徑、跑一回合的搜尋，同一次呼叫的工作在本行程的多條執行緒
    跑、結果依工作順序。`EnginePlanner` 與 `PooledPlanner` 並排在 Harness。
  - 根在選牌提示上的計畫（引擎只能在兩個動作之間匯入）：三種情況——戰鬥一開場就是提示（隨機玩家的 48 局裡 959 場有 31 場）、
    計畫在回合結束引出的提示上被截斷、某一步在提示上對不上。裁定：先做 (A)，這類計畫照舊交給重播世界的規劃器，不寫新
    程式、結果精確；等引擎跑起來後量冠軍自己的對局裡這類計畫佔多少 CPU，佔比大才做 (B)（在複本上跨過提示、每個答案一次
    工作請求，之後回到引擎）。我先前提的「在引擎裡重新套用＋用網路直接選」撤回：它是第二套機制，而且網路估的是狀態不是答案。
  - 選項 `--combat-world replay|engine`，預設 `replay`，不進冠軍名稱、記在對局的選項裡。**預設在本計畫結束前不改**：其他
    工作階段拿 HEAD 跑訓練與測試，切換要在收尾時告知用戶後才做。不碰 `eval/champions/`。
  - 驗收條件：同一批種子兩種世界各跑一次，決策紀錄逐筆相同（畫面、觀測雜湊、動作、計畫、候選與估值），兩邊重新規劃次數
    都是 0，而且引擎世界用 1 條與 N 條執行緒的紀錄相同；比較工具要進 repo（每次遊戲改版都會再跑）。
  - engine-bridge 另報：四套牌組的戰鬥掃描加上新的遺物抽法後 4,320 場、104,924 個決策，對重播世界 0 分歧。
- 06:00 （engine-port）里程碑 2 之後：**每一場戰鬥都能從匯入打到結束，而且每一步都與真遊戲相同**。commit 203ea647、14eae2d5、
  e276a9d7、75ac7f53、9ae5da9e、af19ccf5、6f53e433、48260d96（都已推送）。筆記 `engine-port.md` 已重寫。
  - 檢查用的掃描：`sim parity --world none --dump --encounter all`，遊戲的 89 個遭遇 × 6 個角色 × 起始牌組與抽出的牌組各一，
    共 1,068 場、25,979 個決策（716 張牌打出過 671 張、286 個能力碰到 281 個、316 個遺物碰到 295 個、113 種怪全碰到）。
    A10 種子 1 那一輪：1,068 場全部打到結束；走步 25,979 筆全部相同；在每個決策點匯入再照紀錄打到底，805,249 步全部相同。
    另一輪 A0 種子 2：1,067 場打到結束、27,693 步相同。兩輪都沒有任何一步「算出來不一樣」。
  - 唯一還進不來的：匯入時怪物的下一招是當場做出來的（暈眩招，`CreatureCmd.Stun` 做的 `MoveState` 不在怪物的狀態機裡）。
    A10 那輪 24,883 個決策點匯入通過、332 個被拒（14 種怪），A0 那輪有一場開場就是這種情況。這要等 delegate 成為值。
  - 這一輪找到的真實差異都出在翻譯器，全部修在翻譯器裡，沒有手寫任何內容：
    (1) 用 `yield` 寫的方法原本一次算完；遊戲裡序列和走它的迴圈是輪流執行的，所以 `IterateHookListeners` 是走到每個
    監聽者時才檢查它還在不在。現在這類方法多一個逐項交給迴圈主體的版本，`Hook` 的 183 個迴圈都走它。
    (2) 判斷「被略過的東西裡有沒有遊戲邏輯」有三個漏洞：同時帶遊戲物件和呈現參數的 void 呼叫（The Devil 升級它打出的牌
    被略掉）、呼叫一個傳進來的函式（`CreatureCmd.Stun` 包裝裡的 `await stunMove(c)`，所有暈眩招自己的邏輯都被略掉）、
    只剩一個由呈現決定的判斷的成員。
  - `SavedProperties`（戰鬥結束把牌加進牌組時寫進歷程）：port 把每個類別的 `[SavedProperty]` 屬性寫成表（`gen/saved.rs`），
    兩個用反射的成員手寫在這張表上；它的七個清單成為狀態，泛型 struct `SavedProperty<T>` 的每種用法在 schema 裡各是一個
    struct。**schema 雜湊因此改變（b15dd701 → f79a712b），之前的 dump 全部過期**；現有的匯出端不用改就能照新 schema 匯出
    （上面兩輪就是照它做的）。
  - 回合上限（第 81 回合殺死玩家）已在 `world.rs`，與模擬器的 FightCap 同一時機。
  - 已知、尚未遇到的差異來源：C# 走到才求值的 LINQ 鏈在這裡是寫下時就求值；port 的報告現在列出這類迴圈（15 個）。
  - port 的數字：翻譯的類別 2,137、成員 14,277，未翻譯 266（都是 `todo!`，兩輪掃描沒有走到任何一個），rustc 拒絕 0，
    stub 377，手寫成員 30，呈現成員 1,323。
  - 接下來（協調排的步驟 b）：delegate 成為值（`Fn0..5` 是函式編號加目標、依 `ldftn` 順序的表、閉包類別、schema 的
    `delegate` 種類），然後事件是 delegate 的清單，`manual/delegates.rs` 與 `card_pile.rs` 隨之拿掉。
- 06:36 （engine-port）步驟 b 的引擎端：**delegate 是值了**。commit 4ef66743、30c60a3f、8f669c74（都已推送）。
  - `FnN` 現在是「表裡的第幾個函式＋它綁定的物件」，可比較、可匯出；`gen/fns.rs` 有 516 個函式和一張以遊戲自己的方法名
    （`<CLR 類別>::<方法>`）排序的表。方法群組直接有名字；lambda 與區域函式在反編譯原始碼裡沒有名字，port 讀 `sts2.dll` 的 IL
    （成員裡每個 `ldftn` 依程式順序對上原始碼的第幾個 lambda）取得編譯器給的名字。
  - 捕獲區域變數的 lambda：編譯器替那個範圍做的類別（`<>c__DisplayClassN_M`）成為引擎的類別、各有自己的 arena（25 個，29 個
    lambda 綁在上面），delegate 的目標就是那個物件。暈眩招的包裝（`CreatureCmd.Stun` 的 `Wrapper`）因此是資料。
  - schema 的 `delegate` 種類、閉包類別、匯入端的讀取都寫好了，但放在開關後面：`engine/port.toml` 的 `[delegates] exported`，
    **目前是關**。關的時候 schema 與之前逐位元組相同（f79a712b，現有 dump 都還能用），匯入照舊重做 delegate；開了之後 schema
    多 18 個 `delegate` 欄位與 25 個閉包類別。要開，只差匯出端寫出 `delegate` 這一種值（engine-bridge 的檔案；要寫什麼在
    筆記 `delegates.md` 最後一節）。開了、重新 dump 之後，那 332 個被拒的匯入（暈眩招）應該就進得來，`manual/delegates.rs`
    與開關一起刪掉。
  - 不靠匯出端能驗的都驗了：engine-bridge 普查（2,160 場戰鬥＋48 局整局）看到的每一個資料欄位裡的方法，表裡都有
    （427/427、416/416；原本缺一個純呈現的 `Rocket.RechargeMove`，現在是表裡一個什麼都不做的函式）；開關開著引擎能編譯；
    讀取器有單元測試。開關關著，掃描結果與之前相同：1,068 場全部打完、走步 25,979 筆全同、匯入 24,883 過 / 332 拒、
    續打 805,249 步全同。
  - 事件（訂閱者是遊戲邏輯的那 8 個）還沒做：等開關開了之後做成 delegate 的清單，`manual/card_pile.rs` 隨之拿掉。更正先前
    的說法：`CombatStateTracker`、`CombatReplayWriter`、`RewardSynchronizer` 目前不在 port.toml 的 drop 清單上（只是在翻譯
    範圍外），到時會放上去，schema 也會帶 drop 清單讓匯出端跳過它們的訂閱。
  - 新工具 `engine/reach.py`：從手寫程式與產生的表出發，沿著產生的程式碼列出「戰鬥走得到、但還沒有程式碼」的成員。
    現在是 657 個沒有程式碼的成員裡 34 個走得到（靜態、寧多勿少；多數是地圖動作、戰鬥外取得遺物）。接下來照這張表補翻譯。
