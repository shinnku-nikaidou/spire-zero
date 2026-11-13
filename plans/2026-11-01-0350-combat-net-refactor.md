# 重構：只留 v7-1f 和 c7-2

台灣時間 2026-11-01 04:10 更新：使用者改了決定，SL 和 NoSL 都升級到 c7-2，其他網路全刪，所以下面第一版裡「c2 的計數路徑要留」已不成立。基準是 `93ffd16`。

## 實際做的事

1. 升級：兩個冠軍檔改成 v7-1f + c7-2；拿掉「網路只能在自己環境玩」的檢查（c7-2 的 `env` 是 SL，NoSL 也要用）。`report` 欄位先不動，等 c7-2 gate 的報告。
2. Python 戰鬥：拿掉 worth，報告搬到 `combat/report.py`。
3. C# 戰鬥：拿掉 worth、V 價格、藥水計數、c1–c4 的輸入；`Graph` 依 `encode.py` 的順序拆開。
4. V：拿掉 V v0（含日誌裡的 `features` 和 `weights` 指令）、V v2、encoders、progress 網路；`--macro l0` 現在一定要給 `--value`；一併拿掉 `--leaf FILE`。
5. 權重：`weights/silent/` 只剩 v7-1f 和 c7-2；被忽略的 `history/`、`nosl/` 是完全相同的副本，一起刪。

## 問使用者才動的部分

既不是網路、也不一定還需要：`bosslab.py` 和 `v.train` 的 boss 資料選項、`run --auto-win`、`run --gauntlet`、`learn/challenge.py`、`--objective floor`。

---

以下是第一版，留作紀錄。

# 戰鬥網路重構：以 c7-2 為準

待使用者核准（台灣時間 2026-11-01 03:50）。還沒改任何程式。基準是 `93ffd16`。

## 目標

讓 `learn/combat/` 和 `CombatNet.cs` 讀起來就是 c7-2，其他網路留下的東西只剩一眼看得出來的幾行。網路結構、環境輸入輸出、c7-2 的打法都不變。先改程式，再由 docs-rewrite 引用行號。

## 查到的事實

用這份計畫之前要先知道的四點，第二、三點和原本的預想不同。

1. **c7-1v 的路徑現在就能刪。** 它的 gate 已經失敗（`b5bc2bd`），沒有任何冠軍檔或工作用它，Python 測試也沒有測它。
2. **c2 的藥水計數路徑，SL 升級之後也刪不掉。** `champions/nosl.json` 也用 c2，c7-2 計畫也寫明 NoSL 留在 c2，而 NoSL 的路徑要保留。所以它要留到 NoSL 有自己的新網路，不是等 SL 升級。它只有幾行，下面會用 `OLD NETS` 標好。
3. **c2 的輸入全部包含在 c7-2 的 43 個之內**（讀網路檔的 ONNX 比對：c2 有 25 個，沒有多出來的）。多出來的 `card_scalars`、`vars`、`var_values`、`var_mask`、`orb_values` 只有 c3、c4 才讀，兩者都不是冠軍，所以這些現在就能刪。程式註解說 `NetNumbers` 的 costs、relic_values 等是「c1–c4 的」，這已經過時：c7-2 也靠它們餵，照註解清掉會弄壞 c7-2。
4. **`--leaf FILE` 沒人用了。** 兩個冠軍檔都沒有它，調這些權重的 SPSA 程式也不在了，只剩註解提到。

## 要做的事

每項一個 commit。每個 commit 都先看 diff 有沒有別的 session 的改動。

### 1. Python：拿掉 worth

- `data.py`：拿掉 `WORTH_CAP`、`fight["before"]`、`r["view"]`、`read` 的 `value`、樣本的 `worth`，並改寫開頭的說明。
- `net.py`：拿掉 `Net(worth=)`、`is_worth`、`Batch.worth`，`forward` 和 `loss` 不再分支。參數建立順序不動，同樣的 seed 初始權重相同。
- `train.py`：拿掉 `--value`、`run_value`、`FLOORS`、對 `v` 的 import，以及報告裡的 worth 分支。
- `export.py`：拿掉 worth 分支和 `AXES` 的 `worth`；網路檔不再寫 `round`（C# 第 3 項起不讀）。
- `encode.py`：修掉提到已不存在的 `CombatNet.Predict` 和「older nets」的註解。

### 2. Python：報告搬出 `train.py`

把 `ranks`、`rank_within`、`auc_within`、`potion_rates`、`log_loss`，以及現在包在 `main` 裡的 `predict`、`potion_report`、`summary` 搬到新檔 `combat/report.py`。`train.py` 只留：讀資料、詞表、訓練、報告、寫檔。報告內容不變。

### 3. C#：拿掉 worth 和 V 的價格

