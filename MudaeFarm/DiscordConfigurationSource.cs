using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Disqord;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Newtonsoft.Json;

namespace MudaeFarm
{
    public class DiscordConfigurationSource : IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder builder) => new DiscordConfigurationProvider();
    }

    public class DiscordConfigurationProvider : IConfigurationProvider
    {
        ICredentialManager _credentials;
        ILogger<DiscordConfigurationProvider> _logger;
        DiscordClient _client;
        IGuild _guild;

        public async Task InitializeAsync(IServiceProvider services, DiscordClient client, CancellationToken cancellationToken = default)
        {
            _credentials = services.GetService<ICredentialManager>();
            _logger      = services.GetService<ILogger<DiscordConfigurationProvider>>();
            _client      = client;
            _guild       = FindConfigurationGuild() ?? await CreateConfigurationGuild(cancellationToken);

            foreach (var channel in await _guild.GetChannelsAsync())
            {
                if (channel is IMessageChannel textChannel)
                    await ReloadAsync(textChannel, cancellationToken);
            }

            client.MessageReceived     += e => ReloadAsync(e.Message.Channel, cancellationToken);
            client.MessageUpdated      += e => ReloadAsync(e.Channel, cancellationToken);
            client.MessageDeleted      += e => ReloadAsync(e.Channel, cancellationToken);
            client.MessagesBulkDeleted += e => ReloadAsync(e.Channel, cancellationToken);

            client.ChannelCreated += e => ReloadAsync(e.Channel, cancellationToken);
            client.ChannelDeleted += e => ReloadAsync(e.Channel, cancellationToken);
            client.ChannelUpdated += e => ReloadAsync(e.NewChannel, cancellationToken);
        }

        IGuild FindConfigurationGuild()
        {
            foreach (var guild in _client.Guilds.Values)
            {
                if (guild.OwnerId != _client.CurrentUser.Id)
                    continue;

                var topic = guild.TextChannels.Values.FirstOrDefault(c => c.Name == "information")?.Topic ?? "";
                var lines = topic.Split('\n');

                var userId  = null as ulong?;
                var profile = null as string;

                foreach (var line in lines)
                {
                    var parts = line.Split(':', 2);

                    if (parts.Length != 2)
                        continue;

                    var key   = parts[0].Trim();
                    var value = parts[1].Trim(' ', '*'); // ignore bolding

                    switch (key.ToLowerInvariant())
                    {
                        case "mudaefarm" when ulong.TryParse(value, out var uid):
                            userId = uid;
                            break;

                        case "profile":
                            profile = value;
                            break;
                    }
                }

                if (userId == _client.CurrentUser.Id && profile == _credentials.SelectedProfile)
                {
                    _logger.LogInformation($"Using configuration server {guild.Id}: {guild.Name}");
                    return guild;
                }
            }

            return null;
        }

        async Task<IGuild> CreateConfigurationGuild(CancellationToken cancellationToken = default)
        {
            _logger.LogWarning("Initializing a new configuration server. This may take a while...");

            var watch = Stopwatch.StartNew();

            var profile   = _credentials.SelectedProfile;
            var guildName = $"MudaeFarm ({profile})";

            if (string.IsNullOrEmpty(profile) || profile.Equals("default", StringComparison.OrdinalIgnoreCase))
                guildName = "MudaeFarm";

            var regions = await _client.GetVoiceRegionsAsync();
            var guild   = await _client.CreateGuildAsync(guildName, (regions.FirstOrDefault(v => v.IsOptimal) ?? regions.First()).Id);

            // delete default channels
            foreach (var channel in await guild.GetChannelsAsync())
                await channel.DeleteAsync();

            var information = await guild.CreateTextChannelAsync("information", c => c.Topic = $"MudaeFarm: **{_client.CurrentUser.Id}**\nProfile: **{_credentials.SelectedProfile}**\nVersion: {Updater.CurrentVersion.ToString(3)}");
            await guild.CreateTextChannelAsync("wished-characters", c => c.Topic = "Configure your character wishlist here. Wildcards characters are supported. Names are *case-insensitive*.");
            await guild.CreateTextChannelAsync("wished-anime", c => c.Topic      = "Configure your anime wishlist here. Wildcards characters are supported. Names are *case-insensitive*.");
            await guild.CreateTextChannelAsync("bot-channels", c => c.Topic      = "Configure channels to enable MudaeFarm autorolling/claiming by sending the __channel ID__.");
            await guild.CreateTextChannelAsync("claim-replies", c => c.Topic     = "Configure automatic reply messages when you claim a character. One message is randomly selected. Refer to https://github.com/chiyadev/MudaeFarm for advanced templating.");
            await guild.CreateTextChannelAsync("wishlist-users", c => c.Topic    = "Configure wishlists of other users to be claimed by sending the __user ID__.");

            var notice = await information.SendMessageAsync(@"
This is your MudaeFarm server where you can configure the bot.

Check <https://github.com/chiyadev/MudaeFarm> for detailed usage guidelines!
".Trim());

            await notice.PinAsync();

            Task addSection<T>(string name) where T : new()
                => information.SendMessageAsync($"> {name}\n```json\n{JsonConvert.SerializeObject(new T(), Formatting.Indented)}\n```");

            await addSection<GeneralOptions>("General");
            await addSection<ClaimingOptions>("Claiming");
            await addSection<RollingOptions>("Rolling");

            _logger.LogInformation($"Took {watch.Elapsed.TotalSeconds:F}s to initialize configuration server {guild.Id}.");

            return guild;
        }

        const int _loadMessages = 1000;

        static async IAsyncEnumerable<IUserMessage> EnumerateMessagesAsync(IMessageChannel channel, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var page in channel.GetMessagesEnumerable(_loadMessages))
            foreach (var message in page)
            {
                if (message is IUserMessage userMessage)
                    yield return userMessage;
            }
        }

        static T DeserializeOrCreate<T>(string value, Action<T, string> configure) where T : class, new()
        {
            if (value.StartsWith('{'))
                return JsonConvert.DeserializeObject<T>(value);

            var t = new T();
            configure(t, value);
            return t;
        }

        static readonly Regex _sectionRegex = new Regex(@"^>\s*(?<section>.*?)\s*```json\s*(?={)(?<data>.*)(?<=})\s*```$", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

        async Task ReloadAsync(IChannel ch, CancellationToken cancellationToken = default)
        {
            if (!(ch is IMessageChannel channel && ch is IGuildChannel guildChannel && guildChannel.GuildId == _guild.Id))
                return;

            try
            {
                var valid = true;

                switch (channel.Name)
                {
                    case "information":
                        await foreach (var message in EnumerateMessagesAsync(channel, cancellationToken))
                        {
                            var match = _sectionRegex.Match(message.Content);

                            if (match.Success)
                            {
                                var section = match.Groups["section"].Value;
                                var data    = match.Groups["data"].Value;

                                // from v4: miscellaneous section is merged into general
                                if (section.Equals("miscellaneous", StringComparison.OrdinalIgnoreCase))
                                {
                                    await message.DeleteAsync();
                                    continue;
                                }

                                _providers[section] = ConvertToProvider(JsonConvert.DeserializeObject(data, section.ToLowerInvariant() switch
                                {
                                    "general"  => typeof(GeneralOptions),
                                    "claiming" => typeof(ClaimingOptions),
                                    "rolling"  => typeof(RollingOptions),

                                    _ => throw new NotSupportedException($"Unknown configuration section '{section}'.")
                                }));
                            }
                        }

                        break;

                    case "wished-characters":
                        var characters = new CharacterWishlist { Items = new List<CharacterWishlist.Item>() };

                        await foreach (var message in EnumerateMessagesAsync(channel, cancellationToken))
                            characters.Items.Add(DeserializeOrCreate<CharacterWishlist.Item>(message.Content, (x, v) => x.Name = v));

                        _providers["Character wishlist"] = ConvertToProvider(characters);
                        break;

                    case "wished-anime":
                        var anime = new AnimeWishlist { Items = new List<AnimeWishlist.Item>() };

                        await foreach (var message in EnumerateMessagesAsync(channel, cancellationToken))
                            anime.Items.Add(DeserializeOrCreate<AnimeWishlist.Item>(message.Content, (x, v) => x.Name = v));

                        _providers["Anime wishlist"] = ConvertToProvider(anime);
                        break;

                    case "bot-channels":
                        var channels = new BotChannelList { Items = new List<BotChannelList.Item>() };

                        await foreach (var message in EnumerateMessagesAsync(channel, cancellationToken))
                        {
                            if (ulong.TryParse(message.Content, out var id))
                            {
                                if (_client.GetChannel(id) is IGuildChannel c)
                                    await message.ModifyAsync(m => m.Content = $"<#{id}> - **{_client.GetGuild(c.GuildId).Name}**");

                                channels.Items.Add(new BotChannelList.Item { Id = id });
                                continue;
                            }

                            var mentionedChannel = message.GetChannelIds().FirstOrDefault();

                            if (mentionedChannel != 0)
                            {
                                channels.Items.Add(new BotChannelList.Item { Id = mentionedChannel });
                                continue;
                            }

                            channels.Items.Add(DeserializeOrCreate<BotChannelList.Item>(message.Content, (x, v) => x.Id = ulong.Parse(v)));
                        }

                        _providers["Bot channels"] = ConvertToProvider(channels);
                        break;

                    case "claim-replies":
                        break;

                    case "wishlist-users":
                        break;

                    default:
                        valid = false;
                        break;
                }

                if (valid)
                    _logger.LogInformation($"Reloaded configuration channel {channel.Id}: {channel.Name}");

                Interlocked.Exchange(ref _reloadToken, new ConfigurationReloadToken()).OnReload();
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, $"Could not reload configuration channel {channel.Id}: {channel.Name}");
            }
        }

        static IConfigurationProvider ConvertToProvider(object data)
        {
            // use System.Text.Json because JsonStreamConfigurationProvider uses that to deserialize
            var provider = new JsonStreamConfigurationProvider(new JsonStreamConfigurationSource { Stream = new MemoryStream(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(data)) });
            provider.Load();
            return provider;
        }

        readonly ConcurrentDictionary<string, IConfigurationProvider> _providers = new ConcurrentDictionary<string, IConfigurationProvider>(StringComparer.OrdinalIgnoreCase);

        public bool TryGet(string key, out string value)
        {
            var parts = key.Split(':', 2);

            if (parts.Length == 2 && _providers.TryGetValue(parts[0], out var provider))
                return provider.TryGet(parts[1], out value);

            value = default;
            return false;
        }

        public IEnumerable<string> GetChildKeys(IEnumerable<string> earlierKeys, string parentPath)
        {
            // ReSharper disable once PossibleMultipleEnumeration
            foreach (var (section, provider) in _providers)
            foreach (var key in provider.GetChildKeys(earlierKeys, parentPath))
                yield return $"{section}:{key}";
        }

        ConfigurationReloadToken _reloadToken = new ConfigurationReloadToken();

        public IChangeToken GetReloadToken() => _reloadToken;

        void IConfigurationProvider.Load() { }
        void IConfigurationProvider.Set(string key, string value) { }
    }
}