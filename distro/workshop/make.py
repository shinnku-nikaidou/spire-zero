# Writes the Workshop title and description in each of the game's 17 languages into distro/workshop/STEAMLANG.bbcode.
# The characters', the ascension's and the Dread mode's names are the game's own (tmp/workshop/names.json, from the pck
# by names.mjs);
# the numbers are the champion v-sl-td4-12-1-d2 + c-sl-12-1-d2's SL runs on test seeds at the test's effort, medium
# (beam 8x300 replan 1), with the search's turn-dominance rule (the user, 2026-11-20: 換，Dread 補測): A0 and A10 from
# test-turn-dominance's rule-on side at c65af642, the Dread mode (played at A10) from test-turn-dominance-dread at
# 9b4b4b74 (nothing between the two changes a medium SL run), all on promotion-1-200, 200 runs of each but the failed
# ones left out (the Defect's Dread 198: the engine's exceptions on two seeds that failed in test v12-1 too). The Silent's
# row is its own champion v-sl-silent-1 + c-sl-silent-1 + f-sl-silent-1's at xhigh (test test-v-sl-silent-1 at a1764104
# on promotion-1-200: A0, A10 and the Dread mode, 200 each), the intro saying so (the user, 2026-11-23); the Regent's
# likewise its own v-sl-regent-1 + c-sl-regent-1 + f-sl-regent-1's at xhigh (test test-v-sl-regent-1-b at bea30dfb,
# 200 each; the user, 2026-11-25: Workshop 的 REGENT 成績也更新); shown with
# the run counts, as the user chose. NoSL's are its champion v0-nosl-2 + c0-nosl-2's (PIMC 8 worlds x 300 nodes, which no
# effort changes; test test-v0-nosl-2 at 3016ff12 on promotion-1-100: A0, A10 and the Dread mode, 100 runs of each but the
# failed ones left out, the Fool's 4, 4 and 2 at Ki Chant's prompt, fixed after the test). Its parentheses are the user's
# words (2026-11-21: 括號裡面寫上完全0作弊, 禁止回檔).
import json
import pathlib

ROOT = pathlib.Path(__file__).resolve().parents[2]
NAMES = json.loads((ROOT / "tmp/workshop/names.json").read_text())
CHARS = ["IRONCLAD", "SILENT", "DEFECT", "REGENT", "NECROBINDER", "FOOL"]
WINS = {  # (A0 won, A0 runs, A10 won, A10 runs, Dread won, Dread runs)
    "IRONCLAD": (199, 200, 181, 200, 103, 200),
    "SILENT": (199, 200, 170, 200, 75, 200),
    "DEFECT": (200, 200, 169, 200, 64, 198),
    "REGENT": (199, 200, 163, 200, 82, 200),
    "NECROBINDER": (198, 200, 138, 200, 40, 200),
    "FOOL": (192, 200, 88, 200, 17, 200),
}
WINS_NOSL = {  # (A0 won, A0 runs, A10 won, A10 runs, Dread won, Dread runs)
    "IRONCLAD": (93, 100, 20, 100, 2, 100),
    "SILENT": (95, 100, 33, 100, 1, 100),
    "DEFECT": (97, 100, 24, 100, 3, 100),
    "REGENT": (82, 100, 18, 100, 2, 100),
    "NECROBINDER": (91, 100, 18, 100, 0, 100),
    "FOOL": (79, 96, 7, 96, 1, 98),
}
REPO = "https://github.com/shinnku-nikaidou/spire-zero"
DOCS = "https://sts2-docs.shinnkunikaidou.workers.dev/"
COMMA = {"deu", "fra", "spa", "esp", "ita", "ptb", "pol", "rus", "tur", "ind"}


def pct(won, runs, lang):
    p = f"{100 * won / runs:.1f}".rstrip("0").rstrip(".")
    if lang in COMMA:
        p = p.replace(".", ",")
    if lang == "tur":
        p = "%" + p
    elif lang in ("deu", "fra", "spa", "esp"):
        p += " %"
    else:
        p += "%"
    return f"{p} ({won}/{runs})"


