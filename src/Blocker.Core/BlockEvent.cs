namespace Blocker.Core;

public class BlockEvent
{
    public long Id { get; set; }
    public DateTime TimeUtc { get; set; }
    public string Kind { get; set; } = "App";
    public string Name { get; set; } = string.Empty;
    public string ProfileName { get; set; } = string.Empty;
    public bool ServiceDispatched { get; set; }
}
