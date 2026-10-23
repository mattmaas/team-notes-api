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

    /// <summary>Anyone who can read a note can edit it: team notes are collaborative, private notes are the author's.</summary>
    public static bool CanEdit(Note note, Guid userId) => CanRead(note, userId);

    /// <summary>
    /// Deleting removes shared work for everyone, so it is narrower than editing: the author, or a team owner for
    /// team notes. Owners still cannot touch private notes they cannot see.
    /// </summary>
    public static bool CanDelete(Note note, Guid userId, TeamRole callerRole)
    {
        ArgumentNullException.ThrowIfNull(note);
        return note.AuthorId == userId
            || (note.Visibility == NoteVisibility.Team && callerRole == TeamRole.Owner);
    }
}
