namespace Clicky.Core.Agent;

/// <summary>The three answers of the permission card.</summary>
public enum PermissionAnswer
{
    AllowOnce,

    /// <summary>Allow this kind of action, without asking again, until the conversation ends. Never offered for irreversible actions.</summary>
    AlwaysThisConversation,

    NotNow,
}

public enum PermissionKind
{
    /// <summary>Clicking, typing, keys, scrolling, opening pages.</summary>
    Control,

    /// <summary>Moving the user's real mouse because UI Automation couldn't do it.</summary>
    Mouse,

    /// <summary>A connected app's tool that can change something.</summary>
    Connector,

    /// <summary>The model asked first (ask_permission).</summary>
    Question,
}

/// <param name="Question">What Clicky wants to do, as a question: "Click "Save" in Notepad?"</param>
/// <param name="Detail">Why, or what exactly (the text to type, the tool's arguments), if useful.</param>
/// <param name="Irreversible">Sends, deletes, buys or can't be undone: shown as a warning, and Always isn't offered.</param>
public sealed record PermissionPrompt(string Question, string? Detail, PermissionKind Kind, bool Irreversible)
{
    public bool OffersAlways => !Irreversible;
}

/// <summary>Asks the user. The agent card is one; an unattended run (M14 Clickys) passes its own.</summary>
public interface IPermissionPrompter
{
    /// <summary>Only cancellation throws; a prompt that can't be shown answers <see cref="PermissionAnswer.NotNow"/>.</summary>
    Task<PermissionAnswer> AskAsync(PermissionPrompt prompt, CancellationToken cancellationToken);
}

/// <summary>
/// What the user has allowed in one conversation. "Always" lasts until the conversation ends (a new
/// Conversation object); irreversible actions are never covered by it. Thread-safe.
/// </summary>
public sealed class PermissionGate
{
    private readonly Lock _gate = new();
    private readonly HashSet<string> _tools = new(StringComparer.Ordinal);
    private bool _control;
    private bool _mouse;
    private bool _nextStep;

    public bool AlwaysControl
    {
        get
        {
            lock (_gate)
            {
                return _control;
            }
        }
    }

    public bool AlwaysMouse
    {
        get
        {
            lock (_gate)
            {
                return _mouse;
            }
        }
    }

    /// <summary>Is <paramref name="tool"/> allowed without asking (Always for a connector tool)?</summary>
    public bool AlwaysTool(string tool)
    {
        lock (_gate)
        {
            return _tools.Contains(tool);
        }
    }

    /// <summary>
    /// The user just said yes to the model's own ask_permission. The next step's actions (even
    /// irreversible ones, which is what they were asked about) run without a second prompt.
    /// </summary>
    public void ApproveNextStep()
    {
        lock (_gate)
        {
            _nextStep = true;
        }
    }

    /// <summary>Takes the one-step approval, if there is one.</summary>
    public bool TakeNextStepApproval()
    {
        lock (_gate)
        {
            var approved = _nextStep;
            _nextStep = false;
            return approved;
        }
    }

    /// <summary>Records an Always answer.</summary>
    /// <param name="tools">Connector tools the prompt covered.</param>
    public void Remember(PermissionKind kind, IEnumerable<string>? tools = null)
    {
        lock (_gate)
        {
            switch (kind)
            {
                case PermissionKind.Mouse:
                    _mouse = true;
                    break;
                case PermissionKind.Control or PermissionKind.Question:
                    _control = true;
                    break;
            }

            foreach (var tool in tools ?? [])
            {
                _tools.Add(tool);
            }
        }
    }
}