T = {}
T["eng"] = dict(
    steam="english", docs="",
    title="Spire Zero: AI Strategist for Slay the Spire 2",
    multi="[b]Multiplayer support is coming.[/b] Subscribe and add it to your favorites now, and you will have it as soon as it is out.",
    intro="An AI inside the game, for single-player runs:",
    advice="[b]Advice[/b]: at every decision it shows what it would do, and each option's chance to win the run.",
    autoplay="[b]Auto-play[/b]: on request it plays a step, the rest of a turn or a room, or keeps going until you pause it.",
    learned="Nobody wrote any part of how it plays, and it learned nothing from human games: it learned all of it from runs it played itself. At each decision it tries the options on a copy of the game, then asks a learned network how likely the run is to be won from where each one leads.",
    modes_h="Two modes (in the settings)",
    sl="[b]SL[/b] (the default): like saving and loading, it tries the options on a true copy of the room you are in, so it sees what will really happen there (the draw order, the random rolls). It cheats, and it plays at its strongest.",
    nosl="[b]NoSL[/b]: only what a player can see. No peeking.",
    mp_h="Multiplayer (experimental)",
    mp="In a run with other players it advises you, in NoSL. It is off by default: turn it on in the game's console (the ~ key) with [b]ai mp on[/b], until the game closes, or [b]ai mp always[/b], which keeps it on; or with the Multiplayer (experimental) switch in the settings' AI tab.",
    how_h="How well it plays",
    how_intro="In SL, with Search effort set to Medium (the Silent and the Regent: XHigh), on test seeds, the share of runs it won:",
    how_nosl="In NoSL (zero cheating, no save-loading), on test seeds, the share of runs it won:",
    req_h="Requirements",
    req_version="The game at exactly [b]v0.114.0[/b], Steam's [b]public-beta[/b] branch (the game's Properties, then Betas). On another version the mod turns itself off and shows nothing.",
    req_platforms="Windows (x64), Linux (x64) or macOS (Apple silicon).",
    req_single="Single-player; multiplayer is experimental (see above).",
    runs_h="What it runs on your computer",
    runs="While the game runs, the mod starts its own brain, a background program where the AI thinks, on half of your logical processors (at most 8). It ends when the game does. There is nothing else to install.",
    src_h="Source and design",
    src_code="Open source (MIT):",
    src_docs="How it is designed, in English, Simplified Chinese and Traditional Chinese:",
    disclaimer="An unofficial fan project, not affiliated with or endorsed by Mega Crit.",
)
T["zht"] = dict(
    steam="tchinese", docs="zh-tw/",
    title="Spire Zero：殺戮尖塔 2 AI 軍師",
    multi="[b]之後會支援多人遊戲。[/b]現在就可以先訂閱、加入最愛，推出時馬上就能用。",
    intro="一個放在遊戲裡的 AI，用在單人遊戲：",
    advice="[b]建議[/b]：每個決策它都告訴你它會怎麼選，以及每個選項有多大機會贏下這一局。",
    autoplay="[b]自動遊玩[/b]：你可以讓它替你打一步、打完這回合或這個房間，或一直打到你按暫停。",
    learned="它的打法沒有一條是人寫的，也沒有學過人類的對局，全是從它自己打的對局裡學來的。每個決策，它都把選項在遊戲的複本裡試一遍，再用學出來的網路估計：試完之後的局面，有多大機會贏下整局。",
    modes_h="兩種模式（在設定裡切換）",
    sl="[b]SL（預設）[/b]：就像存檔讀檔，它在你所在房間的真實複本上試選項，看得到這個房間真正會發生什麼（抽牌順序、隨機結果）。這是作弊的打法，也是它最強的時候。",
    nosl="[b]NoSL[/b]：只用玩家看得到的資訊，不偷看。",
    mp_h="多人遊戲（實驗性）",
    mp="和其他玩家一起打的時候，它會用 NoSL 給你建議。預設是關閉的：在遊戲的主控台（~ 鍵）輸入 [b]ai mp on[/b]，開到遊戲關掉為止；或輸入 [b]ai mp always[/b]，之後一直開著；也可以在設定的 AI 分頁裡打開「多人模式（實驗性）」開關。",
    how_h="它打得多好",
    how_intro="在 SL、思考深度設為「中」（寂靜獵手和儲君為「極高」）、測試種子上，它贏下的局數比例：",
    how_nosl="在 NoSL（完全0作弊，禁止回檔）、測試種子上，它贏下的局數比例：",
    req_h="需求",
    req_version="遊戲版本必須剛好是 [b]v0.114.0[/b]，也就是 Steam 的 [b]public-beta[/b] 測試分支。版本不同時，mod 會自己停用，畫面上什麼都不顯示。",
    req_platforms="Windows（x64）、Linux（x64）或 macOS（Apple 晶片）。",
    req_single="單人遊戲；多人遊戲是實驗性支援（見上）。",
    runs_h="它會在你的電腦上跑什麼",
    runs="遊戲開著的時候，mod 會在背景啟動它自己的大腦程式（AI 在這裡思考），用一半的邏輯處理器（最多 8 個）。遊戲關掉時它也跟著結束。不需要另外安裝任何東西。",
    src_h="原始碼與設計說明",
    src_code="開放原始碼（MIT）：",
    src_docs="設計說明（繁體中文、簡體中文、英文）：",
    disclaimer="非官方的粉絲作品，與 Mega Crit 無關，也沒有得到它的認可。",
)
T["zhs"] = dict(
    steam="schinese", docs="zh-cn/",
    title="Spire Zero：杀戮尖塔 2 AI 军师",
    multi="[b]之后会支持多人游戏。[/b]现在就可以先订阅、加入收藏，推出时马上就能用。",
    intro="一个放在游戏里的 AI，用于单人游戏：",
    advice="[b]建议[/b]：每个决策它都告诉你它会怎么选，以及每个选项有多大机会赢下这一局。",
    autoplay="[b]自动游玩[/b]：你可以让它替你打一步、打完这回合或这个房间，或一直打到你按暂停。",
    learned="它的打法没有一条是人写的，也没有学过人类的对局，全是从它自己打的对局里学来的。每个决策，它都把选项在游戏的副本里试一遍，再用学出来的网络估计：试完之后的局面，有多大机会赢下整局。",
    modes_h="两种模式（在设置里切换）",
    sl="[b]SL（默认）[/b]：就像存档读档，它在你所在房间的真实副本上试选项，看得到这个房间真正会发生什么（抽牌顺序、随机结果）。这是作弊的打法，也是它最强的时候。",
    nosl="[b]NoSL[/b]：只用玩家看得到的信息，不偷看。",
    mp_h="多人游戏（实验性）",
    mp="和其他玩家一起打的时候，它会用 NoSL 给你建议。默认是关闭的：在游戏的控制台（~ 键）输入 [b]ai mp on[/b]，开到游戏关闭为止；或输入 [b]ai mp always[/b]，之后一直开着；也可以在设置的 AI 标签页里打开“多人模式（实验性）”开关。",
    how_h="它打得多好",
    how_intro="在 SL、思考深度设为“中”（静默猎手和储君为“极高”）、测试种子上，它赢下的局数比例：",
    how_nosl="在 NoSL（完全0作弊，禁止回档）、测试种子上，它赢下的局数比例：",
    req_h="需求",
    req_version="游戏版本必须正好是 [b]v0.114.0[/b]，也就是 Steam 的 [b]public-beta[/b] 测试分支。版本不同时，mod 会自己停用，画面上什么都不显示。",
    req_platforms="Windows（x64）、Linux（x64）或 macOS（Apple 芯片）。",
    req_single="单人游戏；多人游戏是实验性支持（见上）。",
    runs_h="它会在你的电脑上运行什么",
    runs="游戏开着的时候，mod 会在后台启动它自己的大脑程序（AI 在这里思考），用一半的逻辑处理器（最多 8 个）。游戏关掉时它也跟着结束。不需要另外安装任何东西。",
    src_h="源代码与设计说明",
    src_code="开源（MIT）：",
    src_docs="设计说明（简体中文、繁体中文、英文）：",
    disclaimer="非官方的粉丝作品，与 Mega Crit 无关，也没有得到它的认可。",
)
T["deu"] = dict(
    steam="german", docs="",
    title="Spire Zero: KI-Stratege für Slay the Spire 2",
    multi="[b]Mehrspieler-Unterstützung kommt.[/b] Jetzt abonnieren und zu den Favoriten hinzufügen, dann ist sie sofort da, sobald sie erscheint.",
    intro="Eine KI im Spiel, für Einzelspieler-Runs:",
    advice="[b]Ratschläge[/b]: Bei jeder Entscheidung zeigt sie, was sie tun würde, und wie gut die Siegchance des Runs mit jeder Option ist.",
    autoplay="[b]Automatisches Spielen[/b]: Auf Wunsch spielt sie einen Schritt, den Rest eines Zuges oder eines Raums, oder spielt weiter, bis du sie pausierst.",
    learned="Niemand hat auch nur einen Teil ihres Spiels von Hand geschrieben, und sie hat nichts aus Partien von Menschen gelernt: Alles hat sie aus Runs gelernt, die sie selbst gespielt hat. Bei jeder Entscheidung probiert sie die Optionen an einer Kopie des Spiels aus und fragt dann ein trainiertes Netz, wie wahrscheinlich der Run von dort aus gewonnen wird, wohin jede Option führt.",
    modes_h="Zwei Modi (in den Einstellungen)",
    sl="[b]SL[/b] (Standard): Wie beim Speichern und Laden probiert sie die Optionen an einer echten Kopie des Raums aus, in dem du bist, und sieht so, was dort wirklich passieren wird (die Reihenfolge der gezogenen Karten, die Zufallsergebnisse). Sie schummelt – und spielt so am stärksten.",
    nosl="[b]NoSL[/b]: nur das, was ein Spieler sehen kann. Kein Spicken.",
    mp_h="Mehrspieler (experimentell)",
    mp="In einem Run mit anderen Spielern berät sie dich, in NoSL. Standardmäßig ist das aus: Schalte es in der Konsole des Spiels (Taste ~) mit [b]ai mp on[/b] ein, bis das Spiel geschlossen wird, oder mit [b]ai mp always[/b], dann bleibt es an; oder mit dem Schalter „Multiplayer (experimental)“ im Reiter AI der Einstellungen.",
    how_h="Wie gut sie spielt",
    how_intro="In SL, mit Search effort auf Medium (die Stille und der Regent: XHigh), auf Test-Seeds, der Anteil gewonnener Runs:",
    how_nosl="In NoSL (null Schummeln, kein Neuladen von Spielständen), auf Test-Seeds, der Anteil gewonnener Runs:",
    req_h="Voraussetzungen",
    req_version="Das Spiel in genau [b]v0.114.0[/b], dem [b]public-beta[/b]-Branch auf Steam (in den Eigenschaften des Spiels auf Steam). Bei einer anderen Version schaltet sich die Mod selbst ab und zeigt nichts an.",
    req_platforms="Windows (x64), Linux (x64) oder macOS (Apple Silicon).",
    req_single="Einzelspieler; Mehrspieler ist experimentell (siehe oben).",
    runs_h="Was sie auf deinem Computer ausführt",
    runs="Während das Spiel läuft, startet die Mod ihr eigenes Gehirn, ein Hintergrundprogramm, in dem die KI denkt, auf der Hälfte deiner logischen Prozessoren (höchstens 8). Es endet mit dem Spiel. Sonst muss nichts installiert werden.",
    src_h="Quellcode und Aufbau",
    src_code="Open Source (MIT):",
    src_docs="Wie sie aufgebaut ist, auf Englisch, vereinfachtem und traditionellem Chinesisch:",
    disclaimer="Ein inoffizielles Fanprojekt, weder mit Mega Crit verbunden noch von Mega Crit unterstützt.",
)
T["fra"] = dict(
    steam="french", docs="",
    title="Spire Zero : stratège IA pour Slay the Spire 2",
    multi="[b]Le multijoueur arrive bientôt.[/b] Abonnez-vous et ajoutez le mod à vos favoris dès maintenant pour l'avoir dès sa sortie.",
    intro="Une IA dans le jeu, pour les parties en solo :",
    advice="[b]Conseils[/b] : à chaque décision, elle montre ce qu'elle ferait et les chances de gagner la partie avec chaque option.",
    autoplay="[b]Jeu automatique[/b] : à la demande, elle joue une action, la fin d'un tour ou d'une salle, ou continue jusqu'à ce que vous la mettiez en pause.",
    learned="Personne n'a écrit la moindre partie de sa façon de jouer, et elle n'a rien appris de parties humaines : elle a tout appris des parties qu'elle a jouées elle-même. À chaque décision, elle essaie les options sur une copie du jeu, puis demande à un réseau entraîné quelles sont les chances de gagner la partie depuis là où mène chaque option.",
    modes_h="Deux modes (dans les paramètres)",
    sl="[b]SL[/b] (par défaut) : comme avec une sauvegarde et un chargement, elle essaie les options sur une copie fidèle de la salle où vous êtes, et voit donc ce qui va vraiment s'y passer (l'ordre de pioche, les tirages aléatoires). Elle triche, et c'est là qu'elle joue le mieux.",
    nosl="[b]NoSL[/b] : seulement ce qu'un joueur peut voir. Aucune triche.",
    mp_h="Multijoueur (expérimental)",
    mp="Dans une partie avec d'autres joueurs, elle vous conseille, en NoSL. C'est désactivé par défaut : activez-le dans la console du jeu (touche ~) avec [b]ai mp on[/b], jusqu'à la fermeture du jeu, ou [b]ai mp always[/b], qui le garde activé ; ou avec l'interrupteur « Multijoueur (expérimental) » de l'onglet AI des paramètres.",
    how_h="Son niveau",
    how_intro="En SL, avec l'effort de recherche sur « Moyen » (la Silencieuse et le Régent : « Très haut »), sur des seeds de test, la part des parties gagnées :",
    how_nosl="En NoSL (zéro triche, aucun rechargement de sauvegarde), sur des seeds de test, la part des parties gagnées :",
    req_h="Configuration requise",
    req_version="Le jeu en version [b]v0.114.0[/b] exactement, la branche [b]public-beta[/b] de Steam (dans les propriétés du jeu sur Steam). Avec une autre version, le mod se désactive et n'affiche rien.",
    req_platforms="Windows (x64), Linux (x64) ou macOS (puce Apple).",
    req_single="Solo ; le multijoueur est expérimental (voir plus haut).",
    runs_h="Ce qu'il lance sur votre ordinateur",
    runs="Pendant que le jeu tourne, le mod lance son propre cerveau, un programme en arrière-plan où l'IA réfléchit, sur la moitié de vos processeurs logiques (8 au plus). Il s'arrête avec le jeu. Rien d'autre à installer.",
    src_h="Code source et conception",
    src_code="Open source (MIT) :",
    src_docs="Sa conception, en anglais, en chinois simplifié et en chinois traditionnel :",
    disclaimer="Un projet de fan non officiel, sans lien avec Mega Crit et non approuvé par Mega Crit.",
)
spa = dict(
    title="Spire Zero: estratega IA para Slay the Spire 2",
    multi="[b]El multijugador llegará pronto.[/b] Suscríbete y añade el mod a favoritos ahora para tenerlo en cuanto salga.",
    intro="Una IA dentro del juego, para partidas de un jugador:",
    advice="[b]Consejos[/b]: en cada decisión muestra lo que haría y la probabilidad de ganar la partida con cada opción.",
    autoplay="[b]Juego automático[/b]: si se lo pides, juega un paso, el resto de un turno o de una sala, o sigue hasta que la pauses.",
    learned="Nadie escribió ninguna parte de su forma de jugar, y no aprendió nada de partidas humanas: lo aprendió todo de las partidas que jugó ella misma. En cada decisión prueba las opciones en una copia del juego y luego pregunta a una red entrenada qué probabilidad hay de ganar la partida desde donde lleva cada una.",
    modes_h="Dos modos (en los ajustes)",
    sl="[b]SL[/b] (por defecto): como guardar y cargar, prueba las opciones en una copia real de la sala en la que estás, así que ve lo que de verdad va a pasar ahí (el orden de robo, los resultados aleatorios). Hace trampa, y es cuando mejor juega.",
    nosl="[b]NoSL[/b]: solo lo que un jugador puede ver. Sin espiar.",
    mp_h="Multijugador (experimental)",
    mp="En una partida con otros jugadores te aconseja, en NoSL. Viene desactivado: actívalo en la consola del juego (tecla ~) con [b]ai mp on[/b], hasta que cierres el juego, o con [b]ai mp always[/b], que lo deja activado; o con el interruptor «Multijugador (experimental)» de la pestaña AI de los ajustes.",
    how_h="Su nivel de juego",
    how_intro="En SL, con el esfuerzo de búsqueda en «Medio» (la Silenciosa y el Regente: «Muy alto»), con semillas de prueba, la proporción de partidas ganadas:",
    how_nosl="En NoSL (cero trampas, sin recargar partidas guardadas), con semillas de prueba, la proporción de partidas ganadas:",
    req_h="Requisitos",
    req_version="El juego exactamente en la [b]v0.114.0[/b], la rama [b]public-beta[/b] de Steam (en las propiedades del juego en Steam). Con otra versión, el mod se desactiva solo y no muestra nada.",
    req_platforms="Windows (x64), Linux (x64) o macOS (Apple silicon).",
    req_single="Un jugador; el multijugador es experimental (ver arriba).",
    runs_h="Qué ejecuta en tu ordenador",
    runs="Mientras el juego está abierto, el mod inicia su propio cerebro, un programa en segundo plano donde piensa la IA, en la mitad de tus procesadores lógicos (8 como máximo). Se cierra con el juego. No hay que instalar nada más.",
    src_h="Código y diseño",
    src_code="Código abierto (MIT):",
    src_docs="Cómo está diseñada, en inglés, chino simplificado y chino tradicional:",
    disclaimer="Un proyecto de fans no oficial, sin relación con Mega Crit ni respaldado por ella.",
)
T["spa"] = dict(spa, steam="spanish", docs="")
T["esp"] = dict(spa, steam="latam", docs="", mp="En una partida con otros jugadores te aconseja, en NoSL. Viene desactivado: actívalo en la consola del juego (tecla ~) con [b]ai mp on[/b], hasta que cierres el juego, o con [b]ai mp always[/b], que lo deja activado; o con el interruptor «Multijugador (experimental)» de la pestaña AI de la configuración.",
                modes_h="Dos modos (en la configuración)",
                runs_h="Qué ejecuta en tu computadora",
                how_intro="En SL, con el esfuerzo de búsqueda en «Medio» (la Silenciosa y el Monarca: «Muy alto»), con semillas de prueba, la proporción de partidas ganadas:")
