# 書：V 的多步目標，和換冠軍之後的數字（台灣時間 2026-11-16 18:03 起草）

**A 部分做完（18:12），B 部分等報告。** 用戶 18:05 說「這個 session 寫」；冠軍的報告（4.1）和 `plans/` 的引用（第 6 節第 3 點）還等用戶。進度在最後一節。

## 1. 為什麼

- 冠軍換成 v11-0-td4 + c11-0（用戶 2026-11-16 決定）。V 的訓練答案變了：每個決策近 4 層用真結果，4 層之後用一個沒看過這一局的 V 的估計。
  書裡有幾句因此變成錯的（第 3 節）。
- 冠軍換了，書裡屬於冠軍的數字和圖的資料也要跟著換（`docs/README.md`：「After a promotion those sentences change together with
  the figures' data」）。這部分在第 4 節，前提是冠軍要有報告。
- `pnpm check` 現在不過（18:02 跑的，107 個問題）：
  - 1 個是換冠軍造成的：`v-walk.json` 還是 v11-0 做的。它的值是 0.000444，v11-0-td4 的第一個測試局是 0.000541。
  - 106 個是 `plans/` 的舊計畫引用了書（`docs: PAGE §N`），跟這份計畫無關（第 6 節第 3 點）。

## 2. 這次要特別守的書規（`docs/README.md`）

- **不寫歷史。** 不寫 td4、v10-0、摺疊 V 的名字，不寫世代名、日期，也不寫「以前／不再」。也不寫「實驗決定了什麼」，所以不寫
  「多步目標的 V 比只用結果的 V 多贏 11.3 個百分點」。設計理由照寫，但要寫成論證（X，因為 Y）。
- **不加新詞。** 固定詞彙表不動：「凍結 V」、「摺疊 V」、「老師」都不進書，改寫成白話的「一個沒看過這一局的 V」，每章第一次用時說清楚。
  理由：讀者需要知道的只有這個性質，不需要叫得出名字；要加新詞就得改 README 的詞彙表。
- **每章獨立。** 用到多步目標的章，各用一句話重講，不互相指。
- **大小有空間。** 段落上限英文 120 字、中文 200 字；每章 6,000 字；全書 55,000 字。現在全書繁中 19,465 字，value-net 約 1,860，
  learning 約 1,370。
- **語言順序。** 先寫英文，再帶到 zh-cn、zh-tw，標題、段落、數字都一樣。繁中用台灣用語和遊戲的名字（頭目、菁英、晉階）。
- **只改變成錯的句子。** 不加程式的事實（檔名、選項、欄位）。

## 3. A 部分：V 的答案（變成錯的句子）

下面的英文是草稿，寫的人可以改字，但事實要照這裡。每段都照 `learn/v/bootstrap.py` 和 `learn/v/net.py` 的 `boot_targets` 核對過：

- n 數的是樓層。
- s' 是同一局裡、在 f + 4 層或更後面的第一個決策。
- s' 之前的每一層標成「活過」（目標 0、權重 1）。
- 從 s' 起，每一層的目標是那個 V 在 s' 給的風險，權重是它估這局活到那一層的機率。
- 沒有 s' 的決策用真結果。
- 留出局永遠用真結果。

### 3.1 learning

1. **frontmatter 的 description**：「...explores on purpose and labels every position with the real outcome, and...」變成錯的。
   草稿：「...explores on purpose and labels every position with what happened next, and...」
2. **開頭段**：「Each recorded position is labelled with what really happened afterwards.」對 V 變成錯的。草稿：
   「Each recorded position is labelled with what really happened next: for V, the next four floors, and past them the
   estimate of a V that never saw the run.」
3. **「Labels: what really happened afterwards」一節**：
   - 標題改成「Labels: what happened next」。
   - V 那一條改成：「**V's labels**: each decision outside combat is labelled with its next four floors as they really
     went, and from there on with the risks that a V which never saw the run gives the run's position four floors later.
     A decision less than four floors before the run's end is labelled with where the run really ended, or with a win.」
   - 戰鬥網那一條不變。
   - 「Both use real outcomes only. A network's own estimates never come back as labels.」變成錯的，換成：「The V that labels a
     run must never have seen it. A V learns its training runs' outcomes within one pass, so its estimate at a later
     position of such a run would hand the outcome back. So the training runs are split in two halves, a V that never
     sees the other half is trained on each, and each half is labelled by the other half's V. The runs held back for
     checking keep their real outcomes.」
   - 要守的是「沒看過另一半」，不是「從沒看過任何一局的網路起」：之後的世代，每一半的 V 從自己上一個接著訓練，還是只看過自己那一半。
