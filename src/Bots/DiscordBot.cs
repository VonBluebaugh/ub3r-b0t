﻿
namespace UB3RB0T
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net.Http;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Discord;
    using Discord.WebSocket;
    using Flurl.Http;
    using Newtonsoft.Json;
    using UB3RIRC;
    using UB3RB0T.Commands;

    public partial class DiscordBot : Bot
    {
        private AudioManager audioManager = new AudioManager();
        private Timer statsTimer;
        private Dictionary<string, IDiscordCommand> discordCommands;

        private List<IModule> modules;

        public DiscordBot(int shard, int totalShards) : base(shard, totalShards)
        {
            // TODO: Add these via reflection processing or config instead of this nonsense
            // order matters
            this.modules = new List<IModule>
            {
                new WordCensorModule(),
                new BotlessModule(),
                new FaqModule(),
                new OcrModule(),
            };
        }

        protected override string UserId => this.Client.CurrentUser.Id.ToString();

        public DiscordSocketClient Client { get; private set; }

        public static ConcurrentDictionary<string, Attachment> imageUrls = new ConcurrentDictionary<string, Attachment>();
        private ConcurrentDictionary<ITextChannel, List<string>> messageBatch = new ConcurrentDictionary<ITextChannel, List<string>>();

        public override BotType BotType => BotType.Discord;

        protected override async Task StartAsyncInternal()
        {
            if (string.IsNullOrEmpty(this.Config.Discord.Token))
            {
                throw new InvalidConfigException("Discord auth token is missing.");
            }

            this.Client = new DiscordSocketClient(new DiscordSocketConfig
            {
                ShardId = this.Shard,
                TotalShards = this.TotalShards,
                LogLevel = LogSeverity.Verbose,
                MessageCacheSize = 100,
            });

            this.Client.Ready += Client_Ready;
            this.Client.Log += Discord_Log;
            this.Client.Disconnected += (ex) => this.HandleEvent(DiscordEventType.Disconnected, ex);

            this.Client.JoinedGuild += (guild) => this.HandleEvent(DiscordEventType.JoinedGuild, guild);
            this.Client.LeftGuild += (guild) => this.HandleEvent(DiscordEventType.LeftGuild, guild);

            this.Client.UserJoined += (user) => this.HandleEvent(DiscordEventType.UserJoined, user);
            this.Client.UserLeft += (user) => this.HandleEvent(DiscordEventType.UserLeft, user);
            this.Client.UserVoiceStateUpdated += (user, beforeState, afterState) => this.HandleEvent(DiscordEventType.UserVoiceStateUpdated, user, beforeState, afterState);
            this.Client.GuildMemberUpdated += (userBefore, userAfter) => this.HandleEvent(DiscordEventType.GuildMemberUpdated, userBefore, userAfter);
            this.Client.UserBanned += (user, guild) => this.HandleEvent(DiscordEventType.UserBanned, user, guild);

            this.Client.MessageReceived += (message) => this.HandleEvent(DiscordEventType.MessageReceived, message);
            this.Client.MessageUpdated += (messageBefore, messageAfter, channel) => this.HandleEvent(DiscordEventType.MessageUpdated, messageBefore, messageAfter, channel);
            this.Client.MessageDeleted += (message, channel) => this.HandleEvent(DiscordEventType.MessageDeleted, message, channel);
            this.Client.ReactionAdded += (message, channel, reaction) => this.HandleEvent(DiscordEventType.ReactionAdded, message, channel, reaction);

            // TODO: Add these via reflection processing or config instead of this nonsense
            this.discordCommands = new Dictionary<string, IDiscordCommand>
            {
                { "debug", new DebugCommand() },
                { "seen", new SeenCommand() },
                { "remove", new RemoveCommand() },
                { "clear", new ClearCommand() },
                { "status", new StatusCommand() },
                { "voice", new VoiceJoinCommand() },
                { "dvoice", new VoiceLeaveCommand() },
                { "devoice", new VoiceLeaveCommand() },
                { "captain_planet", new CaptainCommand() },
                { "jpeg", new JpegCommand() },
                { "userinfo", new UserInfoCommand() },
                { "serverinfo", new ServerInfoCommand() },
                { "roles", new RolesCommand() },
                { "admin", new AdminCommand() },
                { "eval", new EvalCommand() },
                { "quickpoll", new QuickPollCommand() },
                { "qp", new QuickPollCommand() },
                { "role", new RoleCommand(true) },
                { "derole", new RoleCommand(false) },
                { "fr", new FeedbackCommand() },
            };

            await this.Client.LoginAsync(TokenType.Bot, this.Config.Discord.Token);
            await this.Client.StartAsync();

            this.statsTimer = new Timer(StatsTimerAsync, null, 3600000, 3600000);
            this.StartBatchMessageProcessing();
        }

        protected override async Task StopAsyncInternal(bool unexpected)
        {
            // explicitly leave all audio channels so that we can say goodbye
            if (!unexpected)
            {
                var audioTask = this.audioManager.LeaveAllAudioAsync();
                var timeoutTask = Task.Delay(20000);
                await Task.WhenAny(audioTask, timeoutTask);
            }

            this.Client.StopAsync().Forget(); // TODO: awaiting this likes to hang -- investigate w/ library
            await Task.Delay(5000);
        }

        public override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            this.statsTimer?.Dispose();
            this.Logger.Log(LogType.Debug, "disposing of client");
            this.Client?.Dispose();
            this.Logger.Log(LogType.Debug, "disposing of audio manager");
            this.audioManager?.Dispose();
        }

        /// <inheritdoc />
        protected override async Task<bool> SendNotification(NotificationData notification)
        {
            var guild = this.Client.GetGuild(Convert.ToUInt64(notification.Server)) as IGuild;

            // if the guild wasn't found, it belongs to another shard.
            if (guild == null)
            {
                return false;
            }

            var channelToUse = this.Client.GetChannel(Convert.ToUInt64(notification.Channel)) as ITextChannel;
            // if the channel doesn't exist or we don't have send permissions, try to get the default channel instead.
            if (!channelToUse?.GetCurrentUserPermissions().SendMessages ?? true)
            {
                channelToUse = await guild.GetDefaultChannelAsync() as ITextChannel;

                // if the default channel couldn't be found or we don't have permissions, then we're SOL.
                // flag as processed.
                if (channelToUse == null || !channelToUse.GetCurrentUserPermissions().SendMessages)
                {
                    return true;
                }
            }

            var settings = SettingsConfig.GetSettings(notification.Server);
            // adjust the notification text to disable discord link parsing, if configured to do so
            if (settings.DisableLinkParsing && notification.Type != NotificationType.Reminder)
            {
                notification.Text = Consts.UrlRegex.Replace(notification.Text, new MatchEvaluator((Match urlMatch) =>
                {
                    return $"<{urlMatch.Captures[0]}>";
                }));
            }

            try
            {
                if (notification.Embed != null && settings.HasFlag(notification.Type))
                {
                    // TODO: discord handles twitter embeds nicely; should adjust the notification data accordingly so we don't need this explicit check here
                    if (notification.Type == NotificationType.Twitter)
                    {
                        await channelToUse.SendMessageAsync(notification.Embed.Url);
                    }
                    else
                    {
                        var customText = settings.NotificationText.FirstOrDefault(n => n.Type == notification.Type)?.Text;
                        var messageText = string.IsNullOrEmpty(notification.Embed.Url) ? string.Empty : $"<{notification.Embed.Url}>";
                        if (!string.IsNullOrEmpty(customText))
                        {
                            messageText += $" {customText}";
                        }

                        await channelToUse.SendMessageAsync(messageText, false, notification.Embed.CreateEmbedBuilder().Build());
                    }
                }
                else
                {
                    await channelToUse.SendMessageAsync(notification.Text);
                }

                var props = new Dictionary<string, string> {
                        { "server", notification.Server },
                        { "channel", notification.Channel },
                        { "notificationType", notification.Type.ToString() },
                };
                this.TrackEvent("notificationSent", props);
            }
            catch (Exception ex)
            {
                // TODO:
                // Add retry support.
                string extraData = null;
                if (notification.Embed != null)
                {
                    extraData = JsonConvert.SerializeObject(notification.Embed);
                }
                this.Logger.Log(LogType.Error, $"Failed to send notification {extraData}: {ex}");
            }

            return true;
        }

        protected override HeartbeatData GetHeartbeatData()
        {
            return new HeartbeatData
            {
                ServerCount = this.Client.Guilds.Count(),
                VoiceChannelCount = this.Client.Guilds.Select(g => g.CurrentUser?.VoiceChannel).Where(v => v != null).Count(),
                UserCount = this.Client.Guilds.Sum(x => x.Users.Count),
                ChannelCount = this.Client.Guilds.Sum(x => x.TextChannels.Count),
            };
        }

        /// <summary>
        /// Timer callback to update various bot stats sites.
        /// </summary>
        /// <param name="state"></param>
        private async void StatsTimerAsync(object state)
        {
            var shardCount = this.TotalShards;
            var guildCount = this.Client.Guilds.Count();
            var shardId = this.Client.ShardId;

            if (!string.IsNullOrEmpty(this.Config.Discord.DiscordBotsKey))
            {
                try
                {
                    var result = await $"https://bots.discord.pw/api/bots/{this.UserId}/stats"
                        .WithHeader("Authorization", this.Config.Discord.DiscordBotsKey)
                        .PostJsonAsync(new { shard_id = shardId, shard_count = shardCount, server_count = guildCount });
                }
                catch (Exception ex)
                {
                    this.Logger.Log(LogType.Warn, $"Failed to update bots.discord.pw stats: {ex}");
                }
            }

            if (!string.IsNullOrEmpty(this.Config.Discord.CarbonStatsKey))
            {
                try
                {
                    var result = await "https://www.carbonitex.net/discord/data/botdata.php"
                        .PostJsonAsync(new { key = this.Config.Discord.CarbonStatsKey, shard_id = shardId, shard_count = shardCount, servercount = guildCount });
                }
                catch (Exception ex)
                {
                    this.Logger.Log(LogType.Warn, $"Failed to update carbon stats: {ex}");
                }
            }

            if (!string.IsNullOrEmpty(this.Config.Discord.DiscordListKey) && this.Shard == 0)
            {
                try
                {
                    using (var httpClient = new HttpClient())
                    {
                        httpClient.BaseAddress = new Uri("https://bots.discordlist.net");
                        var content = new FormUrlEncodedContent(new[]
                        {
                            new KeyValuePair<string, string>("token", this.Config.Discord.DiscordListKey),
                            new KeyValuePair<string, string>("servers", (guildCount * shardCount).ToString()),
                        });

                        var result = await httpClient.PostAsync("/api", content);
                        if (!result.IsSuccessStatusCode)
                        {
                            this.Logger.Log(LogType.Warn, await result.Content.ReadAsStringAsync());
                        }
                    }
                }
                catch (Exception ex)
                {
                    this.Logger.Log(LogType.Warn, $"Failed to update discordlist.net stats: {ex}");
                }
            }

            if (!string.IsNullOrEmpty(this.Config.Discord.DiscordBotsOrgKey))
            {
                try
                {
                    var result = await $"https://discordbots.org/api/bots/{this.UserId}/stats"
                        .WithHeader("Authorization", this.Config.Discord.DiscordBotsOrgKey)
                        .PostJsonAsync(new { shard_id = shardId, shard_count = shardCount, server_count = guildCount });
                }
                catch (Exception ex)
                {
                    this.Logger.Log(LogType.Warn, $"Failed to update discordbots.org stats: {ex}");
                }
            }
        }

        private void BatchSendMessageAsync(ITextChannel channel, string text)
        {
            messageBatch.AddOrUpdate(channel, new List<string> { text }, (ITextChannel key, List<string> val) =>
            {
                val.Add(text);
                return val;
            });
        }

        private void StartBatchMessageProcessing()
        {
            Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        // copy the current channels
                        ITextChannel[] channels = messageBatch.Keys.ToArray();

                        foreach (var channel in channels)
                        {
                            messageBatch.TryRemove(channel, out var messages);

                            string messageToSend = string.Empty;
                            foreach (var message in messages)
                            {
                                if (messageToSend.Length + message.Length + 1 < Consts.MaxMessageLength)
                                {
                                    messageToSend += message + "\n";
                                }
                                else
                                {
                                    await channel.SendMessageAsync(messageToSend);
                                    messageToSend = message + "\n";
                                }
                            }

                            if (!string.IsNullOrEmpty(messageToSend))
                            {
                                await channel.SendMessageAsync(messageToSend);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        this.Logger.Log(LogType.Warn, $"{ex}");
                    }

                    await Task.Delay(10000);
                }
            }).Forget();
        }

        private bool IsAuthorOwner(SocketUserMessage message)
        {
            return message.Author.Id == this.Config.Discord?.OwnerId;
        }

        private bool IsAuthorPatron(ulong userId)
        {
            return this.Config.Discord.Patrons.Contains(userId);
        }
    }
}
