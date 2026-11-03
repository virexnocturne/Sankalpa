namespace Blocker.Core;

public class BlockSession
{
    public int Id { get; set; }
    public int ProfileId { get; set; }
    public DateTime StartUtc { get; set; }
    public long DurationSeconds { get; set; }
    public bool Locked { get; set; }
    public DateTime? EndedUtc { get; set; }

    public bool IsPomodoro { get; set; } = false;
    public int PomodoroCycle { get; set; } = 1;
    public int TotalPomodoroCycles { get; set; } = 4;
    public PomodoroPhase PomodoroPhase { get; set; } = PomodoroPhase.Focus;

    public bool IsActive(DateTime nowUtc) => BlockSessionLogic.IsActive(this, nowUtc);
}
