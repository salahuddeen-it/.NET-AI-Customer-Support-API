using AISupport.API.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AISupport.API.Data;

/// <summary>Creates the database and inserts demo users, knowledge base and sample conversations (for analytics).</summary>
public static class DbSeeder
{
    public static async Task SeedAsync(AppDbContext db, IPasswordHasher<User> hasher, ILogger logger)
    {
        await db.Database.EnsureCreatedAsync();
        if (await db.Users.AnyAsync()) return;

        User NewUser(string name, string email, string password, UserRole role)
        {
            var u = new User { FullName = name, Email = email, Role = role, CreatedAt = DateTime.UtcNow.AddDays(-30) };
            u.PasswordHash = hasher.HashPassword(u, password);
            return u;
        }

        var admin = NewUser("Support Admin", "admin@support.ai", "Admin@123", UserRole.Admin);
        var sana = NewUser("Sana Iqbal", "agent@support.ai", "Agent@123", UserRole.Agent);
        var omar = NewUser("Omar Farooq", "agent2@support.ai", "Agent@123", UserRole.Agent);
        var customers = new[]
        {
            NewUser("Ali Raza", "customer@support.ai", "Customer@123", UserRole.Customer),
            NewUser("Ayesha Siddiqui", "ayesha@example.com", "Customer@123", UserRole.Customer),
            NewUser("Bilal Ahmed", "bilal@example.com", "Customer@123", UserRole.Customer),
            NewUser("Fatima Noor", "fatima@example.com", "Customer@123", UserRole.Customer)
        };
        db.Users.AddRange(admin, sana, omar);
        db.Users.AddRange(customers);

        // ---------------- Knowledge base ----------------
        KnowledgeArticle Kb(string title, string category, string keywords, string content) =>
            new() { Title = title, Category = category, Keywords = keywords, Content = content };

        db.KnowledgeArticles.AddRange(
            Kb("Delivery times", "Shipping", "delivery, shipping, deliver, days, arrive, courier, late",
               "Orders inside major cities (Karachi, Lahore, Islamabad) are delivered in 2-3 working days. Other cities take 3-5 working days. International delivery takes 7-12 working days."),
            Kb("Tracking your order", "Orders", "track, tracking, order status, where is my order, parcel, courier",
               "You can track your order from My Account > Orders. A tracking number is also sent by email and SMS as soon as the order is shipped. Tracking updates can take up to 24 hours to appear."),
            Kb("Shipping charges", "Shipping", "shipping fee, delivery charges, free shipping, cost",
               "Shipping is free on orders above Rs. 3,000. Orders below that have a flat delivery charge of Rs. 200."),
            Kb("Return policy", "Returns", "return, exchange, replace, damaged, wrong item, defective",
               "You can return or exchange most items within 14 days of delivery if they are unused and in original packaging. Damaged or wrong items can be returned free of charge – just share a photo with our support team."),
            Kb("Refund process", "Billing", "refund, money back, reimburse, refund time, credited",
               "Once a return is approved, refunds are processed within 3-5 working days. Card payments are refunded to the same card (bank may take up to 7 days), and Cash on Delivery orders are refunded by bank transfer."),
            Kb("Payment methods", "Billing", "payment, pay, card, cash on delivery, cod, jazzcash, easypaisa, bank transfer",
               "We accept Visa / Mastercard debit and credit cards, Cash on Delivery, JazzCash, Easypaisa and bank transfer. All card payments are secured with 3-D Secure."),
            Kb("Cancelling an order", "Orders", "cancel, cancellation, cancel order",
               "Orders can be cancelled free of charge before they are shipped from My Account > Orders > Cancel. Once an order has been shipped, it cannot be cancelled but you can return it after delivery."),
            Kb("Reset your password", "Account", "password, reset, forgot, login, sign in, otp, locked",
               "Click 'Forgot password' on the login page and enter your email. You will receive a reset link valid for 30 minutes. Check your spam folder if it does not arrive within 5 minutes. Accounts are locked for 15 minutes after 5 failed login attempts."),
            Kb("Product warranty", "Products", "warranty, guarantee, repair, faulty, claim",
               "Electronics come with a 1-year brand warranty. To claim, keep your invoice and contact support with your order number and a short description of the fault."),
            Kb("Customer support hours", "General", "support hours, contact, phone, email, working hours, timing",
               "Our human support team is available Monday to Saturday, 9 AM to 9 PM (PKT). The AI assistant is available 24/7. You can also email support@shopcore.pk."),
            Kb("Delete or update account", "Account", "delete account, close account, update email, change phone, profile",
               "You can update your name, phone and address from My Account > Profile. To change your email or permanently delete your account, please contact our support team for verification."));

        await db.SaveChangesAsync();

        // ---------------- Sample conversations (for dashboard & analytics) ----------------
        var rnd = new Random(42);
        var samples = new (CustomerIntent Intent, string Question, string Answer, string Title)[]
        {
            (CustomerIntent.OrderStatus, "Where is my order? I placed it 3 days ago.", "You can track your order from My Account > Orders. A tracking number has also been sent to you by email and SMS.", "Order tracking"),
            (CustomerIntent.ShippingDelivery, "How long does delivery take to Multan?", "Delivery to other cities like Multan takes 3-5 working days.", "Delivery time to Multan"),
            (CustomerIntent.Refund, "I returned my shoes last week, when will I get my refund?", "Refunds are processed within 3-5 working days after the return is approved.", "Refund status"),
            (CustomerIntent.Billing, "Can I pay with JazzCash?", "Yes! We accept JazzCash, Easypaisa, cards, bank transfer and Cash on Delivery.", "Payment methods"),
            (CustomerIntent.AccountHelp, "I forgot my password", "Click 'Forgot password' on the login page and we'll email you a reset link valid for 30 minutes.", "Password reset"),
            (CustomerIntent.Cancellation, "I want to cancel my order", "If it hasn't shipped yet, you can cancel it from My Account > Orders > Cancel.", "Order cancellation"),
            (CustomerIntent.ProductInquiry, "Does the headphone have warranty?", "Yes, electronics come with a 1-year brand warranty.", "Warranty question"),
            (CustomerIntent.Complaint, "This is the worst service, my parcel came damaged!", "I'm really sorry about that. I'm connecting you with a human agent to arrange a free replacement.", "Damaged parcel complaint"),
            (CustomerIntent.TechnicalIssue, "The app crashes when I open my cart", "Sorry about that! Please update the app to the latest version. I'm also passing this to our team.", "App crash on cart")
        };
        var sentimentsFor = new Dictionary<CustomerIntent, Sentiment[]>
        {
            [CustomerIntent.Complaint] = new[] { Sentiment.Angry, Sentiment.Negative },
            [CustomerIntent.Refund] = new[] { Sentiment.Negative, Sentiment.Neutral },
            [CustomerIntent.TechnicalIssue] = new[] { Sentiment.Negative, Sentiment.Neutral }
        };
        var agents = new[] { sana, omar };

        for (var i = 0; i < 36; i++)
        {
            var s = samples[rnd.Next(samples.Length)];
            var customer = customers[rnd.Next(customers.Length)];
            var created = DateTime.UtcNow.AddDays(-rnd.Next(0, 14)).AddHours(-rnd.Next(0, 12)).AddMinutes(-rnd.Next(0, 59));
            var sentiment = sentimentsFor.TryGetValue(s.Intent, out var opts) ? opts[rnd.Next(opts.Length)] : (rnd.NextDouble() < 0.3 ? Sentiment.Positive : Sentiment.Neutral);
            var escalate = s.Intent == CustomerIntent.Complaint || (s.Intent is CustomerIntent.Refund or CustomerIntent.TechnicalIssue && rnd.NextDouble() < 0.5) || rnd.NextDouble() < 0.08;
            var finished = rnd.NextDouble() < 0.8;

            var conv = new Conversation
            {
                UserId = customer.Id,
                Title = s.Title,
                CurrentIntent = s.Intent,
                LastSentiment = sentiment,
                CreatedAt = created,
                LastMessageAt = created.AddMinutes(escalate ? 25 : 3)
            };

            conv.Messages.Add(new Message { Role = MessageRole.User, Content = s.Question, SenderUserId = customer.Id, SenderName = customer.FullName, Intent = s.Intent, Sentiment = sentiment, CreatedAt = created });
            conv.Messages.Add(new Message
            {
                Role = MessageRole.Assistant, Content = s.Answer, SenderName = "Aria", Intent = s.Intent, Sentiment = sentiment,
                Confidence = escalate ? 0.4 : Math.Round(0.7 + rnd.NextDouble() * 0.28, 2), TokensUsed = rnd.Next(350, 900),
                LatencyMs = rnd.Next(550, 2200), Provider = "Seed data", CreatedAt = created.AddSeconds(2)
            });
            conv.MessageCount = 2;

            if (escalate)
            {
                var agent = agents[rnd.Next(agents.Length)];
                var resolved = finished;
                conv.IsEscalated = true;
                conv.AssignedAgentId = agent.Id;
                conv.Escalations.Add(new Escalation
                {
                    Reason = s.Intent == CustomerIntent.Complaint ? "Customer is very upset" : "Request needs order access",
                    Trigger = s.Intent == CustomerIntent.Complaint ? "Rule:Sentiment" : "AI",
                    Priority = sentiment == Sentiment.Angry ? EscalationPriority.Urgent : EscalationPriority.High,
                    Status = resolved ? EscalationStatus.Resolved : EscalationStatus.Assigned,
                    AssignedAgentId = agent.Id,
                    CreatedAt = created.AddSeconds(3),
                    AssignedAt = created.AddMinutes(rnd.Next(1, 15)),
                    ResolvedAt = resolved ? created.AddMinutes(rnd.Next(20, 90)) : null,
                    ResolutionNote = resolved ? "Issue resolved by agent." : null
                });
                conv.Messages.Add(new Message { Role = MessageRole.System, Content = $"Conversation escalated to {agent.FullName}.", CreatedAt = created.AddSeconds(3) });
                conv.Messages.Add(new Message { Role = MessageRole.Agent, Content = "Hi, this is " + agent.FullName.Split(' ')[0] + " from the support team. I've checked your order and I'm sorting this out for you now.", SenderUserId = agent.Id, SenderName = agent.FullName, CreatedAt = created.AddMinutes(12) });
                conv.MessageCount = 3;
                conv.Status = resolved ? ConversationStatus.Resolved : ConversationStatus.Escalated;
                conv.ResolvedBy = resolved ? ResolvedBy.Human : null;
                conv.ClosedAt = resolved ? created.AddMinutes(rnd.Next(30, 120)) : null;
                conv.Rating = resolved && rnd.NextDouble() < 0.7 ? rnd.Next(3, 6) : null;
            }
            else
            {
                conv.Status = finished ? ConversationStatus.Closed : ConversationStatus.Active;
                conv.ResolvedBy = finished ? ResolvedBy.AI : null;
                conv.ClosedAt = finished ? created.AddMinutes(rnd.Next(2, 10)) : null;
                conv.Rating = finished && rnd.NextDouble() < 0.6 ? rnd.Next(4, 6) : null;
            }

            db.Conversations.Add(conv);
        }

        await db.SaveChangesAsync();
        logger.LogInformation("Database seeded: users, knowledge base and sample conversations.");
    }
}
