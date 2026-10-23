namespace TeamNotes.Api.Domain;

/// <summary>
/// Note visibility rules, kept in one place so single-note reads and (later) list queries agree.
/// Team membership is checked separately and is always required first.
/// </summary>
public static class NoteAccess
{
    /// <summary>Team notes are readable by every member; private notes only by their author. Owners get no override.</summary>
    public static bool CanRead(Note note, Guid userId)
    {
        ArgumentNullException.ThrowIfNull(note);
        return note.Visibility == NoteVisibility.Team || note.AuthorId == userId;
    }
}
