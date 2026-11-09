namespace Lockstep.Core;

public enum RiskLevel
{
    /// <summary>Safe for everyone; no visible functionality lost.</summary>
    Recommended,
    /// <summary>Turns off a feature some people use.</summary>
    Limited,
    /// <summary>Noticeably breaks or weakens something. Read the warning.</summary>
    Aggressive,
    /// <summary>Cuts Windows off from Microsoft services (updates, Store, sign-in).</summary>
    Extreme,
}

public enum TweakStatus { Unknown, NotApplied, Partial, Applied, NotApplicable }

public enum ActionState { NotApplied, Applied, NotApplicable }

public sealed class TweakContext(BackupStore backup, Action<string> log)
{
    public BackupStore Backup { get; } = backup;
    public Action<string> Log { get; } = log;
}

/// <summary>A single, reversible system change.</summary>
public interface ITweakAction
{
    /// <summary>Stable identity used as the backup key.</summary>
    string Key { get; }
    string Describe();
    ActionState GetState();
    void Apply(TweakContext ctx);
    void Revert(TweakContext ctx);
    /// <summary>True if Lockstep made this change (so "revert everything" may undo it).</summary>
    bool IsOwnedBy(BackupStore backup);
}

public sealed class Tweak
{
    public string Id { get; init; } = "";
    public string Category { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public RiskLevel Risk { get; init; }
    public string? Warning { get; init; }
    public IReadOnlyList<ITweakAction> Actions { get; init; } = [];

    /// <summary>
    /// Other sections that also list this tweak (e.g. Bing search is a privacy tweak that also
    /// makes Start faster). Sharing one tweak avoids two entries fighting over the same values.
    /// </summary>
    public IReadOnlyList<string> AlsoIn { get; internal set; } = [];

    public bool IsIn(string category) =>
        Category == category || AlsoIn.Contains(category, StringComparer.Ordinal);

    /// <summary>
    /// Optional safety check. Returns a reason when the tweak must not be applied on this PC
    /// right now (e.g. it would lock you out), or null when it's fine.
    /// </summary>
    public Func<string?>? Blocker { get; init; }

    public string? GetBlockReason()
    {
        try { return Blocker?.Invoke(); }
        catch { return null; }
    }

    public TweakStatus GetStatus()
    {
        int applied = 0, notApplied = 0;
        foreach (var action in Actions)
        {
            ActionState state;
            try { state = action.GetState(); }
            catch { return TweakStatus.Unknown; }

            if (state == ActionState.Applied) applied++;
            else if (state == ActionState.NotApplied) notApplied++;
        }

        if (applied == 0 && notApplied == 0) return TweakStatus.NotApplicable;
        if (notApplied == 0) return TweakStatus.Applied;
        return applied == 0 ? TweakStatus.NotApplied : TweakStatus.Partial;
    }

    /// <returns>Number of actions that failed.</returns>
    public int Apply(TweakContext ctx) => Run(ctx, apply: true);

    /// <returns>Number of actions that failed.</returns>
    public int Revert(TweakContext ctx) => Run(ctx, apply: false);

    public bool IsOwnedBy(BackupStore backup) => Actions.Any(a => a.IsOwnedBy(backup));

    private int Run(TweakContext ctx, bool apply)
    {
        int failures = 0;
        foreach (var action in Actions)
        {
            try
            {
                if (apply) action.Apply(ctx); else action.Revert(ctx);
            }
            catch (Exception ex)
            {
                failures++;
                ctx.Log($"  ✗ {action.Describe()} — {ex.Message}");
            }
        }
        return failures;
    }
}
