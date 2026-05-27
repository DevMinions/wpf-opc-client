namespace Dc.App.Dashboard;

public enum AlertSeverity { Critical, Warning }

public sealed record AlertItem(
    AlertSeverity Severity,
    string TaskId,
    string TaskName,
    string Message,
    DateTimeOffset OccurredAt);
