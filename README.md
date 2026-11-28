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

**Architecture diagrams.**  [TempHire's architecture](docs/ARCHITECTURE.md) is a C4 model of the
system: its context, its containers (the two clients, this server, the two databases) and the
server's components.  [Quinn's architecture](docs/QUINN-ARCHITECTURE.md) shows the assistant's
components, the flow of a question and of a change, and why it is built that way.

> **A note on the .NET version.** The server targets **`net10.0`** (LTS, supported to November
> 2028) with the 10.0.x package set. It stays on .NET 10 rather than .NET 11 because Breeze 8
> supports .NET 8, 9 and 10, and `Breeze.Persistence.EFCore` 8 requires EF Core 10 (it pins
> `Microsoft.EntityFrameworkCore.Relational` to `[10.0.0, 11.0.0)`).

**Contents**

- [Prerequisites](#prerequisites)
- [Running it](#running-it)
  - [Sample users](#sample-users)
  - [Swagger](#swagger)
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
  - [Query limits](#query-limits)
- [Users and sign-in](#users-and-sign-in)
  - [Using TempHire's MCP server from Claude Code](#using-temphires-mcp-server-from-claude-code)
  - [Roles](#roles)
  - [Adding an external provider such as Microsoft Entra ID](#adding-an-external-provider-such-as-microsoft-entra-id)
- [Validation](#validation)
  - [Validating the database as it will be after the save](#validating-the-database-as-it-will-be-after-the-save)
  - [Concurrency](#concurrency)
- [Address completion](#address-completion)
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
| `emma@harbor.test` | Harbor Staffing | none |
| `carla@summit.test` | Summit Talent | Editor |
| `david@summit.test` | Summit Talent | Reader |
| `felix@summit.test` | Summit Talent | none |

An **Editor** can add, change and delete staffing resources; a **Reader** can only look at them;
a user with **no role** can sign in, and talk to Quinn about its name, but sees none of the
company's data (see [Roles](#roles)).  The server creates any sample user that is missing on every
start, so a database seeded before one was added gets it too.  When the server runs in the Development environment, its sign-in page lists
the sample users, and a click on one signs in as that user; anywhere else it doesn't offer them.

### Swagger

In the Development environment the server describes its API with
[Swashbuckle](https://github.com/domaindrivendev/Swashbuckle.AspNetCore): Swagger UI at
**`http://localhost:5000/swagger`**, and the OpenAPI document at `/swagger/v1/swagger.json`.  The
server's own root, `http://localhost:5000/`, goes straight there, unless a client build has been
copied into `wwwroot`, which then keeps its place at `/`.  In a client repository, use the port that
client's server runs on (5100 for React).

- **Signing in.**  Every endpoint needs an access token.  Click **Authorize**, then **Authorize** in
  the dialog (the client id and the `staffing` scope are filled in): the sign-in page opens in a
  popup, and after signing in Swagger UI sends the token with every request.  Swagger UI is an OAuth
  client of its own, `temphire-swagger`, registered at startup in Development only, with PKCE and no
  secret.  Its tokens are good at the API only, not at the MCP endpoint.
- **What it describes**: the `api/` endpoints, each with what it does, its parameters, its
  responses and who may call it.  Not the OAuth endpoints (`connect/*`, the sign-in pages, client
  registration) or the MCP endpoint.  The descriptions come from the controllers' `///` comments, so
  a new endpoint is documented where it is written; `SwaggerDocumentTests` fails for one without a
  summary or with an undescribed response.
- **Json.NET.**  The schemas come from Swashbuckle's Newtonsoft support, so they match what the API
  sends: PascalCase on the Breeze endpoints, camelCase on the others.  The `$id`, `$ref` and `$type`
  properties Breeze adds to every answer are not in the schemas.
- **Breeze queries** (`IQueryable` endpoints) take their query as the whole query string, JSON such
  as `?{"where":{"LastName":"Fuller"},"take":5}` URL-encoded.  OpenAPI can't describe a nameless
  query string, so from Swagger UI they return every row you may see.  A **save** takes a Breeze save
  bundle, which a Breeze client builds; Swagger UI shows its endpoint but can't help write one.
- **Not in production.**  `SwaggerSetup` is only added in Development, like the sample users on the
  sign-in page.

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

`TempHire.Server.Tests` has over 80 tests, most against an in-memory SQLite database, so they run
real SQL and need no SQL Server:

| Tests | What they cover |
|---|---|
| `SaveRulesTests` | Saves through the real save pipeline: lookups are read-only, another company's rows can't be changed, the validation rules hold against the database as it will be after the save (deleting the last address alone, two users together), and errors name the property as the client knows it |
| `ConcurrencyTests` | Optimistic concurrency: which entities carry a version, how a save increments it, and that changing or deleting a row someone else changed is a conflict that writes nothing |
| `SampleDataTests` | All the sample data passes every rule the server checks on a save, with a positive control |
| `ResourceQueryTests` | The staffing resource queries return what the client asks for, and load related collections with one SQL query each (the client's load and Quinn's) |
| `AuthorizationRulesTests` | Every controller action declares who may call it, and the save and Quinn's tools check the same roles as the policies |
| `AddressUpdateToolsTests` | Changing an address through the MCP tools, with an MCP client over HTTP and a stand-in for the user: which address, completing the new one (with the house number and ZIP code Photon lacks), and the save only after the user confirms, through the save interceptor; another resource's or company's address, a change that breaks the rules, a reader, and an answer the tool never asked for change nothing |
| `ToolResultChangesTests` | What the tools of an answer saved, read from their results for the client to refresh and to keep with the answer, and only what a result reports under `changed` |
| `QuinnQuestionsTests` | The questions Quinn's answer waits on: only the user asked can answer, once, with one of the choices offered; a question whose answer stream ends is cancelled |
| `QuinnUsageTests` | Quinn's budgets: days, weeks and months in UTC, which rule applies, what tokens cost (cache reads and writes included), and which budget stops a user and until when |
| `BreezeQueryLimitsTests` | The query limits over HTTP, through the real filter pipeline: the 1000-row cap, how deep each endpoint may expand or select, that no `[BreezeQueryFilter]` attribute leaves a limit out, and rows in key order unless the client's `orderBy` replaces it |
| `SwaggerDocumentTests` | The Swagger description, generated as the server generates it: only the API, every endpoint with a summary and described responses, 401 and the role policies' 403, and Json.NET's property names |

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

Everything runs in this server, behind the same sign-in as the rest of the app.  (The same, as
C4 component and sequence diagrams: [Quinn's architecture](docs/QUINN-ARCHITECTURE.md).)

```
client ──POST /api/agent/chat──► AgentController ──► QuinnAgent ──► IChatClient (Ollama or Claude)
 (pane)  (access token)                                 │                  │ tool calls
                                                        └── MCP client ────┴──► /mcp (MCP server)
                                                          (user's token)          StaffingTools ──► EF Core
                                                                                  AddressUpdateTools (editors)
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
  Breeze, so the tenant filter applies to them as to everything else.  The tools in
  `AddressUpdateTools` let an editor change an existing address, and nothing else; see
  [Changing an address through Quinn](#changing-an-address-through-quinn).
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

### Changing an address through Quinn

An experiment in letting Quinn change data, safely: an **editor** can ask Quinn to change an
existing address of a staffing resource (*Change Ann Lee's home address to 908 W Capital Way,
Tacoma*).  Nothing else can change: no new address, no other property, no other data.

1. **Which address.**  `choose_address_to_update` takes the person and the address type, if the
   user named one.  If they named none and the person has several addresses, or named one the
   person doesn't have, the chat pane shows the addresses as radio buttons, with OK and Cancel.
2. **The new address, completed.**  `complete_address` asks [Photon](#address-completion) for it.
   Photon's U.S. house numbers have gaps, so a match that is only the street gets the house number
   the user typed, and a match without a ZIP code the user's ZIP code if it is in the match's state,
   as in the address form (see
   [Address completion](#address-completion)).  Several matches: the pane
   shows them as radio buttons, with OK and Cancel; after Cancel, Quinn asks for a corrected address
   and tries again.
3. **Check, confirm, save.**  `update_staffing_resource_address` checks the change, then the pane
   shows whose address changes, the current address and the new one, with Confirm and Cancel.  Only
   Confirm saves it.

**The user answers, not the model.**  The choices and the confirmation are
[MCP elicitations](https://modelcontextprotocol.io/specification/2025-11-25/client/elicitation):
the tool asks the user, through the MCP client, in the middle of its call.  The model only sees the
tool's result, so it can't pick or confirm for the user, and neither can text in the data that it
reads.  The MCP server is stateless, so a question travels as a multi round-trip request
(SEP-2322, in the `2026-07-28` protocol revision): the tool answers its first call with the question and a
request state, and the client calls again with the user's answer.  The request state is the
question's fingerprint, protected with ASP.NET Core Data Protection for that user for 15 minutes
(`Mcp/UserInput.cs`), so an answer counts only for the question it was given to: a client can't
answer a question it was never asked, and an answer to an address that has changed since is asked
again.  An MCP client without multi round-trip requests gets a refusal.

**How the question reaches the user.**  `QuinnAgent` is the MCP client.  Its elicitation handler
puts the question in the answer stream (an `input` line with the question and its choices), and
waits for the user's click, which the client posts to `api/agent/answer`; `QuinnQuestions` holds
the waiting question until then, for that user only, for up to 10 minutes.  Unanswered, it counts
as cancelled.  Claude Code and other MCP clients that support elicitation ask the user in their own
way.

**What the update checks, whatever the model passes** (`AddressUpdateTools`):

- **An editor.**  `McpAccessRules` leaves the three tools out of the tool list for anyone else, so
  the model isn't offered them, and refuses their calls; the tools check the role again.
- **The right company.**  The staffing resource and the address belong to the user's tenant: the
  query filter already sees to that, and the tools check the rows' tenant as well.
- **The right person.**  The address's `StaffingResourceId` is the staffing resource's id.
- **Only an existing address, and only its street, city, state and ZIP code.**
- **The validation rules, explicitly, before the user confirms**: the same check the save
  interceptor makes (`TempHirePersistenceManager.ValidateAsync`), so the user is never asked to
  confirm a change that can't be saved.

**Saved through the save interceptor.**  The change is saved as a Breeze save bundle that the tool
builds, through `TempHirePersistenceManager`, so everything a client save goes through applies:
the editor check, the entity-type check, the audit columns, the row version, the validation again,
and the tenant check on the write.  An address someone else changed since it was read is a
conflict, and nothing is saved.

**After the change.**  The tool's result lists what it saved (`changed`), and `QuinnAgent` passes
that on in the answer stream as a `saved` line (`ToolResultChanges`), taken from the tool result,
never from the model.  The clients announce it as a save: the search list reloads, and Resource
Management reloads the resource it shows.  A user who is editing that resource with unsaved changes
is asked whether to discard them; if not, the rest of the record is refreshed and their changes
stay, ready to save.  Each change also has a one-line summary, which the client keeps with the
answer and sends back with the conversation; `AgentController` puts it in front of that answer
(`[Saved in this answer: ...]`).  The conversation comes back without the tool calls of earlier
answers, and without the summary Quinn, seeing an answer that said it had changed an address and
no update behind it, took the change back on the next question.

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

### Query limits

A Breeze client composes its own queries, so the server bounds what one may cost, following
[Breeze's security guide](https://breeze.github.io/breeze-server-v3/guide/security).  The
tenant filter above decides *who sees what*; these limits decide *how much one query may ask
for*.

| Endpoints | Rows (`MaxTake`) | `expand` / `select` depth (`MaxDepth`) |
|---|---|---|
| Every Breeze query, unless it says otherwise (registered globally) | 1000 | 2 |
| `api/resourcemgt/StaffingResources`, the aggregate root | 1000 | 2: a resource with its addresses and their states |
| The other `api/resourcemgt` queries (addresses, phone numbers, rates, skills, work experience, the search) | 1000 | 1: an address with its type, state or resource, nothing beyond |
| `api/lookups` | 1000 | 0: no expand at all |

- **Registered once**, in `BreezeQueryLimits.AddTo` (called from `Program.cs`), so a controller
  added later is bounded whether or not its author thought about it.  An endpoint that doesn't
  return an `IQueryable` is unaffected.
- **A tighter limit goes on the controller or the action**, as its own
  `[BreezeQueryFilter(MaxDepth = 1)]`.  The filter nearest the action answers the query, and a
  limit it leaves unset comes from the global one, so the row cap stays.  `BreezeQueryLimitsTests`
  fails for an attribute that loosens a limit.
- **Beyond the limits** a query answers `400 Bad Request`, naming the path (`MaxDepth exceeded:
  Addresses.StaffingResource.PhoneNumbers`).  `MaxTake` doesn't refuse; it adds `Take(1000)`, and
  it doesn't cap the collections an `expand` brings.
- **Ordered by key.**  Every query action returns its set ordered by key (the search: by last name,
  then key), so the cap takes the same rows every time, and EF Core doesn't warn about a `Take`
  without an `OrderBy` on every query.  A client's `orderBy` replaces that order: Breeze applies
  its first term with `OrderBy`, not `ThenBy`.  A new query action should order its set too.

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
     └──(5) Authorization: Bearer <access token> ──► /api/model, /api/lookups, /api/resourcemgt, /api/agent, /mcp (via Quinn)
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

| | Home | Resource Management | Add, edit, delete | Quinn's answers about the data | Change an address through Quinn |
|---|---|---|---|---|---|
| **Editor** | yes | yes | yes | yes | yes |
| **Reader** | yes | yes, read-only | no | yes | no |
| no role | yes | no | no | no | no |

- **The server enforces them, endpoint by endpoint.**  The roles are signed into the token, and
  every endpoint declares who may call it, through the policies in `TempHirePolicies`:

  | Who | Endpoints |
  |---|---|
  | Anyone | The sign-in page, `connect/authorize`, `connect/token`, `connect/logout`, `connect/register`, the metadata under `.well-known`, the client app's own files |
  | Any signed-in user | `api/account/me`; the Breeze metadata (`api/model`) and the shared lookups (`api/lookups`), which the client loads at sign-in; the validation metadata; Quinn's `api/agent/*`; connecting to `/mcp` |
  | Editor or Reader (`ReadStaffingData`) | The Resource Management workspace's queries (`api/resourcemgt`): `StaffingResources`, `StaffingResourceListItems` and the child collections; the MCP tools that read |
  | Editor (`EditStaffingData`) | `api/resourcemgt/SaveChanges`; `api/address/suggest`; the MCP address update tools |

  Anyone else gets `401` (not signed in) or `403 Forbidden` (signed in, wrong role).
- **Deny by default.**  A fallback policy requires a signed-in user for any endpoint that doesn't
  say, so a new endpoint without `[Authorize]` is closed rather than open; what must work before
  sign-in says `[AllowAnonymous]`.  `AuthorizationRulesTests` lists every controller action with
  its rule, so a new endpoint fails the tests until someone decides who may call it.
- **Checked twice where it matters.**  The save interceptor (`TempHirePersistenceManager`) refuses
  a save by anyone but an editor again, and the MCP server's filters (`McpAccessRules`) refuse a
  tool call by a user with neither role, and an address update by anyone but an editor.  They use the same `TempHirePolicies.CanEdit` and `CanRead` as the
  policies, so the rules live in one place.
- **The clients follow them**, from the roles `api/account/me` returns: they keep Resource
  Management to editors and readers, and leave out what a reader may not do.  None of that is
  security; it only keeps a client from offering what the server refuses.
- **Quinn follows them too.**  An editor and a reader get the same answers; only an editor is
  offered the tools that change an address, and Quinn's system prompt tells a reader that their
  account can only read.  A user with neither role can still chat with Quinn, about its name for
  instance, but every tool call answers "Access denied" (`McpAccessRules`, the MCP server's
  filters), and Quinn's system prompt says the user has no access, so it tells them so rather than
  guessing.  The filters apply to any MCP client, not only Quinn.
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

> Validation uses [IdeaBlade Validation](https://github.com/Breeze/ideablade-validation) 0.1.1,
> the [`IdeaBlade.Validation`](https://www.nuget.org/packages/IdeaBlade.Validation) package on
> NuGet.  As with any 0.x version, its API may still change between minor versions.
>
> `$schema` in `TempHire.Server/Validation/validation.metadata.json` points at the library's
> schema on GitHub, pinned to the commit of the version the server uses (`v0.1.1`), so the editor
> checks the rules the package runs; move it along with the package version.  (Not a relative
> path into a client's `node_modules`: Visual Studio reports "Error loading schema" for it, and it
> leads nowhere in a clone of this repository on its own.)

The validation rules are written once, here, in two places:

- **the entities' data annotations** - `[Required]`, `[StringLength]` - which reach the clients in
  the Breeze metadata as validators;
- **the validation metadata**, `TempHire.Server/Validation/validation.metadata.json`, for the rules
  annotations cannot express: an address is a U.S. address, with a state and a ZIP code; a phone
  number is a U.S. number, in one field, written in any of the common ways - 10 digits, dashes,
  dots or spaces between its parts, the area code in parentheses, a leading 1 or +1 - and the
  clients rewrite a valid one as `(206) 555-1234` when the user leaves the field; a rate is a
  valid amount and not negative; dates are valid dates; a date of birth is neither in the future
  nor more than 123 years ago (the longest documented life is 122); and a staffing resource has at
  least one address and one phone number, has a date of birth once it has a hire date, and is at
  least 18 years old when hired.

The sample data a new database is seeded with passes all of them: `SampleDataTests` validates
every seeded row with the server's own rules, so a new rule that the sample data would break fails
a test rather than leaving a sample record nobody can save.

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

## Address completion

As an editor types the first line of an address, the clients suggest U.S. addresses that start that
way, and picking one fills in the street, city, state and ZIP code.  They ask the server
(`GET api/address/suggest?q=...`, editors only), which asks
[Photon](https://github.com/komoot/photon), a geocoder over OpenStreetMap data, and matches
Photon's state names to TempHire's states.

- **No API key.**  The default is komoot's public Photon server (`AddressSuggestions:BaseUrl`).
  Its maintainers welcome use within a reasonable limit and call it a demo and test service with
  no availability guarantee, which suits TempHire.  For more than that, run Photon yourself
  (GraphHopper publishes weekly data dumps) and point `BaseUrl` at it, or use a keyed service.
- **Gentle on the public server.**  The server asks only for text of three characters or more,
  caches each answer for ten minutes, and gives up after three seconds; the clients wait for a
  pause in the typing.  A failure gives no suggestions, and the user goes on typing.
- **OpenStreetMap's coverage.**  Its U.S. house numbers have gaps, so some addresses come back as a
  street only, or without a ZIP code.  The server fills those gaps from what the user typed
  (`PhotonAddressSuggester`): a street gets the house number at the start of the text, and an
  address without a ZIP code gets one typed after it (*908 W Capital Way, Tacoma 98401*), but only
  if the ZIP code is in that address's state, told by its first three digits (`ZipCodeStates`):
  a ZIP code typed for Montana doesn't end up on a street in Texas.  An address Photon knows the
  number or ZIP code of keeps its own.  Every caller gets this: the
  clients' suggestions and Quinn's `complete_address` (see
  [Changing an address through Quinn](#changing-an-address-through-quinn)).  Only the Address 1
  text counts, not the form's ZIP code field, which may still hold the previous address's.  A
  suggestion fills the form; the address rules (a U.S. state, a ZIP code) still check what it
  filled.
- **The credit.**  OpenStreetMap's licence asks for "© OpenStreetMap contributors" wherever its data
  is shown: the endpoint returns it, and the clients show it under the suggestions.

Nominatim, OpenStreetMap's own geocoder, needs no key either, but its usage policy forbids
search-as-you-type on the public server.  The USPS Web Tools APIs were retired in January 2026,
and their successors need an OAuth key; Google Places needs a key and billing.

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
| Token signing and encryption keys | `Program.cs` | **Development certificates**, which OpenIddict creates in the current user's certificate store; outside Development the server starts only once you load real ones (`AddSigningCertificate`, `AddEncryptionCertificate`) |
| `OAuth:Origins` | `appsettings.json` (optional) | The server's public origins, which name the token audiences; defaults to the URLs it listens on.  Set it behind a proxy |
| `AllowedClientOrigins` | `appsettings.json` | CORS allow-list for the client's dev server, and where the app's OAuth redirect URIs may point |
| `AddressSuggestions:BaseUrl` | `appsettings.json` | The Photon server for [address completion](#address-completion); komoot's public one by default |
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
├── docs/                      # architecture diagrams: TempHire's C4 model, Quinn's architecture
├── dotnet-tools.json          # dotnet-ef
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
│   │   ├── ModelController.cs    # the Breeze metadata
│   │   ├── LookupsController.cs  # the lookups, loaded at sign-in (AllTypes)
│   │   ├── ResourceMgtController.cs  # the Resource Management workspace: queries, search, SaveChanges
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
│   ├── Swagger/               # Swagger UI and the OpenAPI document, in Development
│   ├── Views/                 # the sign-in and consent pages
│   └── Program.cs
└── TempHire.Server.Tests/     # saves, rules, sample data, queries, who may call what, budgets, Swagger
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
- **Quinn's questions wait in one server's memory.**  While a tool waits for the user's answer
  (an address to pick, a change to confirm), the question is held by the server instance that is
  streaming Quinn's answer (`QuinnQuestions`), and the answer must reach that instance.  TempHire
  runs one; several behind a load balancer would need session affinity for `api/agent`, or a
  shared store.  The MCP tools themselves keep nothing between calls.
- **Search has no paging.** `StaffingResourceListItems` returns every match, exactly as
  `StaffingResourceSearchService.Simple` did, up to the 1000-row cap
  ([Query limits](#query-limits)); a search with more matches shows the first 1000 by last name,
  and nothing tells the user there were more.
- **The `.sdf` sample databases are not migrated.** The seed data is re-created from the ported
  `SampleDataProvider`, so the original nine people keep their ids (in Harbor Staffing), but any
  data you added to the old SQL Compact files does not come across.

## Credits

The OAuth logo on the sign-in and consent pages (`Views/Shared/_OAuthMark.cshtml`) is by Chris
Messina, [CC BY-SA 3.0](https://creativecommons.org/licenses/by-sa/3.0/), from
[oauth.net](https://oauth.net/).
