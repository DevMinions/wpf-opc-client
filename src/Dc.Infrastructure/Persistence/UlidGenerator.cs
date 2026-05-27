namespace Dc.Infrastructure.Persistence;

public static class UlidGenerator
{
    public static string NewId() => Ulid.NewUlid().ToString();
}
