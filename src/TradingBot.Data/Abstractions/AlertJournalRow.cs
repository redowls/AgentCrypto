namespace TradingBot.Data.Abstractions;

public sealed record AlertJournalRow(
    DateTime SentAtUtc,
    byte     Severity,
    string   Title,
    string   Body,
    string   Fingerprint,
    string   Transports,
    string   InstanceId,
    Guid?    CorrelationId,
    long     Id = 0);
