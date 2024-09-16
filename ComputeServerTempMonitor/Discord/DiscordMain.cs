using ComputeServerTempMonitor.ComfyUI.Models;
using ComputeServerTempMonitor.ComfyUI;
using ComputeServerTempMonitor.Common;
using ComputeServerTempMonitor.Discord.Models;
using Discord;
using Discord.WebSocket;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ImageMagick;
using ComputeServerTempMonitor.Hardware.Model;
using ComputeServerTempMonitor.Software;
using ComputeServerTempMonitor.Software.Models;
using Discord.Net;
using ComputeServerTempMonitor.Hardware;

namespace ComputeServerTempMonitor.Discord
{
    public static class DiscordMain
    {
        private static DiscordSocketClient _client;
        public static DiscordMeta discordInfo = new DiscordMeta();
        public static CommandUsage usage = new CommandUsage();
        const string userFile = "data/discordUsers.json";
        const string usageFile = "data/discordUsage.json";
        static CancellationToken cancellationToken;

        public static async Task InitBot()
        {
            _client = new DiscordSocketClient();
            _client.Log += DiscordLogger;
            _client.Ready += Client_Ready;
            _client.SlashCommandExecuted += SlashCommandHandler;
            _client.ButtonExecuted += ButtonExecuted;
            _client.SelectMenuExecuted += SelectMenuExecuted;
            await _client.LoginAsync(TokenType.Bot, SharedContext.Instance.GetConfig().DiscordBotToken);
            await _client.StartAsync();
        }

        public static void LoadUsers()
        {
            if (File.Exists(userFile))
            {
                SharedContext.Instance.Log(LogLevel.INFO, "Main", "Loading users");
                discordInfo = JsonConvert.DeserializeObject<DiscordMeta>(File.ReadAllText(userFile)) ?? new DiscordMeta();
            }
            else
            {
                SharedContext.Instance.Log(LogLevel.INFO, "Main", userFile + " not found. Creating default users file.");
                discordInfo = new DiscordMeta();
                File.WriteAllText(userFile, JsonConvert.SerializeObject(discordInfo, Formatting.Indented));
            }
        }

        public static void LoadUsage()
        {
            if (File.Exists(usageFile))
            {
                SharedContext.Instance.Log(LogLevel.INFO, "Main", "Loading usage history");
                usage = JsonConvert.DeserializeObject<CommandUsage>(File.ReadAllText(usageFile)) ?? new CommandUsage();
            }
        }
        public static void WriteToUsage(string guild, string user, string command)
        {
            bool changed = false;
            if (user != "")
            {
                if (!usage.UsagePerUser.ContainsKey(user))
                    usage.UsagePerUser[user] = new Dictionary<string, uint>();
                if (!usage.UsagePerUser[user].ContainsKey(command))
                    usage.UsagePerUser[user][command] = 0;

                usage.UsagePerUser[user][command]++;
                changed = true;
            }

            if (guild != "")
            {
                if (!usage.UsagePerServer.ContainsKey(guild))
                    usage.UsagePerServer[guild] = new Dictionary<string, uint>();
                if (!usage.UsagePerServer[guild].ContainsKey(command))
                    usage.UsagePerServer[guild][command] = 0;

                usage.UsagePerServer[guild][command]++;
                changed = true;
            }
            if(changed)
                File.WriteAllText(usageFile, JsonConvert.SerializeObject(usage, Formatting.Indented));
        }

        private static Task DiscordLogger(LogMessage msg)
        {
            SharedContext.Instance.Log(LogLevel.INFO, msg.Source, msg.Message);
            return Task.CompletedTask;
        }

        static string WriteUsers()
        {
            File.WriteAllText(userFile, JsonConvert.SerializeObject(discordInfo, Formatting.Indented));
            return "Users file updated";
        }

        public async static void Exit()
        {
            WriteUsers();
        }

        public async static void Init(CancellationToken ct)
        {
            cancellationToken = ct;
            LoadUsers();
            LoadUsage();
            await InitBot();
        }

