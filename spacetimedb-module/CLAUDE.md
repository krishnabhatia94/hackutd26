

# SpacetimeDB Core Concepts

SpacetimeDB is a relational database that is also a server. It lets you upload application logic directly into the database as modules, eliminating the traditional web/game server layer entirely. Rust, C#, and C++ modules compile to WebAssembly, while TypeScript modules run on V8.

---

## Critical Rules

1. **Reducers are transactional.** They do not return data to callers. Use subscriptions to read data.
2. **Reducers must be deterministic.** Do not use filesystem, network, external clocks, or external random sources in reducers. Use the reducer context (`ctx`) for SpacetimeDB-provided timestamp and deterministic random values.
3. **Read data via tables/subscriptions**, not reducer return values. Clients get data through subscribed queries.
4. **Auto-increment IDs are not sequential.** Gaps are normal, do not use for ordering. Use timestamps or explicit sequence columns.
5. **`ctx.sender` is the authenticated principal.** Never trust identity passed as arguments.

---

## Feature Implementation Checklist

1. **Backend:** Define table(s) to store the data
2. **Backend:** Define reducer(s) to mutate the data
3. **Client:** Subscribe to the table(s)
4. **Client:** Call the reducer(s) from UI
5. **Client:** Render the data from the table(s)

---

## Debugging Checklist

1. Is SpacetimeDB server running? (`spacetime start`)
2. Is the module published? (`spacetime publish`)
3. Are client bindings generated? (`spacetime generate`)
4. Check server logs for errors (`spacetime logs <db-name>`)
5. Is the reducer actually being called from the client?

---

## Tables

- **Private tables** (default): Only accessible by reducers and the database owner.
- **Public tables**: Exposed for client read access through subscriptions. Writes still require reducers.

Organize data by access pattern, not by entity:

```
Player          PlayerState         PlayerStats
id         <--  player_id           player_id
name            position_x          total_kills
                position_y          total_deaths
                velocity_x          play_time
```

## Reducers

Reducers are transactional functions that modify database state. They run atomically, cannot interact with the outside world, and do not return data to callers. See the language-specific server skills for syntax.

## Event Tables

Event tables broadcast reducer-specific data to clients. Rows are never stored in the client cache (`count()` returns 0, `iter()` yields nothing); only `onInsert` callbacks fire.

## Subscriptions

Subscriptions replicate database rows to clients in real-time.

1. **Subscribe**: Register SQL queries describing needed data
2. **Receive initial data**: All matching rows are sent immediately
3. **Receive updates**: Real-time updates when subscribed rows change
4. **React to changes**: Use callbacks (`onInsert`, `onDelete`, `onUpdate`)

Best practices:
- Group subscriptions by lifetime
- Subscribe before unsubscribing when updating subscriptions
- Avoid overlapping queries
- Use indexes for efficient queries

## Modules

Modules contain application logic that runs inside the database.

- **Tables**: Define the data schema
- **Reducers**: Define callable functions that modify state
- **Event Tables**: Broadcast reducer-specific data to clients
- **Views**: Read-only functions that expose computed subsets of data to clients
- **Procedures**: (Unstable) Functions that can have side effects (HTTP requests, `ctx.withTx`)

Server-side modules can be written in: Rust, C#, TypeScript, C++

Lifecycle: Write → Compile → Publish (`spacetime publish`) → Hot-swap (republish without disconnecting clients)

## Inter-Database Communication

Reducers can't make network requests, but a reducer can call a reducer in another database on the same server through an **outbox table** (async IDC). The reducer inserts a row, and after its transaction commits, the row is delivered as a reducer call to the target database exactly once, in order with other calls through the same outbox to the same database. The sending reducer doesn't wait, and the two databases don't commit together: the receiving reducer runs later in its own transaction and can fail. An optional `on_result` reducer in the sending database receives the result, so it can undo or record the work.

Use it to split an application into databases that scale and deploy independently, such as a lobby that sends players to separate game world databases. Only Rust and TypeScript modules can declare outbox tables. See the language-specific server skills for syntax.

