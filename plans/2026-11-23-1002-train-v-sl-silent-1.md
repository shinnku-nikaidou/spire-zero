# 訓練 v-sl-silent-1：用 sl-silent-g0 已有的局先訓一次（台灣時間 2026-11-23 10:02 起草）

起草於 2026-11-23 10:02（台灣時間），在「訓練 v-sl-silent-1」這個 session（由進度 session 開的）。

用戶的話（2026-11-23）：

- 「我覺得我已經可以停下來嘗試訓練了啊,新開一個session專門負責訓練」：生成照常跑到 40,000 局，現在先用已有的局訓練；
  之後局數多了可以再訓一次。
- 「訓練的時候停掉pushqkzh」「不然會搶cpu」

之前定的（plans/2026-11-20-1231-silent-shiv-poison.md 2.6、plans/2026-11-22-0445-sl-silent-g0.md 10）：

- 「訓練只用這一代」；「我們這一批只訓練獵人, 之後所有神經網路分開了」。

## 0. 一頁看完

- **清單**：sl-silent-g0 在 hub 上已完成的局的快照（約 13,000 局，只有 SILENT），不合併 d2 的舊清單（1）。
- **起點**：打這批資料的那一包 v-sl-silent-0：V v-sl-silent-0（d2 的 V，SILENT 的 30 張卡重置）、c c-sl-12-1-d2、
  f f-sl-12-1-d2。不從零開始。
- **V**：多步目標 n = 4（`--bootstrap FA,FB --horizon 4`），兩個摺疊 V 從 v-sl-silent-0 在同一份清單上訓練（2）。
- **出來的一包**：v-sl-silent-1 = V v-sl-silent-1 + c c-sl-silent-1 + f f-sl-silent-1，一起訓練、一起測試、一起晉升或放棄。
- **機器**：pushqkzh 的兩張 4080。訓練時 pushqkzh 從池子拿下來（用戶的話），訓完放回 13 個執行緒；其他機器照常生成（3）。
- **留出**：run id % 5 == 0；摺疊 A 是 {1, 2}、B 是 {3, 4}（4）。
- **測試**：訓完就測（記憶 test-after-training）：v-sl-silent-1 對 SILENT 現在的冠軍 v-sl-silent-0（sl.json 從 11-22 起
  每個角色自己的 V，c、f 是 d2 的），只打 SILENT，xhigh（用戶 11-23：「medium? xhigh」，和生成同一個深度），promotion-1-200，
  A10、恐懼、A0 各 200，兩包同一個 rev 一起打，報配對勝場（5）。晉升等用戶。

## 1. 清單

- **快照**：`pull.sh pushqkzh` 把 sl-silent-g0 的 60 個 job 從 hub 拉到 pushqkzh（只讀 hub，不動正在跑的 step；
  沒有 done- 的 chunk 不拉）。拉完那一刻 `train.sh lists` 列出的 log / rec 檔就是這次的清單，之後到的 chunk 不算。
  局數、勝場寫進 ledger。
- **不合併 d2 的舊清單**（約 125,000 局、六個角色）：
  - 用戶定過「訓練只用這一代」，而且之後每個角色的網路分開。
  - 這一批的目的是糾正 V 對小刀等牌的偏見（小刀與毒計畫）；舊清單是有那個偏見的 medium 打的，放回去會把偏見帶回來。
  - capacity-test-merged 量到的是共用網路裝得下多代的好處；這裡是單一角色、要改掉舊打法，情況不同。
  - 以後 sl-silent-g0 打完再訓時也一樣只用這一代（局數變多）。
- **兩個 rev**：這一步的局在 8e1e6d4b 和 8acb2510（2026-11-22 11:55 UTC 起的早停修正）打的。訓練用 8acb2510 的 learn/
  和編碼器（HEAD 之後只改了 mod 的檔）。先在 60 局的 mini 清單上，用 8e1e6d4b 和 8acb2510 的編碼器各訓一個 epoch，
  V 和 c 的 epoch 檔要逐位元相同（`train.sh check`，CPU，幾分鐘）。

## 2. 訓練

照 NoSL v0-nosl-2 那次的方式（`tmp/train-silent-1/train.sh`，在 pushqkzh 的
`~/sts2/data/silent/sl/forks/2026-11-23-train-v-sl-silent-1/`）：

