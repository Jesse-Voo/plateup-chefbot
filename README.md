<div align="center">

# PlateUp! ChefBot

**Extra chefs for PlateUp!: bots that join your restaurant as real players, cook from the game's own recipe data, serve and wash up.**

Add a cook, a server and a dishwasher, mark which counters are for what, and tell them what to keep stocked.

![game: PlateUp!](https://img.shields.io/badge/game-PlateUp!-F97316)
![platform: Windows](https://img.shields.io/badge/platform-Windows-0078D6)
![language: C#](https://img.shields.io/badge/language-C%23-239120)
![license: MIT](https://img.shields.io/badge/license-MIT-green)

</div>

---

## What it does

ChefBot is a local mod. Each bot joins your game as an extra player and is driven with the same controller input a human produces: it walks, grabs, puts things down, holds the interact button to chop or wash, and presses ready-up at night. There is no memory hacking and no game file is changed.

It does not have recipes hard-coded. At start-up it reads every item, process and appliance from the game's own data, works out how to make each dish in *your* kitchen, and writes the result to `cookbook.txt` so you can see what it knows.

### Roles

| Role | Job |
| --- | --- |
| **Cook** | Keeps your stock list ready: cooked pizza, steaks of every doneness, chopped toppings. Never touches plates. |
| **Server** | Plates what the cooks made and serves it to the customers. Waits for the cook instead of cooking. |
| **Dishwasher** | Collects dirty plates, washes them, restocks clean plates. |
| **All-rounder** | Does all three (meant to be used alone). |

You can run as many bots as the restaurant has free player slots (see `max_players` below).

### Features

- **Recipe planner** built from the game data: fetching, chopping, kneading, cooking, merging into groups in an order the game accepts, slicing portions off bigger items (pizza, oil).
- **Stock list with toppings:** "keep 2 plain pizzas and 1 with mushrooms ready". Plain and topped items are tracked separately, so a plain order is never served a topped pizza.
- **Per-bot recipes:** give each bot its own stock list, for example one cook that makes mushroom pizza and one that makes plain.
- **Names and looks:** name your bots; each gets a colour, hat and outfit that match its job (cook, server, dishwasher). The names show in the player bar.
- **Dish rack:** the dishwasher puts dirty plates on the Dirty Plate Stack (or a rack / soaking sink) first, then collects the clean plates.
- **Sharp Knife:** one cook picks up the Sharp Knife from a Sharp Knife Provider (chopping 3x faster) each day (`use_knife`). Choose who gets it per bot with `knife=high | normal | off` (or the Knife button in the F1 panel).
- **Bins:** rubbish such as corn husks is thrown into a bin with room; a full bin is emptied into the outside bin first (`empty_bins`).
- **Sides:** servers add a customer's side order (corn on the cob, onion rings ...) to the plate, and cooks keep side dishes in stock (`serve_sides`, `make_sides`).
- **Mopping:** a dishwasher with nothing to wash picks up a mop from a Mop Bucket and cleans the floors, and puts it down as soon as there are plates again (`mop_floors`).
- **Trays:** if you have a Tray Stand, the dishwasher picks up a tray and carries two dirty plates per trip (`use_tray` in the config).
- **Counter marking:** choose which counters finished products go on (per product) and which are the bots' working counters.
- **In-game map (F1):** a top-down view of your kitchen where you click counters, edit the stock list and manage the bots.
- **Oven handling:** switches the oven on, waits for the result, switches it off, then takes the food out.
- **Works with any layout:** pathfinding uses the game's own reachability rules.
- **More than 4 players:** the game's player limit is raised (default 8, up to 16), for friends and bots alike.
- **Bots don't make the restaurant harder:** they are ignored when the game scales customers per day (and money, queue patience and fire spread) by player count, so only real players count.

## Controls

| Key | Action |
| --- | --- |
| **F8** | Start / stop the bots |
| **F1** | Open / close the kitchen map |
| **F7** | Add another bot |
| **F5** | Mark the counter you are facing as an **output** counter (finished products) |
| **F6** | Mark the counter you are facing as a **stash** counter (bots' working space) |
| **F3** | Choose which product the next output mark is for |
| **F4** | Clear all counter marks |
| **F2** | Reload `config.txt` |
| **F9** | Dump the current kitchen to `kitchen.txt` (for debugging) |

On the map, left-click a counter to cycle it (output, stash, off) and right-click to clear it.

## Configuration

Settings live in `%USERPROFILE%\AppData\LocalLow\It's Happening\PlateUp\ChefBot\config.txt`. The map window edits the same file.

```text
# shared stock list, in priority order (empty everywhere = the bots decide)
make Pizza - Cooked x2
make Steak - Medium x2

# the bots: a name is optional, and 'make' lines right below a bot are THAT bot's own recipes
bot cook "Mushroom Chef" knife=high
  make Pizza - Cooked with Mushroom - Chopped x1
bot cook "Plain Chef"
  make Pizza - Cooked x2
bot server "Sam"
bot dishwasher

# the dishwasher puts dirty plates in appliances with these words in their name first
dish_rack_names = Rack, Soak

# the dishwasher uses a tray from a Tray Stand to carry two plates at once
use_tray = true

# cooks use the Sharp Knife; servers/cooks handle side dishes
mop_floors = true
use_knife = true
serve_sides = true
make_sides = true

# optional: pick the hat and outfit for a job (names are listed in cosmetics.txt)
outfit cook : Chef Hat , Chef Jacket

# where things go (set with F5 / F6 or on the map)
output * : 5,2 5,3
stash : 9,4

# players
max_players = 8                  # the game's own limit is 4; 4-16 allowed
bots_count_as_players = false    # false = only real players raise the customers per day
```

Item names are the game's own names: see `cookbook.txt` and `gamedata.txt`, and `cosmetics.txt` for hats and outfits. They are written next to the config on every start.

## Install

### From a release

Download `ChefBot-*.zip` from the [Releases](https://github.com/Jesse-Voo/plateup-chefbot/releases) page and extract it into your PlateUp! folder so you end up with `PlateUp\Mods\ChefBot\ChefBot.dll` (for a Steam install: `...\steamapps\common\PlateUp\PlateUp\Mods`).

### From source

You need the game, the [.NET SDK](https://dotnet.microsoft.com/download) and a Windows PC.

```powershell
git clone https://github.com/Jesse-Voo/plateup-chefbot
cd plateup-chefbot
dotnet build -c Release
```

The build references the game's own libraries and copies `ChefBot.dll` to `PlateUp\Mods\ChefBot\`. If PlateUp! is not in the default Steam folder, pass its path:

```powershell
dotnet build -c Release -p:GameDir="D:\Games\steamapps\common\PlateUp\PlateUp"
```

Restart the game (mods only load at launch), open a restaurant, and press **F8** during the night phase. No other mod (KitchenLib or similar) is needed.

## How it works

- **Join:** each bot sends input through the game's own local-input route, exactly like an extra controller, so it becomes a normal player (with the game's own player limit).
- **Move and act:** it converts a target position into camera-relative movement and holds grab / interact for a few frames, then checks the result in the game state.
- **Plan:** a bottom-up solver computes the cheapest way to obtain every item in the current kitchen (providers, workstations, splits, group merges) and rebuilds a plan tree for the item it needs.
- **Coordinate:** bots claim orders and stock lines so two of them do not make or serve the same thing.

## Status

Built and tested by watching it in one restaurant (pizza kitchen, game version 1.5.3).

**Tested:** joining as a player, pathfinding, ready-up, the whole pizza chain (dough, oil, sauce, cheese, bake, stop the oven, take it out, stock it) and cooking steak.

**Not tested, so expect rough edges:** the Server and Dishwasher roles, the dish rack, the tray, mopping, the sharp knife, sides (and husking corn), bot names and outfits, per-bot recipes, running more than one bot at once, the F1 map, topping variants (for example mushroom pizza), the raised player limit, bots being ignored in the customer count, and hosting a networked session.

**Known gaps (dishes it cannot plan yet):** stir-fry, the pasta dishes (spaghetti bolognese, pomodoro, white sauce), pumpkin soup and pie, corn on the cob, oysters, tea and birthday cake. They cook in a wok or pot and are portioned out of it, or use ingredients that come from garden plants, and the mod does not model that yet.

**Multiplayer:** the host's PC runs the whole kitchen, so the bots only work on the **host's** PC. A client cannot run them. The raised player limit applies to the game's own player manager; an online lobby may still have its own cap.

## Disclaimer

This is an unofficial fan mod. It is not affiliated with or endorsed by It's Happening or Yogscast Games, and it contains no game code or assets: it only talks to the game through its public mod loader. Use it in your own games and with friends who agree to it, and follow the game's terms. A modded game is flagged as modded by the game itself.

## License

MIT, see [LICENSE](LICENSE).
