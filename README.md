# TempHire server (.NET + Breeze + EF Core)

The server of **TempHire**, a reference implementation of a data-driven line-of-business
application.  It is shared by both TempHire clients, which include it as a git submodule in their
`server` folder:

- [temphire.angular](https://github.com/Breeze/temphire.angular) - **Angular 22** with Optimus UI;
- [temphire.react](https://github.com/Breeze/temphire.react) - **React 19** with Mantine and Vite.

It is **ASP.NET Core 10** with the **Breeze .NET server 8**
([breeze-server-v3](https://github.com/Breeze/breeze-server-v3)) and **EF Core 10** on SQL Server.
It serves the Breeze metadata, queries and saves, signs users in as its own OAuth 2.1 authorization
server, confines every user to their company's data, and runs **Quinn**, an AI assistant, through
an MCP server of its own.  The clients' READMEs describe the application as a whole, how it came
from the original Silverlight and WPF versions, and the client side.

> **A note on the .NET version.** The server targets **`net10.0`** (LTS, supported to November
> 2028) with the 10.0.x package set. It stays on .NET 10 rather than .NET 11 because Breeze 8
> supports .NET 8, 9 and 10, and `Breeze.Persistence.EFCore` 8 requires EF Core 10 (it pins
> `Microsoft.EntityFrameworkCore.Relational` to `[10.0.0, 11.0.0)`).

**Contents**

- [Prerequisites](#prerequisites)
- [Running it](#running-it)
  - [Sample users](#sample-users)
  - [In a client repository](#in-a-client-repository)
  - [Tests](#tests)
- [Quinn, the assistant](#quinn-the-assistant)
  - [How it works](#how-it-works)
  - [Ollama or Claude](#ollama-or-claude)
  - [Setting up Ollama](#setting-up-ollama)
  - [Setting up Claude](#setting-up-claude)
  - [Prompt caching](#prompt-caching)
  - [Budgets: usage and quotas](#budgets-usage-and-quotas)
- [Multi-tenancy](#multi-tenancy)
- [Users and sign-in](#users-and-sign-in)
  - [Using TempHire's MCP server from Claude Code](#using-temphires-mcp-server-from-claude-code)
  - [Roles](#roles)
  - [Adding an external provider such as Microsoft Entra ID](#adding-an-external-provider-such-as-microsoft-entra-id)
- [Validation](#validation)
  - [Validating the database as it will be after the save](#validating-the-database-as-it-will-be-after-the-save)
  - [Concurrency](#concurrency)
- [The database schema](#the-database-schema)
- [Using a different database](#using-a-different-database)
- [Configuration](#configuration)
- [Layout](#layout)
- [Known gaps](#known-gaps)
- [Credits](#credits)

---

## Prerequisites

- [.NET SDK](https://dotnet.microsoft.com/download) — the 10.0 SDK or later
- SQL Server **LocalDB** (ships with the Visual Studio "Data storage and processing" workload and
  with [SQL Server Express](https://www.microsoft.com/sql-server/sql-server-downloads)), or any
  SQL Server instance — see [Using a different database](#using-a-different-database)
- For Quinn, one of: [Ollama](https://ollama.com) with the `qwen3:4b-instruct` model, the default
  (see [Setting up Ollama](#setting-up-ollama)); or an Anthropic API key for Claude (see
  [Setting up Claude](#setting-up-claude)).  The rest of the app works without either; Quinn then
  explains that the model isn't available.

## Running it

The server's validation uses a preview of
[IdeaBlade Validation](https://github.com/Breeze/ideablade-validation), which isn't published yet.
Its package is in `packages/`, and `nuget.config` makes `dotnet` take it from there (see
`packages/README.md`), so there is nothing extra to install.

```
cd TempHire.Server
dotnet run
```

It listens on `http://localhost:5000`.  On startup it brings two databases (`TempHire` and
`TempHireIdentity`) up to date with EF Core migrations, creating them on the first run, and seeds
them: two companies, two users each, and 25 staffing resources per company.  Harbor Staffing's
include the nine people the original sample shipped with.  See
[The database schema](#the-database-schema) for why migrating on startup is fine here but not in
production.

`TempHire.sln` holds the three projects, for Visual Studio or Rider.

### Sample users

The password is **`password`** for all of them:

| User | Company | Role |
|---|---|---|
| `anna@harbor.test` | Harbor Staffing | Editor |
| `ben@harbor.test` | Harbor Staffing | Reader |
| `carla@summit.test` | Summit Talent | Editor |
| `david@summit.test` | Summit Talent | Reader |

An **Editor** can add, change and delete staffing resources; a **Reader** can only look at them
(see [Roles](#roles)).  When the server runs in the Development environment, its sign-in page lists
the sample users, and a click on one signs in as that user; anywhere else it doesn't offer them.

### In a client repository

Each client repository has this repository as a submodule in `server/`.  Clone the client with
`--recurse-submodules`, or run `git submodule update --init` in a clone that was made without it.
The client runs the server from there: its README shows how, and its `npm run build:server` writes
the production build into `server/TempHire.Server/wwwroot`, which the server then serves from its
own origin (see `Program.cs`).

**What differs per client** goes in `server.appsettings.json`, in the client repository's root next
to `server/`.  The server reads it after `appsettings.json` and the launch profile, so its settings
win; the command line still wins over it.  The Angular client uses the defaults and has none; the
React client's lets both versions run side by side, each with its own data:

```json
{
  "urls": "http://localhost:5100",
  "ConnectionStrings": {
    "TempHire": "Server=(localdb)\\MSSQLLocalDB;Database=TempHireReact;...",
    "TempHireIdentity": "Server=(localdb)\\MSSQLLocalDB;Database=TempHireReactIdentity;..."
  },
  "AllowedClientOrigins": [ "http://localhost:5173" ]
}
```

To work on the server from a client repository, change and commit in `server/` as in any clone of
this repository, push it, and then commit the submodule's new commit in the client repository.

### Tests

```
dotnet test
```

`TempHire.Server.Tests` covers saves, concurrency, and the rules checked on save, against an
in-memory SQLite database.

---

## Quinn, the assistant

Quinn answers questions about the signed-in user's company's staffing data, in plain language:
*Who knows SQL?*, *How many people do we have in each state?*, *Who has worked as a project
manager?*  The clients show Quinn in a chat pane, and link the people Quinn mentions to their
records.

Ask Quinn about their name, and they tell you where it comes from: *Punch*, the drink, is named
after the Hindi *panch*, five, for its five ingredients, and TempHire began on IdeaBlade's Punch
framework.  *Quinn* comes from the Latin *quinque*, five as well.

### How it works

Everything runs in this server, behind the same sign-in as the rest of the app:

```
client ──POST /api/agent/chat──► AgentController ──► QuinnAgent ──► IChatClient (Ollama or Claude)
 (pane)  (access token)                                 │                  │ tool calls
                                                        └── MCP client ────┴──► /mcp (MCP server)
                                                          (user's token)          StaffingTools ──► EF Core
```

- **The client** only shows the conversation.  It sends the conversation so far to
  `POST /api/agent/chat` and appends the answer; the server keeps no chat state.
- **Answers stream.**  `api/agent/chat` answers with newline-delimited JSON: the text in pieces as
  the model writes it, the people the answer can mention as the tools return them (see below), and
  `done` at the end.  While tools run, nothing streams.  This matters most with a local model,
  which writes slowly; Claude often thinks before answering on tool results and then delivers most
  of the answer at once.
- **The agent** (`TempHire.Server/Agent/`) uses
  [Microsoft.Extensions.AI](https://learn.microsoft.com/dotnet/ai/microsoft-extensions-ai):
  `ChatClientFactory` builds an `IChatClient` for the configured provider, with automatic function
  invocation, and `QuinnAgent` gives it Quinn's system prompt (`QuinnPersona`) and the MCP server's
  tools.  `AgentController` (`api/agent/info`, `api/agent/chat`) is the HTTP entry point.
- **The MCP server** (`TempHire.Server/Mcp/`) uses the
  [MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk) and is mapped at `/mcp` with
  `RequireAuthorization()`.  Its tools in `StaffingTools` are read-only: search people by name,
  skill or location, one person's details, skills, rate statistics, counts by location, and work
  history.  They query with **EF Core LINQ, not SQL**, through the same `TempHireDbContext` as
  Breeze, so the tenant filter applies to them as to everything else.
- **The agent calls the MCP server as the user.**  `QuinnAgent` connects to `/mcp` over HTTP with
  the signed-in user's own access token, so the tools run with that user's identity and company.
  Nothing in the agent can reach another company's data, whatever the model asks for.  The app's
  tokens are issued for both the API and the MCP endpoint for this reason.  Other MCP clients,
  such as Claude Code, sign in to `/mcp` themselves; see
  [Using TempHire's MCP server from Claude Code](#using-temphires-mcp-server-from-claude-code).
- **Links to people are made by the client, from people the server names.**  The model doesn't
  write links, and neither does the server: what a link to a person looks like depends on the
  client, and another client, a mobile app say, would link differently.  The tools return each
  person's id along with their name.  `ToolResultPeople` picks those people out of the tool results
  of the answer, and the stream sends them to the client as a list of names and ids.  The tools
  return full names, middle name included, and a full name beats someone else's short form, so
  "Jane Doe" and "Jane M Doe" each keep their own id; a name that still fits two people is left
  out.  The client turns those names in the answer into links to the person's record, so only
  people the tools really returned are linked, and always to the right record; a name the model
  gets wrong, or invents, stays plain text.

### Ollama or Claude

The `Agent` section of `TempHire.Server/appsettings.json` picks the model:

```json
"Agent": {
  "Provider": "Ollama",            // or "Claude"
  "MaxToolCalls": 8,
  "Ollama": { "Endpoint": "http://127.0.0.1:11434", "Model": "qwen3:4b-instruct" },
  "Claude": { "Model": "claude-sonnet-5-5", "MaxTokens": 4096 }
}
```

**Ollama** is the default: free, local, and nothing leaves your machine.  A 4-billion-parameter
model is a good demo but not a careful analyst; it sometimes misreads a tool's result, which is why
the clients note that Quinn can make mistakes.  **Claude** gives noticeably better answers.

**Small local model or large hosted one: the same Quinn, different results.**  Both get the same
system prompt, the same tools and the same data; only the model differs, and that shows mostly in
how well it uses the tools.  Asked "Who has the Test skill?" (two people have it):

- **qwen3:4b-instruct on a laptop GPU with 4 GB** found them in about three of four tries.  The
  rest of the time it answered "I don't have information about a 'Test' skill" without looking, or
  called `list_skills`, which only counts people per skill, and stopped there.  Even when it got
  it right it tended to write "let me look that up" before calling a tool, and it often took two
  rounds of tools where one would do.  The system prompt's rules - call a tool first, use
  `search_staffing_resources` to find people - took it from none of three tries to most, and that
  is about as far as wording gets a model this size.  Forcing a tool call isn't possible either:
  Ollama's API has no setting for it.
- **Claude**, asked the same question, called the search tool straight away and named both people,
  with nothing like "let me look that up" in the answer.

A larger local model narrows the gap, if the machine can hold it: `qwen3:8b` needs about 5 GB of
GPU memory, so on a 4 GB card Ollama runs part of it on the processor, which is far slower.  Pick
the model in `Agent:Ollama:Model`.

### Setting up Ollama

1. **Install Ollama** from <https://ollama.com/download>: the installer on Windows and macOS, or
   `curl -fsSL https://ollama.com/install.sh | sh` on Linux.  It runs in the background and serves
   its API at `http://127.0.0.1:11434`, the endpoint in `appsettings.json`.
2. **Download the model** (about 2.5 GB):

   ```
   ollama pull qwen3:4b-instruct
   ```

3. **Check it** - the model is listed, and answers:

   ```
   ollama list
   ollama run qwen3:4b-instruct "Say hello"
   ```

4. **Start the server** (or restart it) and open Quinn's pane.  Its header shows
   *qwen3:4b-instruct via Ollama*.  If Ollama isn't running, Quinn replies that it can't reach
   Ollama, with the endpoint and the `ollama pull` command to check.

The first question after Ollama starts takes longer while the model loads into memory; Ollama
unloads it again after a few idle minutes.

**Another model.**  Quinn needs a model that supports tool calling: in the
[Ollama library](https://ollama.com/library) those are tagged *tools*.  Pull it and set
`Agent:Ollama:Model` to its name.  A larger model answers more reliably, if your machine has the
memory for it.  To use Ollama on another machine, set `Agent:Ollama:Endpoint` to its address.

### Setting up Claude

Claude needs an Anthropic API key (from the [Claude Console](https://platform.claude.com)), which
belongs in the server's [user secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets),
never in `appsettings.json`.  `Set-ClaudeApiKey.ps1` prompts for it and stores it there without
echoing it.  Run it from **PowerShell**, in this repository's root (the `server` folder of a client
repository):

```powershell
.\Set-ClaudeApiKey.ps1 -UseClaude   # prompts for the key, stores it, and switches Quinn to Claude
.\Set-ClaudeApiKey.ps1 -UseOllama   # back to Ollama; the key stays stored
.\Set-ClaudeApiKey.ps1 -Remove      # deletes the key
```

From Command Prompt, `.\Set-ClaudeApiKey.ps1` opens the script in Notepad instead of running it;
run it through PowerShell there:

```
powershell -ExecutionPolicy Bypass -File .\Set-ClaudeApiKey.ps1 -UseClaude
```

(`-ExecutionPolicy Bypass` also helps in PowerShell if it reports that running scripts is disabled.)

`-UseClaude` and `-UseOllama` set `Agent:Provider` in user secrets, which overrides
`appsettings.json` on your machine only.  Restart the server after either; Quinn's header then
shows the model in use.  User secrets are read in the Development environment; in production, set
`Agent__Claude__ApiKey` from your secret store.  Every copy of the server on a machine shares the
same user secrets, so a key stored once is used by both clients' servers.

### Prompt caching

With Claude, Quinn uses **prompt caching**: the parts of a request that the previous request also
sent are read from Anthropic's cache instead of being processed again, at a twentieth of the input
price on Claude Sonnet 5.5 ($0.10 instead of $2.00 per million tokens).  Writing to the cache costs
a quarter more than plain input ($2.50), so caching pays off from the first reuse.

Most of Quinn's input is repeated: every model call sends the tool definitions, the system prompt
and the conversation again, and a question takes at least two calls (one to call a tool, one to
answer with its results).  Two cache breakpoints cover it:

- **The system prompt** (`QuinnAgent`): the tools and the system prompt come first in every
  request and stay the same for a user all day, so every question reads them from the cache.
- **The end of each request** (Claude's automatic caching, set in `ChatClientFactory.OptionsFor`):
  each round of the tool loop reads everything before it, and pays full price only for what's new.

Entries last five minutes and every read renews them, so a user who keeps asking keeps the cache
warm.  Measured in the app, two questions in one conversation:

| | Input tokens | Read from cache | Written to cache | Cost | At full input price |
|---|---|---|---|---|---|
| "Who knows SQL?", nothing cached yet | 5,163 | 2,353 | 2,804 | $0.0103 | $0.0134 |
| The same question again within five minutes | 5,163 | 5,157 | 0 | $0.0035 | $0.0133 |
| A follow-up, "Which of them has the highest rate?" | 5,766 | 4,965 | 795 | $0.0049 | $0.0139 |

A follow-up reads the tools and the system prompt from the cache, but not the earlier question's
tool round: the client sends back only the text of the conversation.  Ollama runs locally and has
no such cache; the cache settings don't reach it.

To see the cache at work, set the `TempHire.Server.Agent.QuinnAgent` log level to `Debug`: every
model call then logs its input tokens, how many were read from and written to the cache, and its
output.  The question's summary (`Quinn used ...`) is logged at `Information`.

### Budgets: usage and quotas

Every answer from a paid model costs money, so Quinn has **budgets in US dollars**: per user and
per company (tenant), per day, week and month, for each model.  While one is used up, Quinn takes no
new questions from that user (or anyone in that company) until it starts over.  The clients show
where everyone stands on a usage page, and Quinn's pane says which budget is used up and how long
until it starts over.

**The budgets as shipped** (`Agent:Quotas` in `appsettings.json`), for Claude Sonnet 5.5:

| | Daily | Weekly | Monthly |
|---|---|---|---|
| Each user | $0.50 | $1.25 | $3.00 |
| Each company | $1.50 | $4.00 | **$10.00** |

A typical question is about 5,400 tokens over two model calls (one to call a tool, one to answer
with its results), 95% of them input - the system prompt, the tool definitions and the tool
results, sent again with each call.  At the full input price that would be about **$0.013**; with
[prompt caching](#prompt-caching) it's **$0.004-0.010**, depending on how much is already in the
cache.  Even at the uncached price a user can ask about **38 questions a day**, 95 a week and 230 a
month, and a company about **115 a day**, 300 a week and **770 a month**; with caching, two to three
times as many.  Longer conversations cost more per question, since the whole conversation is sent
each time.  The company's monthly limit is the budget; the others keep one day, or one user, from
using all of it.

Ollama costs nothing, so it has no limits; its usage is still counted and shown, in tokens.

**How usage is counted.**

- The model reports the tokens of every call it answers (Microsoft.Extensions.AI's
  `UsageContent`, which both providers send on a streamed answer, once per model call).
  `QuinnAgent` adds them up for the question, and `AgentController` records them when the answer
  ends, however it ends: finished, failed or cancelled.
- The question's **cost** is worked out from the model's prices in its quota rule, and stored with
  the tokens, in micro-dollars (millionths of a dollar), at the prices of that moment: a later price
  change doesn't rewrite the past.  Input read from or written to the prompt cache is counted
  separately (`CacheReadTokens`, `CacheWriteTokens`) and charged at its own price.
- The counts are rows in `QuinnUsage`, one per company, user, model and UTC day, in the business
  database.  The table has a context of its own (`QuinnUsageDbContext`, `Agent/Usage/`), apart from
  the business model that Breeze publishes, with its own migrations and migrations history table.
  Each question adds to its row in a single `UPDATE ... SET x = x + n`, so parallel questions can't
  lose counts.  A week or a month is the sum of at most 31 rows.
- Days, weeks (from Monday) and months are in **UTC**, so a budget starts over at midnight UTC: in
  the late afternoon or evening in the Americas.

**Before a question**, `AgentController.Chat` reads where the user and their company stand with the
model that would answer.  If a budget is used up it answers **429** with `Retry-After`, and a
message that names it and the wait: *"You've used up your daily Quinn allowance ($0.50).  You can
ask again in 6 hours and 41 minutes, when it starts over at midnight UTC."*
`GET api/usage` reports the same figures to the clients: per model, today, this week and this
month, for the user and for the company, in dollars and tokens, with the limits.

**A slight overrun is expected, and fine.**  A question is checked *before* it starts and then
answered in full, so the question that crosses a limit finishes - a user at $0.49 of $0.50 gets
their answer, and ends the day at about $0.50.  Questions that are already under way when a limit
is reached finish too.  In practice a budget can be overrun by about one question per user who was
asking at the time: a cent or two.  Stopping in the middle of an answer would save that cent and
lose the answer it paid for.

**Configuring the budgets.**  `Agent:Quotas` is a list of rules; the first that matches the model
Quinn uses applies:

```json
"Quotas": [
  {
    "Provider": "Claude",
    "Model": "claude-sonnet-5-5",
    "Prices": { "InputPerMillion": 2.00, "OutputPerMillion": 10.00,
                "CacheReadPerMillion": 0.10, "CacheWritePerMillion": 2.50 },
    "User": { "Daily": 0.50, "Weekly": 1.25, "Monthly": 3.00 },
    "Tenant": { "Daily": 1.50, "Weekly": 4.00, "Monthly": 10.00 },
    "Tenants": { "summit.test": { "Monthly": 25.00 } }
  },
  { "Provider": "Claude", "Prices": { "InputPerMillion": 10.00, "OutputPerMillion": 50.00, ... }, ... },
  { "Provider": "Ollama" }
]
```

- **`Provider` and `Model`** pick the models a rule applies to; without `Model`, every model of the
  provider.  Put a rule for one model before its provider's rule.  (A list rather than names as
  keys, because Ollama model names contain colons, which .NET configuration can't use in a key.)
- **`Prices`** are dollars per million tokens, as the provider lists them.  `CacheReadPerMillion`
  and `CacheWritePerMillion` are the prompt cache's prices; the write price is the one for
  5-minute cache writes, which is what Quinn uses.  Left out, they err on the safe side: reads are
  charged at the input price, writes at 1.25 times it.
- **`User`** and **`Tenant`** are the dollar limits per day, week and month.  A limit left out or
  `null` is **unlimited**, and a rule without limits makes the model unlimited.
- **`Tenants`** gives a company a budget of its own, by its email domain - a company on a bigger
  plan, say.  Only the limits it names replace the rule's `Tenant` limits.
- **The second Claude rule is a safety net.**  A Claude model without a rule of its own is charged
  at the highest Claude price under the same budget, so switching Quinn to another model can never
  make it free or overspend the budget; it only gets fewer questions until it has its own rule with
  its real prices.  A model that no rule matches at all is free and unlimited, so give every paid
  provider a rule.
- **Changes apply at once:** the rules are read on every check, and `appsettings.json` is reloaded
  when it changes, so there's no restart.  Settings from environment variables or user secrets are
  read at startup, as usual.

**When prices change** - or when you switch Quinn to another model:

1. Look up the model's prices per million input and output tokens on the provider's price list
   (for Claude, the pricing page in the Claude documentation), and for Claude also the cache read
   and 5-minute cache write prices.
2. Update `Prices` in the model's rule, or add a rule for a new model, with `Model` set to the
   name in `Agent:Claude:Model`, before the provider-wide rule.
3. Revisit the limits if the new prices change how far a budget goes; the arithmetic above is the
   one to redo.

Questions asked before the change keep the cost they were recorded at.

**Trying it out.**  To see a budget run out without spending much, lower one for a run, for example
from PowerShell:

```powershell
$env:Agent__Quotas__0__User__Daily = '0.01'; dotnet run --project TempHire.Server
```

One question then uses up the day, and Quinn's pane counts down to midnight UTC.

---

## Multi-tenancy

Each user belongs to a company, a **tenant**, and sees and changes only that company's data.

- **Tenants come from the email domain.**  The `Tenants` table maps an email domain to a tenant:
  `harbor.test` is Harbor Staffing, `summit.test` is Summit Talent.  Whenever a token is issued or
  refreshed, `TokenClaims` asks `TenantResolver` for the tenant of the user's email domain and
  refuses users whose domain has none.  The tenant's id is a **GUID**, not the domain, and it
  travels in the access token as the `tenant_id` claim, so requests need no lookup.
  `HttpCurrentTenant` reads it back as `ICurrentTenant`.
- **Every query is filtered.**  Every entity that belongs to a tenant implements `ITenantOwned`
  (through `AuditEntityBase`), and `TempHireDbContext` gives each of them an EF Core **global query
  filter**, `e.TenantId == CurrentTenantId`.  Breeze queries, the search projection and Quinn's
  tools all go through it; none of them mentions the tenant.  Lookup tables (states, address and
  phone types, rate types) are shared.
- **Every save is checked.**  `SaveChanges` stamps new entities with the current tenant, and
  rejects a change to, or deletion of, a row that belongs to another tenant (`403 Forbidden`).
  The filter alone would not stop that: a Breeze save names rows by key, without querying them.
- **The client never sees the tenant.**  `TenantId` is a server-only column: it is left out of the
  JSON and of the Breeze metadata (`TempHirePersistenceManager.BuildJsonMetadata`), so it is not in
  the clients' generated entity classes either.

---

## Users and sign-in

Users are kept by **ASP.NET Core Identity**, in their own database, `TempHireIdentity`
(`TempHireIdentityDbContext`, users of type `TempHireUser`).  A user's name is their email address.

The server is its own **OAuth 2.1 authorization server**, built with
[OpenIddict](https://documentation.openiddict.com/) on top of Identity.  Every client signs the
user in the same way, with the **Authorization Code flow and PKCE**: TempHire's own client app,
Quinn's MCP connection (with the app's token), and outside MCP clients such as Claude Code.  No
client ever sees a password.

```
client app ──(1) redirect──► /connect/authorize ──(2)──► /account/login   sign-in page, cookie
     ▲                               │
     └──(3) /auth/callback?code=... ◄┘
     └──(4) POST /connect/token (code + PKCE verifier) ──► access token (30 min) + refresh token
     └──(5) Authorization: Bearer <access token> ──► /api/breeze, /api/agent, /mcp (via Quinn)
```

- **Signing in.**  The client sends the browser to `connect/authorize`, which sends it on to the
  server's sign-in page (`SignInController`, a Razor view in the app's look) if the user isn't
  signed in there yet.  `SignInManager` checks the password against Identity's salted hash; five
  failed attempts lock the account for five minutes.  The page sets a cookie for the server only,
  and the browser goes back to `connect/authorize`, which returns a code to the app's
  `/auth/callback`.  The app redeems it at `connect/token` with the PKCE verifier, which never left
  the browser tab.
- **Staying signed in.**  Access tokens last 30 minutes.  The client renews one shortly before it
  expires with the refresh token, which the server replaces at each use.  The server's cookie makes
  signing in again, in another tab say, a quick round trip without the sign-in page.
- **Signing out** goes through `connect/logout`, which ends the server's cookie and returns to the
  app, and so to the sign-in page.
- **What the token says.**  `TokenClaims` decides what goes into the access token, whenever one is
  issued or refreshed: the user's name and id, a `role` claim per Identity role, and the company
  (`tenant_id`, `tenant_name`) from the email domain (see [Multi-tenancy](#multi-tenancy)).  So a
  role change, or a lockout, takes effect at the next refresh.  The tokens are signed JWTs, left
  unencrypted so you can read one at [jwt.io](https://jwt.io); `api/account/me` tells the app who
  signed in.
- **Each token is for named resources** (RFC 8707).  The API (`<server>/api`) and the MCP endpoint
  (`<server>/mcp`) each accept only tokens whose audience names them (`AudienceCheck`).  The app's
  tokens name both, because Quinn passes the user's token on to `/mcp`; an MCP client's tokens
  name only `/mcp`, so they can't be used to call the Breeze API.
- **No sign-up or user management.**  The sample users and their roles are seeded
  (`SampleDataProvider`, through Identity's own password hasher); there are no screens to register
  or manage users.

| Endpoint | What it does | Where |
|---|---|---|
| `GET /account/login` | The sign-in page | `SignInController` |
| `/connect/authorize` | Sign-in check, consent, code | `AuthorizationController.Authorize` |
| `POST /connect/token` | Code or refresh token for tokens | `AuthorizationController.Exchange` |
| `/connect/logout` | Sign out | `AuthorizationController.Logout` |
| `POST /connect/register` | Dynamic client registration (RFC 7591), for MCP clients | `ClientRegistrationController` |
| `/.well-known/oauth-authorization-server`, `/.well-known/openid-configuration` | Authorization server metadata | OpenIddict |
| `/.well-known/oauth-protected-resource` | The MCP endpoint's resource metadata (RFC 9728) | MCP SDK, `Program.cs` |

### Using TempHire's MCP server from Claude Code

`/mcp` follows the [MCP authorization specification](https://modelcontextprotocol.io/specification/2025-11-25/basic/authorization),
so an MCP client needs nothing but the URL:

```
claude mcp add --transport http temphire http://localhost:5000/mcp
```

Then run `/mcp` in Claude Code and choose to authenticate TempHire.  What happens:

1. Claude Code calls `/mcp` without a token and gets `401` with a `WWW-Authenticate` header that
   points to the resource metadata, which names TempHire's server as the authorization server.
2. It reads the authorization server's metadata and **registers itself** at `connect/register`
   (dynamic client registration; OpenIddict 7 has no registration endpoint of its own, so
   `ClientRegistrationController` creates the client through OpenIddict's application manager).
3. Your browser opens the sign-in page, and then a **consent screen** that names the client and
   the host the approval goes to.  The answer is remembered for that client.
4. Claude Code gets an access token for `/mcp` only, plus a refresh token, and can call the tools
   with your identity, company and role.

A registered client gains nothing by itself: anyone may register, as MCP intends, but only for
`/mcp`, only with HTTPS or local redirect URIs, rate-limited per address, and every user still
signs in and consents.  Clients that run in a browser, such as the MCP Inspector, also need their
origin in `AllowedClientOrigins` for CORS.

### Roles

Two Identity roles, `TempHireRoles`:

| | Home | Resource Management | Add, edit, delete | Quinn's answers about the data |
|---|---|---|---|---|
| **Editor** | yes | yes | yes | yes |
| **Reader** | yes | yes, read-only | no | yes |
| no role | yes | no | no | no |

- **The server enforces them, endpoint by endpoint.**  The roles are signed into the token, and
  every endpoint declares who may call it, through the policies in `TempHirePolicies`:

  | Who | Endpoints |
  |---|---|
  | Anyone | The sign-in page, `connect/authorize`, `connect/token`, `connect/logout`, `connect/register`, the metadata under `.well-known`, the client app's own files |
  | Any signed-in user | `api/account/me`; the Breeze metadata and the shared lookups, which the client loads at sign-in; the validation metadata; Quinn's `api/agent/*`; connecting to `/mcp` |
  | Editor or Reader (`ReadStaffingData`) | The Breeze queries for the company's data: `StaffingResources`, `StaffingResourceListItems` and the child collections; MCP tool calls |
  | Editor (`EditStaffingData`) | `api/breeze/SaveChanges` |

  Anyone else gets `401` (not signed in) or `403 Forbidden` (signed in, wrong role).
- **Deny by default.**  A fallback policy requires a signed-in user for any endpoint that doesn't
  say, so a new endpoint without `[Authorize]` is closed rather than open; what must work before
  sign-in says `[AllowAnonymous]`.  `AuthorizationRulesTests` lists every controller action with
  its rule, so a new endpoint fails the tests until someone decides who may call it.
- **Checked twice where it matters.**  The save interceptor (`TempHirePersistenceManager`) refuses
  a save by anyone but an editor again, and the MCP server's call-tool filter refuses a tool call
  by a user with neither role.  They use the same `TempHirePolicies.CanEdit` and `CanRead` as the
  policies, so the rules live in one place.
- **The clients follow them**, from the roles `api/account/me` returns: they keep Resource
  Management to editors and readers, and leave out what a reader may not do.  None of that is
  security; it only keeps a client from offering what the server refuses.
- **Quinn follows them too.**  Its tools only read, so an editor and a reader get the same answers.
  A user with neither role can still chat with Quinn, about its name for instance, but every tool
  call answers "Access denied" (a call-tool filter on the MCP server, in `Program.cs`), and Quinn's
  system prompt says the user has no access, so it tells them so rather than guessing.  The filter
  applies to any MCP client, not only Quinn.
- **A role change takes effect at the next token refresh**, within 30 minutes, when `TokenClaims`
  reads the roles again.

### Adding an external provider such as Microsoft Entra ID

With TempHire's server as the authorization server, an external provider is just another way to
get past its sign-in page; the clients don't change at all.  Identity keeps external logins
(provider and key) in `AspNetUserLogins`, `TenantResolver` picks the company from the email domain,
which for Entra ID is one the company has verified, and `TokenClaims` builds the same token
whatever the sign-in.  What a provider adds:

1. **Register an application** with the provider (for Entra ID, an app registration, multi-tenant
   if several companies' directories sign in), with a redirect URI on the server such as
   `https://<server>/signin-oidc`.
2. **Add the provider's authentication scheme** in `Program.cs`, for example
   `AddOpenIdConnect("EntraId", ...)` with the authority, client id and secret, signing in to an
   external cookie (`IdentityConstants.ExternalScheme`).
3. **Add a "Sign in with Microsoft" button to the sign-in page** and two actions to
   `SignInController`: one that starts the sign-in (`Challenge` with the scheme, returning to the
   same `ReturnUrl`), and a callback that reads the external sign-in
   (`SignInManager.GetExternalLoginInfoAsync`), finds the Identity user linked to it
   (`FindByLoginAsync`), or links one by email (`AddLoginAsync`) or creates it, and then sets the
   sign-in cookie exactly as a password sign-in does.  The browser carries on to
   `connect/authorize`, and the client gets its code as usual.

---

## Validation

> **Preview.**  Validation uses a preview of
> [IdeaBlade Validation](https://github.com/Breeze/ideablade-validation) (`0.1.0-preview.4`),
> which isn't published to NuGet or npm yet: the build is in `packages/`, where `nuget.config`
> finds it (`packages/README.md` explains how to update it).  The framework's API may still change
> before its first release, and so may its name.

The validation rules are written once, here, in two places:

- **the entities' data annotations** - `[Required]`, `[StringLength]` - which reach the clients in
  the Breeze metadata as validators;
- **the validation metadata**, `TempHire.Server/Validation/validation.metadata.json`, for the rules
  annotations cannot express: an address is a U.S. address, with a state and a ZIP code; a phone
  number has a 3-digit area code and a 7-digit number (a dash or a dot may follow the first 3
  digits); a rate is a valid amount and not negative; dates are valid dates; and a staffing resource
  has at least one address and one phone number, and it is at least 18 years old when hired.

Both reach the framework the same way.  The metadata's global `breeze` validator brings in the
annotations: on the client it runs Breeze's validators, which Breeze builds from them; here it runs
the annotations themselves (`AddDataAnnotations("breeze")` in `Program.cs`).  So every rule,
annotation or not, surfaces as the same kind of `ValidationError`, on both sides.

`ValidationController` serves the metadata to the clients (`api/validation/Metadata`), which check
every rule before a save is sent.  The server checks them again when it saves
(`TempHirePersistenceManager`), against the database as it will be after the save
(`SaveValidationStore`).  Its messages name properties as the payload does (`'StateId' is
required`, `The City field is required.`).

### Validating the database as it will be after the save

A Breeze save bundle holds only what the client changed: the entities it added, modified or
deleted.  A resource's other addresses, its phone numbers, and the resource itself aren't in it
unless they changed too.  But a rule about related entities needs them.  "A staffing resource has
at least one address" reads the resource's addresses (`store('Address')` in the metadata), and a
save that deletes an address has to be checked against the addresses that remain.

So before it validates, the server loads what the rules read (`SaveValidationStore`, used by
`TempHirePersistenceManager`):

- **the staffing resources concerned**: the ones in the save, and the ones whose addresses, phone
  numbers, rates, skills or work experience the save adds, changes, deletes or moves to another
  resource;
- **their stored children**, of every child type, so that `store('Address')` sees all of a
  resource's addresses, not just the ones in the save;
- **with the save applied on top**: deleted rows are taken out, added and changed ones put in.

The rules then run against **the database as it will be after the save**, not as the client saw it
when it loaded the data.  That difference matters: another user may have changed or deleted records
in the meantime.  Say a resource has two addresses, and two users each delete a different one.  On
each user's screen one address remains, so both saves are valid on the client.  The first save
goes through.  The second save deletes an address that nobody else touched, so the concurrency
check below has nothing to catch.  But checked against the database, the resource would be left
with no address, and the server refuses the save.  Data integrity holds even when no single save
was wrong on its own (`ARuleBrokenByTwoUsersTogetherIsCaught` in `TempHire.Server.Tests`).

**This isn't something the validation framework does for you.**  It knows the rules, but not the
model.  To IdeaBlade Validation, `store('Address')` is a name to look up.  Which addresses belong to
which resource, which entities a save can affect, and how to load them from the database are
knowledge of TempHire's model, so `SaveValidationStore` is TempHire's own code.  An AI coding
assistant such as Claude can write and maintain it: it reads the rules in
`validation.metadata.json`, sees which entities they read (`store(type)`), and works out from the
model how to load them.  That's how this one was written.  It loads a resource and all of its
children generically, so a new rule about a resource and its children needs no change.  A rule that
reads beyond that, such as one comparing a resource with other resources, does.  When the rules
change, ask the assistant to check that the store still loads everything they read.

### Concurrency

Every entity the client can save (`AuditEntityBase`) has a `RowVersion`, an EF Core concurrency
token, as DevForce's `ConcurrencyStrategy.AutoIncrement` was in the original.  A save only updates
or deletes a row while it still has the version the client loaded.  If another user saved or deleted
it in the meantime, the whole save fails with a concurrency conflict (409) and nothing is written.
The clients tell the user so, in plain words, rather than showing the database's message: someone
else changed this data, so click Refresh to load the latest, then make the changes again and save.
The lookups (states, address, phone number and rate types) have no version: they are read-only,
and the server refuses any save that includes one.

A concurrency check covers the rows in the save, and validation covers the rules that read rows
outside it, as in the example above.  Neither serializes two saves that run at exactly the same
moment: both read the database before either writes.  Closing that gap would take a database
constraint, or a serializable transaction around validating and writing.

---

## The database schema

The schema is managed with **EF Core migrations**, in `TempHire.Model/Migrations/`
(`TempHire/` and `Identity/`, one set per database).  On startup, `DatabaseSetup.Prepare` applies
any pending migrations to both databases and then seeds them.  Quinn's usage table has a set of its
own, in `TempHire.Server/Migrations/QuinnUsage/`, applied to the business database right after (see
[Budgets: usage and quotas](#budgets-usage-and-quotas)).  A database created by an earlier
version of this sample with `EnsureCreated()` has no migration history; it is **dropped and
re-created**, losing its data.

> **This is not how a production application does it.**  Migrating on startup means every instance
> of the server races to change the schema, the application's database account needs the rights to
> alter it, and a bad migration takes the site down with it.  A real deployment applies migrations
> as a separate, reviewed step: a [migration bundle](https://learn.microsoft.com/ef/core/managing-schemas/migrations/applying#bundles)
> or an idempotent SQL script run by the release pipeline.  And it never drops a database it cannot
> migrate.  For a sample whose data is all seed data, both are fine.

**After changing an entity**, add a migration.  `dotnet-ef` is a local tool of the repository
(`dotnet-tools.json`), with the same version as the EF Core packages:

```
dotnet tool restore
dotnet ef migrations add <Name> --context TempHireDbContext --project TempHire.Model --startup-project TempHire.Server --output-dir Migrations/TempHire
```

For the identity database, use `--context TempHireIdentityDbContext` and `--output-dir Migrations/Identity`.
For Quinn's usage table, use `--context QuinnUsageDbContext --project TempHire.Server` and
`--output-dir Migrations/QuinnUsage`.

The next server start applies it.  To start over with fresh sample data, drop the database
(`dotnet ef database drop --context TempHireDbContext --project TempHire.Model --startup-project TempHire.Server`)
and restart the server.

A changed entity also changes the Breeze metadata, so each client regenerates its entity classes
from it (`npm run model` in the client; see the client's README).

Earlier versions of this sample kept the users in a `TempHireSecurity` database.  Nothing uses it
any more; drop it with `sqlcmd -S "(localdb)\MSSQLLocalDB" -Q "DROP DATABASE TempHireSecurity"` if
it is still around.

## Using a different database

The server targets SQL Server through `Microsoft.EntityFrameworkCore.SqlServer`. To point at a real
instance instead of LocalDB, change the two connection strings in
`TempHire.Server/appsettings.json`.

To move to another provider, swap the package in `TempHire.Model.csproj` and the three
`options.UseSqlServer(...)` calls in `Program.cs`. Nothing else in the model is SQL Server-specific
apart from `[Column(TypeName = "decimal(18,2)")]` on `Rate.Amount`.

The migrations in `TempHire.Model/Migrations/` and `TempHire.Server/Migrations/` are generated for SQL Server; after switching
provider, delete them and add a new initial migration (see [The database schema](#the-database-schema)).

## Configuration

| Setting | Where | Notes |
|---|---|---|
| `ConnectionStrings:TempHire` | `appsettings.json` | Business data |
| `ConnectionStrings:TempHireIdentity` | `appsettings.json` | Users (ASP.NET Core Identity) — a separate database, as in the original |
| `OAuth:SigningCertificate`, `OAuth:EncryptionCertificate` (`Path`, `Password`) | secret store or environment | OpenIddict's token keys, as PFX files.  **Required outside Development**, where the server won't start without them; in Development it uses development certificates in the current user's store instead.  Self-signed is fine; the signing key needs DigitalSignature, the encryption key KeyEncipherment (`Auth/OAuthCertificates.cs`) |
| `OAuth:Origins` | `appsettings.json` (optional) | The server's public origins, which name the token audiences; defaults to the URLs it listens on.  Set it behind a proxy |
| `AllowedClientOrigins` | `appsettings.json` | CORS allow-list for the client's dev server, and where the app's OAuth redirect URIs may point |
| `Agent:Provider` | `appsettings.json`, or user secrets via `Set-ClaudeApiKey.ps1` | `Ollama` (default) or `Claude` |
| `Agent:Ollama:*`, `Agent:Claude:Model` | `appsettings.json` | Endpoint and model names |
| `Agent:Claude:ApiKey` | user secrets, via `Set-ClaudeApiKey.ps1` | **Never in `appsettings.json`** |
| `Agent:MaxToolCalls` | `appsettings.json` | Tool-calling rounds per question |
| `Agent:Quotas` | `appsettings.json` | Per model: prices, and dollar limits per user and company per day, week and month; see [Budgets: usage and quotas](#budgets-usage-and-quotas) |
| Any of these, per client | `server.appsettings.json` in the client repository's root | Overrides `appsettings.json` and the launch profile's URL; see [In a client repository](#in-a-client-repository) |

## Layout

```
temphire.server/
├── TempHire.sln
├── nuget.config               # the local package source (packages/)
├── dotnet-tools.json          # dotnet-ef
├── packages/                  # the IdeaBlade.Validation preview
├── Set-ClaudeApiKey.ps1       # stores the Claude API key in user secrets
├── TempHire.Model/            # POCO entities, DbContexts, sample data
│   ├── Entities/              # ← DomainModel/, plus Tenant
│   ├── Identity/              # ASP.NET Core Identity user and DbContext (← Security/)
│   ├── Migrations/            # EF Core migrations, one folder per database
│   ├── Projections/           # ← DomainModel/Projections/
│   ├── SampleData/            # ← DomainServices/SampleData/, plus generated people
│   ├── DatabaseSetup.cs       # migrates and seeds on startup
│   └── TempHireDbContext.cs   # the staffing data, tenant filters
├── TempHire.Server/
│   ├── Controllers/
│   │   ├── BreezeController.cs   # metadata, queries, SaveChanges, lookups, search
│   │   ├── ValidationController.cs  # the validation metadata
│   │   ├── AccountController.cs  # me: who the token belongs to
│   │   ├── AuthorizationController.cs  # OAuth: authorize, token, logout
│   │   ├── ClientRegistrationController.cs  # OAuth: MCP clients register
│   │   ├── SignInController.cs   # the sign-in page
│   │   ├── AgentController.cs    # Quinn: info / chat, with the budget check
│   │   └── UsageController.cs    # Quinn's usage and budgets for the signed-in user
│   ├── Agent/                 # Quinn: persona, chat client factory, agent
│   │   └── Usage/             # budgets: quota rules, usage table and its DbContext, store
│   ├── Migrations/QuinnUsage/ # the usage table's migrations
│   ├── Mcp/                   # MCP server tools (StaffingTools)
│   ├── Persistence/
│   │   ├── TempHirePersistenceManager.cs   # ← DomainModel/SaveInterceptor.cs
│   │   └── SaveValidationStore.cs   # what the save-time rules read from the database
│   ├── Validation/            # validation.metadata.json
│   ├── Auth/                  # OAuth settings and clients, token claims, audience check, current tenant
│   ├── Views/                 # the sign-in and consent pages
│   └── Program.cs
└── TempHire.Server.Tests/     # saves, concurrency, save-time rules, who may call what (dotnet test)
```

The `←` marks name what each part replaces in the original WPF version, in the
[Punch repository](https://github.com/IdeaBlade/Punch/tree/master/Samples/TempHire); every ported
file carries a doc comment naming the file it came from.

## Known gaps

- **Migrations run on startup**, and a database without migration history is dropped; see
  [The database schema](#the-database-schema).
- **An access token is valid until it expires**, up to 30 minutes, even after signing out: the API
  checks its signature, not the token store.  Signing out ends the server's sign-in cookie, and a
  refresh token stops working once its consent (authorization) is revoked.  OpenIddict's
  `EnableTokenEntryValidation` would check every request against the store instead.
- **Registered MCP clients are never cleaned up.**  Each dynamic registration adds a row to
  `OpenIddictApplications`; there is no expiry or admin screen yet.
- **No Client ID Metadata Documents.**  The newest MCP specification prefers them to dynamic
  registration.  OpenIddict 8 plans both; this server offers dynamic registration only.
- **Quinn's budgets are configuration only.**  There is no screen to change a company's budget or
  the prices, and prices must be updated by hand when the provider changes them; see
  [Budgets: usage and quotas](#budgets-usage-and-quotas).  Budgets start over at midnight UTC, not in
  the company's time zone, and a budget can be overrun by the questions under way when it runs out.
- **Search has no paging.** `StaffingResourceListItems` returns every match, exactly as
  `StaffingResourceSearchService.Simple` did. `[BreezeQueryFilter(MaxTake = ...)]` is the place to
  add a cap.
- **The `.sdf` sample databases are not migrated.** The seed data is re-created from the ported
  `SampleDataProvider`, so the original nine people keep their ids (in Harbor Staffing), but any
  data you added to the old SQL Compact files does not come across.

## Credits

The OAuth logo on the sign-in and consent pages (`Views/Shared/_OAuthMark.cshtml`) is by Chris
Messina, [CC BY-SA 3.0](https://creativecommons.org/licenses/by-sa/3.0/), from
[oauth.net](https://oauth.net/).