        private static async Task SelectMenuExecuted(SocketMessageComponent arg)
        {
            string[] parts = arg.Data.CustomId.Split(":");
            if (parts.Length != 2)
            {
                await arg.RespondAsync($"Invalid command");
                return;
            }
            string guildName = "";
            if (arg.GuildId.HasValue)
            {
                SocketGuild guild = _client.GetGuild(arg.GuildId.Value);
                guildName = guild.Name;
            }
            WriteToUsage(guildName, arg.User.GlobalName, parts[0]);
            switch (parts[0])
            {
                case "upscale":
                    {
                        Task.Run(async () =>
                        {
                            if (arg.ChannelId == null)
                                await arg.RespondAsync("Request failed");
                            IMessageChannel? chan = _client.GetChannel(arg.ChannelId ?? 0) as IMessageChannel;
                            if (chan == null)
                            {
                                SharedContext.Instance.Log(LogLevel.ERR, "Upscale", "Channel not found");
                                await arg.RespondAsync("Request failed");
                            }
                            //int num;
                            //if (!int.TryParse(parts[1], out num))
                            //    return;
                            float upscale_by = 2.0f; // default
                            if (!float.TryParse(arg.Data.Values.FirstOrDefault(), out upscale_by))
                                return;
                            await arg.RespondAsync($"{upscale_by}x upscale request accepted.\n{GetDrawStatus()}");
                            HistoryResponse? hr = await ComfyMain.Upscale(parts[1], 0, upscale_by, arg.User.Username);
                            if (hr == null)
                            {
                                SharedContext.Instance.Log(LogLevel.ERR, "Upscale", "Result is null");
                                await chan.SendMessageAsync("Request failed");
                            }
                            else
                            {
                                DiscordImageResponse res = CreateImageGenResponse(hr, arg.GuildId);
                                uint filesize = 0;
                                uint.TryParse(res.Statistics[ImageGenStatisticType.FileSize], out filesize);
                                if (filesize > SharedContext.Instance.GetConfig().ComfyUI.Settings.MaximumFileSize)
                                {
                                    await arg.ModifyOriginalResponseAsync((s) =>
                                    {
                                        s.Content = $"{arg.User.Mention} your image was too large to upload: {(int)Math.Ceiling(filesize / 1000000.0)}MB\n{res.Statistics[ImageGenStatisticType.Width]}x{res.Statistics[ImageGenStatisticType.Height]}";
                                        s.AllowedMentions = new AllowedMentions(AllowedMentionTypes.Users);
                                    });
                                }
                                else
                                {
                                    await arg.ModifyOriginalResponseAsync((s) =>
                                    {
                                        s.Content = $"Here is your upscaled image {arg.User.Mention}\n{res.Statistics[ImageGenStatisticType.Width]}x{res.Statistics[ImageGenStatisticType.Height]} @ {(int)Math.Ceiling(filesize / 1000000.0)}MB";
                                        s.Attachments = res.Attachments;
                                        s.Components = res.Components.Build();
                                        s.AllowedMentions = new AllowedMentions(AllowedMentionTypes.Users);
                                    });
                                }
                                //await chan.SendFilesAsync(res.Attachments, $"I made it bigger for you {arg.User.Mention}", false, null, RequestOptions.Default, AllowedMentions.All, null, res.Components.Build());
                            }
                        });
                    }
                    break;
                case "variation":
                    {
                        Task.Run(async () =>
                        {
                            if (arg.Data.Values.Count == 0)
                            {
                                // explicitly deselected. Do nothing.
                                await arg.RespondAsync("Selection cleared");
                                await arg.DeleteOriginalResponseAsync();
                            }
                            if (arg.ChannelId == null)
                                await arg.RespondAsync("Request failed");
                            IMessageChannel? chan = _client.GetChannel(arg.ChannelId ?? 0) as IMessageChannel;
                            if (chan == null)
                            {
                                SharedContext.Instance.Log(LogLevel.ERR, "Button", "Channel not found");
                                await arg.RespondAsync("Request failed");
                            }
                            float vary_by = 0.5f; // default
                            if (!float.TryParse(arg.Data.Values.FirstOrDefault(), out vary_by))
                                return;
                            await arg.RespondAsync($"{Math.Round(vary_by * 100)}% variation request accepted.\n{GetDrawStatus()}");
                            HistoryResponse? hr = await ComfyMain.Variation(parts[1], 0, vary_by, arg.User.Username);
                            if (hr == null)
                            {
                                SharedContext.Instance.Log(LogLevel.ERR, "Button", "Result is null");
                                await chan.SendMessageAsync("Request failed");
                            }
                            else
                            {
                                DiscordImageResponse res = CreateImageGenResponse(hr, arg.GuildId);
                                uint filesize = 0;
                                uint.TryParse(res.Statistics[ImageGenStatisticType.FileSize], out filesize);
                                if (filesize > SharedContext.Instance.GetConfig().ComfyUI.Settings.MaximumFileSize)
                                {
                                    await arg.ModifyOriginalResponseAsync((s) =>
                                    {
                                        s.Content = $"{arg.User.Mention} your image was too large to upload: {(int)Math.Ceiling(filesize / 1000000.0)}MB\n{res.Statistics[ImageGenStatisticType.Width]}x{res.Statistics[ImageGenStatisticType.Height]}";
                                        s.AllowedMentions = new AllowedMentions(AllowedMentionTypes.Users);
                                    });
                                }
                                else
                                {
                                    await arg.ModifyOriginalResponseAsync((s) =>
                                    {
                                        s.Content = $"Here is your variation {arg.User.Mention}\n{res.Statistics[ImageGenStatisticType.Width]}x{res.Statistics[ImageGenStatisticType.Height]} @ {(int)Math.Ceiling(filesize / 1000000.0)}MB";
                                        s.Attachments = res.Attachments;
                                        s.Components = res.Components.Build();
                                        s.AllowedMentions = new AllowedMentions(AllowedMentionTypes.Users);
                                    });
                                }
                                //await chan.SendFilesAsync(res.Attachments, $"I made it bigger for you {arg.User.Mention}", false, null, RequestOptions.Default, AllowedMentions.All, null, res.Components.Build());
                            }
                        });
                    }
                    break;
                default:
                    {
                        await arg.RespondAsync($"Unknown select menu: {arg.Data.CustomId}");
                    }
                    break;
            }
            // move upscale here as well?
        }

