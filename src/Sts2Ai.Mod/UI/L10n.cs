using System.Globalization;
using MegaCrit.Sts2.Core.Localization;

namespace Sts2Ai.Mod.UI;

/// <summary>
/// The mod's own words in the game's language: English, French, Spanish (both of the game's), Simplified
/// and Traditional Chinese, and Japanese; every other language reads English. A line is looked up by its English text,
/// which is also what a line missing from the table shows. Names (cards, relics, rooms, monsters) come from the game's
/// own tables instead (<see cref="Describe"/>).
/// </summary>
internal static class L10n
{
    /// <summary>The game's language codes, in the order of each line's translations.</summary>
    private static readonly string[][] Codes = [["fra"], ["esp", "spa"], ["zhs"], ["zht"], ["jpn"]];

    /// <summary>English → French, Spanish, Simplified Chinese, Traditional Chinese, Japanese. {0}, {1}: the arguments.</summary>
    private static readonly Dictionary<string, string[]> Table = new()
    {
        // The panel's head and messages.
        ["starting"] = ["démarrage", "iniciando", "启动中", "啟動中", "起動中"],
        ["offline"] = ["hors ligne", "sin conexión", "离线", "離線", "オフライン"],
        ["ready"] = ["prêt", "listo", "就绪", "就緒", "準備完了"],
        ["off"] = ["désactivé", "desactivado", "关闭", "關閉", "オフ"],
        ["off for this run"] = ["désactivé pour cette partie", "desactivado en esta partida", "本局关闭", "本局關閉", "このランはオフ"],
        ["run sent"] = ["partie envoyée", "partida enviada", "已发送本局", "已送出本局", "ラン送信済み"],
        ["error"] = ["erreur", "error", "出错", "出錯", "エラー"],
        ["Connecting to the brain"] = ["Connexion au cerveau", "Conectando con el cerebro", "正在连接大脑", "正在連接大腦", "ブレインに接続中"],
        ["No brain running"] = ["Aucun cerveau lancé", "No hay ningún cerebro en marcha", "大脑没有在运行", "大腦沒有在執行", "ブレインが起動していません"],
        ["{0} · trying again every 2 s"] = ["{0} · nouvel essai toutes les 2 s", "{0} · reintentando cada 2 s", "{0} · 每 2 秒重试", "{0} · 每 2 秒重試", "{0} · 2秒ごとに再試行"],
        ["Start or continue a run"] = ["Lancez ou reprenez une partie", "Empieza o continúa una partida", "开始或继续一局", "開始或繼續一局", "ランを開始または再開してください"],
        ["single-player only"] = ["solo uniquement", "solo un jugador", "仅限单人", "僅限單人", "シングルプレイのみ"],
        ["Off in multiplayer"] = ["Désactivé en multijoueur", "Desactivado en multijugador", "多人模式下关闭", "多人模式下關閉", "マルチプレイではオフ"],
        ["Brain error"] = ["Erreur du cerveau", "Error del cerebro", "大脑出错", "大腦出錯", "ブレインのエラー"],
        ["{0} · back on at the next run"] = ["{0} · de retour à la prochaine partie", "{0} · vuelve en la próxima partida", "{0} · 下一局恢复", "{0} · 下一局恢復", "{0} · 次のランで再開"],
        ["Waiting for the brain"] = ["En attente du cerveau", "Esperando al cerebro", "等待大脑", "等待大腦", "ブレインを待っています"],
        ["it loads the champion for this run"] = ["il charge le champion de cette partie", "está cargando el campeón de esta partida", "正在为本局载入冠军", "正在為本局載入冠軍", "このラン用のチャンピオンを読み込み中"],
        ["Waiting for a decision"] = ["En attente d'une décision", "Esperando una decisión", "等待下一个决定", "等待下一個決定", "次の判断を待っています"],
        ["open the chest"] = ["ouvrez le coffre", "abre el cofre", "打开宝箱", "打開寶箱", "宝箱を開けてください"],
        ["No SL clone of this room"] = ["Pas de copie SL de cette salle", "No hay copia SL de esta sala", "这个房间没有 SL 副本", "這個房間沒有 SL 複本", "この部屋のSLコピーがありません"],
        ["{0} · the next room has one"] = ["{0} · la prochaine salle en aura une", "{0} · la próxima sala tendrá una", "{0} · 下个房间会有", "{0} · 下個房間會有", "{0} · 次の部屋にはあります"],
        ["no checkpoint"] = ["pas de point de sauvegarde", "sin punto de control", "没有存档点", "沒有存檔點", "チェックポイントなし"],
        ["Thinking…"] = ["Réflexion…", "Pensando…", "思考中…", "思考中…", "思考中…"],
        ["Mod error"] = ["Erreur du mod", "Error del mod", "Mod 出错", "Mod 出錯", "Modのエラー"],
        ["floor {0}"] = ["étage {0}", "piso {0}", "第 {0} 层", "第 {0} 層", "{0}階"],
        ["HP {0}/{1}"] = ["PV {0}/{1}", "PV {0}/{1}", "HP {0}/{1}", "HP {0}/{1}", "HP {0}/{1}"],
        ["Click to open or close the panel, drag to move it"] = ["Cliquer pour ouvrir ou fermer le panneau, glisser pour le déplacer",
            "Clic para abrir o cerrar el panel; arrastra para moverlo", "点击打开或收起面板，拖动可移动", "點擊打開或收起面板，拖曳可移動", "クリックでパネルを開閉、ドラッグで移動"],

        // The toolbar and the auto-play state.
        ["Auto"] = ["Auto", "Auto", "自动", "自動", "オート"],
        ["Pause"] = ["Pause", "Pausa", "暂停", "暫停", "一時停止"],
        ["Play one"] = ["Jouer une", "Jugar una", "打一张", "打一張", "1枚使う"],
        ["Pick for me"] = ["Choisir", "Elegir", "帮我选", "幫我選", "代わりに選ぶ"],
        ["Step"] = ["Avancer", "Avanzar", "走一步", "走一步", "1歩進む"],
        ["Finish turn"] = ["Finir le tour", "Acabar turno", "打完回合", "打完回合", "ターンを終える"],
        ["Finish room"] = ["Finir la salle", "Acabar sala", "走完房间", "走完房間", "部屋を終える"],
        ["Next room"] = ["Salle suivante", "Siguiente sala", "打完下一房", "打完下一房", "次の部屋まで"],
        ["Your move"] = ["À vous", "Tu turno", "等你操作", "等你操作", "あなたの番"],
        ["Auto on"] = ["Auto en cours", "Automático", "自动中", "自動中", "オート中"],
        ["Acting"] = ["En action", "Actuando", "执行中", "執行中", "実行中"],
        ["Stopped"] = ["Arrêté", "Detenido", "已停下", "已停下", "停止"],
        ["an elite is next"] = ["un élite est le prochain", "lo siguiente es un élite", "下一个是精英", "下一個是菁英", "次はエリート"],
        ["the boss is next"] = ["le boss est le prochain", "lo siguiente es el jefe", "下一个是 Boss", "下一個是頭目", "次はボス"],
        ["the win chance fell {0} points"] = ["la chance de victoire a perdu {0} points", "la probabilidad de ganar bajó {0} puntos", "胜率掉了 {0} 点", "勝率掉了 {0} 點", "勝率が{0}ポイント下がった"],
        ["the action did nothing"] = ["l'action n'a rien fait", "la acción no hizo nada", "动作没有生效", "動作沒有生效", "操作が反映されない"],
        ["the AI cannot press this: {0}"] = ["l'IA ne peut pas faire ceci : {0}", "la IA no puede hacer esto: {0}", "AI 按不了：{0}", "AI 按不了：{0}", "AIには押せない：{0}"],
        ["the run is over"] = ["la partie est finie", "la partida terminó", "本局结束", "本局結束", "ランが終わった"],
        ["you acted"] = ["vous avez joué", "jugaste tú", "你动手了", "你動手了", "あなたが操作した"],
        ["no SL clone of this room"] = ["pas de copie SL de cette salle", "no hay copia SL de esta sala", "这个房间没有 SL 副本", "這個房間沒有 SL 複本", "この部屋のSLコピーがありません"],
        ["AI stopped: {0}"] = ["L'IA s'est arrêtée : {0}", "La IA se detuvo: {0}", "AI 停下了：{0}", "AI 停下了：{0}", "AIが停止：{0}"],
        ["Speed"] = ["Vitesse", "Velocidad", "速度", "速度", "速度"],
        ["Slow"] = ["Lent", "Lento", "慢", "慢", "遅い"],
        ["Medium"] = ["Moyen", "Medio", "中", "中", "普通"],
        ["Fast"] = ["Rapide", "Rápido", "快", "快", "速い"],
        ["Effort"] = ["Réflexion", "Esfuerzo", "思考深度", "思考深度", "思考の深さ"],
        ["Brain"] = ["Cerveau", "Cerebro", "大脑默认", "大腦預設", "既定"],
        ["Low"] = ["Bas", "Bajo", "低", "低", "低"],
        ["High"] = ["Haut", "Alto", "高", "高", "高"],
        ["Max"] = ["Max", "Máx", "最高", "最高", "最大"],
        ["Stop at"] = ["Arrêt", "Parar", "停在", "停在", "停止"],
        ["elites, bosses"] = ["élites, boss", "élites, jefes", "精英、Boss 前", "菁英、頭目前", "エリート・ボス前"],
        ["win chance -3"] = ["victoire -3", "victoria -3", "胜率掉 3 点", "勝率掉 3 點", "勝率-3"],

        // The answer.
        ["Win chance"] = ["Chance de victoire", "Prob. de ganar", "本局胜率", "本局勝率", "勝率"],
        ["Next"] = ["Ensuite", "Siguiente", "下一步", "下一步", "次の一手"],
        ["This turn"] = ["Ce tour", "Este turno", "本回合计划", "本回合計畫", "このターン"],
        ["{0} more turns planned"] = ["{0} tours de plus prévus", "{0} turnos más planeados", "之后还规划 {0} 回合", "之後還規劃 {0} 回合", "さらに{0}ターン計画済み"],
        ["Route to the boss"] = ["Chemin jusqu'au boss", "Ruta hasta el jefe", "到 Boss 的路线", "到頭目的路線", "ボスまでのルート"],
        ["Considered"] = ["Envisagé", "Considerado", "候选", "候選", "候補"],
        ["win"] = ["victoire", "victoria", "胜率", "勝率", "勝率"],
        ["HP left"] = ["PV rest.", "PV rest.", "剩 HP", "剩 HP", "残りHP"],
        ["diff"] = ["écart", "dif.", "差", "差", "差"],
        ["value ±CI"] = ["valeur ±IC", "valor ±IC", "分数 ±CI", "分數 ±CI", "値 ±CI"],
        ["this fight {0} · {1} HP left"] = ["ce combat {0} · {1} PV restants", "este combate {0} · quedan {1} PV", "这场胜率 {0} · 剩 {1} HP", "這場勝率 {0} · 剩 {1} HP", "この戦闘 {0} · 残りHP {1}"],
        ["win chance {0}"] = ["chance de victoire {0}", "prob. de ganar {0}", "整局胜率 {0}", "整局勝率 {0}", "勝率 {0}"],
        ["Recent"] = ["Récent", "Reciente", "刚才做了", "剛才做了", "直近の行動"],

        // Actions (Describe).
        ["drink {0}"] = ["boire {0}", "beber {0}", "喝 {0}", "喝 {0}", "{0}を飲む"],
        ["discard {0}"] = ["jeter {0}", "tirar {0}", "丢掉 {0}", "丟掉 {0}", "{0}を捨てる"],
        ["pick none"] = ["ne rien choisir", "no elegir nada", "都不选", "都不選", "何も選ばない"],
        ["pick {0}"] = ["choisir {0}", "elegir {0}", "选 {0}", "選 {0}", "{0}を選ぶ"],
        ["pick cards"] = ["choisir des cartes", "elegir cartas", "选牌", "選牌", "カードを選ぶ"],
        ["go to {0}"] = ["aller à : {0}", "ir a: {0}", "去 {0}", "去 {0}", "{0}へ進む"],
        ["skip the rest, proceed"] = ["passer le reste, continuer", "omitir el resto y continuar", "跳过其余，继续", "略過其餘，繼續", "残りをスキップして進む"],
        ["proceed"] = ["continuer", "continuar", "继续", "繼續", "進む"],
        ["leave"] = ["partir", "salir", "离开", "離開", "出発する"],
        ["take {0} gold"] = ["prendre {0} or", "tomar {0} de oro", "拿 {0} 金币", "拿 {0} 金幣", "{0}ゴールドを取る"],
        ["take {0}"] = ["prendre {0}", "tomar {0}", "拿 {0}", "拿 {0}", "{0}を取る"],
        ["open the card reward"] = ["ouvrir la récompense de cartes", "abrir la recompensa de cartas", "打开卡牌奖励", "打開卡牌獎勵", "カード報酬を開く"],
        ["the card"] = ["la carte", "la carta", "那张牌", "那張牌", "そのカード"],
        ["remove a card"] = ["retirer une carte", "quitar una carta", "移除一张牌", "移除一張牌", "カードを1枚削除"],
        ["bundle {0}"] = ["lot {0}", "lote {0}", "第 {0} 包", "第 {0} 包", "パック{0}"],
        ["leave the shop"] = ["quitter la boutique", "salir de la tienda", "离开商店", "離開商店", "ショップを出る"],
        ["remove a card ({0} gold)"] = ["retirer une carte ({0} or)", "quitar una carta ({0} de oro)", "移除一张牌（{0} 金币）", "移除一張牌（{0} 金幣）", "カード削除（{0}ゴールド）"],
        ["buy {0} ({1} gold)"] = ["acheter {0} ({1} or)", "comprar {0} ({1} de oro)", "买 {0}（{1} 金币）", "買 {0}（{1} 金幣）", "{0}を買う（{1}ゴールド）"],
        ["skip the relic"] = ["passer la relique", "omitir la reliquia", "跳过遗物", "略過遺物", "レリックをスキップ"],
        ["big divination at {0}"] = ["grande divination en {0}", "gran adivinación en {0}", "在 {0} 大占卜", "在 {0} 大占卜", "{0}で大占い"],
        ["small divination at {0}"] = ["petite divination en {0}", "pequeña adivinación en {0}", "在 {0} 小占卜", "在 {0} 小占卜", "{0}で小占い"],
        ["end turn"] = ["finir le tour", "terminar turno", "结束回合", "結束回合", "ターン終了"],
        ["self"] = ["soi", "uno mismo", "自己", "自己", "自分"],
        ["enemy"] = ["ennemi", "enemigo", "敌人", "敵人", "敵"],

        // The settings tab.
        ["Assist"] = ["Assistance", "Asistencia", "辅助", "輔助", "アシスト"],
        ["AI assist"] = ["Assistance IA", "Asistencia IA", "AI 辅助", "AI 輔助", "AIアシスト"],
        ["off: the mod asks nothing and draws nothing"] = ["désactivé : le mod ne demande rien et n'affiche rien", "desactivado: el mod no pregunta ni muestra nada",
            "关闭：mod 不询问也不显示任何东西", "關閉：mod 不詢問也不顯示任何東西", "オフ：Modは何も問い合わせず何も表示しない"],
        ["Environment"] = ["Environnement", "Entorno", "环境", "環境", "環境"],
        ["SL sees the true outcome of the room you are in; NoSL never peeks"] = ["SL voit le vrai résultat de la salle actuelle ; NoSL ne regarde jamais",
            "SL ve el resultado real de la sala actual; NoSL nunca mira", "SL 能看到当前房间的真实结果；NoSL 从不偷看", "SL 能看到目前房間的真實結果；NoSL 從不偷看",
            "SLは今いる部屋の本当の結果を見る。NoSLは一切覗かない"],
        ["Panel"] = ["Panneau", "Panel", "面板", "面板", "パネル"],
        ["F8 hides everything at once"] = ["F8 masque tout d'un coup", "F8 oculta todo a la vez", "F8 一次隐藏全部", "F8 一次隱藏全部", "F8ですべて隠す"],
        ["Considered options"] = ["Options envisagées", "Opciones consideradas", "候选项", "候選項", "候補"],
        ["in the panel"] = ["dans le panneau", "en el panel", "在面板里", "在面板裡", "パネル内"],
        ["Values"] = ["Valeurs", "Valores", "数值", "數值", "数値"],
        ["Values + bars"] = ["Valeurs + barres", "Valores + barras", "数值 + 条", "數值 + 長條", "数値＋バー"],
        ["Off"] = ["Désactivé", "Desactivado", "关", "關", "オフ"],
        ["On"] = ["Activé", "Activado", "开", "開", "オン"],
        ["Order numbers on cards"] = ["Numéros d'ordre sur les cartes", "Números de orden en las cartas", "卡牌上的出牌顺序", "卡牌上的出牌順序", "カードの順番表示"],
        ["the cards this turn's plan plays"] = ["les cartes que joue le plan de ce tour", "las cartas que juega el plan de este turno", "本回合计划要打的牌", "本回合計畫要打的牌", "このターンの計画で使うカード"],
        ["Target and end-turn marks"] = ["Marques de cible et de fin de tour", "Marcas de objetivo y fin de turno", "目标与结束回合标记", "目標與結束回合標記", "対象とターン終了の印"],
        ["Map route"] = ["Itinéraire sur la carte", "Ruta en el mapa", "地图路线", "地圖路線", "マップのルート"],
        ["Next point"] = ["Point suivant", "Siguiente punto", "下一个点", "下一個點", "次の地点"],
        ["Whole route"] = ["Itinéraire complet", "Ruta completa", "整条路线", "整條路線", "ルート全体"],
        ["Panel opacity"] = ["Opacité du panneau", "Opacidad del panel", "面板不透明度", "面板不透明度", "パネルの不透明度"],
        ["Reset AI settings"] = ["Réinitialiser les réglages IA", "Restablecer ajustes de IA", "重置 AI 设置", "重設 AI 設定", "AI設定をリセット"],
        ["Search effort"] = ["Effort de recherche", "Esfuerzo de búsqueda", "思考深度", "思考深度", "探索の深さ"],
        ["how hard the SL planner searches each fight; a change starts the run anew for the brain"] = [
            "jusqu'où le planificateur SL cherche à chaque combat ; un changement fait repartir la partie pour le cerveau",
            "cuánto busca el planificador SL en cada combate; al cambiarlo, el cerebro empieza la partida de nuevo desde ahí",
            "SL 规划每场战斗搜得多深；切换后大脑从这里重新开始这一局", "SL 規劃每場戰鬥搜得多深；切換後大腦從這裡重新開始這一局",
            "SLプランナーが各戦闘をどこまで探索するか。変更するとブレインはここからランをやり直す"],
        ["Auto-play"] = ["Jeu automatique", "Juego automático", "自动打", "自動打", "オートプレイ"],
        ["Speed of auto-play"] = ["Vitesse du jeu automatique", "Velocidad del juego automático", "自动打的速度", "自動打的速度", "オートプレイの速度"],
        ["the pause before each of the AI's actions"] = ["la pause avant chaque action de l'IA", "la pausa antes de cada acción de la IA", "AI 每个动作前停多久", "AI 每個動作前停多久", "AIの各操作の前の間"],
        ["Stop before elites and bosses"] = ["S'arrêter avant les élites et les boss", "Parar antes de élites y jefes", "精英、Boss 前停下", "菁英、頭目前停下", "エリート・ボスの前で止まる"],
        ["Stop when the win chance falls 3 points"] = ["S'arrêter si la victoire perd 3 points", "Parar si la probabilidad de ganar baja 3 puntos", "胜率掉 3 点时停下", "勝率掉 3 點時停下", "勝率が3ポイント下がったら止まる"],
    };

    /// <summary>The line in the game's language, with <paramref name="args"/> in its holes.</summary>
    public static string T(string english, params object[] args)
    {
        string line = english;
        if (Index() is int at && Table.TryGetValue(english, out var lines)) line = lines[at];
        return args.Length == 0 ? line : string.Format(CultureInfo.InvariantCulture, line, args);
    }

    /// <summary>Which translation the game's language reads, or null for English.</summary>
    private static int? Index()
    {
        string? language = LocManager.Instance?.Language;
        for (int i = 0; i < Codes.Length; i++)
            if (Array.IndexOf(Codes[i], language) >= 0) return i;
        return null;
    }
}
