using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;
using PokemonInvestBatch.Application.Alerting;

namespace PokemonInvestBatch.Infrastructure.Alerting;

public sealed record SmtpOptions
{
    public string Host { get; init; } = "smtp.gmail.com";

    public int Port { get; init; } = 587;

    /// <summary>Gmail address; also the From and To.</summary>
    public string? Address { get; init; }

    /// <summary>Gmail app password (requires 2FA), never the account password.</summary>
    public string? AppPassword { get; init; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Address) && !string.IsNullOrWhiteSpace(AppPassword);
}

/// <summary>Emails the operator via Gmail SMTP. An alerting failure is logged,
/// never thrown — a dead mail server must not kill the crawl.</summary>
public sealed class SmtpAlerter(SmtpOptions options, ILogger<SmtpAlerter> logger) : IAlerter
{
    public async Task RaiseAsync(string subject, string body, CancellationToken cancellationToken)
    {
        if (options.Address is not { } address || options.AppPassword is not { } appPassword)
        {
            logger.LogError("SmtpAlerter used without configuration; alert lost: {Subject}", subject);
            return;
        }

        try
        {
            var message = new MimeMessage();
            message.From.Add(MailboxAddress.Parse(address));
            message.To.Add(MailboxAddress.Parse(address));
            message.Subject = $"[PokemonInvestBatch] {subject}";
            message.Body = new TextPart("plain") { Text = body };

            using var client = new SmtpClient();
            await client.ConnectAsync(options.Host, options.Port, SecureSocketOptions.StartTls, cancellationToken);
            await client.AuthenticateAsync(address, appPassword, cancellationToken);
            await client.SendAsync(message, cancellationToken);
            await client.DisconnectAsync(quit: true, cancellationToken);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Alert email failed to send: {Subject}", subject);
        }
    }
}

/// <summary>Stands in until SMTP is configured, so alerts land in journald
/// rather than vanishing.</summary>
public sealed class LogOnlyAlerter(ILogger<LogOnlyAlerter> logger) : IAlerter
{
    public Task RaiseAsync(string subject, string body, CancellationToken cancellationToken)
    {
        logger.LogCritical("ALERT (email unconfigured): {Subject}\n{Body}", subject, body);
        return Task.CompletedTask;
    }
}