        private static async Task ButtonExecuted(SocketMessageComponent arg)
        {
            string[] parts = arg.Data.CustomId.Split(":");
            if (parts.Length != 2)
            {
                await arg.RespondAsync($"Invalid command");
                return;
            }
            string[] args = parts[1].Split(",");
            if (discordInfo.CheckPermission(arg.GuildId, arg.User.Id) == AccessLevel.None)
            {
                await arg.RespondAsync($"Button '{parts[0]}' not allowed.");
                return; // failed
            }
            string guildName = "";
            if (arg.GuildId.HasValue)
            {
                SocketGuild guild = _client.GetGuild(arg.GuildId.Value);
                guildName = guild.Name;
            }
            WriteToUsage(guildName, arg.User.GlobalName, parts[0]);
            switch (parts[0])
            {
                case "regenerate":
                    {
                        Task.Run(async () =>
                        {
                            if (arg.ChannelId == null)
                                await arg.RespondAsync("Request failed");
                            IMessageChannel? chan = _client.GetChannel(arg.ChannelId ?? 0) as IMessageChannel;
                            if (chan == null)
                            {
                                SharedContext.Instance.Log(LogLevel.ERR, "Button", "Channel not found");
                                await arg.RespondAsync("Request failed");
                            }
                            await arg.RespondAsync($"Regenerate request accepted.\n{GetDrawStatus()}");
                            HistoryResponse? hr = await ComfyMain.Regenerate(args[0], arg.User.Username);
                            if (hr == null)
                            {
                                SharedContext.Instance.Log(LogLevel.ERR, "Button", "Result is null");
                                await chan.SendMessageAsync("Request failed");
                            }
                            else
                            {
                                DiscordImageResponse res = CreateImageGenResponse(hr, arg.GuildId);
                                uint filesize = 0;
                                uint.TryParse(res.Statistics[ImageGenStatisticType.FileSize], out filesize);
                                if (filesize > SharedContext.Instance.GetConfig().ComfyUI.Settings.MaximumFileSize)
                                {
                                    await arg.ModifyOriginalResponseAsync((s) =>
                                    {
                                        s.Content = $"{arg.User.Mention} your image was too large to upload: {(int)Math.Ceiling(filesize / 1000000.0)}MB\n{res.Statistics[ImageGenStatisticType.Width]}x{res.Statistics[ImageGenStatisticType.Height]}";
                                        s.AllowedMentions = new AllowedMentions(AllowedMentionTypes.Users);
                                    });
                                }
                                else
                                {
                                    await arg.ModifyOriginalResponseAsync((s) =>
                                    {
                                        s.Content = $"Here is your regenerated image {arg.User.Mention}\n{res.Statistics[ImageGenStatisticType.Width]}x{res.Statistics[ImageGenStatisticType.Height]} @ {(int)Math.Ceiling(filesize / 1000000.0)}MB";
                                        s.Attachments = res.Attachments;
                                        s.Components = res.Components.Build();
                                        s.AllowedMentions = new AllowedMentions(AllowedMentionTypes.Users);
                                    });
                                }
                                //await chan.SendFilesAsync(res.Attachments, $"Is this version better {arg.User.Mention}?", false, null, RequestOptions.Default, AllowedMentions.All, null, res.Components.Build());
                            }
                        });
                    }
                    break;
                default:
                    await arg.RespondAsync($"Unknown command");
                    break;
            }
        }

