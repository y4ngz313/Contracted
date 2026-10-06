# Contracted

![About Y4NGZ](https://raw.githubusercontent.com/y4ngz313/Y4NGZMedia/main/shared/about-y4ngz.png)

I am Y4NGZ, an up and coming rapper out of NYC/Detroit. All of my mods are
inspired by the music I write. If you are interested in knowing more about me,
check out my SoundCloud: https://soundcloud.com/y4ngz

## Generative AI usage

The entire code base of this mod was created with generative AI. Assets are
free, licensed, paid for, or generated with AI tools. If you do not wish to play
with AI-generated content, do not install this mod.

## What Contracted does

The goal of Contracted is to enhance the vanilla gameplay loop by adding "Contracts"
as a guaranteed part of every moon landing, including rewards and penalties for successfully
completing or failing a contract. All contracts scale with risk level, and are intended
to be vanilla friendly in regards to assets and theme. Additionally there are "Escalations"
which are random events that have a chance to occur mid-round to disrupt the mission including:
a chainsaw killer, a special-ops team, and a mysterious congregation outside of the facility. All
features can be individually enabled or disabled in the config.

**How it works**

1. Route to a moon. The terminal and ship monitor show the contract attached to it.
2. Land and complete the objective before you leave, or fail it.
3. The performance report settles it: success lowers your quota by the moon's risk percentage,
   failure raises it by the same percentage.

| Risk | D | C | B | A | S |
| --- | --- | --- | --- | --- | --- |
| Quota +/- | 15% | 15% | 20% | 25% | 30% |

Defaults shown. The host can change the table per risk tier or per contract.

![Contract HUD](https://raw.githubusercontent.com/y4ngz313/Y4NGZMedia/main/contracted/ui.gif)

![Contract on the terminal](https://raw.githubusercontent.com/y4ngz313/Y4NGZMedia/main/contracted/terminal.png)

![Contract assignment on the ship monitor](https://raw.githubusercontent.com/y4ngz313/Y4NGZMedia/main/contracted/ship-monitor.png)

![End-of-round performance report](https://raw.githubusercontent.com/y4ngz313/Y4NGZMedia/main/contracted/performance-report.gif)

## Requirements

Your mod manager installs these automatically:

- **BepInExPack**
- **LethalLib**
- **DawnLib**
- **CSync**
- **LethalNetworkAPI**
- **LethalCompany InputUtils**
- **Y4NGZCore**

## Contracts

Contracts are assigned when you route to a moon and are locked to that route. Every time the
ship returns to orbit contracts are re-rolled and moons get a new contract.

### Blackout Audit

The facility starts without power. Find and reset every breaker box; each one restores part of
the power, and moon risk sets how many breakers there are and how often the power flickers back on.

<details>
<summary>Show preview</summary>
<br>
<img src="https://raw.githubusercontent.com/y4ngz313/Y4NGZMedia/main/contracted/blackout-audit.gif" alt="Blackout Audit" width="640">
</details>

### Survey

Carry survey beacons to marked rooms deep in the facility and deploy each one.

<details>
<summary>Show preview</summary>
<br>
<img src="https://raw.githubusercontent.com/y4ngz313/Y4NGZMedia/main/contracted/survey.gif" alt="Survey" width="640">
</details>

### Containment Breach

Stay inside the locked-down facility and survive every enemy wave; leaving before the last wave
fails the contract. Enemies come from the moon's own enemy pool, and the config sets how many spawn.

<details>
<summary>Show preview</summary>
<br>
<img src="https://raw.githubusercontent.com/y4ngz313/Y4NGZMedia/main/contracted/containment-breach.gif" alt="Containment Breach" width="640">
</details>

### Shadow Raid

Collect the drill from the drop ship, drop it in front of the sealed container, wait for it to
finish drilling, then recover the hard drive inside.

<details>
<summary>Show preview</summary>
<br>
<img src="https://raw.githubusercontent.com/y4ngz313/Y4NGZMedia/main/contracted/shadow-raid.png" alt="Shadow Raid" width="640">
</details>

### Payload

Push a heavy cargo cart through the facility and deliver it to the ship.

<details>
<summary>Show preview</summary>
<br>
<img src="https://raw.githubusercontent.com/y4ngz313/Y4NGZMedia/main/contracted/payload.gif" alt="Payload" width="640">
</details>

### Whistleblower

An informant is hiding deep in the facility and runs for an exit. Find and kill him before he
escapes, or the contract fails.

### Pest Control

Lure a monster onto the containment platform, drop the cage on it, and leave the moon with it.

<details>
<summary>Show preview</summary>
<br>
<img src="https://raw.githubusercontent.com/y4ngz313/Y4NGZMedia/main/contracted/pest-control.gif" alt="Pest Control" width="640">
</details>

### Waste Disposal

Feed scrap into the waste incinerator up to the target value, repair any malfunctions, and wait
for the burn to finish before you leave.

<details>
<summary>Show preview</summary>
<br>
<img src="https://raw.githubusercontent.com/y4ngz313/Y4NGZMedia/main/contracted/waste-disposal.png" alt="Waste Disposal" width="640">
</details>

### Lategame Upgrades contracts (optional)

With **Lategame Upgrades** installed, five of its contracts join the rotation. They are
assigned like any other contract and play as Lategame Upgrades built them, with its loot on
top of the contract reward:

- **Data Retrieval** - Find the abandoned laptop, run `bruteforce` on its IP from the ship
  terminal, log in, and download `survey.db`.
- **Extermination** - Kill the hoarding bugs guarding the nest, then destroy the nest.
- **Extraction** - Find the downed scavenger, treat him with a medkit, and carry him back to
  the ship.
- **Exorcism** - Find the pentagram, run `demon` with the demon's name on the terminal for its
  item list, and place those items in the pentagram. A wrong item calls ghost girls onto the
  crew and fails the contract.
- **Defusal** - Find the armed device, run `lookup` with its serial on the terminal, and cut
  the wires in the order it gives before the countdown on the card runs out.

Contracted turns off Lategame Upgrades' own contract shop by default; set
`Contracts / Suppress LGU Contract Shop` to false to buy those contracts yourself.

## Escalations

Any landing can escalate, independently of the contract roll, and you will not know until it
starts.

### The Stranger

A chainsaw killer hunts the crew by sound and sight, sprints when he loses you, and can only be
knocked down temporarily; he chases one victim at a time, marked on the UI, and moves to the
next when that victim dies. If **ShipSystems** is installed he can also destroy the ship's battery.

<details>
<summary>Spoiler - show The Stranger</summary>
<br>
<img src="https://raw.githubusercontent.com/y4ngz313/Y4NGZMedia/main/contracted/the-stranger.gif" alt="The Stranger" width="640">
</details>

### Blacksite Retrieval

A rival squad arrives by ship, either landing or hovering while its soldiers rope down, and its
two guards patrolling outside carry the Assault Rifle when **Better Armory** is installed. Inside,
melee raiders go for your objective on Whistleblower, Payload and Shadow Raid (otherwise the
Apparatus or the facility's scrap) and call for backup when they meet danger.

<details>
<summary>Spoiler - show Blacksite Retrieval</summary>
<br>
<img src="https://raw.githubusercontent.com/y4ngz313/Y4NGZMedia/main/contracted/blacksite-retrieval.gif" alt="Blacksite Retrieval" width="640">
<br><br>
<img src="https://raw.githubusercontent.com/y4ngz313/Y4NGZMedia/main/contracted/blacksite-retrieval-2.gif" alt="Blacksite drop" width="640">
<br><br>
<img src="https://raw.githubusercontent.com/y4ngz313/Y4NGZMedia/main/contracted/blacksite-idle.gif" alt="Blacksite soldiers holding position" width="640">
</details>

### The Congregation

Cultists gather outside the facility to perform a ritual. Kill the Cult Leader or every cultist
before the ritual finishes, or it summons a monster.

<details>
<summary>Spoiler - show The Congregation</summary>
<br>
<img src="https://raw.githubusercontent.com/y4ngz313/Y4NGZMedia/main/contracted/congregation.gif" alt="The Congregation" width="640">
</details>

## Configuration

| Section | What it controls |
| --- | --- |
| **Each contract** | Every contract has its own section with an `Enabled` switch, a selection weight, a quota-unlock gate, optional moon/constellation/interior allow and deny lists, and that contract's own balance values. Turn off any contract you don't want, or make a favorite roll more often. |
| **`Contracts`** | Rewards and penalties default to a shared risk-tier table in the `Contracts` section, and every contract can override its own reward or failure penalty percent. |
| **`Quota`** | Controls the starting quota, per-completion increase, first-quota player scaling, and how crew deaths punish the quota (`Death Penalty Mode`: Off, Static, or Dynamic). |
| **`Escalations`** | Has a master switch plus per-escalation enables, chances, delays, and headcounts. |
| **`The Stranger`** | Carries his volumes, speeds, and stagger/finisher tuning. |

Risk scaling is one line per value: entries like
`Breakers Required By Risk = C=1, B=2, A=3, S=3` set all four tiers at once.

> Settings marked "Host only" are read from the host's config in multiplayer.
The configuration file is `BepInEx/config/com.y4ngz.company.cfg`.

## Compatibility

Independent installation: Contracted needs only Y4NGZCore and the libraries listed under
Requirements. The other Y4NGZ packages are optional companions - install any combination
and each one lights up its extra behavior.

The required Core package is `Y4NGZ313-Y4NGZCore-1.0.10` or newer.

| Mod | What it adds |
| --- | --- |
| **LethalCCTV** | Contract camera pings, mainframe and Company Stash integration, and facility security reactions; Contracted also feeds the stash loot table. |
| **Y4NGZ Monitor Takeover** | Quota milestones and takeover broadcasts. |
| **Y4NGZ Upgrades** | Contract XP sources and contract-aware upgrades. |
| **LethalConstellations** | Constellation based contract blacklist/whitelist and weights. |
| **FacilityMeltdown** | A failed Defuse starts the meltdown sequence. |
| **Lategame Upgrades** | Its five contracts (Data Retrieval, Extermination, Extraction, Exorcism, Defusal) join the Contracted rotation, assigned by the Company instead of bought from its shop. |
| **More Company** | Contracted Performance Report re-sized to fit More Company's Performance report. |
| **Better Armory** | Blacksite Retrieval's two patrolling guards outside are armed with the Assault Rifle. |
| **MoonsPlus** | Compatible with MoonsPlus. |

## Asset credits

- [Chainsaw Attacks](https://assetstore.unity.com/packages/3d/animations/chainsaw-attacks-227373)
- [Chainsaw Industrial with Blood FX](https://assetstore.unity.com/packages/3d/props/weapons/chainsaw-industrial-with-blood-fx-88655)
- [ASGS Interactive IED Briefcase](https://assetstore.unity.com/packages/3d/props/weapons/asgs-interactive-ied-briefcase-290206)
- [HQ Shipping Container Modular](https://assetstore.unity.com/packages/3d/props/industrial/hq-shipping-container-modular-203201)
- [Keypad FREE](https://assetstore.unity.com/packages/3d/props/electronics/keypad-free-262151)
- [The Humble Bundle](https://pizzadoggy.itch.io/the-humble-bundle)
- [Payday 2 Drill](https://sketchfab.com/3d-models/payday-2-drill-3bb1dd716d2a4b8b9d794c6da71828ea)
- [Ophanim Throne Angel](https://assetstore.unity.com/packages/3d/characters/creatures/ophanim-throne-angel-281106)
- [SCP MTF Nine-Tailed Fox](https://sketchfab.com/3d-models/scp-mtf-ninetailedfox-2673984b99a24836986c9987d2977100)

## GitHub

Source code: https://github.com/y4ngz313/Contracted

## Bugs

Report bugs at https://github.com/y4ngz313/Contracted/issues.
