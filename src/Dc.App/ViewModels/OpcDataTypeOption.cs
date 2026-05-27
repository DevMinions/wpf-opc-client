namespace Dc.App.ViewModels;

public sealed record OpcDataTypeOption(int Code, string DisplayName)
{
    public override string ToString() => $"{Code} - {DisplayName}";

    public static readonly IReadOnlyList<OpcDataTypeOption> All = new[]
    {
        new OpcDataTypeOption(0, "默认"),
        new OpcDataTypeOption(11, "Boolean"),
        new OpcDataTypeOption(16, "Int8"),
        new OpcDataTypeOption(17, "UInt8"),
        new OpcDataTypeOption(2,  "Int16"),
        new OpcDataTypeOption(18, "UInt16"),
        new OpcDataTypeOption(3,  "Int32"),
        new OpcDataTypeOption(19, "UInt32"),
        new OpcDataTypeOption(20, "Int64"),
        new OpcDataTypeOption(21, "UInt64"),
        new OpcDataTypeOption(4,  "Float32"),
        new OpcDataTypeOption(5,  "Float64"),
        new OpcDataTypeOption(8,  "String"),
        new OpcDataTypeOption(7,  "DateTime"),
    };

    public static OpcDataTypeOption FromCode(int code) =>
        All.FirstOrDefault(o => o.Code == code) ?? new OpcDataTypeOption(code, $"未知({code})");
}
