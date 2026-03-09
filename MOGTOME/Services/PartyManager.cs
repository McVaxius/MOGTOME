using Dalamud.Game.ClientState.Party;
using Dalamud.Game.ClientState.Objects.Types;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MOGTOME.Services;

public class PartyManager : IDisposable
{
    private readonly Configuration config;
    private bool isLeader = false;
    private int lastPartySize = 0;
    private DateTime lastPartyCheck = DateTime.MinValue;
    private readonly List<string> currentPartyMembers = new();

    public PartyManager(Configuration config)
    {
        this.config = config;
        Service.Log.Info("PartyManager initialized");
    }

    public bool IsPartyLeader()
    {
        try
        {
            if (!Service.ClientState.IsLoggedIn || Service.ClientState.LocalPlayer == null)
            {
                return false;
            }

            var partyList = Service.PartyList;
            if (partyList.Length == 0)
            {
                return false; // No party, no leader
            }

            var playerName = Service.ClientState.LocalPlayer.Name.ToString();
            var leader = partyList[0]; // Party leader is always first in list
            
            isLeader = leader.Name.ToString() == playerName;
            config.IsPartyLeader = isLeader;
            
            return isLeader;
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error checking party leader status: {ex.Message}");
            return false;
        }
    }

    public PartyRole GetRole()
    {
        try
        {
            if (!Service.ClientState.IsLoggedIn)
            {
                return PartyRole.None;
            }

            var partyList = Service.PartyList;
            if (partyList.Length == 0)
            {
                return PartyRole.Solo;
            }

            if (IsPartyLeader())
            {
                return PartyRole.Leader;
            }

            return PartyRole.Follower;
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error getting party role: {ex.Message}");
            return PartyRole.None;
        }
    }

    public int GetPartySize()
    {
        try
        {
            return Service.PartyList.Length;
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error getting party size: {ex.Message}");
            return 0;
        }
    }

    public List<string> GetPartyMembers()
    {
        try
        {
            var partyList = Service.PartyList;
            var members = new List<string>();

            for (var i = 0; i < partyList.Length; i++)
            {
                var member = partyList[i];
                members.Add(member.Name.ToString());
            }

            return members;
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error getting party members: {ex.Message}");
            return new List<string>();
        }
    }

    public bool IsInParty()
    {
        try
        {
            return Service.PartyList.Length > 0;
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error checking if in party: {ex.Message}");
            return false;
        }
    }

