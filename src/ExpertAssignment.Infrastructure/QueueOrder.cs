namespace ExpertAssignment.Infrastructure;

/// <summary>
/// The single definition of queue order, shared by the worker, by close (hand the freed
/// slot to the next queued request) and by create (don't let a new request jump the queue).
/// </summary>
internal static class QueueOrder
{
    /// <summary>Normal requests waiting this long join the Urgent tier (anti-starvation).</summary>
    public const string AgingInterval = "30 minutes";

    /// <summary>0 = top tier (Urgent, or Normal aged past <see cref="AgingInterval"/>), 1 = other Normal.</summary>
    public static string TierOf(string alias) =>
        $"CASE WHEN {alias}.\"Priority\" = 1 OR {alias}.\"CreatedAt\" <= now() - interval '{AgingInterval}' THEN 0 ELSE 1 END";

    /// <summary>Row value that sorts in queue order; <c>KeyOf("a") &lt; KeyOf("b")</c> means a is ahead of b.</summary>
    public static string KeyOf(string alias) => $"({TierOf(alias)}, {alias}.\"CreatedAt\", {alias}.\"Id\")";

    /// <summary>ORDER BY list for the requests table aliased as <c>r</c>.</summary>
    public static readonly string OrderBy = $"{TierOf("r")}, r.\"CreatedAt\", r.\"Id\"";
}
