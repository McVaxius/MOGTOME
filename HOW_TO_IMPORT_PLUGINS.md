# How to Import M.O.G.T.O.M.E. Plugin

## 🚀 Quick Start Guide

### Prerequisites
- **FFXIV**: Active subscription and game installed
- **Dalamud**: Plugin framework installed
- **.NET 7.0 Runtime**: Required for plugin execution

## 📦 Installation Steps

### Step 1: Locate Your Dalamud Folder
1. Open FFXIV
2. Open the Dalamud interface (XLPlugins button near chat)
3. Click **Settings** → **Open Dalamud Folder**
4. This opens your Dalamud installation directory

### Step 2: Navigate to Plugin Directory
1. In the Dalamud folder, navigate to: `installedPlugins\`
2. Create a new folder named: `MOGTOME\`
3. The full path should be: `[DalamudFolder]\installedPlugins\MOGTOME\`

### Step 3: Copy Plugin Files
1. Navigate to your downloaded MOGTOME folder
2. Copy **all files and folders** from the MOGTOME directory
3. Paste them into the Dalamud plugin folder created in Step 2

### Step 4: Install Dependencies
Before using MOGTOME, ensure these plugins are installed:

#### Required Plugins
```
AutoDuty
├── Download from: [Plugin Repository]
├── Install via Dalamud Installer
└── Configure: Settings → Duty Finder → Unsync+LevelSync

VNavMesh
├── Download from: [Plugin Repository]
├── Install via Dalamud Installer
└── Configure: Default settings acceptable

BossMod
├── Download from: https://love.puni.sh/ment.json
├── Install via custom repository
└── Configure: Import AD presets

Rotation Solver Reborn
├── Download from: https://raw.githubusercontent.com/FFXIV-CombatReborn/CombatRebornRepo/main/pluginmaster.json
├── Install via custom repository
└── Configure: No setup required

SimpleTweaks
├── Download from: [Plugin Repository]
├── Install via Dalamud Installer
└── Configure: Enable "Fix '/target' Command"

Cutscene Skipper
├── Download from: [Plugin Repository]
├── Install via Dalamud Installer
└── Configure: Enable all cutscene types

YesAlready
├── Download from: [Plugin Repository]
├── Install via Dalamud Installer
└── Configure: See YesAlready Configuration below
```

### Step 5: Configure YesAlready
1. Open YesAlready settings
2. Add these dialog patterns:
   ```
   YesNo:
   - /Repair all displayed items for.*/
   - /Exit.*/
   - /Move immediately to sealed area.*/
   
   Lists:
   - /Retire to an inn room.*/
   
   Bothers:
   - Duties -> ContentFinderConfirm [x]
   ```

### Step 6: Load the Plugin
1. Restart FFXIV (or reload Dalamud with `/xlreload`)
2. Open Dalamud interface
3. Find **M.O.G.T.O.M.E.** in the plugin list
4. Click **Install** or **Enable**
5. Plugin should appear in your plugin list

### Step 7: Initial Setup
1. Type `/mogtome` in chat to open the main window
2. Configure basic settings:
   - Daily target (default: 99 duties)
   - Food item (default: Orange Juice)
   - Repair threshold (default: 25%)
3. Save configuration

## 🔧 Configuration Details

### Game Settings
1. **Controller Mode**: DISABLED (causes chat issues)
2. **Duty Finder**: Set to Unsync+LevelSync
3. **AutoDuty**: Select W2W Ritsuko path for Praetorium

### Performance Optimization (Optional)
To reduce CPU/GPU usage:
```
Custom Resolution Scaler:
├── Gameplay → Pixelated → 0.1 → Enabled
└── Result: N64/PS1 style graphics

Chillframes:
├── 15 FPS out of combat
├── 30 FPS in combat
└── Result: Significant CPU/GPU reduction
```

### Party Configuration
**Recommended setups:**
- **WAR SCH SMN/MCH MNK** (balanced)
- **WAR SCH MCH MCH** (high DPS)
- **GNB SGE MCH MCH** (advanced)
- **BLU BLU BLU BLU** (blue mage)

** healer settings (RSR):**
- SCH: Disable Adloquim, Succor, Physick
- SGE: Disable GCD-attack, Ruin, Ruin II

## 🚨 Troubleshooting

### Plugin Won't Load
**Symptoms**: Plugin not visible in Dalamud list
**Solutions**:
1. Verify .NET 7.0 runtime is installed
2. Check for missing dependencies
3. Ensure all files are copied correctly
4. Restart FFXIV completely

### Errors on Startup
**Symptoms**: Red error messages in chat
**Solutions**:
1. Check Dalamud log file
2. Verify AutoDuty is properly configured
3. Ensure all required plugins are installed
4. Try reloading with `/xlreload`

### AutoDuty Issues
**Symptoms**: AutoDuty not responding or pathing issues
**Solutions**:
1. One Dalamud folder per client (no sharing)
2. Clear VNavMesh cache if pathing fails
3. Restart client if issues persist
4. Verify path file is correctly selected

### Performance Issues
**Symptoms**: FPS drops, stuttering
**Solutions**:
1. Enable performance optimization settings
2. Reduce client count if running multiple
3. Check for conflicting plugins
4. Monitor system resources

### Stuck Detection Issues
**Symptoms**: Frequent stuck recovery attempts
**Solutions**:
1. Increase stuck timeout in settings
2. Verify VNavMesh is working properly
3. Check for custom resolution conflicts
4. Ensure path file is appropriate

## 📱 Commands

### Basic Commands
```
/mogtome          - Open main window
/mogtome config   - Open configuration
/mogtome start    - Start automation
/mogtome stop     - Stop automation
/mogtome status   - Show current status
/mogtome reset    - Reset daily counter
```

### Debug Commands
```
/mogtome debug    - Toggle debug mode
/mogtome test     - Test stuck detection
/mogtome backup   - Create configuration backup
```

## 🔄 Updates and Maintenance

### Updating the Plugin
1. Close FFXIV
2. Replace files in the plugin folder
3. Restart FFXIV
4. Configuration will be preserved

### Backup Configuration
1. Use `/mogtome backup` command
2. Or manually copy configuration files
3. Store backups safely

### Clean Installation
1. Use `/xlplugins stop` to stop all plugins
2. Delete the MOGTOME folder
3. Restart from Step 2

## 🎯 Best Practices

### Before Starting
1. Verify all dependencies are working
2. Test with a single duty first
3. Check maintenance status (food/repair)
4. Ensure proper party setup

### During Operation
1. Monitor the plugin window for status
2. Check for stuck recovery notifications
3. Watch for error messages in chat
4. Verify progress counter increments

### After Extended Sessions
1. Restart client if performance degrades
2. Check VNavMesh cache size
3. Verify all plugins are still responsive
4. Review progress and statistics

## 📞 Support

### Getting Help
1. Check this documentation first
2. Review the changelog for known issues
3. Enable debug mode for detailed logs
4. Report issues with system information

### Information to Include
- FFXIV version
- Dalamud version
- Plugin version
- Error messages
- Steps to reproduce
- System specifications

---

## 🎉 Success!

You're now ready to use M.O.G.T.O.M.E. for automated duty farming and tome acquisition!

**Quick verification steps:**
1. Plugin loads without errors ✓
2. Main window opens with `/mogtome` ✓
3. Configuration saves correctly ✓
4. Dependencies are all working ✓

Happy farming! 🚀
