using System;
using System.Threading;
using System.Threading.Tasks;
using Disqord;
using Disqord.Logging;
using Disqord.Rest;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using IDisqordLogger = Disqord.Logging.ILogger;

namespace MudaeFarm
{
    public interface IDiscordClientService : IHostedService
    {
        ValueTask<DiscordClient> GetClientAsync();
    }

    public class DiscordClientService : BackgroundService, IDiscordClientService
    {
        readonly IOptionsMonitor<GeneralOptions> _options;
        readonly ICredentialManager _credentials;
        readonly ILogger<DiscordClient> _logger;
        readonly IConfigurationRoot _configuration;
        readonly IServiceProvider _services;

        public DiscordClientService(IOptionsMonitor<GeneralOptions> options, ICredentialManager credentials, ILogger<DiscordClient> logger, IConfigurationRoot configuration, IServiceProvider services)
        {
            _options       = options;
            _credentials   = credentials;
            _logger        = logger;
            _configuration = configuration;
            _services      = services;
        }

        readonly TaskCompletionSource<DiscordClient> _source = new TaskCompletionSource<DiscordClient>();

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // ReSharper disable AccessToDisposedClosure
            await using var client = new DiscordClient(TokenType.User, _credentials.GetToken(), new DiscordClientConfiguration
            {
                Logger                = new LoggerAdaptor(_logger),
                MessageCache          = new DefaultMessageCache(20),
                DefaultRequestOptions = new RestRequestOptionsBuilder().WithCancellationToken(stoppingToken).Build()
            });

            client.Ready += async args =>
            {
                try
                {
                    foreach (var provider in _configuration.Providers)
                    {
                        if (provider is DiscordConfigurationProvider discordProvider)
                            await discordProvider.InitializeAsync(_services, client, stoppingToken);
                    }

                    // at this point all option values are available
                    await client.SetPresenceAsync(_options.CurrentValue.FallbackStatus);

                    _source.TrySetResult(client);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Could not initialize Discord configuration providers.");
                    _source.TrySetException(e);
                }
            };

            await client.RunAsync(stoppingToken);
            // ReSharper enable AccessToDisposedClosure
        }

        public ValueTask<DiscordClient> GetClientAsync()
        {
            if (_source.Task.IsCompletedSuccessfully)
                return new ValueTask<DiscordClient>(_source.Task.Result);

            return new ValueTask<DiscordClient>(_source.Task);
        }

        sealed class LoggerAdaptor : IDisqordLogger
        {
            readonly ILogger<DiscordClient> _logger;

            public LoggerAdaptor(ILogger<DiscordClient> logger)
            {
                _logger = logger;
            }

            public event EventHandler<MessageLoggedEventArgs> MessageLogged;

            public void Log(object sender, MessageLoggedEventArgs e)
            {
                MessageLogged?.Invoke(sender, e);

                var level = e.Severity switch
                {
                    LogMessageSeverity.Trace       => LogLevel.Trace,
                    LogMessageSeverity.Debug       => LogLevel.Debug,
                    LogMessageSeverity.Information => LogLevel.Information,
                    LogMessageSeverity.Warning     => LogLevel.Warning,
                    LogMessageSeverity.Error       => LogLevel.Error,
                    LogMessageSeverity.Critical    => LogLevel.Critical,

                    _ => LogLevel.None
                };

                _logger.Log(level, e.Exception, $"[{e.Source}] {e.Message}");
            }
        }
    }
}