4. **「Hundreds of decisions, one outcome」一節**：「The project does four things about this at present」改成 five，多加一條：
   「**Four floors of what happened, then an estimate.** The luck after a decision's next four floors stays out of its
   label, so decisions four floors apart no longer share their noise: a run gives about 9.6 independent labels instead of
   about 1.2. The price is that, past four floors, a label is only as right as the V that gives it.」
   - 「Labels like this have no bias but a lot of noise」講的是整局結果，還是對的，留著。
   - 1.2 和 9.6 是在留出局上，用一個和冠軍一樣好的 V 估遠處量出來的。實際給標籤的兩個 V 只訓練了一半的局，略弱，它們給的數沒有另外量。
     所以寫「about」；要不要四捨五入成「about 1」和「about 10」，由寫的人決定。
5. **查過、不用改的**：
   - 流程圖的「Record every position and the real outcome」：記下來的照樣是真結果。
   - 「Who plays」一節。
   - 「Continuing from the champion」的三段：學生照樣從之前的網路接著訓練，不是從零開始。「Older batches are a trade-off」也還成立：舊打法
     照樣進到真實的 4 層，給遠處標籤的 V 也學過同一批局。
   - 「Labels are given by floor」那一條。

### 3.2 value-net

1. **「How it learns」第一段**：整段變成錯的，結尾還有「The labels use only real outcomes, never the network's own estimates」。草稿：
   「Each recorded decision is one sample, labelled floor by floor. The floors before the run's position four floors on
   are labelled 0, since the run lived through them. From that position on, each floor's label is the risk that a V
   which never saw this run gives there, weighed by that V's chance that the run is still alive on that floor. A decision
   less than four floors before the end keeps where the run really ended: survived floors 0, the floor of death 1, and
   every floor 0 in a won run.」
2. **新加一段（為什麼這樣算剛好、為什麼是 4 層）**：
   「This is the loss of a real ending, averaged over where the other V expects the run to end from the later position: a
   V certain of the real ending would give the real label back. Four floors balances two errors. Fewer floors leave more
   of each label to the other V's mistakes, and more floors leave more luck in it.」
   - 「為什麼不能是看過這一局的 V」，learning 那章講過。這章要獨立，所以要用一句話重講：「The V that labels a run never saw it,
     or its estimate would hand the run's outcome back.」
3. **「one run, one vote」和第二個頭的兩段**不變。
4. **數字段**：「the error of its predicted chance of winning is 0.084, against 0.144」改成 0.083 對 0.144（v11-0-td4 的 Brier
   0.08334；只看樓層的基準 0.14429）。「about 69,000 runs and about 10.6 million samples」不變。分數是在留出局的真結果上算的，
   這一點要在這章說到（第 1 段的草稿可以接一句「The runs held back for checking keep their real outcomes」）。
5. **NetWalk 那句**（56 %、9 %、0.04 %）屬於 B 部分：`v-walk.py` 用 v11-0-td4 重做之後照新數字改。v11-0-td4 在這個局面的勝率是 0.054 %。

### 3.3 idea

- **「Learn: from its own runs」**：「When a run is over, the AI labels each of its positions with the real outcome: the floor it
  died on, or a win.」對 V 變成錯的。草稿：「When a run is over, the AI labels its positions with what really happened next:
  for V the next four floors, and past them the estimate of a V that never saw this run; for the combat net, how each
  combat ended.」

### 3.4 limits

1. **「A run has only one outcome」那一條**：對冠軍的 V 不再完全成立。草稿：「**What a decision does more than four floors later
   comes through another V.** That V learns from whole runs, where hundreds of decisions share one outcome, so a slow
   consequence it does not see, V does not learn either.」
2. **「Exploration is mixed into the labels」那一條**：「The same position gets a lower label in runs that explore more」只剩
   近 4 層成立。草稿：「**Exploration is mixed into the labels.** A random draw in a decision's next four floors lowers its
   label, the V that labels the rest learned from runs that explore, and V does not know how much a run explored.」
3. **「Not built yet」的「Search results as labels」**：「Today the only labels are the real outcomes of runs.」變成錯的。草稿：
   「Today the labels are real outcomes and, for V past four floors, the estimate of another V.」
4. 冠軍的數字（A10 約四分之一、愚者 1 / 200、Necrobinder 31 / 200、912 局裡 590 局死在頭目）屬於 B 部分。
5. **查過、不用改的**：「The networks have not learned the Dread mode」。v11-0-td4 是搬過來的，沒訓練過 Dread 的局，這句照樣對。

