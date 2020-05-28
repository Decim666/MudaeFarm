using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Disqord;
using Microsoft.Extensions.Options;

namespace MudaeFarm
{
    public interface IMudaeReplySender
    {
        Task SendAsync(IMessageChannel channel, ReplyEvent @event, object substitutions, CancellationToken cancellationToken = default);
    }

    public class MudaeReplySender : IMudaeReplySender
    {
        readonly IOptionsMonitor<GeneralOptions> _options;
        readonly IOptionsMonitor<ReplyList> _replies;

        public MudaeReplySender(IOptionsMonitor<GeneralOptions> options, IOptionsMonitor<ReplyList> replies)
        {
            _options = options;
            _replies = replies;
        }

        readonly Random _random = new Random();

        public async Task SendAsync(IMessageChannel channel, ReplyEvent @event, object substitutions, CancellationToken cancellationToken = default)
        {
            var selected = SelectItem(@event);

            if (selected == null)
                return;

            var message = ApplySubstitutions(selected.Content, substitutions);

            if (message.Length == 0)
                return;

            var options    = _options.CurrentValue;
            var typingTime = TimeSpan.FromMinutes(message.Length / options.ReplyTypingCpm);

            lock (_random)
                typingTime *= 0.9 + 0.2 * _random.NextDouble();

            await Task.Delay(typingTime, cancellationToken);

            await channel.SendMessageAsync(message);
        }

        ReplyList.Item SelectItem(ReplyEvent @event)
        {
            var items = _replies.CurrentValue.Items.Where(x => x.Event == @event).ToArray();

            if (items.Length == 0)
                return null;

            double selected;

            lock (_random)
                selected = _random.NextDouble();

            selected *= items.Sum(x => x.Weight);

            var current = 0.0;

            foreach (var item in items)
            {
                current += item.Weight;

                if (selected <= current)
                    return item;
            }

            return null;
        }

        static string ApplySubstitutions(string str, object obj)
        {
            var builder = new StringBuilder(str);

            if (obj != null)
                foreach (var property in obj.GetType().GetProperties())
                {
                    if (property.CanRead)
                        builder.Replace($"*{property.Name}*", property.GetValue(obj).ToString());
                }

            return builder.ToString();
        }
    }
}