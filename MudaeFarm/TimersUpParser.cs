using System;
using System.Text.RegularExpressions;
using Discord;

namespace MudaeFarm
{
    public static class TimersUpParser
    {
        static readonly Regex _timeRegex = new Regex(@"((?<hour>\d\d?)h\s*)?(?<minute>\d\d?)\**\s*min", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

        static bool TryParseTime(string str, out TimeSpan time)
        {
            var match = _timeRegex.Match(str);

            if (!match.Success)
            {
                time = TimeSpan.Zero;
                return false;
            }

            int.TryParse(match.Groups["hour"].Value, out var hours);
            int.TryParse(match.Groups["minute"].Value, out var minutes);

            time = new TimeSpan(hours, minutes, 0);
            return true;
        }

        static readonly Regex _intRegex = new Regex(@"\d+", RegexOptions.Compiled | RegexOptions.Singleline);

        static bool TryParseInt(string str, out int value)
            => int.TryParse(_intRegex.Match(str).Value, out value);

        public static bool TryParse(IDiscordClient client, IMessage message, out MudaeState state)
        {
            if (!message.Content.StartsWith($"**{client.CurrentUser.Username}**") ||
                message.Embeds.Count != 0 ||
                message.Attachments.Count != 0)
            {
                state = null;
                return false;
            }

            state = new MudaeState();

            var now    = DateTime.Now;
            var failed = 0;

            var lines = message.Content.Trim().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var line1 in lines)
            {
                var line = line1.ToLowerInvariant();

                if (line.Contains("claim"))
                {
                    if (TryParseTime(line, out var time))
                        state.ClaimReset = now + time;

                    state.CanClaim = line.Contains("now");
                }

                else if (line.Contains("rolls") && line.Contains("left"))
                {
                    if (TryParseInt(line, out var value))
                        state.RollsLeft = value;
                }

                else if (line.Contains("rolls") && line.Contains("reset"))
                {
                    if (TryParseTime(line, out var time))
                        state.RollsReset = now + time;
                }

                else if (!line.Contains("power") && line.Contains("react") && line.Contains("kakera"))
                {
                    if (TryParseTime(line, out var time))
                        state.KakeraReset = now + time;
                }

                else if (line.Contains("power") && line.Contains("kakera"))
                {
                    var parts = line.Split(new[] { '(' }, StringSplitOptions.RemoveEmptyEntries);

                    foreach (var part in parts)
                    {
                        if (part.Contains("consume"))
                        {
                            if (TryParseInt(part, out var value))
                                state.KakeraConsumption = value / 100.0;
                        }

                        else if (part.Contains("power"))
                        {
                            if (TryParseInt(part, out var value))
                                state.KakeraPower = value / 100.0;
                        }
                    }
                }

                else if (line.Contains("stock") && line.Contains("kakera"))
                {
                    if (TryParseInt(line, out var value))
                        state.KakeraStock = value;
                }

                else if (line.Contains("$dk"))
                {
                    if (TryParseTime(line, out var time))
                        state.KakeraDailyReset = now + time;

                    state.CanKakeraDailyReset = line.Contains("ready");
                }

                else
                {
                    ++failed;
                }
            }

            var success = failed < lines.Length / 2;

            if (success)
                state.LastUpdatedTime = now;

            return success;
        }
    }
}