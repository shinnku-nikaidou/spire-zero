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
