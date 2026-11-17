namespace TileStartMenu.Core.Hotkey;

/// <summary>
/// Win 键手势的纯逻辑分类器：不接触钩子 / P/Invoke / 时间墙，全部由外部喂入事件与时间戳。
/// 判定规则（与契约一致）：
///   - 按下 Win → 期间没有其它键按下 → 抬起，且按住时长 ≤ MaxTapHoldMs：吞掉这次 Win 键，触发菜单。
///   - 一旦期间按下任何其它键（组合键）或按住超时：这次 Win 键必须"放行"给系统（由钩子层补送一次 Win 按下），
///     之后无论再按什么都不得再触发菜单。
/// 该类可在无 Windows 环境下直接单元自测。
/// </summary>
public sealed class WinKeyGestureClassifier
{
    /// <summary>分类器内部状态。</summary>
    public enum State
    {
        /// <summary>空闲：没有 Win 键被按下。</summary>
        Idle,
        /// <summary>Win 已按下且被钩子吞掉，正在等待是"短按"还是"组合/长按"。</summary>
        Armed,
        /// <summary>本次 Win 已经判定为组合/长按，必须原样放行给系统，直到 Win 抬起。</summary>
        Released
    }

    private readonly int _maxTapHoldMs;

    public WinKeyGestureClassifier(int maxTapHoldMs)
    {
        _maxTapHoldMs = maxTapHoldMs < 0 ? 0 : maxTapHoldMs;
    }

    public State Current { get; private set; } = State.Idle;

    /// <summary>本次吞掉的 Win 键按下时刻（毫秒，单调时间戳语义由调用方提供）。</summary>
    public long DownTimestampMs { get; private set; }

    /// <summary>
    /// 收到一次 Win 键按下。返回 true 表示钩子应当吞掉这次按下；false 表示应当放行。
    /// </summary>
    public bool OnWinDown(long nowMs)
    {
        switch (Current)
        {
            case State.Idle:
                DownTimestampMs = nowMs;
                Current = State.Armed;
                return true; // 先吞掉，后续根据其他按键 / 时长决定是否补送
            case State.Armed:
                // 已经按着一个 Win（例如 LeftWin 还没抬就按 RightWin）：视为组合场景，放行第二个 Win。
                Current = State.Released;
                return false;
            default:
                // Released：本次 Win 已经在放行中，又来一个 Win 按下，直接放行。
                return false;
        }
    }

    /// <summary>收到一次非 Win 键的按下。返回值无意义（其它键永远放行）。</summary>
    public void OnOtherKeyDown(long nowMs)
    {
        if (Current == State.Armed)
        {
            // 用户在 Win 按住期间按了别的键：这是组合键，必须补送 Win 按下，后续放行。
            Current = State.Released;
        }
    }

    /// <summary>收到一次非 Win 键的抬起。其它键永远放行，不影响 Win 手势判定。</summary>
    public void OnOtherKeyUp(long nowMs) { }

    /// <summary>
    /// 收到 Win 键抬起。返回 true 表示钩子应当吞掉这次抬起并触发 StartKeyPressed；
    /// 返回 false 表示应当把这次抬起放行给系统。
    /// </summary>
    public bool OnWinUp(long nowMs)
    {
        switch (Current)
        {
            case State.Armed:
            {
                long held = nowMs - DownTimestampMs;
                Current = State.Idle;
                // 短按且干净：吞掉抬起，触发菜单。
                // 长按：放行（钩子层若尚未补送 Win 按下，需在返回后补送一次 Win 按下再放行抬起）。
                return held <= _maxTapHoldMs;
            }
            case State.Released:
                Current = State.Idle;
                return false;
            default:
                return false;
        }
    }

    /// <summary>
    /// 定时器到期（按住超过 MaxTapHoldMs）：由钩子层在外部调用。
    /// 返回 true 表示"本次 Win 仍处于 Armed 状态，需要补送一次 Win 按下以放行给系统"。
    /// </summary>
    public bool OnHoldTimeout(long nowMs)
    {
        if (Current != State.Armed) return false;
        Current = State.Released;
        return true;
    }

    /// <summary>强制放弃本次手势（紧急卸载 / 设置变更时调用）。</summary>
    public void Reset()
    {
        Current = State.Idle;
        DownTimestampMs = 0;
    }
}
