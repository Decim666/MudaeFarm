using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Discord.WebSocket;

namespace MudaeFarm
{
    public class AutoRoller
    {
        readonly DiscordSocketClient _client;
        readonly ConfigManager _config;
        readonly MudaeStateManager _state;

        public AutoRoller(DiscordSocketClient client, ConfigManager config, MudaeStateManager state)
        {
            _client = client;
            _config = config;
            _state  = state;
        }

        // guildId - cancellationTokenSource
        readonly ConcurrentDictionary<ulong, CancellationTokenSource> _cancellations
            = new ConcurrentDictionary<ulong, CancellationTokenSource>();

        public async Task InitializeAsync()
        {
            await ReloadWorkers();

            _client.JoinedGuild      += guild => ReloadWorkers();
            _client.LeftGuild        += guild => ReloadWorkers();
            _client.GuildAvailable   += guild => ReloadWorkers();
            _client.GuildUnavailable += guild => ReloadWorkers();
        }

        Task ReloadWorkers()
        {
            var guildIds = new HashSet<ulong>();

            // start worker for rolling in guilds, on separate threads
            foreach (var guild in _client.Guilds)
            {
                guildIds.Add(guild.Id);

                if (_cancellations.ContainsKey(guild.Id))
                    continue;

                var source = _cancellations[guild.Id] = new CancellationTokenSource();
                var token  = source.Token;

                _ = Task.Run(
                    async () =>
                    {
                        while (true)
                        {
                            try
                            {
                                await RunAsync(guild, token);
                                return;
                            }
                            catch (TaskCanceledException)
                            {
                                return;
                            }
                            catch (Exception e)
                            {
                                Log.Warning($"Error while rolling in guild '{guild}'.", e);
                            }
                        }
                    },
                    token);
            }

            // stop workers for unavailable guilds
            foreach (var id in _cancellations.Keys)
            {
                if (!guildIds.Remove(id) && _cancellations.TryRemove(id, out var source))
                {
                    source.Cancel();
                    source.Dispose();

                    Log.Debug($"Stopped rolling worker for guild {id}.");
                }
            }

            return Task.CompletedTask;
        }

        async Task RunAsync(SocketGuild guild, CancellationToken cancellationToken = default)
        {
            Log.Debug($"Entering rolling loop for guild '{guild}'.");

            while (!cancellationToken.IsCancellationRequested)
            {
                var state = _state.Get(guild);

                if (state.AverageRollInterval == null)
                    state = await _state.RefreshAsync(guild);

                if (state.AverageRollInterval == null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                    continue;
                }

                foreach (var channel in guild.TextChannels)
                {
                    if (!_config.BotChannelIds.Contains(channel.Id))
                        continue;

                    using (channel.EnterTypingState())
                    {
                        await Task.Delay(_config.RollTypingDelay, cancellationToken);

                        try
                        {
                            await channel.SendMessageAsync(_config.RollCommand);

                            Log.Debug($"{channel.Guild} {channel}: Rolled '{_config.RollCommand}'.");
                        }
                        catch (Exception e)
                        {
                            Log.Warning($"{channel.Guild} {channel}: Could not send roll command.", e);
                        }
                    }

                    break;
                }

                await Task.Delay(state.AverageRollInterval.Value, cancellationToken);
            }
        }
    }
}