namespace Blocker.Core;

public class WebRule
{
    public string Pattern { get; set; } = string.Empty;
    public bool Allow { get; set; } // true = exception (always allowed), false = block
}
