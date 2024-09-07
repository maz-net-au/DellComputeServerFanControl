using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace ComputeServerTempMonitor
{
    public class ServerInfo
    {
        public string Name { get; set; } = "";
        public List<ulong> Admins { get; set; } = new List<ulong>();
        public List<ulong> Users { get; set; } = new List<ulong>();
        public Dictionary<PreferenceNames, object> Preferences { get; set; } = new Dictionary<PreferenceNames, object>();
    }
    public class DiscordMeta
    {
        public ulong OwnerServer { get; set; } = 0;
        public List<ulong> Owners { get; set; } = new List<ulong>();
        public List<ulong> GlobalAdmins { get; set; } = new List<ulong>();
        public List<ulong> GlobalUsers { get; set; } = new List<ulong>(); // temporary while I move them all to servers
        public Dictionary<PreferenceNames, object> GlobalPreferences { get; set; } = new Dictionary<PreferenceNames, object>();
        public Dictionary<ulong, ServerInfo> Servers { get; set; } = new Dictionary<ulong, ServerInfo>();
        public AccessLevel CheckPermission(ulong? server, ulong user)
        {
            if (Owners.Contains(user))
                return AccessLevel.Owner;
            if (GlobalAdmins.Contains(user))
                return AccessLevel.Admin;
            if (server.HasValue)
            {
                if (Servers.ContainsKey(server.Value) && Servers[server.Value].Admins.Contains(user))
                    return AccessLevel.Admin;
                if (Servers.ContainsKey(server.Value) && Servers[server.Value].Users.Contains(user))
                    return AccessLevel.User;
            }
            return AccessLevel.None;
        }

        public object? GetPreference(ulong? server, PreferenceNames pref)
        {
            if (server.HasValue && Servers.ContainsKey(server.Value))
            {
                if (Servers[server.Value].Preferences.ContainsKey(pref))
                {
                    object? result = Servers[server.Value].Preferences[pref];
                    if (result != null)
                        return result;
                }
            }
            if (GlobalPreferences.ContainsKey(pref))
            {
                object? result = GlobalPreferences[pref];
                if (result != null)
                    return result;
            }
            return null;
        }
    }

    public enum AccessLevel
    {
        None = 0,
        User = 10,
        Admin = 20,
        Owner = 30
    }
    public enum PreferenceNames // primatives only
    {
        SpoilerImages = 1,          // bool
        ShowUpscaleButton = 2,      // bool
        ShowRegenerateButton = 3,   // bool
        ShowVariationMenu = 4,      // bool
    }
}
