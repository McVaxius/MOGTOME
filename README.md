# M.O.G.T.O.M.E.

---

**Help fund my AI overlords' coffee addiction so they can keep generating more plugins instead of taking over the world**

[☕ Support development on Ko-fi](https://ko-fi.com/mcvaxius)

[XA and I have created some Plugins and Guides here at -> aethertek.io](https://aethertek.io/)
### Repo URL:
```
https://aethertek.io/x.json
```

---

**Management Of Grand Tome Operations & Management Engine**

A Dalamud plugin for automated duty farming and tome acquisition in FFXIV.

---

## Overview

MOGTOME automates farming of The Praetorium (99 runs) and The Porta Decumana (until daily reset) for efficient tome acquisition. Converted from the G.O.O.N. SND script to a native Dalamud plugin.

### Features

- ✅ **Automated Duty Queueing**: Automatically queues for Praetorium (1-99) and Decumana (100+)
- ✅ **Daily Reset Detection**: Auto-resets counters at 7 AM UTC (3 AM EST / 12 AM PST)
- ✅ **Repair Management**: Self-repair with dark matter or NPC repair automation
- ✅ **Food Management**: Auto-consume food when buff expires
- ✅ **Combat Integration**: BossMod or Rotation Solver Reborn support
- ✅ **Filtered Statistics**: Unsynced testing runs stay out of stats unless you explicitly opt in
- ✅ **Persistent Run Counters**: Duty counters and summary stats save against the active account after login and duty completion
- ✅ **Stuck Detection**: Auto-recovery from stuck states
- ✅ **Boss Mechanics**: Tank mitigation, potion usage, limit breaks
- ✅ **Per-Character Configuration**: Separate settings for each character

---

## Requirements

### Required Plugins
- **AutoDuty** - Duty path execution when ADS duty backend is disabled
- **ADS (AI Duty Solver)** - Inn entry handoff through `/ads enterinn`; optional duty backend when enabled
- **vnavmesh** - Navigation and pathfinding
- **XA Slave** - Provides `/xa skipcutscenes on` startup control
- **YesAlready** - Auto-confirm dialogs
- **BossMod Reborn** OR **Rotation Solver Reborn** - Combat automation

### Optional Plugins
- **Automaton (Pandora)** - AutoQueue tweak

### Game Configuration (MANDATORY)
- **NOT in controller mode** (causes chat spam)
- **Duty Finder**: Unrestricted Party + Level Sync enabled
- **XA Slave**: Installed and loaded. MOGTOME runs `/xa skipcutscenes on` before each manual start
- **AutoDuty**: "Leave Duty" disabled OR "Only when duty complete"
- **YesAlready**: Configured for repair, exit, sealed area dialogs

See [how-to-import-plugins.md](how-to-import-plugins.md) for detailed setup instructions.

---

## Installation

### Development Version

1. Clone this repository
2. Open `MOGTOME.sln` in Visual Studio 2022 or JetBrains Rider
3. Build the solution (Debug or Release)
4. Add the DLL path to Dalamud Dev Plugin Locations:
   - `/xlsettings` → Experimental → Dev Plugin Locations
   - Add: `D:\temp\MOGTOME\MOGTOME\bin\x64\Debug\MOGTOME.dll`
5. Enable in `/xlplugins` → Dev Tools → Installed Dev Plugins

### Release Version

*Once published to official Dalamud repository:*

1. `/xlplugins` → Search for "MOGTOME"
2. Click Install
3. Enable the plugin

---

## Usage

### Starting the Bot

1. Ensure you're outside a duty
2. Open MOGTOME: `/mogtome`
3. Click **Start** button
4. MOGTOME sends `/xa skipcutscenes on`, then queues, runs duties, repairs, and consumes food automatically

### Configuration

1. Open config: `/mogtome config`
2. Configure settings per character:
   - Duty counter, repair threshold, food, potions, etc.
3. Settings save automatically

### Commands

- `/mogtome` - Open status window
- `/mog config` - Open configuration window
- `/mog start` - Start the bot
- `/mog stop` - Stop the bot
- `/mog inn` - Delegate inn entry to ADS with `/ads enterinn`
- `/mog status` - Print current status

---

## How It Works

1. **Queue Phase**: Queues for The Praetorium (runs 1-99) or The Porta Decumana (runs 100+)
2. **Duty Phase**: AutoDuty handles navigation, BossMod/RSR handles combat
3. **Boss Mechanics**: Automatic tank mitigation, potion usage, limit breaks
4. **Completion**: Calculates completion time, leaves duty, increments counter
5. **Maintenance**: Auto-repair when threshold reached, auto-consume food when buff expires
6. **Daily Reset**: Auto-resets counter at 7 AM UTC

---

## Recommended Party Composition

### Standard (Balanced)
- WAR + SCH + 2 DPS

### Fast Clears (9:50-10:00)
- GNB + 3 MCH

### Notes
- GNB has best survivability with automation
- MCH has highest DPS for level 50 content
- SCH > SGE for healer DPS

---

## Known Issues

1. **AutoDuty Path Corruption**: Use 1 client per Dalamud folder
2. **Movement Type Change**: Game sometimes switches Legacy ↔ Standard
3. **Job Change Issues**: `/xlkill` and restart client if issues occur
4. **YesAlready Disabled**: Ensure YesAlready is enabled before starting

---

## Development

### Project Structure

```
MOGTOME/
├── MOGTOME/
│   ├── Models/          # Data models
│   ├── Services/        # Core services
│   ├── Windows/         # UI windows
│   ├── IPC/            # Plugin IPC integrations
│   ├── Configuration.cs
│   └── Plugin.cs
├── PROJECT_GAMEPLAN.md  # Detailed project plan
├── CHANGELOG.md         # Version history
└── README.md           # This file
```

### Building

```bash
dotnet build -c Debug   # Debug build
dotnet build -c Release # Release build
```

### Contributing

See [PROJECT_GAMEPLAN.md](PROJECT_GAMEPLAN.md) for detailed technical documentation and implementation phases.

---

## Credits

- **Original Script**: G.O.O.N. (Generally Ordered Optimized Navigation) by dhogGPT
- **Inspiration**: @Akasha, @Ritsuko for ideas and code
- **Dependencies**: AutoDuty, vnavmesh, XA Slave, YesAlready, BossMod/RSR

---

## License

This project is licensed under the same terms as the Dalamud Plugin Template.

---

## Support

For issues, bugs, or feature requests:
1. Check CHANGELOG.md for recent changes
2. Review PROJECT_GAMEPLAN.md for technical details
3. Check Dalamud log: `/xllog`
4. Report issues with full error logs

---

**Happy tome farming!** 🎮
