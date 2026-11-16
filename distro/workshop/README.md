# The Steam Workshop item

The mod goes to the Workshop as one item: the folder `distro/build.sh` makes (`out/Sts2Ai/`), with a title and a
description in each of the game's 17 languages. The game reads a subscribed item's folder as it reads `mods/`.

| file | what |
| --- | --- |
| `LANGUAGE.bbcode` | the title on the first line, a blank line, then the description in Steam's BBCode; `LANGUAGE` is Steam's API language code (`english`, `schinese`, `tchinese`, `koreana`, `latam`, ...) |
| `make.py` | writes every `LANGUAGE.bbcode`: the texts and the win numbers are in it, the characters' and Ascension's names are the game's own |
| `names.mjs` | reads those names from the game's `.pck` into `tmp/workshop/names.json`, which `make.py` reads |
| `item.txt` | the item's id, written once by `workshop create` |
| `preview.html`, `preview.png` | the item's preview image, 1024 x 1024 (Steam takes up to 1 MB), and the page it is rendered from in a headless Chrome |

```bash
node distro/workshop/names.mjs "PATH/Slay the Spire 2/SlayTheSpire2.pck" ascension main_menu_ui > tmp/workshop/names.json
python3 -I distro/workshop/make.py
google-chrome --headless=new --hide-scrollbars --user-data-dir="$PWD/tmp/workshop/chrome" --window-size=1024,1024 \
  --virtual-time-budget=8000 --screenshot="$PWD/distro/workshop/preview.png" "file://$PWD/distro/workshop/preview.html"
```

## Uploading

`src/Sts2Ai.Workshop` (the `workshop` tool) uploads as the game, through the Steam client running and logged in on
this machine, with the game's own Steamworks.NET and `libsteam_api.so`; no password goes anywhere. Run it from the
repository's root:

```bash
dotnet build src/Sts2Ai.Workshop -c Release
src/Sts2Ai.Workshop/bin/Release/net9.0/workshop check
src/Sts2Ai.Workshop/bin/Release/net9.0/workshop create
src/Sts2Ai.Workshop/bin/Release/net9.0/workshop upload --content out/Sts2Ai --preview distro/workshop/preview.png --visibility private --branches public-beta:public-beta --note "what changed"
```

- `check` says what Steam sees: logged on, the game's branch, and the item's title, visibility, size and last update.
- `create` makes the item once and writes its id to `item.txt`.
- `upload` sends the folder, the preview (`--preview FILE`), the visibility, the game branches it supports and the
  English texts in one update, then each other language's texts in an update of its own (Steam takes one language an
  update). A new item stays hidden until its owner accepts the Steam Workshop agreement on the website.
- Steam allows one machine of an account to run the game at a time, and the tool runs as the game: close the game on
  every machine first, or Steam logs this one off ("Logged In Elsewhere") and the upload fails.
- The first update that carries new content can end in `k_EResultTimeout` at its commit, with the content already on
  Steam; the same command again goes through.
- `--branches public-beta:public-beta`: the mod runs only on the game version it was built for, which is on Steam's
  `public-beta` branch; on another branch the game itself says the item does not support it.