        public static string GetDrawStatus()
        {
            return $"Queue length: {ComfyMain.CurrentQueueLength + 1}\nCurrent temp: {HardwareMain.GetCurrentMaxGPUTemp()}\nFan speed: {HardwareMain.GetChassisFanSpeed()}";
        }
        // seems obnoxious, but we'll try get some re-use going
        public static DiscordImageResponse CreateImageGenResponse(HistoryResponse hr, ulong? server)
        {
            DiscordImageResponse dir = new DiscordImageResponse();
            try
            {
                List<ComfyUI.Models.Image> images = new List<ComfyUI.Models.Image>();
                foreach (var node in hr.outputs)
                {
                    foreach (var results in node.Value)
                    {
                        images = results.Value.Where(x => x.type == "output").ToList();
                    }
                }
                List<FileAttachment> files = new List<FileAttachment>();
                ComponentBuilder cb = new ComponentBuilder();
                int i = 0;
                dir.Statistics.Add(ImageGenStatisticType.Id, hr.prompt[1].ToString() ?? "");
                uint maxDim = 0;
                foreach (var img in images)
                {
                    string imgPath = SharedContext.Instance.GetConfig().ComfyUI.Paths.Outputs + img.subfolder + "/" + img.filename;
                    // get the stats
                    if (File.Exists(imgPath))
                    {
                        byte[] file = File.ReadAllBytes(imgPath);
                        dir.Statistics.Add(ImageGenStatisticType.FileName, img.filename);
                        dir.Statistics.Add(ImageGenStatisticType.FileSize, file.Length.ToString());
                        using MagickImage image = new MagickImage();
                        image.Read(file);
                        dir.Statistics.Add(ImageGenStatisticType.Height, image.Height.ToString());
                        dir.Statistics.Add(ImageGenStatisticType.Width, image.Width.ToString());
                        maxDim = Math.Max(image.Height, image.Width);
                    }
                    files.Add(new FileAttachment(imgPath, img.filename, null, (bool)discordInfo.GetPreference(server, PreferenceNames.SpoilerImages) != false, false));
                    if (maxDim <= SharedContext.Instance.GetConfig().ComfyUI.Settings.MaximumControlsDimension && (bool)(discordInfo.GetPreference(server, PreferenceNames.ShowUpscaleButton) ?? true) != false)
                    {
                        ActionRowBuilder uarb = new ActionRowBuilder();
                        List<SelectMenuOptionBuilder> lsmob = new List<SelectMenuOptionBuilder>();
                        lsmob.Add(new SelectMenuOptionBuilder("1.25x", "1.25"));
                        //if(maxDim <= 2048)
                        lsmob.Add(new SelectMenuOptionBuilder("1.5x", "1.5"));
                        if (maxDim <= 1536)
                            lsmob.Add(new SelectMenuOptionBuilder("2x", "2.0"));
                        if (maxDim <= 1200)
                            lsmob.Add(new SelectMenuOptionBuilder("2.5x", "2.5"));
                        if (maxDim <= 1024)
                            lsmob.Add(new SelectMenuOptionBuilder("3x", "3.0"));
                        if (maxDim <= 768)
                            lsmob.Add(new SelectMenuOptionBuilder("4x", "4.0"));
                        SelectMenuBuilder vsmb = new SelectMenuBuilder($"upscale:{hr.prompt[1].ToString()}", lsmob, "Upscale By", 1, 1);
                        uarb.AddComponent(vsmb.Build());
                        cb.AddRow(uarb);
                    }
                    if (maxDim <= SharedContext.Instance.GetConfig().ComfyUI.Settings.MaximumControlsDimension && (bool)(discordInfo.GetPreference(server, PreferenceNames.ShowVariationMenu) ?? true) != false)
                    {
                        ActionRowBuilder varb = new ActionRowBuilder();
                        List<SelectMenuOptionBuilder> lsmob = new List<SelectMenuOptionBuilder>();
                        lsmob.Add(new SelectMenuOptionBuilder("40%", "0.4"));
                        lsmob.Add(new SelectMenuOptionBuilder("60%", "0.6"));
                        lsmob.Add(new SelectMenuOptionBuilder("75%", "0.75"));
                        lsmob.Add(new SelectMenuOptionBuilder("85%", "0.85"));
                        lsmob.Add(new SelectMenuOptionBuilder("90%", "0.9"));
                        lsmob.Add(new SelectMenuOptionBuilder("92%", "0.92"));
                        lsmob.Add(new SelectMenuOptionBuilder("94%", "0.94"));
                        lsmob.Add(new SelectMenuOptionBuilder("96%", "0.96"));
                        SelectMenuBuilder vsmb = new SelectMenuBuilder($"variation:{hr.prompt[1].ToString()}", lsmob, "Variation Amount", 1, 0);
                        varb.AddComponent(vsmb.Build());
                        cb.AddRow(varb);
                    }
                    //ButtonBuilder bb = new ButtonBuilder("v1", res.prompt[1].ToString(), ButtonStyle.Secondary, null, Emote.Parse("<:arrow_heading_up:>"), false, null);
                    if (maxDim <= SharedContext.Instance.GetConfig().ComfyUI.Settings.MaximumControlsDimension && (bool)(discordInfo.GetPreference(server, PreferenceNames.ShowRegenerateButton) ?? true) != false)
                    {
                        ActionRowBuilder arb = new ActionRowBuilder();
                        ButtonBuilder bbredo = new ButtonBuilder($"Reroll", $"regenerate:{hr.prompt[1].ToString()}", ButtonStyle.Success, null, new Emoji("🎲"), false, null);
                        //ButtonBuilder mtest = new ButtonBuilder($"Modal Test", $"modal:{hr.prompt[1].ToString()}", ButtonStyle.Success, null, new Emoji("🤔"), false, null);
                        arb.AddComponent(bbredo.Build());
                        //arb.AddComponent(mtest.Build());
                        cb.AddRow(arb);
                    }
                }
                dir.Attachments = files;
                dir.Components = cb;
                return dir;
            }
            catch (Exception ex)
            {
                SharedContext.Instance.Log(LogLevel.ERR, "Main", ex.ToString());
                return dir;
            }
        }

        public static async Task Client_Ready()
        {
            // do we need to do anything here?
            // send a message to my own server to tell me it started?
        }