## Identity

- **Identity**: A long-lived, globally unique identifier for a user.
- **ConnectionId**: Identifies a specific client connection.
- Always use `ctx.sender` / `ctx.Sender` / `ctx.sender()` for authorization.

SpacetimeDB works with many OIDC providers, including SpacetimeAuth (built-in), Auth0, Clerk, Keycloak, Google, and GitHub.




# SpacetimeDB CLI

Use this skill when the user needs help with the `spacetime` CLI tool - initializing projects, building modules, publishing databases, querying data, managing servers, or troubleshooting CLI issues.

## Quick Reference

### Project Initialization & Development

```bash
# Initialize new project
spacetime init my-project --lang rust|csharp|typescript|cpp
spacetime init my-project --template <template-id>

# Build module
spacetime build                    # release build
spacetime build --debug            # faster iteration, slower runtime

# Dev mode (auto-rebuild, auto-publish, generates bindings)
spacetime dev
spacetime dev --client-lang typescript --module-bindings-path ./client/src/module_bindings

# Generate client bindings
spacetime generate --lang typescript|csharp|rust --out-dir ./bindings --module-path ./server
spacetime generate --lang unrealcpp --uproject-dir ./MyGame --module-path ./server --unreal-module-name MyGame
```

### Client and Module Bindings

`spacetime generate --bindings client` (default) generates bindings for clients connecting to a database. `--bindings module` generates receiver bindings used inside a **sending database module** to call reducers in another **receiving database** through asynchronous IDC. Rust and TypeScript module bindings are supported; C#/C++ module bindings are not implemented yet. `unrealcpp` is client-only; `cpp` is accepted only in module mode.

In `spacetime.json`, each sending database's non-inherited `dependencies` array requires its own `module-path` and dependency `name` plus receiving-module `module-path` or `database`. A `database` reference resolves another target's local source/build settings in the same config; it does not fetch a running database's schema. A direct receiving module source takes precedence. Dependencies do not themselves publish receiving databases or inherit the sending module's build settings. `generate <sending-database>` processes both its regular `generate` entries (client bindings by default) and `dependencies` (module bindings automatically); no `--bindings` flag or second `generate` entry is needed for a dependency. For dependencies, module binding language defaults to the sending module's language and Rust/TypeScript output defaults to `<sending module-path>/src/remote_bindings`. Explicit config `out-dir` is config-relative. A regular `generate` entry can select `"bindings": "module"`, but does not use dependency language or output defaults. The CLI `--bindings` override is allowed only when a single generation entry is selected.

`dev` generates only root dependencies before building the sending module, and watches their source paths. Child targets are available for source lookup, not recursive dependency generation. `--skip-generate` skips receiver/client generation and receiver-only watching; `--skip-publish` does not. See https://spacetimedb.com/docs/cli-reference/spacetime-json#receiver-dependencies for examples.

### Publishing & Deployment

```bash
# Publish to Maincloud (default)
spacetime publish my-database --yes

# Publish to local server
spacetime publish my-database --server local --yes

# Clear database and republish
spacetime publish my-database --delete-data=always --yes
```

### Database Interaction

```bash
# SQL queries
spacetime sql my-database "SELECT * FROM users"
spacetime sql my-database --interactive   # REPL mode

# Call reducers (each argument is a separate positional arg)
spacetime call my-database my_reducer '"value"' '123'

# Subscribe to changes
spacetime subscribe my-database "SELECT * FROM users" --num-updates 10

# View logs
spacetime logs my-database -f              # follow logs
spacetime logs my-database -n 100          # up to 100 log lines

# Describe schema
# (without --json, output is human-readable text)
spacetime describe my-database --json
spacetime describe my-database tables --json   # also views, reducers, procedures, routes, env, types
spacetime describe my-database tables users --json
spacetime describe my-database reducers my_reducer --json
```

### Database Management

```bash
# List databases
spacetime list

# Delete database
spacetime delete my-database

# Rename database
spacetime rename <database-identity> --to new-name
```

