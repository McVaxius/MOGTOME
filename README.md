# M.O.G.T.O.M.E. - Management Of Grand Tome Operations & Management Engine

## 🎯 Project Overview

M.O.G.T.O.M.E. is a Dalamud plugin for FFXIV that converts the G.O.O.N. (Generally Ordered Optimized Navigation) SND script into a robust C# plugin for automated duty farming and tome acquisition.

### 📋 Primary Objectives

- **Automated Duty Farming**: Convert existing SND script logic to plugin format
- **Tome Acquisition**: Optimize Allagan Tome of Mendacity/Genesis farming
- **Party Management**: Handle party leader/follower coordination
- **Maintenance Automation**: Auto-repair, auto-food, and gear management
- **Stuck Recovery**: Intelligent detection and recovery from navigation issues

## 🏗️ Project Structure

```
MOGTOME/
├── MOGTOME.csproj                 # Project file
├── README.md                      # This file
├── .gitignore                     # Git ignore patterns
├── CHANGELOG.md                   # Implementation changelog
├── HOW_TO_IMPORT_PLUGINS.md       # Plugin installation guide
├── MOGTOME/
│   ├── Plugin.cs                  # Main plugin entry point
│   ├── Configuration.cs           # Plugin configuration
│   ├── Services/
│   │   ├── DutyManager.cs         # Duty queue and automation
│   │   ├── MaintenanceManager.cs  # Repair/food/gear management
│   │   ├── StuckDetection.cs      # Navigation recovery
│   │   ├── ProgressTracker.cs    # Counter and progress tracking
│   │   └── PartyManager.cs        # Party coordination
│   ├── Windows/
│   │   ├── MainWindow.cs          # Main UI window
│   │   └── ConfigWindow.cs        # Configuration interface
│   ├── Models/
│   │   ├── FarmingConfig.cs       # Configuration model
│   │   └── DutyState.cs           # Duty state tracking
│   └── Utilities/
│       ├── GameHelpers.cs         # Game interaction helpers
│       └── Logger.cs              # Enhanced logging
├── Backups/                       # Timestamped backups
└── Tests/                         # Unit tests (future)
```

## 🔧 Dependencies & Applications

### Required Plugins
- **AutoDuty**: Primary automation engine
- **VNavMesh**: Navigation and pathfinding
- **BossMod**: Combat automation and presets
- **Rotation Solver Reborn**: Alternative rotation handling
- **SimpleTweaks**: Targeting fixes and QOL improvements
- **Cutscene Skip**: Automated cutscene skipping
- **YesAlready**: Dialog confirmation automation

### External Dependencies
- **dfunc.lua**: Utility functions (McVaxius/dhogsbreakfeast)
- **AutoDuty Path Files**: W2W Ritsuko Praetorium path

### Development Tools
- **Visual Studio 2022**: C# development
- **.NET 7.0**: Framework target
- **Dalamud SDK**: Plugin framework
- **ImGui**: UI framework

## 📚 Knowledge Base & References