T["ita"] = dict(
    steam="italian", docs="",
    title="Spire Zero: stratega IA per Slay the Spire 2",
    multi="[b]Il multigiocatore arriverà presto.[/b] Iscriviti e aggiungi la mod ai preferiti ora: lo avrai appena esce.",
    intro="Un'IA dentro il gioco, per le partite in singolo:",
    advice="[b]Consigli[/b]: a ogni decisione mostra cosa farebbe e la probabilità di vincere la partita con ciascuna opzione.",
    autoplay="[b]Gioco automatico[/b]: su richiesta gioca un passo, il resto di un turno o di una stanza, oppure continua finché non la metti in pausa.",
    learned="Nessuno ha scritto una sola parte del suo modo di giocare, e non ha imparato nulla da partite umane: ha imparato tutto dalle partite che ha giocato da sola. A ogni decisione prova le opzioni su una copia del gioco, poi chiede a una rete addestrata quanto è probabile vincere la partita da dove porta ciascuna.",
    modes_h="Due modalità (nelle impostazioni)",
    sl="[b]SL[/b] (predefinita): come salvare e ricaricare, prova le opzioni su una copia reale della stanza in cui ti trovi, quindi vede cosa succederà davvero lì (l'ordine di pesca, i risultati casuali). Bara, ed è così che gioca al meglio.",
    nosl="[b]NoSL[/b]: solo ciò che un giocatore può vedere. Niente sbirciate.",
    mp_h="Multigiocatore (sperimentale)",
    mp="In una partita con altri giocatori ti dà consigli, in NoSL. È disattivato di base: attivalo nella console del gioco (tasto ~) con [b]ai mp on[/b], fino alla chiusura del gioco, oppure con [b]ai mp always[/b], che lo lascia attivo; oppure con l'interruttore «Multiplayer (experimental)» nella scheda AI delle impostazioni.",
    how_h="Quanto è forte",
    how_intro="In SL, con Search effort su Medium (Silent e Regent: XHigh), su seed di prova, la quota di partite vinte:",
    how_nosl="In NoSL (zero imbrogli, niente ricaricamenti dei salvataggi), su seed di prova, la quota di partite vinte:",
    req_h="Requisiti",
    req_version="Il gioco esattamente alla [b]v0.114.0[/b], il ramo [b]public-beta[/b] di Steam (nelle proprietà del gioco su Steam). Con un'altra versione la mod si disattiva da sola e non mostra nulla.",
    req_platforms="Windows (x64), Linux (x64) o macOS (Apple silicon).",
    req_single="Giocatore singolo; il multigiocatore è sperimentale (vedi sopra).",
    runs_h="Cosa esegue sul tuo computer",
    runs="Mentre il gioco è aperto, la mod avvia il proprio cervello, un programma in background dove l'IA pensa, sulla metà dei tuoi processori logici (al massimo 8). Si chiude insieme al gioco. Non c'è altro da installare.",
    src_h="Codice e progettazione",
    src_code="Open source (MIT):",
    src_docs="Come è progettata, in inglese, cinese semplificato e cinese tradizionale:",
    disclaimer="Un progetto amatoriale non ufficiale, non affiliato a Mega Crit né approvato da Mega Crit.",
)
T["ptb"] = dict(
    steam="brazilian", docs="",
    title="Spire Zero: estrategista IA para Slay the Spire 2",
    multi="[b]O multijogador chega em breve.[/b] Inscreva-se e adicione o mod aos favoritos agora para tê-lo assim que sair.",
    intro="Uma IA dentro do jogo, para partidas solo:",
    advice="[b]Conselhos[/b]: em cada decisão, ela mostra o que faria e a chance de vencer a partida com cada opção.",
    autoplay="[b]Jogo automático[/b]: quando você pedir, ela joga um passo, o resto de um turno ou de uma sala, ou continua até você pausar.",
    learned="Ninguém escreveu nenhuma parte do jeito como ela joga, e ela não aprendeu nada com partidas de humanos: aprendeu tudo com partidas que ela mesma jogou. Em cada decisão, ela testa as opções numa cópia do jogo e depois pergunta a uma rede treinada qual é a chance de vencer a partida a partir de onde cada uma leva.",
    modes_h="Dois modos (nas configurações)",
    sl="[b]SL[/b] (padrão): como salvar e carregar, ela testa as opções numa cópia real da sala em que você está, então vê o que vai mesmo acontecer ali (a ordem de compra das cartas, os resultados aleatórios). Ela trapaceia, e é assim que joga melhor.",
    nosl="[b]NoSL[/b]: só o que um jogador pode ver. Sem espiar.",
    mp_h="Multijogador (experimental)",
    mp="Em uma partida com outros jogadores, ela dá conselhos a você, em NoSL. Vem desligado: ative no console do jogo (tecla ~) com [b]ai mp on[/b], até o jogo fechar, ou com [b]ai mp always[/b], que deixa ligado; ou pela chave \"Multiplayer (experimental)\" na aba AI das configurações.",
    how_h="O quanto ela joga bem",
    how_intro="No SL, com Search effort em Medium (a Sorrateira e o Regente: XHigh), em seeds de teste, a parcela de partidas vencidas:",
    how_nosl="No NoSL (zero trapaça, sem recarregar saves), em seeds de teste, a parcela de partidas vencidas:",
    req_h="Requisitos",
    req_version="O jogo exatamente na [b]v0.114.0[/b], o branch [b]public-beta[/b] da Steam (nas propriedades do jogo na Steam). Em outra versão, o mod se desliga sozinho e não mostra nada.",
    req_platforms="Windows (x64), Linux (x64) ou macOS (Apple silicon).",
    req_single="Um jogador; o multijogador é experimental (veja acima).",
    runs_h="O que ele roda no seu computador",
    runs="Enquanto o jogo está aberto, o mod inicia o próprio cérebro, um programa em segundo plano onde a IA pensa, em metade dos seus processadores lógicos (no máximo 8). Ele fecha junto com o jogo. Não é preciso instalar mais nada.",
    src_h="Código e design",
    src_code="Código aberto (MIT):",
    src_docs="Como ela foi projetada, em inglês, chinês simplificado e chinês tradicional:",
    disclaimer="Um projeto de fã não oficial, sem vínculo com a Mega Crit e sem o endosso dela.",
)
T["pol"] = dict(
    steam="polish", docs="",
    title="Spire Zero: strateg SI do Slay the Spire 2",
    multi="[b]Tryb wieloosobowy jest w drodze.[/b] Zasubskrybuj i dodaj mod do ulubionych już teraz, a dostaniesz go, gdy tylko się pojawi.",
    intro="SI w grze, do rozgrywek jednoosobowych:",
    advice="[b]Podpowiedzi[/b]: przy każdej decyzji pokazuje, co by zrobiła, i jaką szansę na wygranie runu daje każda opcja.",
    autoplay="[b]Automatyczna gra[/b]: na żądanie rozgrywa jeden krok, resztę tury lub pomieszczenia albo gra dalej, aż ją zatrzymasz.",
    learned="Nikt nie napisał ani kawałka jej sposobu gry i nie uczyła się z partii ludzi: wszystkiego nauczyła się z runów, które rozegrała sama. Przy każdej decyzji sprawdza opcje na kopii gry, a potem pyta wytrenowaną sieć, jak duża jest szansa na wygranie runu z miejsca, do którego prowadzi każda z nich.",
    modes_h="Dwa tryby (w ustawieniach)",
    sl="[b]SL[/b] (domyślny): jak przy zapisywaniu i wczytywaniu, sprawdza opcje na prawdziwej kopii pomieszczenia, w którym jesteś, więc widzi, co naprawdę się tam stanie (kolejność dobierania kart, wyniki losowań). Oszukuje – i wtedy gra najmocniej.",
    nosl="[b]NoSL[/b]: tylko to, co widzi gracz. Bez podglądania.",
    mp_h="Tryb wieloosobowy (eksperymentalny)",
    mp="W runie z innymi graczami doradza ci, w trybie NoSL. Domyślnie jest wyłączony: włącz go w konsoli gry (klawisz ~) poleceniem [b]ai mp on[/b], do zamknięcia gry, albo [b]ai mp always[/b], wtedy zostaje włączony; albo przełącznikiem „Multiplayer (experimental)” w zakładce AI ustawień.",
    how_h="Jak dobrze gra",
    how_intro="W SL, z Search effort ustawionym na Medium (Zjawa i Monarcha: XHigh), na testowych seedach, odsetek wygranych runów:",
    how_nosl="W NoSL (zero oszukiwania, bez wczytywania zapisów), na testowych seedach, odsetek wygranych runów:",
    req_h="Wymagania",
    req_version="Gra w wersji dokładnie [b]v0.114.0[/b], czyli gałąź [b]public-beta[/b] na Steamie (we właściwościach gry na Steamie). Przy innej wersji mod sam się wyłącza i niczego nie wyświetla.",
    req_platforms="Windows (x64), Linux (x64) lub macOS (Apple silicon).",
    req_single="Gra jednoosobowa; tryb wieloosobowy jest eksperymentalny (patrz wyżej).",
    runs_h="Co uruchamia na twoim komputerze",
    runs="Gdy gra jest włączona, mod uruchamia własny mózg – program w tle, w którym myśli SI – na połowie twoich procesorów logicznych (najwyżej 8). Kończy działanie razem z grą. Nie trzeba niczego doinstalowywać.",
    src_h="Kod i projekt",
    src_code="Otwarte źródło (MIT):",
    src_docs="Jak jest zaprojektowana, po angielsku, chińsku uproszczonym i chińsku tradycyjnym:",
    disclaimer="Nieoficjalny projekt fanowski, niezwiązany z Mega Crit ani przez nie niepopierany.",
)
T["rus"] = dict(
    steam="russian", docs="",
    title="Spire Zero: ИИ-стратег для Slay the Spire 2",
    multi="[b]Скоро появится поддержка мультиплеера.[/b] Подпишитесь и добавьте мод в избранное уже сейчас, чтобы получить её сразу после выхода.",
    intro="ИИ прямо в игре, для одиночных забегов:",
    advice="[b]Подсказки[/b]: при каждом решении показывает, что выбрал бы сам, и шанс выиграть забег с каждым вариантом.",
    autoplay="[b]Автоигра[/b]: по запросу делает один шаг, доигрывает ход или комнату или играет дальше, пока вы не поставите паузу.",
    learned="Никто не писал ни одной части его игры, и он ничему не учился на партиях людей: всему он научился на забегах, которые сыграл сам. При каждом решении он пробует варианты на копии игры, а затем спрашивает обученную нейросеть, насколько вероятно выиграть забег оттуда, куда ведёт каждый из них.",
    modes_h="Два режима (в настройках)",
    sl="[b]SL[/b] (по умолчанию): как с сохранением и загрузкой, он пробует варианты на точной копии комнаты, в которой вы находитесь, и видит, что там действительно произойдёт (порядок добора карт, случайные исходы). Это жульничество, и так он играет сильнее всего.",
    nosl="[b]NoSL[/b]: только то, что видит игрок. Без подглядывания.",
    mp_h="Мультиплеер (экспериментально)",
    mp="В забеге с другими игроками ИИ даёт вам подсказки, в режиме NoSL. По умолчанию это выключено: включите в консоли игры (клавиша ~) командой [b]ai mp on[/b] — до закрытия игры, или [b]ai mp always[/b] — тогда останется включённым; или переключателем «Multiplayer (experimental)» на вкладке AI в настройках.",
    how_h="Насколько хорошо он играет",
    how_intro="В SL, с Search effort на Medium (Безмолвная и Наследник: XHigh), на тестовых сидах, доля выигранных забегов:",
    how_nosl="В NoSL (ноль читерства, без загрузки сохранений), на тестовых сидах, доля выигранных забегов:",
    req_h="Требования",
    req_version="Игра строго версии [b]v0.114.0[/b], ветка [b]public-beta[/b] в Steam (в свойствах игры в Steam). На другой версии мод сам отключается и ничего не показывает.",
    req_platforms="Windows (x64), Linux (x64) или macOS (Apple Silicon).",
    req_single="Одиночная игра; мультиплеер — экспериментально (см. выше).",
    runs_h="Что он запускает на вашем компьютере",
    runs="Пока игра запущена, мод запускает собственный «мозг» — фоновую программу, в которой думает ИИ, — на половине ваших логических процессоров (не больше 8). Он закрывается вместе с игрой. Больше ничего устанавливать не нужно.",
    src_h="Исходный код и устройство",
    src_code="Открытый исходный код (MIT):",
    src_docs="Как он устроен — на английском, упрощённом и традиционном китайском:",
    disclaimer="Неофициальный фанатский проект, не связанный с Mega Crit и не одобренный ею.",
)
T["tur"] = dict(
    steam="turkish", docs="",
    title="Spire Zero: Slay the Spire 2 için yapay zekâ stratejisti",
    multi="[b]Çok oyunculu destek yakında geliyor.[/b] Şimdiden abone olun ve favorilerinize ekleyin, çıkar çıkmaz elinizde olsun.",
    intro="Oyunun içinde, tek oyunculu oyunlar için bir yapay zekâ:",
    advice="[b]Tavsiye[/b]: her kararda ne yapacağını ve her seçeneğin oyunu kazanma şansını gösterir.",
    autoplay="[b]Otomatik oynama[/b]: istersen bir adımı, turun ya da odanın geri kalanını oynar veya sen duraklatana kadar devam eder.",
    learned="Nasıl oynadığının hiçbir parçası elle yazılmadı ve insanların oyunlarından hiçbir şey öğrenmedi: her şeyi kendi oynadığı oyunlardan öğrendi. Her kararda seçenekleri oyunun bir kopyasında dener, sonra eğitilmiş bir ağa, her seçeneğin götürdüğü yerden oyunu kazanma ihtimalini sorar.",
    modes_h="İki mod (ayarlarda)",
    sl="[b]SL[/b] (varsayılan): kaydedip yüklemek gibi, seçenekleri bulunduğun odanın gerçek bir kopyasında dener; böylece orada gerçekten ne olacağını görür (kart çekme sırası, rastgele sonuçlar). Hile yapar ve en güçlü hâli budur.",
    nosl="[b]NoSL[/b]: yalnızca bir oyuncunun görebildikleri. Gizlice bakmak yok.",
    mp_h="Çok oyunculu (deneysel)",
    mp="Başka oyuncularla oynanan bir oyunda sana NoSL ile tavsiye verir. Varsayılan olarak kapalıdır: oyunun konsolunda (~ tuşu) [b]ai mp on[/b] yazarak oyun kapanana kadar ya da [b]ai mp always[/b] yazarak kalıcı olarak aç; veya ayarların AI sekmesindeki \"Multiplayer (experimental)\" anahtarıyla aç.",
    how_h="Ne kadar iyi oynuyor",
    how_intro="SL'de, Search effort Medium iken (Sessiz ve Naip: XHigh), test seed'lerinde kazandığı oyunların oranı:",
    how_nosl="NoSL'de (sıfır hile, kayıt yüklemek yok), test seed'lerinde kazandığı oyunların oranı:",
    req_h="Gereksinimler",
    req_version="Oyun tam olarak [b]v0.114.0[/b] sürümünde olmalı, yani Steam'in [b]public-beta[/b] dalı (Steam'de oyunun özelliklerinden). Başka bir sürümde mod kendini kapatır ve hiçbir şey göstermez.",
    req_platforms="Windows (x64), Linux (x64) veya macOS (Apple silicon).",
    req_single="Tek oyunculu; çok oyunculu deneyseldir (yukarıya bak).",
    runs_h="Bilgisayarında ne çalıştırır",
    runs="Oyun açıkken mod, yapay zekânın düşündüğü arka plan programı olan kendi beynini mantıksal işlemcilerinin yarısında (en fazla 8) başlatır. Oyunla birlikte kapanır. Başka bir şey kurmana gerek yok.",
    src_h="Kaynak kod ve tasarım",
    src_code="Açık kaynak (MIT):",
    src_docs="Nasıl tasarlandığı; İngilizce, Basitleştirilmiş Çince ve Geleneksel Çince:",
    disclaimer="Resmî olmayan bir hayran projesidir; Mega Crit ile bağlantılı değildir ve Mega Crit tarafından onaylanmamıştır.",
)
T["jpn"] = dict(
    steam="japanese", docs="",
    title="Spire Zero：Slay the Spire 2 の AI 軍師",
    multi="[b]マルチプレイにも今後対応します。[/b]今のうちにサブスクライブしてお気に入りに追加しておけば、対応しだいすぐに使えます。",
    intro="ゲーム内で動く、シングルプレイ用の AI です。",
    advice="[b]アドバイス[/b]：あらゆる選択で、AI ならどうするかと、各選択肢でランに勝てる確率を表示します。",
    autoplay="[b]オートプレイ[/b]：頼めば 1 手だけ、ターンや部屋の残りを、または一時停止するまでずっとプレイします。",
    learned="プレイの仕方は一切人の手で書かれておらず、人間のプレイからも何も学んでいません。すべて自分でプレイしたランから学びました。選択のたびに、ゲームのコピーで各選択肢を実際に試し、その先の局面からランに勝てる確率を、学習したネットワークに尋ねます。",
    modes_h="2 つのモード（設定で切り替え）",
    sl="[b]SL[/b]（デフォルト）：セーブ＆ロードのように、今いる部屋の本物のコピーで選択肢を試すので、そこで本当に何が起きるか（ドローの順番、ランダムな結果）が見えます。ズルですが、いちばん強いモードです。",
    nosl="[b]NoSL[/b]：プレイヤーに見える情報だけを使います。のぞき見はなし。",
    mp_h="マルチプレイ（実験的）",
    mp="ほかのプレイヤーと一緒のランでは、NoSL で助言します。初期設定ではオフです。ゲームのコンソール（~ キー）で [b]ai mp on[/b] と入力するとゲームを閉じるまで、[b]ai mp always[/b] と入力するとずっとオンになります。設定の AI タブにある「マルチプレイ（実験的）」スイッチでもオンにできます。",
    how_h="どれくらい強いか",
    how_intro="SL、探索の深さ「普通」（サイレントとリージェントは「超高」）、テスト用シードで勝ったランの割合：",
    how_nosl="NoSL（不正は一切なし、セーブ＆ロード禁止）、テスト用シードで勝ったランの割合：",
    req_h="動作条件",
    req_version="ゲームのバージョンがちょうど [b]v0.114.0[/b]（Steam の [b]public-beta[/b] ブランチ。Steam のゲームのプロパティから選べます）であること。バージョンが違うと MOD は自動で無効になり、何も表示しません。",
    req_platforms="Windows（x64）、Linux（x64）、macOS（Apple シリコン）。",
    req_single="シングルプレイ。マルチプレイは実験的に対応（上記）。",
    runs_h="PC で動くもの",
    runs="ゲームの起動中、MOD は AI が考えるためのバックグラウンドプログラム（頭脳）を起動し、論理プロセッサの半分（最大 8）を使います。ゲームを終了すると一緒に終了します。ほかに何もインストールする必要はありません。",
    src_h="ソースコードと設計",
    src_code="オープンソース（MIT）：",
    src_docs="設計の解説（英語・簡体字中国語・繁体字中国語）：",
    disclaimer="非公式のファンプロジェクトです。Mega Crit とは関係がなく、承認も受けていません。",
)
T["kor"] = dict(
    steam="koreana", docs="",
    title="Spire Zero: Slay the Spire 2의 AI 책사",
    multi="[b]멀티플레이도 곧 지원합니다.[/b] 지금 구독하고 즐겨찾기에 추가해 두면 나오는 즉시 사용할 수 있습니다.",
    intro="게임 안에서 작동하는 싱글 플레이용 AI입니다.",
    advice="[b]조언[/b]: 모든 선택에서 AI라면 무엇을 할지, 그리고 각 선택지로 런을 이길 확률을 보여 줍니다.",
    autoplay="[b]자동 플레이[/b]: 원하면 한 단계, 턴이나 방의 나머지를 플레이하거나, 일시 정지할 때까지 계속 플레이합니다.",
    learned="플레이 방식은 한 줄도 사람이 작성하지 않았고, 사람의 플레이에서 배운 것도 없습니다. 모두 스스로 플레이한 런에서 배웠습니다. 선택할 때마다 게임의 복사본에서 선택지를 직접 시험해 보고, 각 선택지가 이끄는 상황에서 런을 이길 확률을 학습된 신경망에 묻습니다.",
    modes_h="두 가지 모드 (설정에서 변경)",
    sl="[b]SL[/b] (기본값): 세이브/로드처럼, 지금 있는 방의 실제 복사본에서 선택지를 시험하므로 그곳에서 실제로 무슨 일이 일어날지(카드를 뽑는 순서, 무작위 결과)를 봅니다. 반칙이지만 가장 강한 모드입니다.",
    nosl="[b]NoSL[/b]: 플레이어가 볼 수 있는 정보만 사용합니다. 엿보기 없음.",
    mp_h="멀티플레이 (실험적)",
    mp="다른 플레이어와 함께하는 런에서는 NoSL로 조언합니다. 기본값은 꺼짐입니다. 게임 콘솔(~ 키)에서 [b]ai mp on[/b]을 입력하면 게임을 끌 때까지, [b]ai mp always[/b]를 입력하면 계속 켜집니다. 설정의 AI 탭에 있는 \"Multiplayer (experimental)\" 스위치로도 켤 수 있습니다.",
    how_h="얼마나 잘하나요",
    how_intro="SL에서 Search effort를 Medium으로 두고(사일런트와 리젠트는 XHigh), 테스트 시드에서 이긴 런의 비율:",
    how_nosl="NoSL에서(부정행위 0, 세이브 로드 금지), 테스트 시드에서 이긴 런의 비율:",
    req_h="요구 사항",
    req_version="게임 버전이 정확히 [b]v0.114.0[/b]이어야 합니다. Steam의 [b]public-beta[/b] 브랜치입니다(Steam의 게임 속성에서 선택). 다른 버전에서는 모드가 스스로 꺼지고 아무것도 표시하지 않습니다.",
    req_platforms="Windows (x64), Linux (x64), macOS (Apple 실리콘).",
    req_single="싱글 플레이. 멀티플레이는 실험적으로 지원합니다(위 참고).",
    runs_h="컴퓨터에서 실행되는 것",
    runs="게임이 실행되는 동안 모드는 AI가 생각하는 백그라운드 프로그램(두뇌)을 시작하고, 논리 프로세서의 절반(최대 8개)을 사용합니다. 게임을 끄면 함께 종료됩니다. 따로 설치할 것은 없습니다.",
    src_h="소스 코드와 설계",
    src_code="오픈 소스 (MIT):",
    src_docs="설계 설명 (영어, 중국어 간체, 중국어 번체):",
    disclaimer="비공식 팬 프로젝트이며, Mega Crit과 관련이 없고 Mega Crit의 승인을 받지 않았습니다.",
)
T["ind"] = dict(
    steam="indonesian", docs="",
    title="Spire Zero: ahli strategi AI untuk Slay the Spire 2",
    multi="[b]Dukungan multipemain segera hadir.[/b] Berlangganan dan tambahkan ke favorit sekarang agar bisa langsung dipakai begitu tersedia.",
    intro="AI di dalam game, untuk permainan pemain tunggal:",
    advice="[b]Saran[/b]: di setiap keputusan, AI menunjukkan apa yang akan dipilihnya dan peluang tiap pilihan untuk memenangkan run.",
    autoplay="[b]Main otomatis[/b]: jika diminta, AI memainkan satu langkah, sisa giliran atau ruangan, atau terus bermain sampai kamu menjedanya.",
    learned="Tidak ada satu bagian pun dari cara bermainnya yang ditulis manusia, dan AI ini tidak belajar dari permainan manusia: semuanya dipelajari dari run yang dimainkannya sendiri. Di setiap keputusan, AI mencoba pilihan-pilihan itu pada salinan game, lalu bertanya pada jaringan yang sudah dilatih seberapa besar peluang memenangkan run dari tempat yang dituju setiap pilihan.",
    modes_h="Dua mode (di pengaturan)",
    sl="[b]SL[/b] (bawaan): seperti simpan dan muat, AI mencoba pilihan pada salinan asli ruangan tempat kamu berada, jadi ia melihat apa yang benar-benar akan terjadi di sana (urutan kartu yang ditarik, hasil acak). Ini curang, dan di sinilah ia bermain paling kuat.",
    nosl="[b]NoSL[/b]: hanya yang bisa dilihat pemain. Tanpa mengintip.",
    mp_h="Multipemain (eksperimental)",
    mp="Dalam run bersama pemain lain, AI memberimu saran, dalam NoSL. Fitur ini mati secara bawaan: nyalakan di konsol game (tombol ~) dengan [b]ai mp on[/b], sampai game ditutup, atau [b]ai mp always[/b], agar tetap menyala; atau dengan sakelar \"Multiplayer (experimental)\" di tab AI pada pengaturan.",
    how_h="Seberapa bagus permainannya",
    how_intro="Di SL, dengan Search effort Medium (Si Silent dan Si Regent: XHigh), dengan seed uji, persentase run yang dimenangkan:",
    how_nosl="Di NoSL (nol kecurangan, tanpa memuat ulang simpanan), dengan seed uji, persentase run yang dimenangkan:",
    req_h="Persyaratan",
    req_version="Game harus tepat versi [b]v0.114.0[/b], yaitu cabang [b]public-beta[/b] di Steam (dari properti game di Steam). Di versi lain, mod akan menonaktifkan dirinya dan tidak menampilkan apa pun.",
    req_platforms="Windows (x64), Linux (x64), atau macOS (Apple silicon).",
    req_single="Pemain tunggal; multipemain masih eksperimental (lihat di atas).",
    runs_h="Apa yang dijalankan di komputermu",
    runs="Selama game berjalan, mod menjalankan otaknya sendiri, program latar tempat AI berpikir, di separuh prosesor logismu (paling banyak 8). Program itu berhenti saat game ditutup. Tidak ada lagi yang perlu dipasang.",
    src_h="Kode sumber dan desain",
    src_code="Sumber terbuka (MIT):",
    src_docs="Cara perancangannya, dalam bahasa Inggris, Tionghoa Sederhana, dan Tionghoa Tradisional:",
    disclaimer="Proyek penggemar tidak resmi, tidak berafiliasi dengan maupun didukung oleh Mega Crit.",
)
T["tha"] = dict(
    steam="thai", docs="",
    title="Spire Zero: กุนซือ AI สำหรับ Slay the Spire 2",
    multi="[b]เร็ว ๆ นี้จะรองรับโหมดผู้เล่นหลายคน[/b] สมัครรับและเพิ่มในรายการโปรดไว้ก่อนได้เลย จะได้ใช้ทันทีที่ออก",
    intro="AI ในเกม สำหรับการเล่นคนเดียว:",
    advice="[b]คำแนะนำ[/b]: ทุกการตัดสินใจ AI จะบอกว่ามันจะเลือกอะไร และแต่ละตัวเลือกมีโอกาสชนะรันนี้เท่าไร",
    autoplay="[b]เล่นอัตโนมัติ[/b]: เมื่อสั่ง มันจะเล่นหนึ่งก้าว เล่นจนจบเทิร์นหรือจบห้อง หรือเล่นต่อไปเรื่อย ๆ จนกว่าคุณจะกดหยุด",
    learned="วิธีเล่นของมันไม่มีส่วนไหนที่คนเขียนขึ้น และไม่ได้เรียนจากเกมที่คนเล่น ทุกอย่างเรียนมาจากรันที่มันเล่นเอง ทุกครั้งที่ต้องตัดสินใจ มันจะลองแต่ละตัวเลือกในสำเนาของเกม แล้วถามโครงข่ายที่ฝึกมาแล้วว่า จากสถานการณ์ที่แต่ละตัวเลือกพาไป มีโอกาสชนะรันนี้เท่าไร",
    modes_h="สองโหมด (ในการตั้งค่า)",
    sl="[b]SL[/b] (ค่าเริ่มต้น): เหมือนการเซฟและโหลด มันลองตัวเลือกบนสำเนาจริงของห้องที่คุณอยู่ จึงเห็นว่าในห้องนั้นจะเกิดอะไรขึ้นจริง ๆ (ลำดับการจั่วการ์ด ผลสุ่ม) นี่คือการโกง และเป็นตอนที่มันเล่นเก่งที่สุด",
    nosl="[b]NoSL[/b]: ใช้เฉพาะข้อมูลที่ผู้เล่นมองเห็น ไม่แอบดู",
    mp_h="โหมดผู้เล่นหลายคน (ทดลอง)",
    mp="ในรันที่เล่นกับผู้เล่นคนอื่น มันจะให้คำแนะนำคุณแบบ NoSL ค่าเริ่มต้นคือปิด: เปิดได้ในคอนโซลของเกม (ปุ่ม ~) ด้วย [b]ai mp on[/b] จนกว่าจะปิดเกม หรือ [b]ai mp always[/b] เพื่อเปิดไว้ตลอด หรือเปิดสวิตช์ \"Multiplayer (experimental)\" ในแท็บ AI ของการตั้งค่า",
    how_h="เล่นเก่งแค่ไหน",
    how_intro="ในโหมด SL โดยตั้ง Search effort เป็น Medium (นักฆ่าแห่งความเงียบและ The Regent: XHigh) บนซีดทดสอบ สัดส่วนรันที่ชนะ:",
    how_nosl="ในโหมด NoSL (ไม่โกงเลย ห้ามโหลดเซฟย้อน) บนซีดทดสอบ สัดส่วนรันที่ชนะ:",
    req_h="ความต้องการของระบบ",
    req_version="เกมต้องเป็นเวอร์ชัน [b]v0.114.0[/b] พอดี คือสาขา [b]public-beta[/b] ของ Steam (เลือกได้ในคุณสมบัติของเกมบน Steam) ถ้าเป็นเวอร์ชันอื่น ม็อดจะปิดตัวเองและไม่แสดงอะไรเลย",
    req_platforms="Windows (x64), Linux (x64) หรือ macOS (Apple silicon)",
    req_single="เล่นคนเดียว ส่วนโหมดผู้เล่นหลายคนยังเป็นแบบทดลอง (ดูด้านบน)",
    runs_h="สิ่งที่ทำงานบนคอมพิวเตอร์ของคุณ",
    runs="ระหว่างที่เกมเปิดอยู่ ม็อดจะเริ่มสมองของมันเอง ซึ่งเป็นโปรแกรมเบื้องหลังที่ AI ใช้คิด โดยใช้โปรเซสเซอร์เชิงตรรกะครึ่งหนึ่งของเครื่อง (ไม่เกิน 8) และจะปิดไปพร้อมกับเกม ไม่ต้องติดตั้งอะไรเพิ่ม",
    src_h="ซอร์สโค้ดและการออกแบบ",
    src_code="โอเพนซอร์ส (MIT):",
    src_docs="การออกแบบ เป็นภาษาอังกฤษ จีนตัวย่อ และจีนตัวเต็ม:",
    disclaimer="โปรเจกต์แฟนเมดที่ไม่เป็นทางการ ไม่เกี่ยวข้องและไม่ได้รับการรับรองจาก Mega Crit",
)
T["ara"] = dict(
    steam="arabic", docs="",
    title="Spire Zero: استراتيجي بالذكاء الاصطناعي للعبة Slay the Spire 2",
    multi="[b]دعم اللعب الجماعي قادم قريبًا.[/b] اشترك وأضفه إلى المفضلة الآن لتحصل عليه فور صدوره.",
    intro="ذكاء اصطناعي داخل اللعبة، للعب الفردي:",
    advice="[b]النصائح[/b]: في كل قرار يعرض ما كان سيفعله، وفرصة الفوز بالجولة مع كل خيار.",
    autoplay="[b]اللعب التلقائي[/b]: عند الطلب يلعب خطوة واحدة، أو بقية الدور أو الغرفة، أو يواصل اللعب حتى توقفه مؤقتًا.",
    learned="لم يكتب أحد أي جزء من طريقة لعبه، ولم يتعلم شيئًا من مباريات البشر: تعلّم كل شيء من الجولات التي لعبها بنفسه. في كل قرار يجرّب الخيارات على نسخة من اللعبة، ثم يسأل شبكة مُدرَّبة عن احتمال الفوز بالجولة انطلاقًا من المكان الذي يقود إليه كل خيار.",
    modes_h="وضعان (في الإعدادات)",
    sl="[b]SL[/b] (الافتراضي): مثل الحفظ والتحميل، يجرّب الخيارات على نسخة حقيقية من الغرفة التي أنت فيها، فيرى ما سيحدث فيها فعلًا (ترتيب سحب الأوراق، النتائج العشوائية). إنه غش، وفيه يلعب بأقصى قوته.",
    nosl="[b]NoSL[/b]: ما يراه اللاعب فقط. بلا تلصص.",
    mp_h="اللعب الجماعي (تجريبي)",
    mp="في جولة مع لاعبين آخرين يقدّم لك النصائح، في وضع NoSL. هذه الميزة معطّلة افتراضيًا: فعّلها في وحدة تحكم اللعبة (المفتاح ~) بالأمر [b]ai mp on[/b] حتى إغلاق اللعبة، أو بالأمر [b]ai mp always[/b] لتبقى مفعّلة؛ أو بمفتاح \"Multiplayer (experimental)\" في تبويب AI في الإعدادات.",
    how_h="ما مدى براعته",
    how_intro="في وضع SL، مع ضبط Search effort على Medium (الصامتة والوصي: XHigh)، على بذور الاختبار، نسبة الجولات التي فاز بها:",
    how_nosl="في وضع NoSL (بلا أي غش، ولا إعادة تحميل للحفظ)، على بذور الاختبار، نسبة الجولات التي فاز بها:",
    req_h="المتطلبات",
    req_version="أن تكون اللعبة بالإصدار [b]v0.114.0[/b] تحديدًا، أي فرع [b]public-beta[/b] على Steam (من خصائص اللعبة في Steam). مع أي إصدار آخر يعطّل التعديل نفسه ولا يعرض أي شيء.",
    req_platforms="Windows (x64) أو Linux (x64) أو macOS (Apple silicon).",
    req_single="اللعب الفردي؛ واللعب الجماعي تجريبي (انظر أعلاه).",
    runs_h="ما الذي يشغّله على حاسوبك",
    runs="أثناء تشغيل اللعبة، يبدأ التعديل عقله الخاص، وهو برنامج يعمل في الخلفية يفكّر فيه الذكاء الاصطناعي، على نصف معالجاتك المنطقية (8 على الأكثر). ويتوقف مع إغلاق اللعبة. لا حاجة لتثبيت أي شيء آخر.",
    src_h="الشيفرة المصدرية والتصميم",
    src_code="مفتوح المصدر (MIT):",
    src_docs="كيف صُمّم، بالإنجليزية والصينية المبسطة والصينية التقليدية:",
    disclaimer="مشروع معجبين غير رسمي، لا ينتمي إلى Mega Crit ولا تعتمده.",
)