### Server Management

```bash
# List configured servers
spacetime server list

# Add server
spacetime server add local --url http://localhost:3000 --default
spacetime server add myserver --url https://my-spacetime.example.com

# Set default server
spacetime server set-default local

# Test connectivity
spacetime server ping local

# Start local instance
spacetime start

# Clear local data
spacetime server clear
```

### Authentication

```bash
# Login (opens browser)
spacetime login

# Login with token
spacetime login --token <token>

# Show login status
spacetime login show

# Logout
spacetime logout
```

## Default Servers

| Name | URL | Description |
|------|-----|-------------|
| `maincloud` | `https://maincloud.spacetimedb.com` | Production cloud (default) |
| `local` | `http://127.0.0.1:3000` | Local development server |

## Common Flags

| Flag | Short | Description |
|------|-------|-------------|
| `--server` | `-s` | Target server (nickname, hostname, or URL) |
| `--yes` | `-y` | Non-interactive mode (skip confirmations) |
| `--anonymous` | | Use anonymous identity |
| `--module-path` | `-p` | Path to module project |
| `--no-config` | | Ignore `spacetime.json` project configuration |

## Troubleshooting

### "Not logged in"
```bash
spacetime login
# Or use --anonymous for public operations
```

### "Server not responding"
```bash
spacetime server ping <server>
# For local: ensure spacetime start is running
```

### "Schema conflict"
```bash
# Clear data and republish
spacetime publish my-db --delete-data=always --yes
```

### "Build failed"
```bash
# Check Rust/C# toolchain
rustup show
# For Rust modules, ensure wasm32-unknown-unknown target
rustup target add wasm32-unknown-unknown
```

## Module Languages

**Server-side (modules):** Rust, C#, TypeScript, C++
**Client SDKs:** TypeScript, C#, Rust, Unreal Engine
**CLI `generate` targets:** TypeScript, C#, Rust, Unreal C++




# SpacetimeDB C# SDK Reference

## Module Structure

Reducers are static methods in a `static partial class`; tables are `public partial struct`s. This reference keeps everything in one `public static partial class Module`, which needs only `using SpacetimeDB;`:

Methods exported through SpacetimeDB attributes, including reducers, procedures, views, HTTP handlers, and routers, must be `public static`; generated bindings invoke them from another class.

```csharp
using SpacetimeDB;

public static partial class Module
{
    [SpacetimeDB.Table(Accessor = "ScoreRecord", Public = true)]
    public partial struct ScoreRecord
    {
        [PrimaryKey]
        [AutoInc]
        public ulong Id;
        public Identity Owner;
        public uint Value;
    }

    [SpacetimeDB.Reducer]
    public static void AddRecord(ReducerContext ctx, uint value)
    {
        ctx.Db.ScoreRecord.Insert(new ScoreRecord { Id = 0, Owner = ctx.Sender, Value = value });
    }
}
```

## Tables

`[SpacetimeDB.Table(...)]` on a `public partial struct`. `Accessor` should be PascalCase:

```csharp
[SpacetimeDB.Table(Accessor = "Entity", Public = true)]
public partial struct Entity
{
    [PrimaryKey]
    [AutoInc]
    public ulong Id;
    [SpacetimeDB.Index.BTree]
    public Identity Owner;
    public string Name;
    [SpacetimeDB.Index.BTree]
    public bool Active;
}
```

Options: `Accessor = "PascalCase"` (recommended), `Public = true`, `Scheduled = nameof(ReducerFn)`, `ScheduledAt = nameof(field)`, `Event = true`

`ctx.Db` accessors use the `Accessor` name: `ctx.Db.Entity`, `ctx.Db.ScoreRecord`.

## Column Types