- 刪 `PricedLeaf.cs`、`EndPrices`、`SlTurnRequest.Prices`、`PooledPlanner` 的 `prices`、`Serve.Priced`、`ChampionOptions.VPrices`（含名字裡的「v prices」）、`--leaf-prices`。
- `CombatNet` 拿掉 `IsWorth`、`Worth()`，`CombatNetFile` 拿掉 `WorthCap`，測試狀態拿掉 `Worth`。
- 刪 `combat-worth-tiny.json` 和它的三個測試（`Dip` 也一起）。
- 改 `.claude/skills/sl-selfplay/SKILL.md` 第 25 行，拿掉 `--leaf-prices v`。

`--combat-net` 和其他選項不變。

### 4. C#：`CombatNet` 只剩 c7-2 的讀法

- `Graph` 現在一個方法 130 行。依 `encode.py` 的順序拆成 `Cards`、`Player`、`Enemies` 等小方法，和現有的 `Allies`、`Bag` 同樣寫法，方便文件逐段講解。
- 拿掉 c3、c4 才讀的五個輸入，也拿掉永遠是 true 的 `_file.Round`。
- `costs`、`mod_amounts`、`relic_values`、`relic_status` 改由 `CombatNet` 自己餵。`NetNumbers.Cards`、`Relics` 只留給 V v2 用，註解跟著改對。
- 類別和參數的說明改寫成以 c7-2 為主。
- 新增測試：載入真的 `weights/silent/c7-2.json`，比對它的 16 個測試狀態。

留下、標 `OLD NETS` 的只有 c2 要的部分：藥水計數（`Load`、`Value` 各一處）、`NetNumbers.Declared`（c2 少 18 個輸入）、`Index` 對缺表的容忍（c2 少 3 張表）、`Env` 可以是空的。標註條件改寫成「NoSL 有自己的網路之後」。

`combat-sl-tiny.json` 留著：它是唯一有盟友狀態的 fixture，也測計數路徑。

### 5. C#：拿掉 `--leaf FILE`

拿掉 `LeafFile`、`LeafChoice` 的權重部分、`CombatNet.Load` 的 `weights`，以及 `weights --leaf`，並改 `CliTests`。`LeafValue` 和 `HandLeaf` 留著：沒有 `--combat-net` 時的預設葉節點，其他角色從零開始也要用。

### 6. 收尾

- 通知 docs-rewrite 程式已落地，它再填行號。
- 通知 a10-dataset-prep 哪些選項不見了（`--leaf-prices`、`--leaf`、`combat.train --value`），這些 A10 都用不到。
- 程式裡有 22 處引用舊文件的章節（`docs: learning/combat-net §2` 這類）。文件改完章節會變，等 docs-rewrite 給出新對照後，我再用一個 commit 更新。

## 不做的事

- 不改網路結構、輸入輸出、損失、資料，不訓練，不跑 fleet 工作。
- 不動 NoSL 的路徑：PIMC、`LeafChoice.Check`、`--env NoSL` 的訓練都留著。
- 不動 `docs/`、不刪權重檔、不改帳本歷史。
- 不升級冠軍。升級由 `討論怎麼訓練 c7-1 戰鬥大腦` 那個 session 做。升級後要把 `tasks.sh:37`、`bosslab.py` 的預設值、`sl-selfplay` 裡寫死的 `c2.json` 改成 c7-2；這是獨立的小 commit，等升級後再做。
- 不動巨集端的 `OLD NETS`（V v0、V v2）。目前沒有冠軍用 V v2，但那屬於 V 的程式，也會動到決策日誌的欄位，這次不碰。

## 怎麼確認打法沒變

1. **測試**：每個 commit 都跑完整的 `uv run pytest` 和 `dotnet test`。
2. **訓練不變**：用 `test_combat.py` 的合成資料和固定 seed，在 `93ffd16` 和新版各訓練一次，比對網路檔的 `tests`、`fit` 和 ONNX。
3. **c7-2 的決策不變**：用 `tmp/build.sh` 建 `93ffd16` 和新版，同樣的選項各跑一次，比對決策日誌和紀錄。日誌裡有候選動作的值，所以葉節點的值也一起比到了。
   - SL：c7-2 + V v7-1f + clone，`promotion-1-300` 前 3 個 seed，低預算和 medium 都跑。
   - NoSL：`champions/nosl.json`（c2），前 2 個 seed，確認計數路徑和 c2 的輸入沒壞。
   - 舊版先同一個 seed 跑兩次，確認它本身是確定性的；不是的話先查清楚再比。
4. 這是本機的小型檢查，不佔 fleet。超過五分鐘的部分，會在 `tmp/compute-timetable.md` 記一列。

全部相同才推送；有差異就停下來查，不用「差不多」帶過。

## 要你決定

1. **第 5 項（拿掉 `--leaf FILE`）**：我建議做。它不是 c7-2 專屬的東西，你也可以先留著。
2. **不再能載入的權重檔**：做完後 c7-1v、c3、c4 在新版載入不了（c1 現在就載入不了）。git 裡的舊版仍然能跑。我的預設是檔案留著不刪，帳本的紀錄也不動。
