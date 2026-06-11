namespace Dc.App.ViewModels;

/// <summary>
/// 把一批原始 (key, value) 更新合并为每 key 仅最新值，保留各 key 首次出现顺序。
/// 纯算法、无框架依赖：高频流下砍掉会被立即覆盖的中间值。复用实例避免每次 flush 分配。
/// </summary>
public sealed class LiveValueCoalescer<TValue>
{
    private readonly Dictionary<string, TValue> _latest = new();
    private readonly List<string> _order = new();

    /// <summary>上次 Coalesce 的输入条数。</summary>
    public int LastInputCount { get; private set; }

    /// <summary>上次 Coalesce 的输出 key 数（实际 apply 次数）。</summary>
    public int LastOutputCount { get; private set; }

    /// <summary>
    /// 排空 tryDequeue 返回的所有项，合并为每 key 最新值；
    /// 然后按 key 首次出现顺序对每 key 触发一次 apply(key, latestValue)。
    /// </summary>
    public void Coalesce(Func<(bool ok, string key, TValue value)> tryDequeue, Action<string, TValue> apply)
    {
        _latest.Clear();
        _order.Clear();
        var input = 0;
        while (true)
        {
            var (ok, key, value) = tryDequeue();
            if (!ok) break;
            input++;
            if (!_latest.ContainsKey(key)) _order.Add(key);
            _latest[key] = value;
        }
        LastInputCount = input;
        LastOutputCount = _order.Count;
        foreach (var key in _order) apply(key, _latest[key]);
    }
}
