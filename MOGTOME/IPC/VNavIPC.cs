using System;
using System.Globalization;
using System.Numerics;
using Dalamud.Plugin.Services;

namespace MOGTOME.IPC;

public class VNavIPC : IDisposable
{
    private readonly IPluginLog log;
    private readonly ICommandManager commandManager;

    public VNavIPC(IPluginLog log, ICommandManager commandManager)
    {
        this.log = log;
        this.commandManager = commandManager;
    }

    public bool MoveTo(Vector3 position)
    {
        try
        {
            var cmd = string.Format(CultureInfo.InvariantCulture,
                "/vnav moveto {0:F2} {1:F2} {2:F2}", position.X, position.Y, position.Z);
            log.Debug($"[VNav] {cmd}");
            return commandManager.ProcessCommand(cmd);
        }
        catch (Exception ex)
        {
            log.Error($"[VNav] MoveTo failed: {ex.Message}");
            return false;
        }
    }

    public bool Stop()
    {
        try
        {
            return commandManager.ProcessCommand("/vnav stop");
        }
        catch (Exception ex)
        {
            log.Error($"[VNav] Stop failed: {ex.Message}");
            return false;
        }
    }

    public bool Rebuild()
    {
        try
        {
            log.Information("[VNav] Rebuilding navmesh");
            return commandManager.ProcessCommand("/vnav rebuild");
        }
        catch (Exception ex)
        {
            log.Error($"[VNav] Rebuild failed: {ex.Message}");
            return false;
        }
    }

    public void Dispose() { }
}