| C# type | Notes |
|---------|-------|
| `byte` / `ushort` / `uint` / `ulong` | unsigned integers |
| `U128` / `U256` | large unsigned integers (SpacetimeDB types) |
| `sbyte` / `short` / `int` / `long` | signed integers |
| `I128` / `I256` | large signed integers (SpacetimeDB types) |
| `float` / `double` | floats |
| `bool` | boolean |
| `string` | text |
| `List<T>` | list/array |
| `Identity` | user identity |
| `ConnectionId` | connection handle |
| `Timestamp` | server timestamp (microseconds since epoch) |
| `TimeDuration` | duration in microseconds |
| `Uuid` | UUID |

Optional columns: nullable types (`string? Nickname`, `uint? HighScore`)

## Column Attributes

The complete set of column attributes:

```csharp
[PrimaryKey]          // primary key
[AutoInc]             // auto-increment (use 0 as placeholder on insert)
[Unique]              // unique constraint; indexes the column, enables .Find()
[SpacetimeDB.Index.BTree]  // btree index (enables .Filter() on this column)
[Default(true)]       // migration-safe default for a newly appended field
```

Defaults support compatible addition of a newly appended field. Do not apply `[Default(...)]` to primary-key, unique, or auto-increment fields.

## Indexes

Write the index attribute fully qualified: `[SpacetimeDB.Index.BTree]`. Prefer inline for single-column; multi-column uses struct-level:

```csharp
// Inline (preferred for single-column):
[SpacetimeDB.Index.BTree]
public ulong AuthorId;
// Access: ctx.Db.Post.AuthorId.Filter(authorId)

// Multi-column (struct-level):
[SpacetimeDB.Table(Accessor = "Membership")]
[SpacetimeDB.Index.BTree(Accessor = "ByGroupUser", Columns = new[] { nameof(GroupId), nameof(UserId) })]
public partial struct Membership { public ulong GroupId; public Identity UserId; ... }
```

Prefer a multi-column index over filtering by one column and looping.

## Reducers

```csharp
[SpacetimeDB.Reducer]
public static void CreateEntity(ReducerContext ctx, string name, int age)
{
    ctx.Db.Entity.Insert(new Entity { Owner = ctx.Sender, Name = name, Age = age, Active = true });
}

// No arguments:
[SpacetimeDB.Reducer]
public static void DoReset(ReducerContext ctx) { ... }
```

## DB Operations

```csharp
var row = ctx.Db.Entity.Insert(new Entity { Name = "Sample" });   // Insert; returns the row with AutoInc fields assigned
ctx.Db.Entity.Id.Find(entityId);                                  // Find by PK → Entity? (nullable)
ctx.Db.Entity.Identity.Find(ctx.Sender);                          // Find by unique column → Entity?
if (ctx.Db.Entity.Id.Find(entityId) is { } entity) { ... }        // unwrap Entity? before member access
ctx.Db.Item.AuthorId.Filter(authorId);                            // Filter by index → IEnumerable<Item>
ctx.Db.Entity.Iter();                                             // All rows → IEnumerable<Entity>
ctx.Db.Entity.Count;                                              // Count rows
ctx.Db.Entity.Id.Update(existing with { Name = newName });        // Update by PK
ctx.Db.Entity.Id.Delete(entityId);                                // Delete by PK
```

Note: Filter/Iter return enumerables. Use `.ToList()` if you need to sort or mutate.

The pattern is `ctx.Db.{Accessor}.{ColumnName}.{Method}(value)` for all indexed column operations.

## Lifecycle Hooks

```csharp
[SpacetimeDB.Reducer(ReducerKind.Init)]
public static void OnInit(ReducerContext ctx) { ... }

[SpacetimeDB.Reducer(ReducerKind.ClientConnected)]
public static void OnConnect(ReducerContext ctx) { ... }

[SpacetimeDB.Reducer(ReducerKind.ClientDisconnected)]
public static void OnDisconnect(ReducerContext ctx) { ... }
```

`ctx.ConnectionId` is `ConnectionId?`, including in connection lifecycle reducers. Check or unwrap it before storing it in a non-nullable column or passing it to an index accessor.

## Views

