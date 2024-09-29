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
using ComputeServerTempMonitor.Oobabooga;
using ComputeServerTempMonitor.Oobabooga.Models;
using System.Reactive;
using System.Reflection;
using System.Security.Cryptography;
using Discord.Interactions;

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
            _client = new DiscordSocketClient(new DiscordSocketConfig()
            {
                GatewayIntents = GatewayIntents.MessageContent | GatewayIntents.AllUnprivileged
            });
            _client.Log += DiscordLogger;
            _client.Ready += Client_Ready;
            _client.SlashCommandExecuted += SlashCommandHandler;
            _client.ButtonExecuted += ButtonExecuted;
            _client.SelectMenuExecuted += SelectMenuExecuted;
            _client.MessageReceived += MessageReceived;
            _client.ModalSubmitted += ModalSubmitted;
            _client.MessageUpdated += MessageUpdated;
            _client.MessageDeleted += MessageDeleted;
            _client.ThreadDeleted += ThreadDeleted;
            _client.ThreadUpdated += ThreadUpdated;
            await _client.LoginAsync(TokenType.Bot, SharedContext.Instance.GetConfig().DiscordBotToken);
            await _client.StartAsync();
        }

        private static async Task ModalSubmitted(SocketModal arg)
        {
            try
            {
                string eventId = arg.Data.CustomId;
                string[] parts = arg.Data.CustomId.Split(":");
                List<string> args = new List<string>();
                if (parts.Length >= 2)
                {
                    args = parts[1].Split(",").ToList(); // thread ID, message ID (usually)
                }
                switch (parts[0])
                {
                    case "llmedit":
                        {
                            string new_message = arg.Data.Components.ToList().FirstOrDefault(x => x.CustomId == "new_message")?.Value;
                            if (arg.Channel is SocketThreadChannel threadChannel &&  new_message != null && new_message != "")
                            {
                                await threadChannel.ModifyMessageAsync(ulong.Parse(args[1]), s => s.Content = new_message);
                                SharedContext.Instance.Log(LogLevel.INFO, "Discord.Modal", "Message content updated");
                                await arg.RespondAsync("Content Updated", null, false, true);
                                await arg.DeleteOriginalResponseAsync();// this might only work if not ephemeral
                            }
                        }
                        return;
                }
            }
            catch (Exception ex)
            {
                SharedContext.Instance.Log(LogLevel.ERR, "Discord.Modal", "An exception occurred while handling a modal interaction:\n" + ex.ToString());
            }
        }

        private static async Task ThreadUpdated(Cacheable<SocketThreadChannel, ulong> arg1, SocketThreadChannel arg2)
        {
            if (arg2.IsArchived)
            {
                // delete archived ones?
                SharedContext.Instance.Log(LogLevel.WARN, "Discord.ThreadUpdated", "Thread has been archived.");
                await arg2.SendMessageAsync("Conversation ended: " + DateTime.Now.ToString());
                var guild = _client.GetGuild(arg2.Guild.Id);
                await arg2.RemoveUserAsync(guild.CurrentUser);
                await OobaboogaMain.DeleteChat(arg2.Id);
            }
        }

        private static async Task ThreadDeleted(Cacheable<SocketThreadChannel, ulong> arg)
        {
            // just delete the chat history
            await OobaboogaMain.DeleteChat(arg.Id);
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
            try
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
            catch (Exception ex)
            {
                SharedContext.Instance.Log(LogLevel.ERR, "Discord.Menu", "An exception occurred while handling a menu interaction:\n" + ex.ToString());
            }
        }

        private static async Task ButtonExecuted(SocketMessageComponent arg)
        {
            try
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
                    case "continue":
                        {
                            Task.Run(async () =>
                            {
                                ulong tId = ulong.Parse(args[0]);
                                ulong msgId = ulong.Parse(args[1]);
                                if (SharedContext.Instance.GetConfig().Oobabooga.DefaultParams.AllowOtherUsersControl || OobaboogaMain.FindExistingChat(arg.User.GlobalName, tId) != null)
                                    await OobaboogaMain.Continue(tId, msgId, arg.User.GlobalName);
                                await arg.RespondAsync("Continued", null, false, true);
                                await arg.DeleteOriginalResponseAsync();
                            }, cancellationToken);
                        }
                        return;
                    case "stop":
                        {
                            Task.Run(async () =>
                            {
                                ulong tId = ulong.Parse(args[0]);
                                if (SharedContext.Instance.GetConfig().Oobabooga.DefaultParams.AllowOtherUsersControl || OobaboogaMain.FindExistingChat(arg.User.GlobalName, tId) != null)
                                    await OobaboogaMain.Stop(tId, arg.User.GlobalName);
                                await arg.RespondAsync("Stopped", null, false, true);
                                await arg.DeleteOriginalResponseAsync();
                            }, cancellationToken);
                        }
                        return;
                    case "deletemsg":
                        {
                            Task.Run(async () =>
                            {
                                ulong tId = ulong.Parse(args[0]);
                                ulong msgId = ulong.Parse(args[1]);
                                if (arg.Channel is SocketThreadChannel threadChannel)
                                {
                                    if (SharedContext.Instance.GetConfig().Oobabooga.DefaultParams.AllowOtherUsersControl || OobaboogaMain.FindExistingChat(arg.User.GlobalName, threadChannel.Id) != null)
                                        await threadChannel.DeleteMessageAsync(msgId);
                                }
                                await arg.RespondAsync("Message Deleted", null, false, true);
                                await arg.DeleteOriginalResponseAsync();
                            }, cancellationToken);
                        }
                        return;
                    case "delete":
                        {
                            Task.Run(async () =>
                            {
                                if (arg.Channel is SocketThreadChannel threadChannel)
                                {
                                    if (SharedContext.Instance.GetConfig().Oobabooga.DefaultParams.AllowOtherUsersControl || OobaboogaMain.FindExistingChat(arg.User.GlobalName, threadChannel.Id) != null)
                                        await threadChannel.DeleteAsync();
                                }
                            }, cancellationToken);
                        }
                        return;
                    case "llmregenerate":
                        {
                            Task.Run(async () =>
                            {
                                ulong tId = ulong.Parse(args[0]);
                                ulong msgId = ulong.Parse(args[0]);
                                if (SharedContext.Instance.GetConfig().Oobabooga.DefaultParams.AllowOtherUsersControl || OobaboogaMain.FindExistingChat(arg.User.GlobalName, tId) != null)
                                    await OobaboogaMain.Regenerate(tId, msgId, arg.User.GlobalName);
                                await arg.RespondAsync("Regenerated", null, false, true);
                                await arg.DeleteOriginalResponseAsync();
                            }, cancellationToken);
                        }
                        return;
                    case "edit":
                        {
                            // check message exists by ID
                            if (arg.Channel is SocketThreadChannel threadChannel)
                            {

                                IMessage msg = await threadChannel.GetMessageAsync(ulong.Parse(args[1]));
                                if (msg != null)
                                {
                                    Task.Run(async () =>
                                    {
                                        // show a modal with the text of the bot's reply in it. then write an update back to that message
                                        ModalBuilder mb = new ModalBuilder()
                                            .WithTitle("Edit message")
                                            .WithCustomId($"llmedit:{args[0]},{args[1]}")
                                            .AddTextInput("Message", "new_message", TextInputStyle.Paragraph, "", null, null, true, msg.Content);
                                        await arg.RespondWithModalAsync(mb.Build());
                                    }, cancellationToken);
                                }
                            }

                        }
                        return;
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
            catch (Exception ex)
            {
                SharedContext.Instance.Log(LogLevel.ERR, "Discord.Button", "An exception occurred while handling a button interaction:\n" + ex.ToString());
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
                        SelectMenuBuilder vsmb = new SelectMenuBuilder($"upscale:{hr.prompt[1].ToString()}", lsmob, "Upscale By", 1, 0);
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

        private static async Task MessageDeleted(Cacheable<IMessage, ulong> arg1, Cacheable<IMessageChannel, ulong> arg2)
        {
        ChatHistory ch = OobaboogaMain.FindExistingChat(null, arg2.Id);
        if (!SharedContext.Instance.GetConfig().Oobabooga.DefaultParams.AllowOtherUsersControl && !SharedContext.Instance.GetConfig().Oobabooga.DefaultParams.AllowOtherUsersParticipate && ch == null)
            return;
        Task.Run(async () =>
        {
            await OobaboogaMain.DeleteMessage(arg2.Id, arg1.Id);
            // replace by ID?
            SharedContext.Instance.Log(LogLevel.INFO, "DiscordMain", $"LLM thread message delete received.");
        }, cancellationToken);
        // check its a thread with an LLM attached
        // so i need to find the message in my context and remove it? 
    }

        private static async Task MessageUpdated(Cacheable<IMessage, ulong> arg1, SocketMessage arg2, ISocketMessageChannel arg3)
        {
            // i think arg3 is the thread? maybe? check?
            if (arg2.Type == MessageType.Default && arg3 is SocketThreadChannel threadChannel)
            {
                ChatHistory ch = OobaboogaMain.FindExistingChat(arg2.Author.GlobalName, threadChannel.Id);
                if (ch != null && ch.IsGenerating)
                    return;
                if (!SharedContext.Instance.GetConfig().Oobabooga.DefaultParams.AllowOtherUsersControl && !SharedContext.Instance.GetConfig().Oobabooga.DefaultParams.AllowOtherUsersParticipate && ch == null)
                    return;
                Task.Run(async () =>
                {
                    if (arg2.Content == "")
                    {
                        SharedContext.Instance.Log(LogLevel.WARN, "DiscordMain", "Unable to read message content. Probably a permissions error");
                    }
                    else
                    {
                        // replace by ID?
                        SharedContext.Instance.Log(LogLevel.INFO, "DiscordMain", $"LLM thread message edit received from {arg2.Author.GlobalName ?? arg2.Author.Username}.");
                        await OobaboogaMain.Replace(arg3.Id, arg2.Id, arg2.Content);
                    }
                }, cancellationToken);
            }
            // check its a thread with an LLM attached
            // if i update the context, the next generation will take that into account?
            //throw new NotImplementedException();
        }

        public static async Task<ulong> SendThreadMessage(ulong tId, string msg, ulong? updateMsgId, bool finished = false)
        {
            if (msg == null || msg == "" || msg.Trim() == "")
                msg = ".";
            ComponentBuilder cb = new ComponentBuilder();
            ActionRowBuilder arb = new ActionRowBuilder();
            if (finished)
            {
                // add regen, remove, edit buttons
                ButtonBuilder bbreg = new ButtonBuilder($"Regen", $"llmregenerate:{tId},{updateMsgId}", ButtonStyle.Primary, null, new Emoji("♻"), false, null);
                ButtonBuilder bbcont = new ButtonBuilder($"Cont.", $"continue:{tId},{updateMsgId}", ButtonStyle.Success, null, new Emoji("➡"), false, null);
                ButtonBuilder bbedit = new ButtonBuilder($"Edit", $"edit:{tId},{updateMsgId}", ButtonStyle.Secondary, null, new Emoji("✂"), false, null);
                ButtonBuilder bbdel = new ButtonBuilder($"Delete", $"deletemsg:{tId},{updateMsgId}", ButtonStyle.Danger, null, new Emoji("💀"), false, null);
                arb.AddComponent(bbreg.Build());
                arb.AddComponent(bbcont.Build());
                arb.AddComponent(bbedit.Build());
                arb.AddComponent(bbdel.Build());
            }
            else
            {
                ButtonBuilder bbredo = new ButtonBuilder($"Stop", $"stop:{tId}", ButtonStyle.Danger, null, new Emoji("🛑"), false, null);
                arb.AddComponent(bbredo.Build());
            }
            cb.AddRow(arb);

            var channel = _client.GetChannel(tId);
            if (channel is SocketThreadChannel threadChannel)
            {
                if (SharedContext.Instance.GetConfig().Oobabooga.DefaultParams.RemovePreviousControls && finished)
                {
                    var messages = await threadChannel.GetMessagesAsync(10).FlattenAsync();
                    foreach (var message in messages)
                    {
                        // skip the one we're working on
                        if (message.Id == updateMsgId)
                        {
                            continue;
                        }
                        if(_client.CurrentUser != null && message?.Author?.Id == _client.CurrentUser.Id)
                            await threadChannel.ModifyMessageAsync(message.Id, m => { m.Components = new ComponentBuilder().Build(); });
                    }
                }
                if (updateMsgId.HasValue)
                {
                    IMessage oldMsg = await threadChannel.GetMessageAsync(updateMsgId.Value);
                    if (oldMsg != null)
                    {
                        await threadChannel.ModifyMessageAsync(updateMsgId.Value, m => { m.Content = msg; m.Components = cb.Build(); });
                        return updateMsgId.Value;
                    }
                }
                else
                {
                    var res = await threadChannel.SendMessageAsync(msg, false, null, null, null, null, cb.Build());
                    return res.Id;
                }
            }
            return 0;
        }

            private static async Task MessageReceived(SocketMessage arg)
        {
            //SharedContext.Instance.Log(LogLevel.INFO, "MessageReceived", $"Received a new message notification: {arg.Type}");
            // i should probably check for message updates as well
            if (!arg.Author.IsBot && arg.Type == MessageType.Default)
            {
                if (arg.Channel is SocketThreadChannel threadChannel)
                {
                    // only the correct user can send messages to their instance of a thread
                    if (!SharedContext.Instance.GetConfig().Oobabooga.DefaultParams.AllowOtherUsersControl && OobaboogaMain.FindExistingChat(arg.Author.GlobalName, threadChannel.Id) == null)
                        return;
                    Task.Run(async () =>
                    {
                        using (threadChannel.EnterTypingState())
                        {
                            if (arg.Content == "")
                            {
                                SharedContext.Instance.Log(LogLevel.WARN, "DiscordMain", "Unable to read message content.");
                            }
                            else
                            {
                                SharedContext.Instance.Log(LogLevel.INFO, "DiscordMain", $"LLM reply received from {arg.Author.GlobalName}.");
                                if (!await OobaboogaMain.Reply(arg.Channel.Id, arg.Id, arg.Content))
                                {
                                    await threadChannel.SendMessageAsync("This conversation has ended.");
                                }
                            }
                        }
                    }, cancellationToken);
                }
            }
        }

        private static void AddLLMUsage(string username, uint promptTokens, uint completionTokens)
        {
            if (!usage.UsagePerUser.ContainsKey(username))
                usage.UsagePerUser.Add(username, new Dictionary<string, uint>());
            if (!usage.UsagePerUser[username].ContainsKey("chat_prompt_tokens"))
                usage.UsagePerUser[username].Add("chat_prompt_tokens", 0);
            usage.UsagePerUser[username]["chat_prompt_tokens"] += promptTokens;
            if (!usage.UsagePerUser[username].ContainsKey("chat_completion_tokens"))
                usage.UsagePerUser[username].Add("chat_completion_tokens", 0);
            usage.UsagePerUser[username]["chat_completion_tokens"] += completionTokens;

            File.WriteAllText(usageFile, JsonConvert.SerializeObject(usage, Formatting.Indented));
        }

        private static async Task SlashCommandHandler(SocketSlashCommand command)
        {
            //Console.WriteLine(command.User.Id);
            string guildName = "";
            SocketGuild guild = null; ;
            if (command.GuildId.HasValue)
            {
                guild = _client.GetGuild(command.GuildId.Value);
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
            SharedContext.Instance.Log(LogLevel.INFO, "DiscordMain", $"{command.User.GlobalName} ({command.User.Username}) attempted to use '{command.Data.Name}' with permissions {Enum.GetName<AccessLevel>(level)}");
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
                            if (SharedContext.Instance.GetConfig().ComfyUI.Flows[flowName].Fields[op.Name].Type == "Attachment" && op.Value.GetType() != typeof(string))
                            {
                                // upload to temp location
                                // replace with string. full path to image or partial?
                                // download
                                Attachment? attch = (op.Value as Attachment);
                                if (attch != null)
                                {
                                    string imgPath = await ComfyMain.DownloadImage(attch.Url, $"{SharedContext.Instance.GetConfig().ComfyUI.Paths.Temp}{randomId}_{attch.Filename}");
                                    var f = new ComfyUIField(SharedContext.Instance.GetConfig().ComfyUI.Flows[flowName].Fields[op.Name].NodeTitle, SharedContext.Instance.GetConfig().ComfyUI.Flows[flowName].Fields[op.Name].Field, imgPath, SharedContext.Instance.GetConfig().ComfyUI.Flows[flowName].Fields[op.Name].Object);
                                    f.Type = SharedContext.Instance.GetConfig().ComfyUI.Flows[flowName].Fields[op.Name].Type;
                                    f.Filter = SharedContext.Instance.GetConfig().ComfyUI.Flows[flowName].Fields[op.Name].Filter;
                                    fields.Add(f);
                                }
                                // dont add if there's a problem
                            }
                            else
                            {
                                var f = new ComfyUIField(SharedContext.Instance.GetConfig().ComfyUI.Flows[flowName].Fields[op.Name].NodeTitle, SharedContext.Instance.GetConfig().ComfyUI.Flows[flowName].Fields[op.Name].Field, op.Value, SharedContext.Instance.GetConfig().ComfyUI.Flows[flowName].Fields[op.Name].Object);
                                f.Type = SharedContext.Instance.GetConfig().ComfyUI.Flows[flowName].Fields[op.Name].Type;
                                f.Filter = SharedContext.Instance.GetConfig().ComfyUI.Flows[flowName].Fields[op.Name].Filter;
                                fields.Add(f);
                            }
                        }
                        await command.ModifyOriginalResponseAsync((s) => { s.Content = $"Request received.\n{GetDrawStatus()}"; });
                        HistoryResponse? res = await ComfyMain.EnqueueRequest(command.User.GlobalName, flowName, fields);
                        //SharedContext.Instance.Log(LogLevel.INFO, "ComfyUI", JsonConvert.SerializeObject(res)));
                        if (res == null || res.status.status_str != "success")
                        {
                            await command.ModifyOriginalResponseAsync((s) => { s.Content = "Your request has failed.";});
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
            switch (command.Data.Name)
            {
                case "chat":
                    {
                        // LLM stuff!
                        // start a new chat
                        string charName = "";
                        string systemPrompt = "";
                        foreach (var op in command.Data.Options)
                        {
                            switch (op.Name)
                            {
                                case "to":
                                    charName = (string)op.Value;
                                    break;
                                case "system_prompt":
                                    systemPrompt = (string)op.Value;
                                    break;
                            }
                        }
                        Task.Run(async () =>
                        {
                            // start a new thread? can i reply inside that?
                            if (command.Channel is SocketTextChannel textChannel)
                            {
                                ChatHistory ch = OobaboogaMain.FindExistingChat(command.User.GlobalName);
                                if (!SharedContext.Instance.GetConfig().Oobabooga.DefaultParams.AllowMultipleConversations && ch != null)
                                {
                                    // check if it exists
                                    var g = _client.GetGuild(ch.ServerId);
                                    SocketThreadChannel oldsct = g.GetThreadChannel(ch.ThreadId); // this will only work if the current guild is the one that made it
                                    if (oldsct != null && !oldsct.IsArchived)
                                    {
                                            // dont start a new one for now
                                            await command.RespondAsync("Please close your existing conversations with the bot first.", null, false, true);
                                            // should this have a button to close it for you?
                                            return;
                                    }
                                    else
                                    {
                                        // its already archived or deleted
                                        OobaboogaMain.DeleteChat(ch.ThreadId);
                                    }
                                }
                                await command.DeferAsync();
                                SocketThreadChannel stc = await textChannel.CreateThreadAsync(SharedContext.Instance.GetConfig().Oobabooga.DisplayCharacters[charName].DisplayName, ((bool)discordInfo.GetPreference(command.GuildId, PreferenceNames.CreatePrivateThreads) ? ThreadType.PrivateThread : ThreadType.PublicThread));
                                using (stc.EnterTypingState())
                                {
                                    // add the user to the private chat we've just created
                                    if (guild != null)
                                    {
                                        IGuildUser usr = guild.GetUser(command.User.Id);
                                        await stc.AddUserAsync(usr);
                                    }
                                    ChatHistory res = await OobaboogaMain.InitChat(command.User.GlobalName, charName, systemPrompt, stc.Id, stc.Guild.Id);

                                    if (res != null)
                                    {
                                        // send the greeting if we've got one
                                        //foreach (OpenAIMessage msg in res.Messages)
                                        //{
                                        //    if (msg.role != Enum.GetName(Roles.system) && msg.content != "")
                                        //        await stc.SendMessageAsync(msg.content);
                                        //}
                                        if (res.Greeting != null && res.Greeting != "")
                                        {
                                            // a button to delete the thread
                                            ComponentBuilder cb = new ComponentBuilder();
                                            ActionRowBuilder arb = new ActionRowBuilder();
                                                // add regen, remove, edit buttons
                                            ButtonBuilder bbdel = new ButtonBuilder($"Delete Thread", $"delete:{stc.Id}", ButtonStyle.Danger, null, new Emoji("❌"), false, null);
                                            arb.AddComponent(bbdel.Build());
                                            cb.AddRow(arb);

                                            await stc.SendMessageAsync(res.Greeting, false, null, null, null, null, cb.Build());

                                        }
                                        await command.DeleteOriginalResponseAsync();
                                    }
                                    else
                                    {
                                        await command.RespondAsync("Unable to start chat.", null, false, true);
                                    }
                                }
                            }
                        }, cancellationToken);
                    }
                    return;
                case "ask":
                    {
                        string systemPrompt = "";
                        string request = "";
                        foreach (var op in command.Data.Options)
                        {
                            switch (op.Name)
                            {
                                case "system_prompt":
                                    systemPrompt = (string)op.Value;
                                    break;
                                case "question":
                                    request = (string)op.Value;
                                    break;
                            }
                        }
                        if (request == "")
                        {
                            await command.RespondAsync($"Prompt is mandatory.", null, false, true);
                            return;
                        }
                        Task.Run(async () =>
                        {
                            await command.DeferAsync();
                            using (command.Channel.EnterTypingState())
                            {
                                LLMResponse res = await OobaboogaMain.Ask(request, systemPrompt);
                                AddLLMUsage(command.User.GlobalName, res.PromptTokens, res.CompletionTokens);
                                await command.ModifyOriginalResponseAsync(s => s.Content = res.Message);
                            }
                        }, cancellationToken);
                    }
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
                case "loadllm":
                    {
                        string model = "";
                        foreach (var op in command.Data.Options)
                        {
                            switch (op.Name)
                            {
                                case "model":
                                    model = (string)op.Value;
                                    break;
                            }
                        }
                        await command.DeferAsync();
                        Task.Run(async () =>
                        {
                            await OobaboogaMain.LoadModel(model);
                            await command.RespondAsync($"Load LLM model is now '{OobaboogaMain.GetLoadedModel()}'");
                        }, cancellationToken);
                        return;
                    }
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
                        ulong guildId = (command.GuildId.HasValue ? command.GuildId.Value : 0);
                        await SendCommands(guildId);
                        await command.RespondAsync($"Discord commands reregistered{(guildId != 0 ? " for guild '" + guild?.Name + "'" : "")}");
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
                    if (System.Diagnostics.Debugger.IsAttached)
                    {
                        await command.RespondAsync($"Command '{command.Data.Name}' not found.");
                    }
                    return; // failed
            }

        }

        public static async Task TestCommands()
        {
            
        }

        public static bool CommandAllowed(ulong serverId, string commands)
        {
            return (serverId == discordInfo.OwnerServer || discordInfo.GlobalCommands.Contains(commands) || discordInfo.Servers[serverId].AllowedCommands.Contains(commands));
        }

        public static async Task SendCommands(ulong updatedGuildId = 0)
        {
            // limiter
            int commandLimit = 25;

            // per guild
            Dictionary<ulong, List<ApplicationCommandProperties>> guildCommands = new Dictionary<ulong, List<ApplicationCommandProperties>>();

            // set up some reusable command groups

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

            var regCommand = new SlashCommandBuilder()
                .WithName("reregister")
                .WithDescription("Rebuilds the discord slash command set from the current config");

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

            var llmmodels = new SlashCommandOptionBuilder()
                    .WithName("model")
                    .WithDescription("The model to load for this chat")
                    .WithType(ApplicationCommandOptionType.String);
            foreach (KeyValuePair<string, ModelConfig> mdlsItem in SharedContext.Instance.GetConfig().Oobabooga.Models)
            {
                llmmodels.AddChoice(mdlsItem.Key, mdlsItem.Key);
            }

            var loadLLMCommand = new SlashCommandBuilder()
                .WithName("loadllm")
                .WithDescription("Loads a new model for the LLM chat bot")
                .AddOption(llmmodels); // I want to have some admin commands for now

            var characters = new SlashCommandOptionBuilder()
                    .WithName("to")
                    .WithDescription("The character to start a chat with")
                    .WithType(ApplicationCommandOptionType.String)
                    .WithRequired(true);
            Dictionary<string, CharacterSettings> chars = SharedContext.Instance.GetConfig().Oobabooga.DisplayCharacters;
            foreach (var item in chars)
            {
                characters.AddChoice(item.Value.DisplayName, item.Key);
            }
            var llmChatCommands = new SlashCommandBuilder()
                .WithName("chat")
                .WithDescription("Starts a conversation with the LLM in a new thread")
                .AddOption(characters) // there will be others here? maybe?
                .AddOption("system_prompt", ApplicationCommandOptionType.String, "The system prompt to use for this conversation", false);

            var llmAskCommands = new SlashCommandBuilder()
                .WithName("ask")
                .WithDescription("Ask the LLM a question")
                .AddOption("question", ApplicationCommandOptionType.String, "The question for the LLM", true)
                .AddOption("system_prompt", ApplicationCommandOptionType.String, "The system prompt to use for the response", false);

            var statusCommand = new SlashCommandBuilder()
                .WithName("status")
                .WithDescription("Gets the full state of the server");

            List<SlashCommandBuilder> drawCommands = new List<SlashCommandBuilder>();
            foreach (KeyValuePair<string, ComfyUIFlow> flow in SharedContext.Instance.GetConfig().ComfyUI.Flows)
            {
                if (!flow.Value.Visible)
                    continue; // don't show the flows not tagged as visible, but they still exist for the system to use
                var flowCommand = new SlashCommandBuilder()
                    .WithName("draw_" + flow.Key)
                    .WithDescription("Generate an image using this flow");
                //.WithType(ApplicationCommandOptionType.SubCommand)
                // add each field
                int fieldCount = 0;
                foreach (KeyValuePair<string, ComfyUIField> field in flow.Value.Fields)
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
                drawCommands.Add(flowCommand);
            }

            foreach (ulong srvId in discordInfo.Servers.Keys)
            {
                guildCommands[srvId] = new List<ApplicationCommandProperties>();
                if (CommandAllowed(srvId, "admin"))
                {
                    guildCommands[srvId].Add(loadLLMCommand.Build());
                }

                if (CommandAllowed(srvId, "hardware"))
                {
                    guildCommands[srvId].Add(fansCommand.Build());
                }

                if (CommandAllowed(srvId, "discord"))
                {
                    guildCommands[srvId].Add(regCommand.Build());
                }

                if (CommandAllowed(srvId, "software"))
                {
                    guildCommands[srvId].Add(programControlCommands.Build());
                }

                if (CommandAllowed(srvId, "users"))
                {
                    guildCommands[srvId].Add(usersCommand.Build());
                }

                if (CommandAllowed(srvId, "chat"))
                {
                    guildCommands[srvId].Add(llmChatCommands.Build());
                    guildCommands[srvId].Add(llmAskCommands.Build());
                }

                if (CommandAllowed(srvId, "status"))
                {
                    guildCommands[srvId].Add(statusCommand.Build());
                }

                if (CommandAllowed(srvId, "draw"))
                {
                    foreach (SlashCommandBuilder flowCommand in drawCommands)
                    {
                        guildCommands[srvId].Add(flowCommand.Build());
                    }
                }
            }

            try
            {
                if (updatedGuildId != 0 && guildCommands.Keys.Contains(updatedGuildId))
                {
                    var guild = _client.GetGuild(updatedGuildId);
                    await guild.BulkOverwriteApplicationCommandAsync(guildCommands[updatedGuildId].ToArray());
                }
                else
                {
                    foreach (ulong guildId in guildCommands.Keys)
                    {
                        var guild = _client.GetGuild(guildId);
                        await guild.BulkOverwriteApplicationCommandAsync(guildCommands[guildId].ToArray());
                    }
                }
                await _client.BulkOverwriteGlobalApplicationCommandsAsync(new ApplicationCommandProperties[0]); // clear all global commands
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