### 3.5 查過、不用改的章

- combat-net：戰鬥網沒換，它的標籤照樣是戰鬥的真結局。
- environments、macro、combat、combat-hidden、game、real-game：沒有講到 V 的標籤怎麼來。它們引用範例局的句子屬於 B 部分。

## 4. B 部分：冠軍的數字和圖的資料

### 4.1 前提：冠軍要有報告

`eval/champions/sl.json` 換冠軍時沒有寫報告（`reports` 是空的）。以下這些都從報告來，所以要先有報告：

- `scripts/book-data.py` 的每角色勝場（首頁的卡片）、評估一章的表（Necrobinder 報告的每層死亡圖）。
- 序言的勝場（83、68、61、44、31、1）。
- 評估一章的表、「288 wins in 1,200 runs, 24%」，和「83 wins in 200 runs」那個區間的例子。
- limits 的數字。

報告的檔案歸主 session 管，我不動。有兩條路，由用戶決定：

- **(a) 用測試的局。** `test-v11-0-td4-c11-0` 裡 v11-0-td4 + c11-0 那一邊的 A10 局（每個角色 200 局，promotion-1-200），
  形狀和舊報告一樣，不用再打。但這一邊的 A10 有 15 局失敗（harness 錯誤）。書自己的規則（評估一章的「Which results do not count」）
  說失敗的局必須是 0，不然整組結果作廢，所以不能直接用。
- **(b) 在 HEAD 為冠軍打一次報告。** A10，每個角色 promotion-1-200 的 200 局，medium，共 1,200 局，在池子上跑，要用戶點頭。
  打完要確認沒有失敗的局。

**我的建議是 (b)。** 理由：(a) 違反書裡寫的規則；而且之後的書都會用這份報告，在現在的程式上打的數字最乾淨。

### 4.2 圖的資料（報告有了之後；都在本機跑）

1. 範例局重打：Silent、seed 0SSL7NS01MU6，SL 和 NoSL 各一局，用新的冠軍檔（指令在 `book-data.py` 的 docstring）。
   V 換了，所以這一局的打法和結局都可能變。
2. 跑 `book-data.py`、`v-walk.py`。
   - 圖要顯示的決策（`PLAN_FIGHT`、`PANEL_SL`、`PANEL_NOSL`、`PANEL_MAP`、`LOG_LINES`）照同樣的用意重挑，script 的註解也這樣要求。
   - `panel-examples.json` 裡的冠軍字串（現在寫 `l0 v11-0`）會跟著重寫。
   - `v-walk.json` 重做之後，`pnpm check` 那一個問題就消失。
3. `combat-walk.py` 不用重跑：戰鬥網還是 c11-0，它也不讀 V，現在的資料照樣通過檢查。

### 4.3 跟著改的句子（圖的資料出來之後）

- 序言：每個角色的勝場。
- evaluation：表、合計、區間的例子。
- limits：
  - 「Ascension 10 is still weak. The champion wins about a quarter」照新報告改，比例會變。
  - 愚者和 Necrobinder 的勝場。
  - 死在頭目的局數。
- 範例局的句子，用同樣的用意重寫：
  - learning、macro：「193 decisions」「the run was won」。
  - macro：第 14 層的地圖和 97.95 % / 97.00 %。
  - combat：第 17 層、Ceremonial Beast、52/70 HP。
  - environments：前三個房間。
  - real-game：面板。
- value-net：NetWalk 那句。

## 5. 步驟

0. 用戶決定：誰寫（第 6 節第 1 點）、報告走哪條路（4.1）、`plans/` 的引用怎麼辦（第 6 節第 3 點）。
1. A 部分的英文（learning、value-net、idea、limits），照第 3 節。不用等報告。
2. B 部分：報告有了之後，照 4.2、4.3 做。
3. 把 A、B 帶到 zh-cn、zh-tw。
4. `pnpm check` 0 個問題。
5. commit 到 main（只放自己的檔，先檢查有沒有別的 session 的改動）並 push。
6. 部署只等用戶的話：從 HEAD 匯出、先掃主機名，再部署。

A 和 B 可以分兩次 commit。A 先上的話，書描述的是新的訓練方式，但冠軍的數字和範例局還是舊的，要等 B 補上。

## 6. 未決

