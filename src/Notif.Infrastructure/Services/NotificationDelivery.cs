using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Notif.Core.Entities;
using Notif.Core.Interfaces;
using Notif.Infrastructure.Data;
using Npgsql;
using SharedKernel.Kafka;

namespace Notif.Infrastructure.Services;

/// <summary>
/// Idempotent notification pipeline shared by the Kafka consumers.
/// One <see cref="NotificationLog"/> row per (aggregateId, eventType, idempotencyKey):
/// a replay short-circuits when the row is already <c>sent</c>, and a failed send is
/// retried on redelivery while reusing the same row.
/// </summary>
internal static class NotificationDelivery
{
    /// <summary>
    /// Sends and records a notification. By default the idempotency key is the status
    /// snapshot, which keeps application notifications one-per-status. Consumers whose
    /// aggregate can repeat a status over its lifetime (job approvals/rejections) must
    /// pass the envelope event id so a later transition is not silently suppressed.
    /// </summary>
    public static async Task<MessageOutcome> SendAndLogAsync(
        NotifDbContext db,
        INotificationService sender,
        ILogger logger,
        Guid eventId,
        Guid aggregateId,
        string eventType,
        string? statusSnapshot,
        string recipientEmail,
        string subject,
        string html,
        string text,
        string templateName,
        CancellationToken ct,
        string? idempotencyKey = null)
    {
        // Lower-cased so "Accepted" and "accepted" share one idempotency row.
        var normalizedStatus = (statusSnapshot?.Trim() ?? string.Empty).ToLowerInvariant();
        var key = string.IsNullOrWhiteSpace(idempotencyKey)
            ? normalizedStatus
            : idempotencyKey.Trim();

        var existing = await db.NotificationLogs.FirstOrDefaultAsync(
            x => x.ApplicationId == aggregateId
                && x.EventType == eventType
                && x.IdempotencyKey == key,
            ct);

        if (existing is { Status: "sent" })
        {
            logger.LogInformation(
                "Notification already sent. AggregateId={AggregateId} EventType={EventType} IdempotencyKey={IdempotencyKey}.",
                aggregateId, eventType, key);
            return MessageOutcome.Handled;
        }

        var log = existing ?? new NotificationLog(
            eventId, aggregateId, eventType, normalizedStatus, recipientEmail, subject, templateName, key);

        if (existing is null)
        {
            db.NotificationLogs.Add(log);
        }

        try
        {
            await sender.SendAsync(recipientEmail, subject, html, text, ct);
            log.MarkSent();
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException dbEx) when (IsUniqueConflict(dbEx))
            {
                // A concurrent delivery already logged this notification; the email
                // went out above, so commit past it instead of looping forever.
                logger.LogInformation(
                    dbEx,
                    "Notification already logged by a concurrent delivery. AggregateId={AggregateId} EventType={EventType}.",
                    aggregateId, eventType);
                return MessageOutcome.Handled;
            }

            logger.LogInformation(
                "Email sent. AggregateId={AggregateId} EventType={EventType} Template={TemplateName}.",
                aggregateId, eventType, templateName);
            return MessageOutcome.Handled;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.MarkFailed(ex.Message);
            await db.SaveChangesAsync(CancellationToken.None);
            logger.LogWarning(
                ex,
                "Email send failed. AggregateId={AggregateId} EventType={EventType}. Will retry.",
                aggregateId, eventType);
            return MessageOutcome.Retry;
        }
    }

    private static bool IsUniqueConflict(DbUpdateException ex)
    {
        Exception? current = ex;
        while (current is not null)
        {
            // Postgres unique_violation (23505), e.g. on
            // IX_notification_logs_ApplicationId_EventType_StatusSnapshot.
            if (current is PostgresException pg && pg.SqlState == "23505")
            {
                return true;
            }

            current = current.InnerException;
        }

        return false;
    }
}
