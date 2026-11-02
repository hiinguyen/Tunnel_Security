using System.Collections.Concurrent;
using BackendV2.Models;

namespace BackendV2.Gateway.Alerts;

/// <summary>Phân loại một giá trị theo ngưỡng: lớn hơn hoặc bằng ngưỡng là vượt.</summary>
public static class RuleEngine
{
    public static ReadingLevel Classify(double value, double? warning, double? critical)
    {
        if (critical is { } c && value >= c)
        {
            return ReadingLevel.Critical;
        }

        if (warning is { } w && value >= w)
        {
            return ReadingLevel.Warning;
        }

        return ReadingLevel.Normal;
    }
}

/// <summary>
/// Lọc dao động mức cảnh báo của từng cảm biến: tăng mức báo ngay, nhưng chỉ hạ mức sau
/// <c>recoverySamples</c> bản đo thấp hơn liên tiếp. Nhờ vậy chỉ những lần đổi mức thật sự mới
/// thành sự kiện cảnh báo, số sự kiện không tăng theo tần suất gửi dữ liệu.
/// </summary>
public sealed class SensorLevelTracker
{
    private readonly int _recoverySamples;
    private readonly ConcurrentDictionary<string, State> _states = new();

    public SensorLevelTracker(int recoverySamples)
    {
        _recoverySamples = Math.Max(1, recoverySamples);
    }

    /// <summary>Quên mức của cảm biến (ví dụ operator đóng cảnh báo tay), để lần vượt ngưỡng sau báo lại.</summary>
    public void Reset(string sensorId) => _states.TryRemove(sensorId, out _);

    /// <summary>Trả về mức mới nếu mức đã lọc thay đổi, ngược lại null.</summary>
    public ReadingLevel? Observe(string sensorId, ReadingLevel level)
    {
        var state = _states.GetOrAdd(sensorId, _ => new State());
        lock (state)
        {
            if (level > state.Current)
            {
                state.Current = level;
                state.LowerStreak = 0;
                return level;
            }

            if (level == state.Current)
            {
                state.LowerStreak = 0;
                return null;
            }

            state.StreakPeak = state.LowerStreak == 0 ? level : (level > state.StreakPeak ? level : state.StreakPeak);
            state.LowerStreak++;
            if (state.LowerStreak < _recoverySamples)
            {
                return null;
            }

            state.Current = state.StreakPeak;
            state.LowerStreak = 0;
            return state.Current;
        }
    }

    private sealed class State
    {
        public ReadingLevel Current = ReadingLevel.Normal;
        public ReadingLevel StreakPeak = ReadingLevel.Normal;
        public int LowerStreak;
    }
}