### Core References
- [SamplePlugin](https://github.com/goatcorp/SamplePlugin) - Basic plugin structure
- [SamplePlugin README](https://github.com/goatcorp/SamplePlugin/blob/master/README.md) - Setup guide
- [SomethingNeedDoing](https://github.com/Jaksuhn/SomethingNeedDoing) - Original SND patterns
- [dfunc.lua](https://github.com/McVaxius/dhogsbreakfeast/blob/main/dfunc.lua) - Utility functions

### Conversion Mapping
| SND Pattern | Dalamud Equivalent |
|-------------|-------------------|
| `yield("/command")` | `Plugin.Chat.ExecuteCommand("/command")` |
| `GetZoneID()` | `Plugin.ClientState.TerritoryType` |
| `GetCharacterCondition(34)` | `Plugin.Condition[ConditionFlag.BoundByDuty]` |
| `IsAddonVisible("Name")` | `Plugin.GameGui.GetAddonByName("Name", 1) != 0` |
| `/callback Name true 0` | `addon->FireCallback(values)` |
| `GetPlayerRawXPos()` | `Plugin.ClientState.LocalPlayer.Position.X` |

### FFXIV Territory Types
- **1044**: The Praetorium
- **1048**: Porta Decumana
- **177/178/179**: Inn rooms (Gridania/Limsa/Uldah)

## 🚀 Implementation Phases

### Phase 1: Foundation (Week 1)
**Objective**: Basic plugin structure and in-game presence

#### Tasks
- [x] Create project structure
- [ ] Basic Plugin.cs with Dalamud integration
- [ ] Simple ImGui window
- [ ] Basic configuration system
- [ ] Git repository setup
- [ ] Build system (Debug/Release)

#### Testing Requirements
- [ ] Plugin loads in Dalamud
- [ ] UI window opens without errors
- [ ] Configuration saves/loads
- [ ] No memory leaks on unload

### Phase 2: Core Logic (Week 2)
**Objective**: Convert main G.O.O.N. automation logic

#### Tasks
- [ ] Duty detection and state management
- [ ] Party leader/follower detection
- [ ] Basic queue automation
- [ ] Progress counter implementation
- [ ] Stuck detection framework
- [ ] Enhanced logging system

#### Testing Requirements
- [ ] Duty detection works in Praetorium
- [ ] Party role detection accurate
- [ ] Queue attempts successful
- [ ] Progress tracking functional
- [ ] Stuck detection triggers appropriately

### Phase 3: Automation Systems (Week 3)
**Objective**: Implement full automation features

#### Tasks
- [ ] AutoDuty integration and control
- [ ] Maintenance system (repair/food)
- [ ] Gear management
- [ ] Rotation plugin integration
- [ ] Dialog handling (YesAlready replacement)
- [ ] Daily reset logic

#### Testing Requirements
- [ ] AutoDuty starts/stops correctly
- [ ] Repair triggers at appropriate durability
- [ ] Food consumption when buff expires
- [ ] Rotation plugins switch properly
- [ ] Dialog confirmations work

### Phase 4: Advanced Features (Week 4)
**Objective**: Advanced automation and optimization

#### Tasks
- [ ] AutoDuty path integration
- [ ] Multi-client coordination
- [ ] Advanced stuck recovery
- [ ] Performance optimization
- [ ] Error handling and recovery
- [ ] Statistics and reporting

#### Testing Requirements
- [ ] Path following works correctly
- [ ] Multi-client sync functional
- [ ] Recovery from various stuck scenarios
- [ ] Performance acceptable (60+ FPS)
- [ ] Graceful error handling

### Phase 5: Polish & Release (Week 5)
**Objective**: Final polish and user experience

#### Tasks
- [ ] UI/UX improvements
- [ ] Comprehensive documentation
- [ ] User guide and tutorials
- [ ] Beta testing feedback integration
- [ ] Release preparation
- [ ] Distribution setup

#### Testing Requirements
- [ ] User-friendly interface
- [ ] Complete documentation coverage
- [ ] No critical bugs in testing
- [ ] Performance under extended use
- [ ] Installation/uninstallation works

## 🔄 Development Workflow

### Pre-Edit Checklist
1. **Backup Creation**: Timestamped backup in `/Backups/` folder
2. **Chelog Update**: Document intended changes
3. **Syntax Check**: Verify code compiles
4. **Memory Check**: Ensure no leaks introduced
5. **Test Plan**: Define specific test requirements

### Post-Edit Verification
1. **Build Success**: Debug and Release build
2. **Functionality Test**: Verify intended behavior
3. **Regression Test**: Ensure no broken features
4. **Performance Check**: Monitor FPS/memory usage
5. **Documentation Update**: Reflect changes in docs

### Git Workflow
```bash
# Before major changes
git add -A
git commit -m "Pre-change backup: [description]"

# After successful implementation
git add -A  
git commit -m "Implement: [feature description]"
git push origin main
```

## 🎮 Game Integration Details

### Duty Flow Logic
1. **Pre-Duty**: Check maintenance, food, gear
2. **Queue**: Party leader queues, followers wait
3. **In-Duty**: AutoDuty handles navigation/combat
4. **Completion**: Auto-leave or stuck recovery
5. **Post-Duty**: Reset, maintenance, next queue

### State Management
```csharp
public enum FarmingState
{
    Idle,
    PreDutyCheck,
    Queueing,
    InDuty,
    Completing,
    PostDuty,
    StuckRecovery,
    Error
}
```

### Configuration Schema
```csharp
public class FarmingConfig
{
    public int DailyTarget { get; set; } = 99;
    public string FoodItem { get; set; } = "Orange Juice";
    public int RepairThreshold { get; set; } = 25;
    public bool AutoRepair { get; set; } = true;
    public bool AutoFood { get; set; } = true;
    public bool EnableStuckRecovery { get; set; } = true;
    public int StuckTimeout { get; set; } = 30;
    public LogLevel LogLevel { get; set; } = LogLevel.Info;
}
```

## 🐛 Known Challenges & Solutions

### SND to Plugin Conversion Issues
| Challenge | SND Approach | Plugin Solution |
|-----------|-------------|----------------|
| **Async Operations** | `yield("/wait")` | `async/await` with `Task.Delay` |
| **UI Interaction** | `/callback` | Direct `AtkUnitBase` manipulation |
| **State Tracking** | Global variables | Structured state management |
| **Error Handling** | Script crashes | Try/catch with recovery |
| **Performance** | Interpreted Lua | Compiled C# with optimization |

### Plugin-Specific Challenges
- **Thread Safety**: Dalamud's main thread requirements
- **Memory Management**: Proper disposal of resources
- **Plugin Communication**: IPC for external plugin control
- **Configuration Persistence**: Dalamud's config system
- **UI Framework**: ImGui integration patterns

## 📊 Success Metrics

### Performance Targets
- **FPS Impact**: < 5% reduction in combat
- **Memory Usage**: < 50MB additional RAM
- **CPU Usage**: < 10% additional load
- **Reliability**: > 99% uptime over 24h periods

### Functionality Targets
- **Duty Success Rate**: > 95% completion
- **Stuck Recovery**: > 90% successful recovery
- **Queue Speed**: < 30 seconds between duties
- **Maintenance Coverage**: 100% automatic handling

## 🚨 Risk Assessment

### High Risk
- **AutoDuty Compatibility**: API changes could break integration
- **Game Updates**: FFXIV patches may require updates
- **Performance**: Multiple clients may cause slowdown

### Medium Risk
- **Plugin Conflicts**: Interactions with other automation plugins
- **Detection Risk**: Automation policy considerations
- **Complexity**: Feature creep affecting stability

### Mitigation Strategies
- **Modular Design**: Isolate components for easy updates
- **Fallback Systems**: Graceful degradation when dependencies fail
- **Extensive Testing**: Automated and manual testing procedures
- **User Communication**: Clear documentation and support channels

## 📝 Testing Protocol

### Unit Testing
```csharp
[Test]
public void DutyDetection_Praetorium_ReturnsCorrectId()
{
    // Mock territory type 1044
    var result = _dutyManager.GetCurrentDuty();
    Assert.AreEqual(DutyType.Praetorium, result);
}
```

### Integration Testing
- Load plugin with various dependency versions
- Test with different party configurations
- Verify stuck recovery scenarios
- Performance testing under load

### User Acceptance Testing
- New user installation and setup
- Extended farming sessions (4+ hours)
- Multi-client scenarios
- Error recovery and support

---

## 🎯 Next Steps

1. **Confirm**: Plugin loads in-game with basic UI
2. **Implement**: Phase 1 foundation components
3. **Test**: Basic functionality and stability
4. **Iterate**: Based on testing feedback
5. **Expand**: Move to Phase 2 core logic

This project represents a significant evolution from script-based automation to a robust, maintainable plugin solution for FFXIV automation.
