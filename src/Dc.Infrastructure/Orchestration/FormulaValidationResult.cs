namespace Dc.Infrastructure.Orchestration;

public readonly record struct FormulaValidationResult(bool IsValid, string? Error)
{
    public static FormulaValidationResult Ok() => new(true, null);
    public static FormulaValidationResult Fail(string error) => new(false, error);
}
