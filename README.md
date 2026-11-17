# 🤖 AI Customer Support API

An **AI-powered customer-support backend** built with **ASP.NET Core 8 Web API**, **Entity Framework Core**, **SQL Server** and an **LLM** (Google Gemini or OpenAI). It combines traditional .NET backend development (auth, sessions, persistence, roles, analytics) with AI (intent detection, sentiment analysis, knowledge-grounded answers and smart escalation to human agents).

---

## ✅ Requirements → Implementation

| Requirement | Implementation |
|---|---|
| ASP.NET Core Web API | RESTful controllers, DTOs, validation, consistent `ApiResponse` envelope, Swagger docs |
| User authentication | JWT bearer tokens, hashed passwords, roles: **Customer**, **Agent**, **Admin** |
| Conversation / session management | Create, list, view, close conversations; conversation memory (last N messages) sent to the LLM |
| Chat API | `POST /api/chat` (one call, auto-creates the session) and `POST /api/conversations/{id}/messages` |
| Store conversation history | Every message saved in SQL Server with AI metadata (intent, sentiment, confidence, tokens, latency, provider) |
| Integrate an LLM | **Gemini** (`generateContent`) or **OpenAI / OpenAI-compatible** (`chat/completions`) with JSON output; offline **Demo** mode without a key |
| Detect customer intent | 13 intents: OrderStatus, ShippingDelivery, Billing, Refund, Cancellation, TechnicalIssue, AccountHelp, ProductInquiry, Complaint, HumanAgentRequest, Feedback, Greeting, Other – plus **sentiment** (Positive/Neutral/Negative/Angry) |
| Generate responses | LLM answers grounded in a **knowledge base** (RAG-style retrieval) – the AI is instructed never to invent policies |
| Escalate difficult queries | AI decision **+** business rules: customer asks for a human, angry sentiment, legal / fraud keywords, low confidence twice in a row, AI failure. Auto-assigned to the least-busy agent with Normal / High / Urgent priority |
| Admin dashboard API | `GET /api/admin/dashboard` – KPIs, escalation rate, AI resolution rate, CSAT, AI latency, tokens, top intents, sentiment |
| Conversation analytics | `GET /api/admin/analytics?days=14` – daily trend, intent breakdown with escalation rate per intent, sentiment, escalation triggers, rating distribution, resolution & wait times, agent performance |

### ⭐ Extras
- **Human agent workflow:** escalation queue, claim, reply as agent, resolve or hand back to AI
- **Knowledge base management** (CRUD) + `search-relevant` endpoint to test retrieval
- **Customer feedback (1-5 stars)** feeding CSAT analytics
- **Rate limiting** on chat endpoints (20 messages/minute per user) to protect the paid LLM API
- Resilient AI layer: timeouts, provider errors and invalid JSON fall back safely to a human agent
- Global exception handling, input validation, UTC timestamps, seeded demo data (36 sample conversations) so analytics work instantly
- **Chat Playground** web page at `/` to try the AI, escalate, and act as an agent – no Postman needed

---

## 🧠 How a message is processed

```
Customer message
   │
   ├─► save message ──► load conversation memory (last 10 messages)
   │
   ├─► retrieve relevant knowledge-base articles
   │
   ├─► LLM (one call, JSON) → intent · sentiment · confidence · shouldEscalate · reply · title
   │
   ├─► Escalation policy (AI decision + business rules)
   │        └─ escalate? → create escalation, assign least-busy agent, AI stops replying
   │
   └─► save AI reply + analytics metadata → return ChatResponse
```

---

## 🚀 How to Run

1. Open `AISupport.sln` in **Visual Studio 2022** (.NET 8 SDK) and press **F5**.
2. Swagger opens; the database `AISupportDb` and demo data are created automatically.
3. Open **http://localhost:5300/** for the **Chat Playground**.

### 🔑 Demo accounts
| Role | Email | Password |
|---|---|---|
| Customer | customer@support.ai | Customer@123 |
| Agent | agent@support.ai | Agent@123 |
| Admin | admin@support.ai | Admin@123 |

### 🤖 Connect a real LLM (recommended)
Without a key the API runs in **Demo mode** (offline keyword rules). For real AI:

**Google Gemini (free key):** <https://aistudio.google.com/apikey>
```json
"AiSettings": { "Provider": "Gemini", "ApiKey": "YOUR_KEY", "Model": "gemini-2.5-flash" }
```
**OpenAI / compatible:**
```json
"AiSettings": { "Provider": "OpenAI", "ApiKey": "sk-...", "Model": "gpt-4o-mini", "OpenAiBaseUrl": "https://api.openai.com/v1" }
```

### 🧪 Try these messages
- "Where is my order?" → `OrderStatus`, answered from the knowledge base
- "How long does a refund take?" → `Refund`
- "My parcel arrived damaged, this is unacceptable!!" → `Angry` → **escalated (Urgent)**
- "I will take legal action" → sensitive-topic rule → **escalated**
- "I want to talk to a human" → `HumanAgentRequest` → **escalated**

Then login as **agent** in the playground (or another browser) to claim, reply and resolve.

---

## 📚 Endpoints

| Area | Endpoints |
|---|---|
| Auth | `POST /api/auth/register`, `POST /api/auth/login`, `GET /api/auth/me` |
| Chat | `POST /api/chat` |
| Conversations | `POST/GET /api/conversations`, `GET /api/conversations/{id}`, `POST /api/conversations/{id}/messages`, `/escalate`, `/close`, `/feedback` |
| Agent | `GET /api/agent/escalations`, `POST /api/agent/escalations/{id}/claim`, `POST /api/agent/escalations/{id}/resolve`, `GET /api/agent/conversations/{id}`, `POST /api/agent/conversations/{id}/reply` |
| Admin | `GET /api/admin/dashboard`, `GET /api/admin/analytics`, `GET /api/admin/conversations`, `GET /api/admin/users`, `POST /api/admin/agents` |
| Knowledge base | `GET/POST /api/knowledge`, `GET/PUT/DELETE /api/knowledge/{id}`, `GET /api/knowledge/search-relevant?question=` |

## 🗂 Project Structure
```
AISupport.API/
├── AI/            Prompt builder, Gemini & OpenAI clients, Demo AI, escalation policy
├── Controllers/   Auth, Chat, Conversations, Agent, Admin, Knowledge
├── Services/      Conversation pipeline, agents, analytics, knowledge retrieval, auth, mapping
├── Data/          AppDbContext, seeder (users, knowledge base, sample conversations)
├── Models/        Entities & enums
├── DTOs/          Requests / responses with validation
├── Middleware/    Global exception handler
├── Common/        ApiResponse, paging, exceptions, current user, UTC converter
└── wwwroot/       Chat Playground (index.html), Swagger theme
```

---
**Developer:** Syed Muhammad Shakeel · [GitHub](https://github.com/salahuddeen-it)