        private static async Task SlashCommandHandler(SocketSlashCommand command)
        {
            //Console.WriteLine(command.User.Id);
            string guildName = "";
            if (command.GuildId.HasValue)
            {
                SocketGuild guild = _client.GetGuild(command.GuildId.Value);
                guildName = guild.Name;
                if (!discordInfo.Servers.ContainsKey(command.GuildId.Value))
                {
                    discordInfo.Servers[command.GuildId.Value] = new ServerInfo();
                    discordInfo.Servers[command.GuildId.Value].Name = guild.Name;
                    SharedContext.Instance.Log(LogLevel.INFO, "DiscordMain", $"Created new server {command.GuildId.Value} ({guild.Name})");
                    SharedContext.Instance.Log(LogLevel.INFO, "DiscordMain", WriteUsers());
                }
            }
            WriteToUsage(guildName, command.User.GlobalName, command.Data.Name);
            AccessLevel level = discordInfo.CheckPermission(command.GuildId, command.User.Id);
            SharedContext.Instance.Log(LogLevel.INFO, "DiscordMain", $"{command.User.GlobalName} ({command.User}) attempted to use {command.Data.Name} with permissions {Enum.GetName<AccessLevel>(level)}");
            if (level == AccessLevel.None)
            {
                await command.RespondAsync($"Command '{command.Data.Name}' not allowed.");
                return; // failed
            }
            if (command.Data.Name.StartsWith("draw_"))
            {
                string comfyFlow = command.Data.Name.Substring(5);
                if (SharedContext.Instance.GetConfig().ComfyUI.Flows.ContainsKey(comfyFlow))
                {
                    Task.Run(async () =>
                    {
                        string randomId = Guid.NewGuid().ToString("D");
                        await command.DeferAsync(false, RequestOptions.Default);
                        // get flow name
                        List<ComfyUIField> fields = new List<ComfyUIField>();
                        var flowName = comfyFlow;
                        foreach (var op in command.Data.Options)
                        {
                            if (SharedContext.Instance.GetConfig().ComfyUI.Flows[flowName][op.Name].Type == "Attachment" && op.Value.GetType() != typeof(string))
                            {
                                // upload to temp location
                                // replace with string. full path to image or partial?
                                // download
                                Attachment? attch = (op.Value as Attachment);
                                if (attch != null)
                                {
                                    string imgPath = await ComfyMain.DownloadImage(attch.Url, $"{SharedContext.Instance.GetConfig().ComfyUI.Paths.Temp}{randomId}_{attch.Filename}");
                                    fields.Add(new ComfyUIField(SharedContext.Instance.GetConfig().ComfyUI.Flows[flowName][op.Name].NodeTitle, SharedContext.Instance.GetConfig().ComfyUI.Flows[flowName][op.Name].Field, imgPath, SharedContext.Instance.GetConfig().ComfyUI.Flows[flowName][op.Name].Object));
                                }
                                // dont add if there's a problem
                            }
                            else
                            {
                                fields.Add(new ComfyUIField(SharedContext.Instance.GetConfig().ComfyUI.Flows[flowName][op.Name].NodeTitle, SharedContext.Instance.GetConfig().ComfyUI.Flows[flowName][op.Name].Field, op.Value, SharedContext.Instance.GetConfig().ComfyUI.Flows[flowName][op.Name].Object));
                            }
                        }
                        await command.ModifyOriginalResponseAsync((s) => { s.Content = $"Request received.\n{GetDrawStatus()}"; });
                        HistoryResponse? res = await ComfyMain.EnqueueRequest(command.User.GlobalName, flowName, fields);
                        //SharedContext.Instance.Log(LogLevel.INFO, "ComfyUI", JsonConvert.SerializeObject(res)));
                        if (res == null || res.status.status_str != "success")
                        {
                            await command.ModifyOriginalResponseAsync((s) => { s.Content = "Your request has failed."; });
                        }
                        else
                        {
                            DiscordImageResponse response = CreateImageGenResponse(res, command.GuildId);
                            try
                            {
                                uint filesize = 0;
                                uint.TryParse(response.Statistics[ImageGenStatisticType.FileSize], out filesize);
                                if (filesize > SharedContext.Instance.GetConfig().ComfyUI.Settings.MaximumFileSize)
                                {
                                    await command.ModifyOriginalResponseAsync((s) =>
                                    {
                                        s.Content = $"{command.User.Mention} your image was too large to upload: {(int)Math.Ceiling(filesize / 1000000.0)}MB\n{response.Statistics[ImageGenStatisticType.Width]}x{response.Statistics[ImageGenStatisticType.Height]}";
                                        s.AllowedMentions = new AllowedMentions(AllowedMentionTypes.Users);
                                    });
                                }
                                else
                                {
                                    await command.ModifyOriginalResponseAsync((s) =>
                                    {
                                        s.Content = $"Here is your image {command.User.Mention}\n{response.Statistics[ImageGenStatisticType.Width]}x{response.Statistics[ImageGenStatisticType.Height]} @ {(int)Math.Ceiling(filesize / 1000000.0)}MB";
                                        s.Attachments = response.Attachments;
                                        s.Components = response.Components.Build();
                                        s.AllowedMentions = new AllowedMentions(AllowedMentionTypes.Users);
                                    });
                                }
                            }
                            catch (Exception ex)
                            {
                                SharedContext.Instance.Log(LogLevel.ERR, "DiscordMain", ex.ToString());
                            }
                        }
                    });
                }
                // draw command found
                return;
            }
            // user commands
            switch (command.Data.Name)
            {
                case "status":
                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine($"Status {DateTime.Now}:\n```");
                    sb.AppendLine(HardwareMain.GetStatus());
                    sb.AppendLine(SoftwareMain.GetStatus());
                    sb.AppendLine("```");
                    await command.RespondAsync(sb.ToString());
                    return;

            }
            if (level < AccessLevel.Admin)
            {
                await command.RespondAsync($"Command '{command.Data.Name}' not allowed.");
                return; // failed
            }
            // admin commands
            switch (command.Data.Name)
            {
                case "user":
                    {
                        string uaction = ""; // add or remove
                        ulong? server = command.GuildId; // required now?
                        IUser user = null;
                        string role = ""; // admin or user
                        foreach (var op in command.Data.Options)
                        {
                            switch (op.Name)
                            {
                                case "action":
                                    {
                                        uaction = op.Value.ToString() ?? "";
                                        break;
                                    }
                                case "id":
                                    {
                                        user = (IUser)op.Value;
                                        break;
                                    }
                                case "role":
                                    {
                                        role = op.Value.ToString() ?? "";
                                        break;
                                    }
                            }
                        }
                        if (user == null)
                            return;
                        switch (uaction)
                        {
                            case "add":
                                {
                                    if (server.HasValue && server.Value != discordInfo.OwnerServer)
                                    {
                                        if (discordInfo.Servers.ContainsKey(server.Value))
                                        {
                                            // server specific
                                            if (role == "user")
                                            {
                                                if (!discordInfo.Servers[server.Value].Users.Contains(user.Id))
                                                    discordInfo.Servers[server.Value].Users.Add(user.Id);
                                            }
                                            else if (role == "admin")
                                            {
                                                if (!discordInfo.Servers[server.Value].Admins.Contains(user.Id))
                                                    discordInfo.Servers[server.Value].Admins.Add(user.Id);
                                            }
                                            break; // we done
                                        }
                                    }
                                    if (role == "user")
                                    {
                                        if (!discordInfo.GlobalUsers.Contains(user.Id))
                                            discordInfo.GlobalUsers.Add(user.Id);
                                    }
                                    else if (role == "admin")
                                    {
                                        if (!discordInfo.GlobalAdmins.Contains(user.Id))
                                            discordInfo.GlobalAdmins.Add(user.Id);
                                    }
                                    break;
                                }
                            case "remove":
                                {
                                    if (server.HasValue && server.Value != discordInfo.OwnerServer)
                                    {
                                        if (discordInfo.Servers.ContainsKey(server.Value))
                                        {
                                            // server specific
                                            if (role == "user")
                                            {
                                                discordInfo.Servers[server.Value].Users.Remove(user.Id);
                                            }
                                            else if (role == "admin")
                                            {
                                                discordInfo.Servers[server.Value].Admins.Remove(user.Id);
                                            }
                                            break; // we done
                                        }
                                    }
                                    if (role == "user")
                                    {
                                        discordInfo.GlobalUsers.Remove(user.Id);
                                    }
                                    else if (role == "admin")
                                    {
                                        discordInfo.GlobalAdmins.Remove(user.Id);
                                    }
                                    break;
                                }
                        }
                        await command.RespondAsync(WriteUsers());
                        return;
                    }
                case "software":
                    var action = command.Data.Options?.First()?.Name;
                    var name = command.Data.Options?.First().Options?.FirstOrDefault()?.Value;
                    switch (action)
                    {
                        case "start":
                            Task.Run(async () =>
                            {
                                if (name != null)
                                {
                                    await command.DeferAsync(false, RequestOptions.Default);
                                    string res = SoftwareMain.StartSoftware((string)name);
                                    SharedContext.Instance.Log(LogLevel.INFO, "SlashCommandHandler", res);
                                    await command.ModifyOriginalResponseAsync((s) => { s.Content = res; });
                                }
                            });
                            break;
                        case "stop":
                            Task.Run(async () =>
                            {
                                if (name != null)
                                {
                                    await command.DeferAsync(false, RequestOptions.Default);
                                    string res = SoftwareMain.StopSoftware((string)name);
                                    SharedContext.Instance.Log(LogLevel.INFO, "SlashCommandHandler", res);
                                    await command.ModifyOriginalResponseAsync((s) => { s.Content = res; });
                                }
                            });
                            break;
                    }
                    return;
            }
            if (level < AccessLevel.Owner)
            {
                await command.RespondAsync($"Command '{command.Data.Name}' not allowed.");
                return; // failed
            }
            // admin commands
            switch (command.Data.Name)
            {
                case "llm":
                    {
                        string llmPrompt = command.Data.Options.First(x => x.Name == "prompt")?.Value?.ToString() ?? "";
                        // alright. now what?
                        // get the channel and make sure its not a thread already
                        if (!(command.Channel is SocketTextChannel textChannel))
                        {
                            // don't try and make a new thread here
                            await command.RespondAsync("Can only start a new conversation from a text channel.");
                            Thread.Sleep(5000);
                            await command.DeleteOriginalResponseAsync();
                            return;
                        }
                        // make a new thread
                        SocketThreadChannel thread = await textChannel.CreateThreadAsync(llmPrompt);
                        // register something to listen to all messages to this thread.

                        // set up semaphor for state: i.e. waiting, generating, end
                        // send message to LLM

                        // await response. send that message to the thread. streaming and update message lots? periodically?
                    }
                    break;
                case "reregister":
                    {
                        SendCommands();
                        await command.RespondAsync("Discord commands reregistered");
                    }
                    return;
                case "fans":
                    {
                        string comName = command.Data.Options.First().Name;
                        switch (comName)
                        {
                            case "set":
                                var speed = command.Data.Options.First().Options?.FirstOrDefault()?.Value;
                                if (speed != null)
                                {
                                    HardwareMain.SetChassisFanSpeed((uint)(long)speed);
                                }
                                break;
                            case "timeout":
                                var time = command.Data.Options.First().Options?.FirstOrDefault()?.Value;
                                if (time != null)
                                {
                                    HardwareMain.SetTimeout((string)time);
                                }
                                break;
                            case "reset":
                                HardwareMain.Reset();
                                await command.RespondAsync($"Fans reverted to {HardwareMain.GetChassisFanSpeed()}%");
                                return;
                        }
                        await command.RespondAsync($"Fans currently {HardwareMain.GetChassisFanSpeed()}% until {new DateTime(HardwareMain.GetChassisFanSpindown()).ToString()}");
                        return;
                    }
                default:
                    await command.RespondAsync($"Command '{command.Data.Name}' not found.");
                    return; // failed
            }

        }


