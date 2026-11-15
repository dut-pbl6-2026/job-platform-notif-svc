namespace Notif.Core.Templates;

/// <summary>Bot replies for the Telegram job alert bot (PBL6-63, SRS TELE-01-02). English only; TELE-01-06 is out of scope.</summary>
public static class TelegramMessages
{
    public const string Help =
        "Job Platform alert bot\n\n"
        + "/subscribe - receive job alerts in this chat\n"
        + "/unsubscribe - stop job alerts\n"
        + "/help - show this message";

    public const string Welcome = "Welcome to the Job Platform alert bot.\n\n" + Help;

    public const string Subscribed = "You are subscribed to job alerts. Send /unsubscribe to stop.";

    public const string AlreadySubscribed = "This chat is already subscribed to job alerts.";

    public const string Unsubscribed = "You are unsubscribed from job alerts.";

    public const string NotSubscribed = "This chat is not subscribed to job alerts.";
}
