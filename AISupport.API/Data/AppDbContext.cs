using AISupport.API.Models;
using Microsoft.EntityFrameworkCore;

namespace AISupport.API.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<Escalation> Escalations => Set<Escalation>();
    public DbSet<KnowledgeArticle> KnowledgeArticles => Set<KnowledgeArticle>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.HasIndex(u => u.Email).IsUnique();
            e.Property(u => u.FullName).HasMaxLength(100).IsRequired();
            e.Property(u => u.Email).HasMaxLength(150).IsRequired();
            e.Property(u => u.PasswordHash).HasMaxLength(500).IsRequired();
            e.Property(u => u.Role).HasConversion<string>().HasMaxLength(20);
        });

        b.Entity<Conversation>(e =>
        {
            e.HasIndex(c => new { c.UserId, c.LastMessageAt });
            e.HasIndex(c => c.Status);
            e.HasIndex(c => c.CreatedAt);
            e.Property(c => c.Title).HasMaxLength(150).IsRequired();
            e.Property(c => c.Channel).HasMaxLength(20);
            e.Property(c => c.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(c => c.CurrentIntent).HasConversion<string>().HasMaxLength(30);
            e.Property(c => c.LastSentiment).HasConversion<string>().HasMaxLength(20);
            e.Property(c => c.ResolvedBy).HasConversion<string>().HasMaxLength(10);
            e.Property(c => c.FeedbackComment).HasMaxLength(500);
            e.HasOne(c => c.User).WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(c => c.AssignedAgent).WithMany().HasForeignKey(c => c.AssignedAgentId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Message>(e =>
        {
            e.HasIndex(m => new { m.ConversationId, m.Id });
            e.HasIndex(m => m.CreatedAt);
            e.Property(m => m.Content).HasMaxLength(4000).IsRequired();
            e.Property(m => m.SenderName).HasMaxLength(100);
            e.Property(m => m.Provider).HasMaxLength(60);
            e.Property(m => m.Role).HasConversion<string>().HasMaxLength(20);
            e.Property(m => m.Intent).HasConversion<string>().HasMaxLength(30);
            e.Property(m => m.Sentiment).HasConversion<string>().HasMaxLength(20);
            e.HasOne(m => m.Conversation).WithMany(c => c.Messages).HasForeignKey(m => m.ConversationId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Escalation>(e =>
        {
            e.HasIndex(x => new { x.Status, x.Priority });
            e.Property(x => x.Reason).HasMaxLength(300).IsRequired();
            e.Property(x => x.Trigger).HasMaxLength(50).IsRequired();
            e.Property(x => x.ResolutionNote).HasMaxLength(500);
            // Status & Priority stored as int so the queue can be ordered by urgency
            e.HasOne(x => x.Conversation).WithMany(c => c.Escalations).HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.AssignedAgent).WithMany().HasForeignKey(x => x.AssignedAgentId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<KnowledgeArticle>(e =>
        {
            e.Property(k => k.Title).HasMaxLength(150).IsRequired();
            e.Property(k => k.Category).HasMaxLength(50).IsRequired();
            e.Property(k => k.Content).HasMaxLength(4000).IsRequired();
            e.Property(k => k.Keywords).HasMaxLength(300);
        });
    }
}