```csharp
// Anonymous view (same result for all clients):
[SpacetimeDB.View(Accessor = "ActiveUsers", Public = true)]
public static List<Entity> ActiveUsers(AnonymousViewContext ctx)
{
    return ctx.Db.Entity.Active.Filter(true).ToList();
}

// Per-user view:
[SpacetimeDB.View(Accessor = "MyEntities", Public = true)]
public static List<Entity> MyEntities(ViewContext ctx)
{
    return ctx.Db.Entity.Owner.Filter(ctx.Sender).ToList();
}
```

View contexts expose read-only table handles. These support `Count` plus `Find`/`Filter` on declared indexes, but not full-table `Iter()`. Start procedural view traversal from an indexed lookup or filter.

Query-builder views use `ViewContext`, `ctx.From`, and return `IQuery<T>` directly. Use `Where` for predicates and `RightSemijoin` when the result should contain right-side rows that have a matching left-side row:

```csharp
ctx.From.Article().Where(article => article.Published.Eq(true));
ctx.From.Subscription().RightSemijoin(
    ctx.From.Account(),
    (subscription, account) => subscription.AccountId.Eq(account.Id)
);
```

Declare a procedural view primary key in its attribute: `[SpacetimeDB.View(Accessor = "CatalogEntry", Public = true, PrimaryKey = nameof(CatalogRow.Sku))]`.

Procedural-view primary keys are explicit schema metadata. Add one only when the view itself is required to expose a primary key; a source table's primary key is not inherited by the view.

Inclusive btree ranges use tuples, for example `ctx.Db.Shipment.DeliverBy.Filter((new Timestamp(1_000), new Timestamp(2_000)))`.

## Client Visibility Filters

```csharp
[ClientVisibilityFilter]
public static readonly Filter PrivateNoteFilter = new Filter.Sql(
    "SELECT * FROM OwnedRow WHERE Owner = :sender"
);
```

## Reducer Context API

`ReducerContext` (`ctx`) is the only source of sender identity, time, and randomness; stdlib clocks and RNG are unavailable in modules.

```csharp
// Auth: ctx.Sender is the caller's Identity
if (row.Owner != ctx.Sender)
    throw new Exception("unauthorized");

// Server timestamp (deterministic per reducer call)
ctx.Db.Item.Insert(new Item { CreatedAt = ctx.Timestamp, .. });

// Timestamp arithmetic
var expiry = ctx.Timestamp + new TimeDuration(delayMicros);

// Deterministic RNG
int roll = ctx.Rng.Next(1, 7);          // [1, 7): inclusive 1, exclusive 7
double f = ctx.Rng.NextDouble();        // [0.0, 1.0)

// Timestamp → milliseconds since epoch
timestamp.MicrosecondsSinceUnixEpoch / 1000
```

## Scheduled Tables

Declare the scheduled table and its reducer in the same `Module` class so `nameof(...)` resolves:

```csharp
[SpacetimeDB.Table(
    Accessor = "TickTimer",
    Scheduled = nameof(Tick),
    ScheduledAt = nameof(ScheduledAt),
    Public = true
)]
public partial struct TickTimer
{
    [PrimaryKey]
    [AutoInc]
    public ulong ScheduledId;
    public ScheduleAt ScheduledAt;
}

[SpacetimeDB.Reducer]
public static void Tick(ReducerContext ctx, TickTimer timer)
{
    // timer row is auto-deleted after this reducer runs
}

// One-time: fires once at a specific time
var at = new ScheduleAt.Time(ctx.Timestamp + new TimeDuration(10_000_000));
// Repeating: fires on an interval
var at = new ScheduleAt.Interval(TimeSpan.FromSeconds(5));

ctx.Db.TickTimer.Insert(new TickTimer { ScheduledId = 0, ScheduledAt = at });
```

Scheduled reducer callbacks use the ordinary `[SpacetimeDB.Reducer]` attribute. There is no `ReducerKind.Scheduled`; the table's `Scheduled` option associates the callback.

To construct a `ConnectionId` from a 128-bit numeric representation, encode it as exactly 16 little-endian bytes and call `ConnectionId.From(bytes)`. The result is nullable and must be checked before use.