        public static async Task SendCommands()
        {
            List<ApplicationCommandProperties> applicationCommandProperties = new();
            List<ApplicationCommandProperties> guildCommandProperties = new();
            // I'm making them as global commands
            // owner - my server only?
            var fansCommand = new SlashCommandBuilder()
                .WithName("fans")
                .WithDescription("Control the server's fans manually")
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("set")
                    .WithDescription("Set the speed (%) of the server fans")
                    .WithType(ApplicationCommandOptionType.SubCommand)
                    .AddOption("speed", ApplicationCommandOptionType.Integer, "the fan speed to set as a %", isRequired: true))
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("timeout")
                    .WithDescription("Set a delay for the fans spin down")
                    .WithType(ApplicationCommandOptionType.SubCommand)
                    .AddOption("timeout", ApplicationCommandOptionType.String, "Enter a new delay as h:mm or mmm", isRequired: true))
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("reset")
                    .WithDescription("Reset the delay for the spin down")
                    .WithType(ApplicationCommandOptionType.SubCommand));
            guildCommandProperties.Add(fansCommand.Build());

            // owner - my server only
            var regCommand = new SlashCommandBuilder()
                .WithName("reregister")
                .WithDescription("Rebuilds the discord slash command set from the current config");
            guildCommandProperties.Add(regCommand.Build());

            var llmCommands = new SlashCommandBuilder()
                .WithName("llm")
                .WithDescription("Starts a conversation with the LLM in a new thread")
                .AddOption("prompt", ApplicationCommandOptionType.String, "The initial request to the LLM", true);
            guildCommandProperties.Add(llmCommands.Build());

            // admin - my server only?
            var progList = new SlashCommandOptionBuilder()
                        .WithName("name")
                        .WithType(ApplicationCommandOptionType.String)
                        .WithDescription("Target application")
                        .WithRequired(true);
            foreach (KeyValuePair<string, SoftwareRef> prog in SharedContext.Instance.GetConfig().Software)
            {
                progList.AddChoice(prog.Value.Name, prog.Key);
            }
            var programControlCommands = new SlashCommandBuilder()
                .WithName("software")
                .WithDescription("Allows starting and stopping of AI software on the compute units")
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("start")
                    .WithDescription("Starts the select piece of software")
                    .WithType(ApplicationCommandOptionType.SubCommand)
                    .AddOption(progList))
               .AddOption(new SlashCommandOptionBuilder()
                    .WithName("stop")
                    .WithDescription("Stops the select piece of software")
                    .WithType(ApplicationCommandOptionType.SubCommand)
                    .AddOption(progList));
            guildCommandProperties.Add(programControlCommands.Build());

            // admin
            var usersCommand = new SlashCommandBuilder()
                .WithName("user")
                .WithDescription("Adds or removes a user in the current server")
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("action")
                    .WithDescription("Determines if the user is to be added or removed")
                    .WithType(ApplicationCommandOptionType.String)
                    .WithRequired(true)
                    .AddChoice("add", "add")
                    .AddChoice("remove", "remove"))
                .AddOption("id", ApplicationCommandOptionType.User, "The user's ID", isRequired: true)
                .AddOption(new SlashCommandOptionBuilder()
                    .WithName("role")
                    .WithDescription("Determines the role of the user")
                    .WithType(ApplicationCommandOptionType.String)
                    .WithRequired(true)
                    .AddChoice("admin", "admin")
                    .AddChoice("user", "user"));
            applicationCommandProperties.Add(usersCommand.Build());

            // user
            int commandLimit = 25;
            // everything is limited to the first 25
            foreach (KeyValuePair<string, Dictionary<string, ComfyUIField>> flow in SharedContext.Instance.GetConfig().ComfyUI.Flows)
            {
                var flowCommand = new SlashCommandBuilder()
                    .WithName("draw_" + flow.Key)
                    .WithDescription("Generate an image using this flow");
                //.WithType(ApplicationCommandOptionType.SubCommand)
                // add each field
                int fieldCount = 0;
                foreach (KeyValuePair<string, ComfyUIField> field in flow.Value)
                {
                    //var parameterOption = new SlashCommandOptionBuilder()
                    //    .WithName(field.Key)
                    //    .WithDescription($"{field.Value.NodeTitle}: {field.Value.Field}")
                    //    .WithType(ApplicationCommandOptionType.SubCommand);

                    switch (field.Value.Type)
                    {
                        case "List<checkpoint>":
                            {
                                List<string> models = ComfyMain.GetCheckpoints(SharedContext.Instance.GetConfig().ComfyUI.Paths.Checkpoints);
                                int count = 0;
                                var modelList = new SlashCommandOptionBuilder()
                                    .WithName("checkpoint")
                                    .WithType(ApplicationCommandOptionType.String)
                                    .WithDescription("Model");
                                foreach (string m in models)
                                {
                                    modelList.AddChoice(m, m);
                                    count++;
                                    if (count >= commandLimit)
                                        break;
                                }
                                modelList.IsRequired = field.Value.Required;
                                flowCommand.AddOption(modelList);
                            }
                            break;
                        case "List<lora>":
                            {
                                List<string> loras = ComfyMain.GetCheckpoints(SharedContext.Instance.GetConfig().ComfyUI.Paths.LoRAs + field.Value.Filter);
                                int count = 0;
                                var modelList = new SlashCommandOptionBuilder()
                                    .WithName("lora")
                                    .WithType(ApplicationCommandOptionType.String)
                                    .WithDescription("Low-rank adaptation");
                                foreach (string l in loras)
                                {
                                    modelList.AddChoice(l, field.Value.Filter + l);
                                    count++;
                                    if (count >= commandLimit)
                                        break;
                                }
                                modelList.IsRequired = field.Value.Required;
                                flowCommand.AddOption(modelList);
                            }
                            break;
                        case "Integer":
                        case "Boolean":
                        case "Number":
                        case "String":
                        case "Attachment":
                            {
                                flowCommand.AddOption(field.Key, Enum.Parse<ApplicationCommandOptionType>(field.Value.Type), field.Value.Field, isRequired: field.Value.Required);
                            }
                            break;
                        case "Random<Integer>":
                        case "Random<Number>":
                            {
                                string type = field.Value.Type.Substring(7, field.Value.Type.Length - 8);
                                flowCommand.AddOption(field.Key, Enum.Parse<ApplicationCommandOptionType>(type), field.Value.Field, isRequired: false);
                            }
                            break;
                        case "List<samplers>":
                        case "List<schedules>":
                        case "List<styles>":
                            {
                                string id = field.Value.Type.Substring(5, field.Value.Type.Length - 6);
                                if (!SharedContext.Instance.GetConfig().ComfyUI.Options.ContainsKey(id))
                                    break;
                                int count = 0;
                                var optionList = new SlashCommandOptionBuilder()
                                    .WithName(field.Key)
                                    .WithType(ApplicationCommandOptionType.String)
                                    .WithDescription($"{id} option");
                                foreach (string item in SharedContext.Instance.GetConfig().ComfyUI.Options[id])
                                {

                                    optionList.AddChoice(item, item);
                                    count++;
                                    if (count >= commandLimit)
                                        break;
                                }
                                optionList.IsRequired = field.Value.Required;
                                flowCommand.AddOption(optionList);
                            }
                            break;
                    }
                    //flowCommand.AddOption(parameterOption);
                    fieldCount++;
                    if (fieldCount >= commandLimit)
                        break;
                }
                applicationCommandProperties.Add(flowCommand.Build());
                //comfyCommands.AddOption(flowCommand);
                //flowCount++;
                //if (flowCount >= commandLimit)
                //    break;
            }

            // user
            var statusCommand = new SlashCommandBuilder()
                .WithName("status")
                .WithDescription("Gets the full state of the server");
            applicationCommandProperties.Add(statusCommand.Build());

            try
            {
                if (discordInfo.OwnerServer != 0)
                {
                    var guild = _client.GetGuild(discordInfo.OwnerServer);
                    await guild.BulkOverwriteApplicationCommandAsync(guildCommandProperties.ToArray());
                }
                else
                {
                    applicationCommandProperties = applicationCommandProperties.Concat(guildCommandProperties).ToList();
                }
                await _client.BulkOverwriteGlobalApplicationCommandsAsync(applicationCommandProperties.ToArray());
                //foreach (var acp in applicationCommandProperties)
                //    await _client.CreateGlobalApplicationCommandAsync(acp);
                // Using the ready event is a simple implementation for the sake of the example. Suitable for testing and development.
                // For a production bot, it is recommended to only run the CreateGlobalApplicationCommandAsync() once for each command.
            }
            catch (HttpException exception)
            {
                // If our command was invalid, we should catch an ApplicationCommandException. This exception contains the path of the error as well as the error message. You can serialize the Error field in the exception to get a visual of where your error is.
                var json = JsonConvert.SerializeObject(exception.Errors, Formatting.Indented);

                // You can send this error somewhere or just print it to the console, for this example we're just going to print it.
                SharedContext.Instance.Log(LogLevel.ERR, "DiscordMain", json);
            }
        }

    }
}
