namespace DDS2ModManager.Services;

/// The last thing that can be put back.
///
/// Deliberately ONE step, not a stack. A multi-level undo across filesystem operations invites
/// the user to walk back through a history that may no longer be true - a mod uninstalled two
/// steps ago might since have been reinstalled by hand, and replaying against that is worse than
/// not offering it. One step covers the case this exists for: "I just clicked the wrong thing".
///
/// Only reversible actions are recorded. Anything that cannot be undone honestly is simply never
/// offered, rather than offered and then failing.
public class UndoService
{
    private static readonly Lazy<UndoService> _instance = new(() => new UndoService());
    public static UndoService Instance => _instance.Value;

    private UndoService() { }

    /// What happened, in words the user will recognise, plus how to reverse it.
    public record UndoableAction(string Description, Func<bool> Undo, DateTime AtUtc);

    private UndoableAction? _last;

    public string? Description => _last?.Description;
    public bool CanUndo => _last != null;

    /// Raised so the UI can show or hide the undo button.
    public event Action? Changed;

    public void Record(string description, Func<bool> undo)
    {
        _last = new UndoableAction(description, undo, DateTime.UtcNow);
        Changed?.Invoke();
    }

    /// Anything that makes a recorded action no longer safe to reverse clears it.
    ///
    /// Called after a scan, an install or anything else that rewrites the mod list: an undo
    /// closure holds references to ModInfo objects, and replaying it against a list those objects
    /// no longer belong to would put files back for a mod the manager has forgotten.
    public void Invalidate()
    {
        if (_last == null) return;
        _last = null;
        Changed?.Invoke();
    }

    public bool Undo()
    {
        if (_last is not { } action) return false;

        // Cleared BEFORE running, so a failed undo can't be retried into a worse state and a
        // successful one can't be applied twice.
        _last = null;
        Changed?.Invoke();

        try
        {
            var ok = action.Undo();
            LoggingService.Instance.Info(ok
                ? $"Undone: {action.Description}"
                : $"Couldn't undo: {action.Description}");
            return ok;
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Error($"Couldn't undo '{action.Description}': {ex.Message}");
            return false;
        }
    }
}
