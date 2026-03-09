# M.O.G.T.O.M.E. Changelog

## Version History

### [Unreleased] - Development Phase
**Status**: Planning and Foundation

#### 🎯 Project Initiation
- **Date**: 2026-03-09
- **Objective**: Convert G.O.O.N. SND script to Dalamud plugin
- **Status**: Project structure created, documentation complete

#### 📋 Planned Features
- [ ] Basic plugin structure and Dalamud integration
- [ ] Duty automation and queue management
- [ ] Party coordination (leader/follower)
- [ ] Maintenance automation (repair/food/gear)
- [ ] Stuck detection and recovery
- [ ] Progress tracking and statistics
- [ ] AutoDuty integration
- [ ] Multi-client support

#### 🔄 Conversion Notes
- Converting from SND Lua patterns to C# Dalamud APIs
- Replacing `yield("/wait")` with `async/await` patterns
- Converting `/callback` commands to direct `AtkUnitBase` manipulation
- Implementing proper state management vs global variables
- Adding robust error handling and recovery

---

## Implementation Log

### 2026-03-09 - Project Foundation
**Changes Made:**
- ✅ Created project structure and documentation
- ✅ Defined development phases and timeline
- ✅ Established conversion mapping from SND to Dalamud
- ✅ Set up Git workflow and backup procedures
- ✅ Created installation and configuration guides

**Technical Decisions:**
- Use .NET 7.0 for compatibility with Dalamud
- Implement modular service architecture
- Follow existing plugin patterns from workspace
- Prioritize stability over feature completeness

**Dependencies Identified:**
- AutoDuty for primary automation
- VNavMesh for navigation
- BossMod/RSR for combat
- SimpleTweaks for targeting fixes
- YesAlready for dialog handling

**Next Steps:**
- Create basic Plugin.cs with Dalamud integration
- Implement simple ImGui window
- Set up configuration system
- Test basic plugin loading

---

## Known Issues & Solutions

### Conversion Challenges
| Issue | SND Method | Plugin Solution | Status |
|-------|-------------|-----------------|---------|
| Async Operations | `yield("/wait")` | `async/await` with `Task.Delay` | Planned |
| UI Callbacks | `/callback Name true 0` | `addon->FireCallback(values)` | Planned |
| State Management | Global variables | Structured state classes | Planned |
| Error Handling | Script crashes | Try/catch with recovery | Planned |
| Performance | Interpreted execution | Compiled C# optimization | Planned |

### Plugin-Specific Challenges
| Challenge | Impact | Mitigation | Status |
|-----------|--------|------------|---------|
| Thread Safety | Critical | Main thread execution | Planned |
| Memory Management | High | Proper disposal patterns | Planned |
| Plugin Communication | Medium | IPC integration | Planned |
| Configuration Persistence | Medium | Dalamud config system | Planned |

---

## Testing Progress

### Phase 1 Tests (Not Started)
- [ ] Plugin loads in Dalamud without errors
- [ ] Main window opens and functions
- [ ] Configuration saves and loads correctly
- [ ] No memory leaks on unload
- [ ] Basic command system works

### Phase 2 Tests (Not Started)
- [ ] Duty detection in Praetorium/Porta
- [ ] Party role detection accuracy
- [ ] Queue automation functionality
- [ ] Progress tracking reliability
- [ ] Stuck detection triggers

### Phase 3 Tests (Not Started)
- [ ] AutoDuty integration stability
- [ ] Maintenance system reliability
- [ ] Gear management accuracy
- [ ] Rotation plugin switching
- [ ] Dialog handling success rate

### Phase 4 Tests (Not Started)
- [ ] Path following precision
- [ ] Multi-client synchronization
- [ ] Stuck recovery effectiveness
- [ ] Performance under load
- [ ] Extended session stability

### Phase 5 Tests (Not Started)
- [ ] User interface usability
- [ ] Documentation completeness
- [ ] Installation/uninstallation
- [ ] Cross-platform compatibility
- [ ] Beta testing feedback

---

## Performance Benchmarks

### Target Metrics
- **FPS Impact**: < 5% reduction
- **Memory Usage**: < 50MB additional
- **CPU Usage**: < 10% additional load
- **Reliability**: > 99% uptime

### Current Status
- **FPS Impact**: Not measured
- **Memory Usage**: Not measured  
- **CPU Usage**: Not measured
- **Reliability**: Not tested

---

## Dependency Versions

### Current Requirements
```
Dalamud: Latest stable
.NET Runtime: 7.0+
AutoDuty: Latest stable
VNavMesh: Latest stable
BossMod: Latest from Punish repo
RSR: Latest from CombatReborn repo
SimpleTweaks: Latest stable
Cutscene Skipper: Latest stable
YesAlready: Latest stable
```

### Version Compatibility
- **FFXIV**: Current patch (6.x)
- **Dalamud**: API 9+
- **Windows**: 10/11 (64-bit)

---

## Documentation Updates

### 2026-03-09
- Created comprehensive README.md
- Added HOW_TO_IMPORT_PLUGINS.md
- Established CHANGELOG.md
- Defined project structure and phases

### Future Documentation
- API documentation for services
- User guide with screenshots
- Troubleshooting guide
- Developer contribution guide

---

## Release Notes

### Pre-Release
- **Status**: Development phase
- **Availability**: Private testing only
- **Stability**: Not guaranteed
- **Support**: Direct developer contact

### Planned Release
- **Version**: 1.0.0
- **Target**: End of Phase 5
- **Distribution**: GitHub releases
- **Support**: Community channels

---

## Contributing

### Development Guidelines
- Follow established code patterns
- Maintain backward compatibility
- Include comprehensive tests
- Update documentation

### Submission Process
1. Fork repository
2. Create feature branch
3. Implement changes with tests
4. Update documentation
5. Submit pull request

### Code Review Criteria
- Functionality correctness
- Performance impact
- Code quality
- Documentation completeness

---

This changelog will be updated throughout the development process to track all changes, decisions, and progress.