- GPU 1：摺疊 V v-sl-silent-1-fb（`v.train --init v-sl-silent-0 --fold B`），然後 c-sl-silent-1
  （`combat.train --init c-sl-12-1-d2`），然後 f-sl-silent-1（`f.train --init f-sl-12-1-d2 --combat-net c-sl-silent-1`：
  f 的標籤用新的 c 算，因為要和它一起打）。
- GPU 0：摺疊 V v-sl-silent-1-fa（`--fold A`），等 fb 結束後學生 v-sl-silent-1
  （`v.train --init v-sl-silent-0 --bootstrap fa,fb --horizon 4`）。
- 每個訓練器一個看守：記憶體超過上限或主機剩不到 6 G 就停掉它。
- 估計：v0-nosl-2 那次 72,000 局，摺疊 18 分、學生 37 分、c 22 分；這次約 13,000 局，全部約 1 小時。
- **n = 4 和小刀與毒 2.6 的差別**：2.6 原本寫 V 用真實結局，理由是 td4 從第 4 層以後的標籤來自凍結的 V，而凍結的 V
  討厭小刀。這次的摺疊 V 是在這一批 xhigh 的局上訓練過的（另一摺的局），不是 d2 原樣；而且從 v11-0-td4 起 n = 4 一直
  比真實結局好（v11-0-td4 對 v11-0：A10 +11.3 點）。所以照 n = 4。用戶要真實結局的話，學生改成不加 `--bootstrap`，
  摺疊 V 不用訓。
- 訓完：權重和報告拿回本機的 `weights/all/`（不進 git）；`weights/put.sh` 和 GitHub release 等用戶的話。
  每個 V 報 `potion_below_empty`。

## 3. 機器

- pushqkzh：訓練前 `box.py threads pushqkzh 0`（在打的局退回 hub 重發，不浪費），訓完 `box.py threads pushqkzh 13`。
- pusheen 不用：兩張 4080 就夠，pusheen 繼續生成。
- 本機不跑任何東西。每次用機器在 `tmp/compute-timetable.md` 記一列。
- 一次最多 2–3 小時：這次訓練約 1 小時，測試在 xhigh 約 2 小時（池子每小時約 620 局 xhigh），各是一批。

## 4. 留出

- held-out：run id % 5 == 0（v/data.py 的分法），V、c、f 都一樣。摺疊 A、B 各是 {1, 2}、{3, 4}。
- 分法只看 run id，所以以後清單變大，這次留出的局還是留出，兩次的留出損失可以直接比。

## 5. 測試

- 一個 step `test-v-sl-silent-1`，在訓完時 HEAD 的 rev，沒有期限（每顆種子回來才結束，兩邊完整配對）。
- 兩包：冠軍（`eval/champions/sl.json` 給 SILENT 的：V v-sl-silent-0 + c-sl-12-1-d2 + f-sl-12-1-d2；小刀與毒 2.7 寫的
  「對 d2」那時 SILENT 還沒有自己的 V）和 v-sl-silent-1（`--value`、`--combat-net`、`--f-net` 換成新的三個），
  `--effort xhigh --workers 4`。看第一批 record 的 champion 字串確認兩邊各打自己的網路。
- 只打 SILENT：A10、恐懼（`--mode dread`，A10）、A0，各 200 局，`eval/seeds/promotion-1-200.txt`，
  job `silent/sl/eval/2026-11-23-{a10,a10-dread,a0}-{v-sl-silent-0-champ,v-sl-silent-1}`，共 1,200 局。
- 測試種子是 promotion-1-200，訓練是 6300000 起的 train 種子：不相碰。
- 報告：每種配對勝場（新 − 冠軍、± 標準誤），先講勝場。sl.json 按角色寫網路，`sim promote` 不寫這種檔，所以
  晉升是用戶說了以後手改 `characters.SILENT`（和 c、f）。

## 6. 步驟

1. 快照、清單、mini 檢查（CPU，小）：已可先做。
2. **訓練**（用戶同意才開）：pushqkzh 下池子，`train.sh all`，訓完放回池子。
3. 測試：訓完就開（第 5 節）。
4. ledger：每個網路檔一列，測試一列。