def gap(label):
    # A full-width colon already spaces what follows it.
    return "" if label.endswith("：") else " "


def name(lang, char):
    return NAMES[lang].get(char) or NAMES["eng"][char]


def dread(lang):
    return NAMES[lang].get("main_menu_ui:DREAD.title") or NAMES["eng"]["main_menu_ui:DREAD.title"]


def ascension(lang, level):
    return NAMES[lang]["main_menu_ui:DAILY_RUN_MENU.ASCENSION"].replace("{ascension}", str(level))


out = ROOT / "distro/workshop"
out.mkdir(exist_ok=True)
for lang, t in T.items():
    rows = [f"[tr][th][/th][th]{ascension(lang, 0)}[/th][th]{ascension(lang, 10)}[/th][th]{dread(lang)}[/th][/tr]"]
    for c in CHARS:
        a0w, a0n, a10w, a10n, dw, dn = WINS[c]
        rows.append(f"[tr][td]{name(lang, c)}[/td][td]{pct(a0w, a0n, lang)}[/td][td]{pct(a10w, a10n, lang)}[/td][td]{pct(dw, dn, lang)}[/td][/tr]")
    nosl = [rows[0]]
    for c in CHARS:
        a0w, a0n, a10w, a10n, dw, dn = WINS_NOSL[c]
        nosl.append(f"[tr][td]{name(lang, c)}[/td][td]{pct(a0w, a0n, lang)}[/td][td]{pct(a10w, a10n, lang)}[/td][td]{pct(dw, dn, lang)}[/td][/tr]")
    # The title on the first line, the description after the blank line (src/Sts2Ai.Workshop reads them so).
    text = f"""{t['title']}

{t['multi']}

{t['intro']}
[list]
[*]{t['advice']}
[*]{t['autoplay']}
[/list]
{t['learned']}

[h2]{t['modes_h']}[/h2]
[list]
[*]{t['sl']}
[*]{t['nosl']}
[/list]

[h2]{t['mp_h']}[/h2]
{t['mp']}

[h2]{t['how_h']}[/h2]
{t['how_intro']}
[table]
{chr(10).join(rows)}
[/table]


{t['how_nosl']}
[table]
{chr(10).join(nosl)}
[/table]

[h2]{t['req_h']}[/h2]
[list]
[*]{t['req_version']}
[*]{t['req_platforms']}
[*]{t['req_single']}
[/list]

[h2]{t['runs_h']}[/h2]
{t['runs']}

[h2]{t['src_h']}[/h2]
{t['src_code']}{gap(t['src_code'])}[url={REPO}]github.com/shinnku-nikaidou/spire-zero[/url]
{t['src_docs']}{gap(t['src_docs'])}[url={DOCS}{t['docs']}]sts2-docs.shinnkunikaidou.workers.dev[/url]

{t['disclaimer']}
"""
    (out / f"{t['steam']}.bbcode").write_text(text)
print(len(T), "languages:", " ".join(t["steam"] for t in T.values()))
