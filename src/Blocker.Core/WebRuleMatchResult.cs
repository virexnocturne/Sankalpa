namespace Blocker.Core;

public class WebRuleMatchResult
{
    public bool Blocked { get; set; }
    public string? ProfileName { get; set; }
    public string? MatchedBlockPattern { get; set; } // pattern or "all websites"
    public string? MatchedExceptionPattern { get; set; }
}
