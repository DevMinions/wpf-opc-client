using DynamicExpresso;

namespace Dc.Infrastructure.Orchestration;

// 公式内置函数/常量的单一注册源：校验器与运行时共用，保证"能校验通过的表达式在运行时一定能算"。
internal static class FormulaBuiltins
{
    private delegate double VariadicDouble(params double[] args);

    public static void Register(Interpreter interp)
    {
        interp.SetFunction("SQRT", new Func<double, double>(Math.Sqrt));
        interp.SetFunction("ABS", new Func<double, double>(Math.Abs));
        interp.SetFunction("SIN", new Func<double, double>(Math.Sin));
        interp.SetFunction("COS", new Func<double, double>(Math.Cos));
        interp.SetFunction("TAN", new Func<double, double>(Math.Tan));
        interp.SetFunction("ASIN", new Func<double, double>(Math.Asin));
        interp.SetFunction("ACOS", new Func<double, double>(Math.Acos));
        interp.SetFunction("ATAN", new Func<double, double>(Math.Atan));
        interp.SetFunction("EXP", new Func<double, double>(Math.Exp));
        interp.SetFunction("LOG", new Func<double, double>(Math.Log));
        interp.SetFunction("LOG10", new Func<double, double>(Math.Log10));
        interp.SetFunction("FLOOR", new Func<double, double>(Math.Floor));
        interp.SetFunction("CEILING", new Func<double, double>(Math.Ceiling));
        interp.SetFunction("POW", new Func<double, double, double>(Math.Pow));
        interp.SetFunction("MIN", new Func<double, double, double>(Math.Min));
        interp.SetFunction("MAX", new Func<double, double, double>(Math.Max));
        interp.SetFunction("ROUND", new Func<double, double, double>((v, d) => Math.Round(v, (int)d)));
        interp.SetFunction("IF", new Func<double, double, double, double>((cond, a, b) => cond != 0 ? a : b));
        interp.SetFunction("AVG", new VariadicDouble(args => args.Average()));
        interp.SetFunction("SUM", new VariadicDouble(args => args.Sum()));
        interp.SetVariable("PI", Math.PI);
        interp.SetVariable("E", Math.E);
    }
}