1. **誰寫。** 這個 session、另開一個 session，或用戶自己。
2. **冠軍的報告**：4.1 的 (a) 或 (b)。我建議 (b)。報告檔和冠軍檔歸主 session。
3. **`plans/` 引用書的 106 處**：`pnpm check` 要求「no tracked file outside docs/ cites the book」，而 `plans/` 從 2026-11-14 進 git，
   裡面的舊計畫有 `docs: PAGE §N` 的引用。兩條路：
   - 檢查略過 `plans/`，README 那句跟著改成「outside docs/ and plans/」。
   - 改舊計畫的文字。

   **我的建議是略過 `plans/`。** 計畫是當時的紀錄，改了它們，脈絡就斷了。不處理的話，第 5 步的 `pnpm check` 不可能是 0。

## 進度

- 2026-11-16 18:03 起草。18:05 用戶：「這個 session 寫」。
- 18:12 A 部分：learning、value-net、idea、limits 照第 3 節改完，英文、zh-cn、zh-tw 都改了。
  B 部分裡不用等報告的 `v-walk.py` 也先做了：`v-walk.json` 改用 v11-0-td4，NetWalk 那句改成 58 %、8 %、0.05 %（原本 56 %、9 %、0.04 %）。
  `pnpm check` 剩 106 個問題，全是 `plans/` 的舊引用（第 6 節第 3 點）。
- 18:14 用戶選 (b)：「在 HEAD 重打報告，用high」。主 session 提醒：high 的報告，它的冠軍字串和 `sl.json` 的 medium 字串不同，
  放進 `reports` 的話，`sim gate`、`sim promote` 和排行榜會拿冠軍沒打過的報告去配對。所以 `sl.json` 不動，報告只放在
  `eval/CHAR/reports/`，`book-data.py` 改成從命令列取每個角色的報告（`book-data.py DIR REPORT.json...`）。
- 18:15 step `report-v11-0-td4-c11-0` 上池子（priority 1，沒有 deadline）：v11-0-td4 + c11-0，`--effort high`，每個角色 A10 200 局，
  promotion-1-200。範例局在本機重打（SL、NoSL）。
- 18:2x 用戶：「升級之後分別200局A0, A10, A10恐懼，作為新的gate, high+ v11-td4-0+c11-0」。同一個 step 加了每個角色的 A0 200 局、
  A10 Dread 200 局，共 18 個工作、3,600 局。這些要不要當冠軍檔的 gate 報告（也就是冠軍檔改成 high），等用戶決定；冠軍檔歸主 session。
  書的評估一章裡 gate 的形狀（A10 每角色 200、A0 主角色 200 加其他 50、medium）要看那個決定再改。
- 18:3x 用戶回答「Yes, move to high」：報告回來之後，`eval/champions/sl.json` 改成 `effort` high、`champion` 用報告的字串、`reports` 放
  六份 A10 報告（每個角色一份；A0 和 Dread 的報告放在 `eval/CHAR/reports/`，Dread 的檔名帶 `dread`）。`nosl.json` 不動
  （PIMC 沒有 effort 選項）。主 session 同意，並請這個 session 自己寫 `sl.json`。
  書要跟著改：評估一章 gate 的形狀（A0、A10、A10 Dread，每個角色各 200，high）；learning 的「Who plays」說自我對弈和評估用同樣的
  搜尋力道，現在自我對弈是 medium、gate 是 high，這句要改成事實。
- 主 session 轉達：用戶問到之後的測試是跟這些 high 局配對、還是重打冠軍時，說「肯定在新版程式上打」。所以每次測試都在挑戰者的 rev 上重打冠軍；
  這 3,600 局只是冠軍的紀錄和書的數字，不當之後挑戰者的對手。評估一章本來就寫「Both sides play again, on the same version of the code」，這句不用改。
- 20:43 報告的 3,600 局打完（rev 1324d2b1），18 份報告做好（還沒 commit）。勝場 A10 / A0 / A10 Dread：SILENT 151 / 198 / 28、
  IRONCLAD 168 / 200 / 70、DEFECT 146 / 198 / 44（199 局）、REGENT 116 / 199 / 25、NECROBINDER 104 / 195 / 21、愚者 25 / 173 / 3。
  兩個問題，報告因此還不能用：
  - IRONCLAD 的三份作廢：6 局記了遊戲錯誤「Tried to pop action GenericHookGameAction ... didn't find it in any queue」
    （A10 3 局、A0 2 局、Dread 1 局；同一個種子在 A10 和 A0 都出現）。
  - DEFECT Dread 少一局：引擎 `loop_power.rs:44` 越界，局失敗。
  兩個修正都提成各自的 session。修好之後，重打受影響的工作，再寫 `sl.json` 和書。