## Procedures and HTTP

Procedures are unstable APIs, so modules using them should include `#pragma warning disable STDB_UNSTABLE`. They receive `ProcedureContext` and may return `[SpacetimeDB.Type]` values:

```csharp
[SpacetimeDB.Type]
public partial struct ResultValue { public string Value; }

[SpacetimeDB.Procedure]
public static ResultValue Inspect(ProcedureContext ctx, string input) =>
    new() { Value = input };
```

Outbound HTTP is available through `ctx.Http`; handle its success/error result before using the response. Open short database transactions with `ctx.WithTx`. Perform network I/O before opening the transaction, and keep only database work inside its callback.

```csharp
var result = ctx.Http.Get(uri);
var text = result.Match(
    response => response.Body.ToStringUtf8Lossy(),
    error => throw new Exception(error.Message)
);

ctx.WithTx(tx =>
{
    tx.Db.ScoreRecord.Insert(new ScoreRecord { Id = 0, Owner = ctx.Sender, Value = 1 });
    return 0;
});
```

`WithTx` is generic: its callback must return a value (use `return 0;` when no result is needed). The callback's generated `tx.Db` exposes module table accessors; `ProcedureContext` and `HandlerContext` do not expose tables directly.

For non-GET requests, construct and send an `HttpRequest` directly:

```csharp
var result = ctx.Http.Send(new HttpRequest
{
    Uri = uri,
    Method = SpacetimeDB.HttpMethod.Post,
    Headers = new() { new HttpHeader("content-type", "text/plain") },
    Body = HttpBody.FromString(payload),
});
```

`HttpResponse.StatusCode` is `ushort`. HTTP headers are `HttpHeader` values, not tuples, and each header's `Value` is `byte[]`; decode text values with `System.Text.Encoding.UTF8.GetString(header.Value)`. Treat bodies as bytes: use `HttpBody.FromString(...)` to create text, `new HttpBody(bytes)` to supply raw bytes, `ToBytes()` to read raw bytes, and `ToStringUtf8Lossy()` to read text. There is no `HttpBody.FromBytes`, and `ToString()` does not return body contents. Qualify `SpacetimeDB.HttpMethod` when .NET's implicit `System.Net.Http` imports could make the name ambiguous.

Scheduled procedures use the ordinary scheduled-table shape. Its `Scheduled` name refers to a `[SpacetimeDB.Procedure]` method taking `ProcedureContext` plus the scheduled row, and database access inside that procedure goes through `ctx.WithTx`.

Inbound HTTP uses handler attributes and one router. Handler database access also goes through `ctx.WithTx`:

```csharp
[SpacetimeDB.HttpHandler]
public static HttpResponse Health(HandlerContext ctx, HttpRequest request) => new(
    200, HttpVersion.Http11, new(), HttpBody.FromString("ok")
);

[SpacetimeDB.HttpRouter]
public static Router Routes() => SpacetimeDB.Router.New().Get("/health", Handlers.Health);
```

`Handlers` is generated from methods marked `[SpacetimeDB.HttpHandler]`; do not declare it yourself. Reference an attributed method in a router as `Handlers.MethodName`.

## Custom Types

```csharp
[SpacetimeDB.Type]
public enum Status { Online, Away, Offline }

[SpacetimeDB.Type]
public partial struct Point { public float X; public float Y; }
```

Tagged enums (discriminated unions): a `partial record` with empty body and no constructor parameters. Payloads are `[Type] partial struct`s:

```csharp
[SpacetimeDB.Type]
public partial struct Circle { public int Radius; }

[SpacetimeDB.Type]
public partial struct Rectangle { public int Width; public int Height; }

[SpacetimeDB.Type]
public partial record Shape : SpacetimeDB.TaggedEnum<(Circle Circle, Rectangle Rectangle)> { }

// Construct variants via the generated nested constructors:
var a = new Shape.Circle(new Circle { Radius = 10 });
var b = new Shape.Rectangle(new Rectangle { Width = 4, Height = 6 });
```
