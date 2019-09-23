using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;

// async methods with no await
#pragma warning disable 1998

namespace MudaeFarm
{
    public class AutoClaimer
    {
        // https://emojipedia.org/hearts/
        static readonly IEmote[] _heartEmotes =
        {
            new Emoji("\uD83D\uDC98"), // cupid
            new Emoji("\uD83D\uDC9D"), // gift_heart
            new Emoji("\uD83D\uDC96"), // sparkling_heart
            new Emoji("\uD83D\uDC97"), // heartpulse
            new Emoji("\uD83D\uDC93"), // heartbeat
            new Emoji("\uD83D\uDC9E"), // revolving_hearts
            new Emoji("\uD83D\uDC95"), // two_hearts
            new Emoji("\uD83D\uDC9F"), // heart_decoration
            new Emoji("\u2764"),       // heart
            new Emoji("\uD83E\uDDE1"), // heart (orange)
            new Emoji("\uD83D\uDC9B"), // yellow_heart
            new Emoji("\uD83D\uDC9A"), // green_heart
            new Emoji("\uD83D\uDC99"), // blue_heart
            new Emoji("\uD83D\uDC9C"), // purple_heart
            new Emoji("\uD83E\uDD0E"), // heart (brown)
            new Emoji("\uD83D\uDDA4"), // heart (black)
            new Emoji("\uD83E\uDD0D"), // heart (white)
            new Emoji("\u2665")        // hearts
        };

        readonly Config _config;
        readonly DiscordSocketClient _client;

        public AutoClaimer(Config config, DiscordSocketClient client)
        {
            _config = config;
            _client = client;
        }

        public void Initialize()
        {
            _client.MessageReceived += HandleMessageAsync;
            _client.ReactionAdded   += HandleReactionAsync;
        }

        async Task HandleMessageAsync(SocketMessage message)
        {
            if (!(message is SocketUserMessage userMessage))
                return;

            if (!MudaeInfo.IsMudae(message.Author))
                return;

            if (message.Channel is IGuildChannel guildChannel)
            {
                var guildBlacklist = _config.ClaimServersBlacklist.Lock(x => x.ToArray());

                if (guildBlacklist.Contains(guildChannel.GuildId))
                    return;
            }

            try
            {
                HandleMudaeMessage(userMessage);
            }
            catch (Exception e)
            {
                Log.Warning($"Could not handle Mudae message {message.Id} '{message.Content}'.", e);
            }
        }

        void HandleMudaeMessage(SocketUserMessage message)
        {
            if (!message.Embeds.Any())
                return;

            var embed = message.Embeds.First();

            // character must not belong to another user
            if (embed.Footer.HasValue && embed.Footer.Value.Text.StartsWith("Belongs to", StringComparison.OrdinalIgnoreCase))
                return;

            //
            if (!embed.Author.HasValue || embed.Author.Value.IconUrl != null)
                return;

            var name  = embed.Author.Value.Name.Trim().ToLowerInvariant();
            var anime = embed.Description.Split('\n')[0].Trim().ToLowerInvariant();

            var channel = message.Channel;
            var guild   = (channel as IGuildChannel)?.Guild;

            // could optimize this with RegexOptions.Compiled and caching
            var nameRegex  = _config.WishlistCharacters.Lock(x => x.Select(c => new Regex(RegexToGlob(c), RegexOptions.Singleline | RegexOptions.IgnoreCase)));
            var animeRegex = _config.WishlistAnime.Lock(x => x.Select(c => new Regex(RegexToGlob(c), RegexOptions.Singleline | RegexOptions.IgnoreCase)));

            if (nameRegex.Any(r => r.IsMatch(name)) || animeRegex.Any(r => r.IsMatch(anime)))
            {
                Log.Warning($"{guild?.Name ?? "DM"} #{channel.Name}: Found character '{name}', trying marriage.");

                // reactions may not have been attached when we received this message
                // remember this message so we can attach an appropriate reaction later when we receive it
                lock (_claimQueue)
                    _claimQueue.Add(message.Id, message);
            }
            else
            {
                Log.Info($"{guild?.Name ?? "DM"} #{channel.Name}: Ignored character '{name}', not wished.");
            }
        }

        static string RegexToGlob(string s) => $"^{Regex.Escape(s).Replace("\\*", ".*").Replace("\\?", ".")}$";

        static readonly Dictionary<ulong, IUserMessage> _claimQueue = new Dictionary<ulong, IUserMessage>();

        async Task HandleReactionAsync(Cacheable<IUserMessage, ulong> cacheable, ISocketMessageChannel channel, SocketReaction reaction)
        {
            IUserMessage message;

            lock (_claimQueue)
            {
                if (!_claimQueue.TryGetValue(reaction.MessageId, out message))
                    return;

                _claimQueue.Remove(reaction.MessageId);
            }

            // reaction must be a heart emote
            if (Array.IndexOf(_heartEmotes, reaction.Emote) == -1)
                return;

            // claim delay
            var delay = _config.ClaimDelay;

            if (delay > 0)
                await Task.Delay(TimeSpan.FromSeconds(delay));

            await message.AddReactionAsync(reaction.Emote);
        }
    }
}