    public bool IsPartyMember(string playerName)
    {
        try
        {
            var partyList = Service.PartyList;
            return partyList.Any(member => member.Name.ToString().Equals(playerName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error checking if player is in party: {ex.Message}");
            return false;
        }
    }

    public bool IsPartyMemberInZone(string playerName)
    {
        try
        {
            var partyList = Service.PartyList;
            var member = partyList.FirstOrDefault(m => m.Name.ToString().Equals(playerName, StringComparison.OrdinalIgnoreCase));
            
            if (member == null) return false;
            
            // Check if the member is in the same territory
            var memberGameObject = Service.ObjectTable.FirstOrDefault(obj => 
                obj is IPlayerCharacter player && 
                player.Name.ToString().Equals(playerName, StringComparison.OrdinalIgnoreCase));
            
            return memberGameObject != null;
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error checking if party member is in zone: {ex.Message}");
            return false;
        }
    }

    public int GetPartyMembersInZone()
    {
        try
        {
            var partyList = Service.PartyList;
            var count = 0;

            for (var i = 0; i < partyList.Length; i++)
            {
                var member = partyList[i];
                if (IsPartyMemberInZone(member.Name.ToString()))
                {
                    count++;
                }
            }

            return count;
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error counting party members in zone: {ex.Message}");
            return 0;
        }
    }

    public bool ShouldWaitForParty()
    {
        try
        {
            if (!config.PartyCoordination || !IsInParty())
            {
                return false;
            }

            var partySize = GetPartySize();
            var membersInZone = GetPartyMembersInZone();
            
            // Wait if not all party members are in the zone
            return membersInZone < partySize;
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error checking if should wait for party: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> CoordinateWithParty()
    {
        try
        {
            if (!config.PartyCoordination)
            {
                return true; // No coordination needed
            }

            var role = GetRole();
            
            switch (role)
            {
                case PartyRole.Leader:
                    return await CoordinateAsLeader();
                
                case PartyRole.Follower:
                    return await CoordinateAsFollower();
                
                default:
                    return true; // Solo or no coordination needed
            }
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error coordinating with party: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> CoordinateAsLeader()
    {
        try
        {
            Service.Log.Debug("Coordinating as party leader");
            
            // Leader takes action immediately
            // Followers will wait for leader's actions
            return true;
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error coordinating as leader: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> CoordinateAsFollower()
    {
        try
        {
            Service.Log.Debug("Coordinating as party follower");
            
            // Follower waits for leader to be ready
            var waitTime = 0;
            var maxWaitTime = 30000; // 30 seconds max wait
            
            while (ShouldWaitForParty() && waitTime < maxWaitTime)
            {
                await Task.Delay(1000);
                waitTime += 1000;
                
                if (waitTime % 5000 == 0) // Log every 5 seconds
                {
                    var membersInZone = GetPartyMembersInZone();
                    var totalMembers = GetPartySize();
                    Service.Log.Debug($"Waiting for party: {membersInZone}/{totalMembers} members in zone");
                }
            }
            
            if (waitTime >= maxWaitTime)
            {
                Service.Log.Warning("Timed out waiting for party members, proceeding anyway");
                return false;
            }
            
            Service.Log.Debug("All party members ready, proceeding");
            return true;
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error coordinating as follower: {ex.Message}");
            return false;
        }
    }

    public void UpdatePartyStatus()
    {
        try
        {
            var now = DateTime.Now;
            
            // Throttle party checks to every 2 seconds
            if ((now - lastPartyCheck).TotalMilliseconds < 2000)
            {
                return;
            }
            
            lastPartyCheck = now;
            
            var currentSize = GetPartySize();
            var currentMembers = GetPartyMembers();
            
            // Check for party changes
            if (currentSize != lastPartySize)
            {
                Service.Log.Info($"Party size changed: {lastPartySize} -> {currentSize}");
                lastPartySize = currentSize;
            }
            
            // Check for member changes
            var membersChanged = !currentMembers.SequenceEqual(currentPartyMembers);
            if (membersChanged)
            {
                Service.Log.Debug($"Party members changed: {string.Join(", ", currentPartyMembers)} -> {string.Join(", ", currentMembers)}");
                currentPartyMembers.Clear();
                currentPartyMembers.AddRange(currentMembers);
                
                // Update leader status
                IsPartyLeader();
            }
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error updating party status: {ex.Message}");
        }
    }

    public bool IsPlayerWhitelisted(string playerName)
    {
        try
        {
            if (config.WhitelistNames == null || config.WhitelistNames.Count == 0)
            {
                return false; // No whitelist configured
            }

            return config.WhitelistNames.Any(name => 
                name.Equals(playerName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error checking if player is whitelisted: {ex.Message}");
            return false;
        }
    }

    public void AddToWhitelist(string playerName)
    {
        try
        {
            if (config.WhitelistNames == null)
            {
                config.WhitelistNames = new List<string>();
            }

            if (!config.WhitelistNames.Contains(playerName, StringComparer.OrdinalIgnoreCase))
            {
                config.WhitelistNames.Add(playerName);
                config.Save();
                Service.Log.Info($"Added {playerName} to whitelist");
                Service.Chat.Print($"Added {playerName} to whitelist");
            }
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error adding player to whitelist: {ex.Message}");
        }
    }

    public void RemoveFromWhitelist(string playerName)
    {
        try
        {
            if (config.WhitelistNames != null)
            {
                var removed = config.WhitelistNames.RemoveAll(name => 
                    name.Equals(playerName, StringComparison.OrdinalIgnoreCase));
                
                if (removed > 0)
                {
                    config.Save();
                    Service.Log.Info($"Removed {playerName} from whitelist");
                    Service.Chat.Print($"Removed {playerName} from whitelist");
                }
            }
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error removing player from whitelist: {ex.Message}");
        }
    }

    public void Reset()
    {
        try
        {
            isLeader = false;
            lastPartySize = 0;
            lastPartyCheck = DateTime.MinValue;
            currentPartyMembers.Clear();
            
            Service.Log.Info("PartyManager reset");
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error resetting PartyManager: {ex.Message}");
        }
    }

    public void Dispose()
    {
        try
        {
            currentPartyMembers.Clear();
            Service.Log.Info("PartyManager disposed");
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error disposing PartyManager: {ex.Message}");
        }
    }
}

public enum PartyRole
{
    None,
    Solo,
    Leader,
    Follower
}
