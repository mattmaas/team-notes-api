using Microsoft.EntityFrameworkCore;
using TeamNotes.Api.Domain;

namespace TeamNotes.Api.Data;

public class NotesDbContext(DbContextOptions<NotesDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<Membership> Memberships => Set<Membership>();
    public DbSet<Note> Notes => Set<Note>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(user =>
        {
            user.Property(u => u.Name).HasMaxLength(100).IsRequired();
        });

        modelBuilder.Entity<Team>(team =>
        {
            team.Property(t => t.Name).HasMaxLength(100).IsRequired();
        });

        modelBuilder.Entity<Membership>(membership =>
        {
            membership.HasKey(m => new { m.UserId, m.TeamId });
            membership.Property(m => m.Role).HasConversion<string>().HasMaxLength(16);
            membership.HasOne<User>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Restrict);
            membership.HasOne<Team>().WithMany().HasForeignKey(m => m.TeamId).OnDelete(DeleteBehavior.Restrict);
            membership.HasIndex(m => m.TeamId);
            membership.ToTable(t => t.HasCheckConstraint("CK_Memberships_Role", "\"Role\" IN ('Member', 'Owner')"));
        });

        modelBuilder.Entity<Note>(note =>
        {
            note.Property(n => n.Title).HasMaxLength(Note.TitleMaxLength).IsRequired();
            note.Property(n => n.Body).HasMaxLength(Note.BodyMaxLength).IsRequired();
            note.Property(n => n.Visibility).HasConversion<string>().HasMaxLength(16);
            note.Property(n => n.Version).IsConcurrencyToken();

            note.HasOne<Team>().WithMany().HasForeignKey(n => n.TeamId).OnDelete(DeleteBehavior.Restrict);
            note.HasOne<User>().WithMany().HasForeignKey(n => n.AuthorId).OnDelete(DeleteBehavior.Restrict);

            // Supports the main query: a team's notes, newest first.
            note.HasIndex(n => new { n.TeamId, n.UpdatedAt });

            // SQLite does not enforce HasMaxLength, so the database-level rules are spelled out as CHECK constraints.
            note.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Notes_Title", $"length(trim(\"Title\")) > 0 AND length(\"Title\") <= {Note.TitleMaxLength}");
                t.HasCheckConstraint("CK_Notes_Body", $"length(\"Body\") <= {Note.BodyMaxLength}");
                t.HasCheckConstraint("CK_Notes_Visibility", "\"Visibility\" IN ('Private', 'Team')");
                t.HasCheckConstraint("CK_Notes_Version", "\"Version\" >= 1");
            });
        });
    }
}
