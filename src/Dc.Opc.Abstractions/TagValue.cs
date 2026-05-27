namespace Dc.Opc.Abstractions;

public sealed record TagValue(
    string Item,
    object? Value,
    ushort Quality,
    DateTimeOffset Timestamp)
{
    public bool IsGood => (Quality & 0xC0) == 0xC0;
    public bool IsUncertain => (Quality & 0xC0) == 0x40;
    public bool IsBad => (Quality & 0xC0) == 0x00;
}
