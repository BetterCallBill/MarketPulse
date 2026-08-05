# MarketPulse — Interview Q&A / 面试问答

Questions and answers grounded in **this** codebase, split into two parts — **Part I — Easy**
(general architecture and engineering concepts) and **Part II — Hard** (code-focused deep
dives) — with each part organised against the fifteen categories in
[docs/intervew-aspects.md](docs/intervew-aspects.md), so every category stays covered.

> 基于**本代码库**的面试问答，分成两个部分——**Part I — Easy**（通用架构与工程概念）和
> **Part II — Hard**（代码级深挖）——每个部分内部仍按
> [docs/intervew-aspects.md](docs/intervew-aspects.md) 里的十五个类别组织，所有类别都保持覆盖。

Every answer points at real files. If an answer cannot point at a file, it is filed under
**Gaps** for that category — the debt is stated, not hidden. Two slices are shipped: slice 1
is a walking skeleton, slice 2 is authentication. Between them they advance 12 of 15
categories and complete none — slice 2 went **deeper** into categories slice 1 had already
opened (security, web API, testing, framework depth) rather than opening a new one. Categories
9 and 11 are still untouched. Saying so out loud is worth more in an interview than pretending
otherwise.

> 每个答案都指向真实文件。指不出文件的，一律归到该类别的 **Gaps（缺口）**——欠债写明，不藏。
> 目前交付了两个 slice：slice 1 是"能走路的骨架"，slice 2 是认证。两者合起来推进了 15 个类别中的
> 12 个，一个也没做完——**slice 2 是在 slice 1 已经打开的类别上往深处走**（安全、Web API、测试、
> 框架深度），**而不是又打开一个新的**。类别 9 和 11 仍然一片空白。面试时主动讲出来，
> 比假装完整更值钱。

**How to use this / 怎么用这份文档**

- **Easy vs Hard** is about the *kind* of answer expected, not the difficulty of sounding
  smart. Easy questions are answered with concepts, trade-offs and architecture reasoning;
  Hard questions demand a detailed walk through the actual implementation. Warm up on Part I,
  then drill Part II.
  > **Easy 和 Hard 分的是"期望的回答类型"，不是显得聪明的难度。** Easy 用概念、取舍和架构推理
  > 来回答；Hard 要求对真实实现做详细讲解。先用 Part I 热身，再攻 Part II。
- **Tests / 考察点** under each question names the knowledge area the question probes
  (concurrency, React state management, security, …) — use it to spot and patch weak areas.
  > 每题下面的 **Tests / 考察点** 标注了这道题在考哪个知识领域（并发、React 状态管理、安全……）
  > ——用它来定位并补齐自己的薄弱环节。
- Answers are written to be *spoken* in 60–90 seconds. The bullet lists under them are the
  detail to deploy only if the interviewer digs.
  > 答案是按"开口讲 60–90 秒"写的。下面的要点只在面试官继续追问时才展开。
- **Follow-up** marks the question a good interviewer asks next. Prepare those hardest.
  > **Follow-up** 是好面试官接下来会问的那一刀。这些要准备得最狠。
- **Gaps** at the end of each category is your honest answer to "what's missing?" — and the
  bridge to what you would build next.
  > 每个类别末尾的 **Gaps** 就是"还缺什么"的诚实回答，同时也是通向下一步要建什么的桥。

**Stack as built / 实际用到的技术栈:** .NET 10 · ASP.NET Core 10 · EF Core 10 · SQL Server 2022
· MediatR 12.5 · FluentValidation 12.1 · SignalR · JWT bearer · ASP.NET Core rate limiting
· React 18.3 · React Router 7 · TypeScript 5.6 · TanStack Query 5 · zod 3 · Vite 5
· Vitest 2 · MSW 2 · Playwright · xUnit · NSubstitute · Testcontainers · Docker
· GitHub Actions

**Status note / 进度说明:** slice 1 (walking skeleton) and slice 2 (authentication) are both
implemented and merged to `test`, closed out on 2026-08-03 after a full local verification
run (build 0 warnings · 63 unit + 41 integration · 3 Playwright journeys) —
[ADR-003](docs/adr/003-cookie-based-sessions.md),
[spec](docs/superpowers/specs/2026-08-02-authentication-design.md). Everything below points
at code that exists. Where the shipped implementation **diverged from the approved design**,
the divergence is called out rather than smoothed over — those are the most useful answers in
this document, because they are the ones where something was learned.

> slice 1（walking skeleton）和 slice 2（认证）都已实现并合入 `test`，2026-08-03 完成收尾——
> 本地全量验证通过（构建 0 warning · 63 个单元 + 41 个集成测试 · 3 条 Playwright 端到端流程）。
> 下面的每个答案指向的都是**真实存在的代码**。凡是**实现与批准的设计发生偏离**的地方都单独点出来，
> 而不是抹平——这些恰恰是这份文档里最有价值的答案，因为它们是真正学到东西的地方。

**Known limitation / 已知局限:** the dev seed account's password hash is a committed
constant, and the JWT signing key lives in `appsettings.Development.json`. Both are
documented showcase trade-offs scheduled for removal in the hardening slice — see Q13.3.

> dev 种子账号的密码哈希是一个提交进仓库的常量，JWT 签名密钥写在 `appsettings.Development.json`
> 里。两者都是**有记录的展示用取舍**，排期在加固 slice 移除——见 Q13.3。

---

## Table of contents / 目录

**[Glossary / 术语表](#glossary--术语表)** — every term used below, explained for developers new
to this stack / 下文用到的每个术语，按新人零基础解释

**Part I — Easy / 第一部分（Easy）** — general architecture & engineering concepts / 通用架构与工程概念

[1. C# & .NET runtime internals](#1-c--net-runtime-internals) ·
[2. JavaScript fundamentals & language internals](#2-javascript-fundamentals--language-internals) ·
[3. TypeScript](#3-typescript) ·
[4. ASP.NET Core framework depth](#4-aspnet-core-framework-depth) ·
[5. React depth](#5-react-depth) ·
[6. HTML/CSS & layout](#6-htmlcss--layout) ·
[7. Web API design](#7-web-api-design) ·
[8. Data access & SQL Server](#8-data-access--sql-server) ·
[9. Browser, network & web performance](#9-browser-network--web-performance) ·
[10. Architecture & system design ⭐](#10-architecture--system-design-) ·
[11. Distributed systems & messaging](#11-distributed-systems--messaging) ·
[12. Cloud & DevOps](#12-cloud--devops) ·
[13. Security (full-stack)](#13-security-full-stack) ·
[14. Testing (full-stack)](#14-testing-full-stack) ·
[15. Engineering practice & behavioural](#15-engineering-practice--behavioural)

**Part II — Hard / 第二部分（Hard）** — code-focused deep dives / 代码级深挖

[1. C# & .NET runtime internals](#1-c--net-runtime-internals-1) ·
[2. JavaScript fundamentals & language internals](#2-javascript-fundamentals--language-internals-1) ·
[3. TypeScript](#3-typescript-1) ·
[4. ASP.NET Core framework depth](#4-aspnet-core-framework-depth-1) ·
[5. React depth](#5-react-depth-1) ·
[6. HTML/CSS & layout](#6-htmlcss--layout-1) ·
[7. Web API design](#7-web-api-design-1) ·
[8. Data access & SQL Server](#8-data-access--sql-server-1) ·
[12. Cloud & DevOps](#12-cloud--devops-1) ·
[13. Security (full-stack)](#13-security-full-stack-1) ·
[14. Testing (full-stack)](#14-testing-full-stack-1)

---

## Glossary / 术语表

Every piece of jargon used in this document, explained assuming no prior exposure. Skim it
once before Part I; come back whenever an answer uses a word you cannot picture.

> 本文档用到的全部行话，按"完全没接触过"的读者来解释。开始 Part I 之前先扫一遍，
> 之后哪个词卡住了就回来查。

### Libraries & tools at a glance / 库与工具速览

- **ASP.NET Core** — Microsoft's web framework: HTTP server, routing, middleware. / 微软的 Web 框架。
- **EF Core** — Microsoft's ORM (object-relational mapper): you write C# and LINQ, it generates
  the SQL and tracks changes. / ORM：写 C# 查询，由它生成 SQL。
- **SignalR** — real-time messaging library over WebSockets; a *hub* is the server endpoint
  clients connect to. / 实时推送库，hub 是客户端连接的服务端端点。
- **MediatR** — in-process dispatcher: controllers send a command/query object; MediatR finds
  and runs its handler. / 进程内命令/查询分发器。
- **FluentValidation** — declarative validation rules for request objects. / 请求校验库。
- **xUnit / NSubstitute** — the .NET test framework, and a library that fakes interfaces for
  unit tests. / .NET 测试框架与接口替身（mock）库。
- **Testcontainers** — starts real infrastructure (here SQL Server) in Docker for a test run.
  / 在 Docker 里为测试启动真实数据库。
- **React** — UI library: the UI is a function of state, re-run when state changes. / UI 库。
- **TanStack Query** — manages *server state* in React: fetching, caching, refetching, error
  states. / React 的服务端状态管理库。
- **zod** — runtime schema validation for TypeScript: verifies at runtime that data really has
  the declared shape, and derives the static type from the same schema. / 运行时校验 + 类型推导。
- **Vite / Vitest** — frontend build tool and its companion test runner. / 前端构建工具及其测试运行器。
- **MSW (Mock Service Worker)** — intercepts real network requests in tests and returns
  scripted responses. / 在网络层拦截请求、返回脚本化响应的测试工具。
- **Playwright** — drives a real browser for end-to-end tests. / 驱动真实浏览器的端到端测试工具。

### Real-time & data flow / 实时与数据流

- **Ticker / 证券代码** — the short code identifying a security, e.g. `IVV`. The app streams
  prices for 25 of them.
  > 标识一只证券的短代码。本应用为 25 个代码推送价格。
- **Tick / 一次价格跳动** — one price update for one ticker at one instant ("IVV is now
  $482.10"). The app produces one per ticker per second, each a small immutable value
  (`PriceTick`).
  > 某个代码在某一时刻的一次价格更新。每个代码每秒产生一个。
- **Stream / 流** — a continuous sequence of values arriving over time, as opposed to one
  request returning one response. Here: the SignalR connection delivering ticks.
  > 随时间持续到达的值序列，区别于"一次请求一次响应"。
- **Stream event / 流事件** — anything the stream pushes at the app: a new tick, or a
  connection lifecycle signal (`onreconnecting`, `onreconnected`, `onclose`). They arrive at
  unpredictable moments — which is why "racing" them matters (Q2.4).
  > 流推给应用的任何东西：新 tick，或连接生命周期信号。它们到达的时机不可预测。
- **Producer / consumer / 生产者-消费者** — one piece of code creates work items (produces
  ticks), another processes them (broadcasts), connected by a buffer so neither waits on the
  other.
  > 一段代码生产、另一段消费，中间用缓冲区解耦。
- **Bounded channel / 有界通道** — .NET's in-memory producer/consumer queue (`Channel<T>`);
  *bounded* means fixed capacity (1000 here), which forces you to decide what happens when it
  fills.
  > .NET 的进程内队列；"有界"= 容量固定，逼你决定装满了怎么办。
- **Backpressure / 背压** — what a system does when the producer outruns the consumer: block,
  drop, or buffer. Buffering without a limit is a memory leak.
  > 生产快于消费时的应对策略：阻塞、丢弃或缓冲。无限缓冲就是内存泄漏。
- **`DropOldest`** — the backpressure policy chosen here: when full, discard the oldest tick.
  Right for prices (a stale price is worthless), wrong for orders (dropping one is a bug).
  > 满了就丢最旧的。对价格是对的，对订单就是 bug。
- **Fan-out / 扇出** — delivering one message to many receivers; `hub.Clients.All` sends every
  tick to every connected browser.
  > 一条消息发给多个接收方。
- **Drift / 漂移** — a repeating task's schedule slipping later every cycle, because the delay
  is measured from *after* the work finishes instead of being anchored to a fixed period.
  `Task.Delay` in a loop drifts; `PeriodicTimer` does not.
  > 周期任务越跑越晚：延迟从"干完活之后"起算，而不是锚定在固定周期上。
- **Re-entrancy / 重入** — a callback firing again before its previous run finished, so the
  same code overlaps itself. `System.Timers.Timer` can re-enter; a slow tick would run
  alongside itself.
  > 上一次回调没跑完，下一次又触发，同一段代码和自己重叠。
- **Stale / staleness / 陈旧** — data still on screen but too old to trust. A price cell dims
  when no tick has arrived for it recently.
  > 还显示着、但旧得不可信的数据。
- **WebSocket / SSE / long-polling** — three ways a server can push to a browser: a true
  two-way socket; a one-way server-sent event stream; or HTTP requests held open until data
  exists. SignalR negotiates the best one available.
  > 服务端推送的三种传输方式，SignalR 会自动协商降级。
- **Backplane** — shared infrastructure (e.g. Redis) that lets several server instances
  broadcast to *each other's* connected clients. Without one, each instance only reaches its
  own (Q11.2).
  > 让多个服务实例互相转发广播的共享设施；没有它每个实例只能推给自己的客户端。
- **Thundering herd / 惊群** — many clients retrying at the same instant (say, all on a
  synchronised 30-second timer) and hammering a service that just recovered.
  > 大量客户端在同一瞬间重试，把刚恢复的服务再打垮。
- **Jitter / 抖动** — deliberate randomness added to retry delays so clients desynchronise and
  the herd never forms.
  > 给重试延迟加随机量，让客户端错开。

### Frontend & TypeScript / 前端与 TypeScript

- **Closure / 闭包** — a function that keeps access to variables from the scope where it was
  created, even after that scope has finished. It is how a cleanup function still "remembers"
  the interval id it must clear.
  > 函数带着定义时所在作用域的变量一起活下去。
- **Event loop / 事件循环** — JavaScript's single-threaded scheduler: run one task to
  completion, then take the next (a timer callback, a network event). Nothing runs in
  parallel; things run *later*.
  > JS 的单线程调度：一个任务跑完再跑下一个，没有并行，只有"稍后"。
- **Reducer / `useReducer`** — a single pure function `(state, action) → newState` through
  which *all* state changes flow. One transition function means two updates cannot interleave
  into an inconsistent state.
  > 所有状态变更都经过的唯一纯函数，天然消除交错更新。
- **Discriminated union / 可辨识联合** — a TypeScript type that is "one of several shapes",
  each tagged by a field (`type: 'tick' | 'close'`); checking the tag tells the compiler which
  shape you hold.
  > "几种形状之一"的类型，靠一个标签字段区分。
- **Narrowing & exhaustiveness / 收窄与穷尽** — narrowing: the compiler refining a broad type
  inside an `if`/`switch`. Exhaustiveness: proving every variant is handled — assign the
  `default` case to `never`, and a forgotten variant becomes a compile error.
  > 收窄：分支内类型变精确。穷尽：漏掉一种变体就编译不过。
- **`memo` / memoisation / 记忆化** — caching a result and reusing it while the inputs are
  unchanged; `React.memo` skips re-rendering a component whose props did not change.
  > 输入没变就复用上次结果；`React.memo` 据此跳过重渲染。
- **Re-render & reconciliation / 重渲染与协调** — React re-running a component to compute the
  new UI, then diffing it against the old (reconciliation). Cheap, but not free at 25×/second
  across a whole tree.
  > 重新执行组件并与旧结果做 diff；便宜但不免费。
- **Server state vs client state / 服务端状态 vs 客户端状态** — server state is data the
  backend owns and the frontend merely caches (the watchlist — TanStack Query's job); client
  state is UI-only (which input is focused). Mixing them in one store causes bugs.
  > 后端拥有、前端只缓存的数据 vs 纯 UI 状态；混在一个 store 里必出事。
- **Optimistic update / 乐观更新** — showing a mutation's expected result immediately, before
  the server confirms, and rolling back on failure. This app deliberately does not (Q5.3).
  > 服务端确认前先把预期结果画出来，失败再回滚。
- **StrictMode double-invoke** — React 18's development mode deliberately mounts, unmounts and
  remounts every component to expose effects that leak resources on cleanup.
  > React 开发模式故意挂载两次，逼出不清理资源的 effect。
- **Single-flight refresh / 单飞刷新** — collapsing N concurrent attempts at the same
  operation into one: the first caller starts the token refresh and stores the promise; the
  other callers `await` that *same* promise instead of firing their own. Critical here because
  a second concurrent refresh would present an already-revoked token and log the user out
  (Q2.5).
  > N 个并发调用合并成一次：第一个发起并存下 promise，其余等同一个 promise。
- **SPA (single-page application)** — the browser loads one HTML page and JavaScript swaps
  views in place; navigation does not reload the page.
  > 单页应用：一次加载，之后由 JS 换视图。

### Backend & architecture / 后端与架构

- **Slice (vertical slice) / 垂直切片** — a unit of delivery that cuts through every layer
  (UI → API → DB) to ship one working capability, instead of building layer by layer.
  > 纵向切穿所有层、交付一个可用能力的开发单位。
- **Walking skeleton / 会走路的骨架** — the thinnest end-to-end version of the system that
  actually runs, built *first* to prove the risky integrations while change is still cheap.
  > 最薄但真正能跑通端到端的版本，先建它来验证高风险集成。
- **Seam / 接缝** — a deliberate boundary (usually an interface) where one implementation can
  be swapped for another without touching surrounding code — e.g. the channel that lets the
  fake price feed become a real one (Q10.3).
  > 刻意留出的边界，换实现不动周围代码。
- **ADR (architecture decision record)** — a short document capturing one decision, its
  context, and the alternatives that were rejected — so future readers know *why*.
  > 记录一个决定、其上下文和被否方案的短文档。
- **Clean Architecture & dependency inversion / 整洁架构与依赖倒置** — layers with
  dependencies pointing inward (Api → Infrastructure → Application → Domain); the inner layer
  *defines* the interfaces the outer layer implements, so the core knows nothing of EF or HTTP.
  > 依赖向内指；内层定义接口、外层实现，核心对框架一无所知。
- **CQRS / handler** — every operation is a command (write) or query (read) object, dispatched
  to exactly one handler class; controllers only forward.
  > 每个操作是一个命令/查询对象，交给唯一的 handler 处理。
- **Middleware pipeline / 中间件管道** — a chain of components every request flows through in
  order (correlation → errors → CORS → rate limit → CSRF → auth). Order *is* behaviour (Q4.1).
  > 请求依次流过的组件链，顺序本身就是行为。
- **DI lifetimes（singleton / scoped / transient）** — how long the dependency-injection
  container keeps an instance: forever, per HTTP request, or new every time. Chosen by who
  owns state.
  > 容器持有实例多久：永久 / 每请求 / 每次新建，按谁拥有状态来选。
- **Captive dependency / 俘获依赖** — a short-lived service accidentally trapped inside a
  long-lived one (a per-request DbContext held by a singleton): a correctness bug (Q4.3).
  > 短命服务被长命服务困住，是正确性 bug。
- **Entity · value object · aggregate · invariant** — DDD terms: an *entity* has identity that
  survives change (`Watchlist`); a *value object* is defined only by its values (`PriceTick`);
  an *aggregate* is an entity cluster changed only as one unit through its root; *invariants*
  are the rules that must always hold (max 20 items, no duplicates).
  > 实体有身份；值对象只有值；聚合作为整体修改；不变量是必须恒真的规则。
- **ProblemDetails (RFC 7807)** — the standard JSON shape for HTTP error responses (`type`,
  `title`, `status`, `detail`) so clients parse one error format everywhere.
  > HTTP 错误响应的标准 JSON 形状。
- **Correlation ID / 相关性 ID** — a unique id stamped onto each request and echoed in the
  response and logs, so one user-visible failure can be traced to its log entries.
  > 盖在每个请求上、响应和日志共用的唯一编号，用于追踪。
- **ORM / migration / seeding** — an ORM maps objects to tables; a *migration* is a versioned
  script evolving the schema; *seeding* inserts reference rows so a fresh database is usable
  (25 tickers, one dev user).
  > ORM 做对象-表映射；迁移是带版本的建库脚本；种子数据让空库开箱可用。
- **Natural vs surrogate key / 自然键 vs 代理键** — using a real-world value as primary key
  (`IVV`) versus a meaningless generated one (GUID). Natural keys save joins but hurt if the
  value ever changes (Q8.3).
  > 用真实值当主键 vs 用生成的无意义值。
- **Concurrency token / `rowversion`** — a column that changes on every update, so a second
  concurrent write is detected and rejected instead of silently overwriting the first.
  > 每次更新都变化的列，让并发写入被发现而不是互相覆盖。
- **TOCTOU（time-of-check / time-of-use）** — state changing between the moment you validated
  it and the moment you acted on it — the root of every check-then-act race (Q8.2).
  > 检查和使用之间状态变了，是所有"先查后改"竞争的根源。
- **In-process channel vs message queue / 进程内通道 vs 消息队列** — `Channel<T>` is memory
  inside one process: gone on restart, invisible to other machines. A real queue (RabbitMQ) is
  durable and crosses process boundaries (Q11.1).
  > 进程内内存 vs 持久、跨进程的真队列。
- **Eventual consistency / 最终一致性** — accepting that parts of a system see an update at
  slightly different times and converge, rather than all changing in one transaction.
  > 各部分先后看到更新、最终收敛，而非同一事务内齐变。
- **Idempotent / 幂等** — an operation safe to apply twice with the same effect as once —
  what makes retries safe.
  > 执行两次和一次效果相同，重试因此安全。
- **Modular monolith / 模块化单体** — one deployable with strictly separated internal
  modules: the boundaries of microservices without the distributed-systems tax (Q10.5).
  > 单个部署物 + 严格内部模块边界。
- **Boxing & allocation / 装箱与分配** — allocation: creating an object on the heap, which the
  garbage collector later pays for; boxing: wrapping a value type (`struct`) in a heap object
  to treat it as `object`. Why `PriceTick` is a `struct` (Q1.3).
  > 分配是在堆上建对象、由 GC 买单；装箱是把值类型包成堆对象。

### Security / 安全

- **JWT (JSON Web Token)** — a *signed* token carrying claims (like a user id) the server can
  verify without a database lookup. Signed, not encrypted: anyone can read it, no one can
  alter it.
  > 带签名、可验证不可篡改的声明载体；能被读，不能被改。
- **Bearer token / 持有者令牌** — a credential where possession is proof: whoever presents it
  is in. That is why *exfiltration* (stealing it for use elsewhere) is the threat that matters.
  > 谁拿着谁就是主人，所以"被偷走"才是要害。
- **Access token vs refresh token** — a short-lived credential (15 min) sent with every
  request, and a long-lived one (14 days) whose only job is minting new access tokens.
  > 短命的干活令牌 + 长命的续期令牌。
- **Token rotation & reuse detection / 令牌轮换与重用检测** — every refresh issues a new
  refresh token and revokes the old one; presenting an already-revoked token proves it was
  stolen, so every session for that user is killed (Q7.5).
  > 每次刷新换新废旧；有人拿旧的来用 = 泄漏，全家撤销。
- **`httpOnly` cookie** — a cookie JavaScript cannot read; the browser attaches it
  automatically. It keeps tokens out of reach of script injection.
  > JS 读不到、浏览器自动携带的 cookie。
- **Origin · same-origin · same-site** — an *origin* is scheme + host + port; a *site* is the
  registrable domain (ports don't count). Cookies think in sites; `app.example.com` and
  `api.example.com` are same-site, `localhost:5173` and `localhost:5100` too (Q12.5).
  > origin 含端口，site 只看注册域名；cookie 按 site 思考。
- **CORS & preflight / 跨源资源共享与预检** — the browser blocks cross-origin API calls
  unless the server explicitly allows that origin; for non-simple requests the browser first
  sends an `OPTIONS` "preflight" asking permission.
  > 浏览器默认拦跨源请求，服务端要显式放行；复杂请求先发 OPTIONS 问路。
- **CSRF & double-submit / 跨站请求伪造与双提交** — tricking a logged-in user's browser into
  sending a request the user never intended (cookies ride along automatically). Double-submit
  defence: a random value lives in a JS-readable cookie *and* must be echoed in a header — a
  cross-site attacker can make the browser *send* the cookie but cannot *read* it to set the
  header (Q13.5).
  > 骗浏览器替用户发请求；防法：cookie 和 header 各带同一随机值，攻击者发得出、读不到。
- **XSS (cross-site scripting)** — attacker-controlled text executing as script inside your
  page. React escapes interpolated content by default.
  > 攻击者的文本在你的页面里当脚本跑。
- **SQL injection / SQL 注入** — user input concatenated into a SQL string and executed;
  prevented by parameterised queries (which EF Core LINQ produces).
  > 用户输入被拼进 SQL 执行；参数化查询免疫。
- **Hash · salt · PBKDF2** — a hash is a one-way transform for storing passwords; a *salt* is
  a random per-user value so identical passwords store differently; PBKDF2 is a deliberately
  *slow* hash (100,000 iterations) so guessing at scale is expensive.
  > 单向存储 + 每人随机盐 + 故意慢的算法，让批量猜密码变贵。
- **CSPRNG / entropy / 熵** — a cryptographically secure random generator; *entropy* is how
  unguessable a value is. A 256-bit CSPRNG token cannot be dictionary-attacked, which is why
  refresh tokens need only fast SHA-256, not slow PBKDF2 (Q7.5).
  > 密码学安全随机数与"不可猜性"；高熵令牌不怕字典攻击。
- **Timing side-channel / 时序侧信道** — leaking information through *how long* an operation
  takes rather than what it returns: "unknown email" answering in microseconds while "wrong
  password" burns 100,000 hash iterations tells the attacker which one happened (Q13.4).
  > 靠耗时差异泄密：响应文案一样、时间不一样，等于说了实话。
- **Rate limiting vs lockout / 限流 vs 锁定** — per-IP request throttling protects an
  *endpoint*; per-account lockout after repeated failures protects one *account* against an
  attack spread across many IPs. They are different defences, not duplicates.
  > 按 IP 护端点，按账号护账户，两道防线各管各的。
- **Defence in depth / 纵深防御** — multiple overlapping controls so one failing does not mean
  compromise: `SameSite` *and* CSRF tokens; C# invariants *and* database constraints.
  > 多层重叠防御，破一层不等于沦陷。
- **Allowlist vs denylist / 白名单 vs 黑名单** — accepting only known-good values versus
  rejecting known-bad ones. Allowlists win: you cannot enumerate all bad inputs.
  > 只放行已知好的 vs 拦截已知坏的；坏的列不全，所以白名单赢。
- **Fail closed & fail fast / 失败即关闭与快速失败** — on error, deny rather than allow (the
  dev-auth stub throws outside Development); and surface problems at startup rather than
  mid-request (config validated at boot, Q4.6).
  > 出错时宁可拒绝；问题在启动时爆，别拖到请求中。
- **Nonce** — a random single-purpose value proving freshness or possession — here, the CSRF
  token.
  > 一次性随机值，用来证明"新鲜"或"持有"。
- **BFF (backend-for-frontend)** — a small server-side proxy owned by the frontend that holds
  the tokens and forwards API calls, so the browser never touches credentials directly (Q12.5).
  > 前端专属的服务端代理，替浏览器保管凭据。
- **OIDC (OpenID Connect)** — the standard for "log in with Google/Microsoft/…": your app
  delegates identity to an external provider. Deliberately deferred to a later slice here.
  > "用第三方账号登录"的标准协议，此项目暂缓。

### Testing / 测试

- **Test pyramid vs trophy / 金字塔 vs 奖杯** — pyramid: mostly unit tests, few integration.
  Trophy: heaviest at *integration*, because in a layered app most bugs live between layers
  (Q14.1).
  > 金字塔单元最多；奖杯集成最重，因为 bug 多住在层与层之间。
- **Unit / integration / E2E** — one class in isolation with fakes; several real layers
  together (real database, real HTTP); the full user journey through a real browser.
  > 单类隔离测 / 多层真环境合测 / 真浏览器走完整旅程。
- **WebApplicationFactory** — boots the *real* ASP.NET Core app in memory for tests: real
  middleware, real DI, real routing, no network port.
  > 在内存里启动真实应用供测试使用。
- **Fixture** — shared, expensive setup reused across a group of tests — here, the SQL Server
  container started once per run.
  > 一组测试共享的昂贵前置设施。
- **Cookie jar** — the container a client uses to store and resend cookies, like a browser
  does; each fake user in the tests gets its own.
  > 客户端存放并回送 cookie 的容器，测试里每个假用户一个。
- **Flaky test / 不稳定测试** — a test that passes or fails nondeterministically, usually
  from racing a timing gap ("container started" vs "database accepting connections").
  > 时好时坏的测试，多半在抢时间差。
- **Invariant (property) test / 不变量测试** — instead of asserting one example, assert a
  property holds across many generated cases: 1,000 chained random walks, each within ±1%
  (Q1.5).
  > 不测单个例子，测性质在大量用例下恒成立。
- **Mutation testing / 变异测试** — deliberately corrupting the code to check the tests
  notice; if they stay green, the tests are weaker than they look.
  > 故意改坏代码看测试会不会红；不红说明测试虚。
- **STAR** — Situation, Task, Action, Result: the standard structure for behavioural-interview
  answers (Q15.2).
  > 行为面试答案的标准结构：情境、任务、行动、结果。

---

## Part I — Easy · general architecture & engineering concepts / 第一部分（Easy）· 通用架构与工程概念

Concept-first questions. Each can be answered by reasoning about design, trade-offs and
engineering principles — the code is the evidence, not the subject.

> 以概念为先的问题。每一题都可以靠设计、取舍和工程原则来回答——代码是证据，不是主角。

### 1. C# & .NET runtime internals

*C# 与 .NET 运行时底层*

#### Q1.2 — Why `PeriodicTimer` instead of `Task.Delay` in a loop or `System.Timers.Timer`?

**中文** — 为什么用 `PeriodicTimer`，而不是循环里 `Task.Delay` 或 `System.Timers.Timer`？

**Tests / 考察点:** Async programming — timer semantics, re-entrancy, drift · 异步编程：定时器语义、重入、漂移

**A.** `PeriodicTimer.WaitForNextTickAsync(ct)` is allocation-light, natively cancellable,
and — critically — cannot re-enter. A `System.Timers.Timer` fires a callback on a thread-pool
thread regardless of whether the previous callback finished, so a slow tick would overlap
itself. `Task.Delay` in a loop drifts, because the delay is added *after* the work rather
than being anchored to a period. See
[FakeTickService.cs:20](src/MarketPulse.Infrastructure/RealTime/FakeTickService.cs#L20).

**答.** 三点：分配少、原生支持取消、**不会重入**。`System.Timers.Timer` 不管上一次回调有没有跑完
都会在线程池线程上再触发一次，一次慢的 tick 就会和自己重叠。循环里的 `Task.Delay` 会**漂移**，
因为延迟是加在工作**之后**的，而不是锚定在一个周期上。

### 2. JavaScript fundamentals & language internals

*JavaScript 语言底层*

#### Q2.3 — Why does the price cell dim on a *timer* rather than when a tick arrives?

**中文** — 为什么价格单元格是靠**定时器**变暗，而不是靠 tick 到达时触发？

**Tests / 考察点:** Event-loop reasoning — time-driven vs event-driven state · 事件循环推理：时间驱动 vs 事件驱动的状态

**A.** This is an event-loop reasoning question disguised as a UI question. Staleness is a
function of *elapsed time*, not of any event. If the feed silently stalls — the socket stays
open but no data flows — nothing triggers a re-render, so a `Date.now()` read taken during
render freezes at its last value and the cell never dims. It shows a stale price as if it
were live, which for a price display is the worst possible failure.

[useNow()](apps/dashboard/src/features/prices/useNow.ts) fixes it by owning its own 1s
interval, ticking state so consumers re-render even when no ticks arrive.
[isStale()](apps/dashboard/src/features/prices/streamReducer.ts) is then a pure function of
`(state, ticker, now)` — trivially testable, no timers inside.

**答.** 这是一道伪装成 UI 题的**事件循环**题。"陈旧"是**经过时间**的函数，不是任何事件的函数。
如果数据流**静默卡死**（socket 还开着但没有数据），就没有任何东西触发重渲染，于是在渲染期间读的
`Date.now()` 永远停在上一次的值，单元格永远不会变暗——**把陈旧价格当成实时价格显示**，对一个价格
看板来说这是最坏的失败模式。

`useNow()` 自己持有一个 1 秒定时器，靠 state 变化把消费者顶起来重渲染，即使没有新 tick。
`isStale()` 因此是 `(state, ticker, now)` 的**纯函数**——极易测试，内部没有定时器。

#### Q2.4 — How does the app avoid a state update racing a stream event?

**中文** — 怎么避免状态更新和流事件互相竞争？

**Tests / 考察点:** React state management & concurrency — a single reducer as the serialisation point · React 状态管理与并发：单一 reducer 消除竞争

**A.** All stream events go through a single `useReducer`
([streamReducer.ts](apps/dashboard/src/features/prices/streamReducer.ts)) rather than
several `useState` setters. Ticks, `onreconnecting`, `onreconnected` and `onclose` all
dispatch actions to one reducer, so there is exactly one state transition function and no
possibility of two setters interleaving into an inconsistent pair of values.

**答.** 所有流事件都走**同一个 `useReducer`**，而不是好几个 `useState` setter。tick、
`onreconnecting`、`onreconnected`、`onclose` 全部 dispatch 到同一个 reducer，因此**只有一个状态
转移函数**，两个 setter 交错产生不一致状态的可能性从根上被消掉。

### 3. TypeScript

*TypeScript*

#### Q3.1 — You validate API responses at runtime with zod. Isn't the TypeScript type enough?

**中文** — 你用 zod 在运行时校验 API 响应。TypeScript 的类型不就够了吗？

**Tests / 考察点:** Type systems & trust boundaries — type erasure, runtime validation · 类型系统与信任边界：类型擦除、运行时校验

**A.** No — and this is the single most important TS point in the codebase. `interface
Watchlist` is erased at build time. It describes what the server *promised*, not what it
*sent*. A deployed API drifting from its client is the normal case, not the exceptional one.

[schemas.ts](packages/api-client/src/schemas.ts) declares the shape once in zod and derives
the static type from it:

```ts
export const watchlistSchema = z.object({ id: z.string(), items: z.array(watchlistItemSchema) });
export type Watchlist = z.infer<typeof watchlistSchema>;
```

One source of truth. The static type cannot drift from the runtime check, because it *is*
the runtime check. The parse happens at the boundary in
[client.ts](packages/api-client/src/client.ts), so everything inside the app is typed and
verified.

**答.** 不够——这是整个代码库里最重要的一个 TS 论点。`interface Watchlist` 在构建时就被**擦除**了。
它描述的是服务端**承诺**了什么，不是服务端**实际发**了什么。而线上 API 与客户端产生漂移是**常态，
不是例外**。

`schemas.ts` 用 zod 声明一次形状，再由它**推导**出静态类型：一个真相来源。静态类型不可能和运行时
校验漂移，因为它**就是**运行时校验。解析发生在边界上（`client.ts`），所以应用内部的一切既有类型、
又被验证过。

**Follow-up: what happens when validation fails? / 校验失败了怎么办？**

Two deliberately different behaviours. On the REST path `watchlistSchema.parse()` **throws** —
a malformed watchlist is unrenderable, and TanStack Query turns the throw into an error
state. On the stream path
[usePriceStream.ts](apps/dashboard/src/features/prices/usePriceStream.ts) uses
`safeParse` and silently drops the bad tick — one corrupt frame out of 25/second must not
tear down a working dashboard. *Fail loud where recovery is impossible, fail quiet where the
next message fixes it.*

> 两种**刻意不同**的行为。REST 路径上 `parse()` 直接**抛**——一个畸形的 watchlist 根本没法渲染，
> TanStack Query 会把抛出转成 error 状态。流路径上用 `safeParse`，**静默丢掉**坏 tick——每秒 25 帧
> 里坏一帧，不该把一个正常工作的看板拆掉。
> **一句话原则：无法恢复的地方大声失败，下一条消息就能自愈的地方安静失败。**

#### Q3.4 — Why is `createApiClient` a factory returning an object, rather than a class?

**中文** — `createApiClient` 为什么是返回对象的工厂函数，而不是一个类？

**Tests / 考察点:** TypeScript API design — type inference, factories vs classes · TS API 设计：类型推导、工厂 vs 类

**A.** `export type ApiClient = ReturnType<typeof createApiClient>` in
[client.ts](packages/api-client/src/client.ts) means the type is *derived* from the
implementation — adding a method cannot leave the interface stale. It also closes over
`baseUrl` without a field, keeps every method independently tree-shakeable, and makes the
thing trivially substitutable in tests without inheritance.

**答.** 因为 `export type ApiClient = ReturnType<typeof createApiClient>` 让类型**从实现推导**
出来——加一个方法不可能让接口变陈旧。此外：用闭包捕获 `baseUrl` 而不需要字段；每个方法可以独立
tree-shake；测试里替换它不需要继承。

### 4. ASP.NET Core framework depth

*ASP.NET Core 框架深度*

#### Q4.5 — Why controllers rather than Minimal APIs?

**中文** — 为什么用控制器而不是 Minimal API？

**Tests / 考察点:** Framework trade-off judgement — conventions vs minimalism · 框架取舍判断：约定 vs 极简

**A.** Honestly, at three endpoints it was close to a coin flip; at nine it has tilted.
Controllers win here on three counts: `[ApiController]` gives automatic model-binding
validation and ProblemDetails conventions for free; `[Authorize]`, `[AllowAnonymous]` and
`[EnableRateLimiting("auth")]` compose as attributes rather than as per-route chained calls;
and the codebase is meant to demonstrate the filter/convention model that most .NET shops run
on.

`WatchlistController` is still three lines of dispatch to `ISender` — deliberately thin
([WatchlistController.cs](src/MarketPulse.Api/Controllers/WatchlistController.cs)).
`AuthController` is not, and that is worth conceding: it holds real cookie-writing logic
([AuthController.cs](src/MarketPulse.Api/Controllers/AuthController.cs)). I'd defend it —
`Set-Cookie` is an HTTP transport concern and has no business in an Application handler, which
is why `AuthResult` returns tokens and the controller decides how they travel — but it is a
genuine exception to "controllers are dispatch only", not an accident.

**答.** 说实话，三个端点的规模下这接近抛硬币；到了九个就已经倾斜了。控制器在三点上胜出：
`[ApiController]` 白送模型绑定校验和 ProblemDetails 约定；`[Authorize]`、`[AllowAnonymous]`、
`[EnableRateLimiting("auth")]` 以**特性**的方式组合，而不是每条路由去链式调用；
而且这个代码库要展示的是大多数 .NET 团队实际在用的 filter/约定模型。

`WatchlistController` 仍然是三行转发给 `ISender`——**刻意做薄**。
但 `AuthController` **不是**，这一点要主动承认：它里面有真正的写 cookie 的逻辑。
我会为它辩护——**`Set-Cookie` 是 HTTP 传输层的关切，不该出现在 Application 的 handler 里**，
这正是 `AuthResult` 只返回 token、由控制器决定它们怎么传输的原因——但它确实是
"控制器只做转发"的**一个真实例外，不是意外**。

### 5. React depth

*React 深度*

#### Q5.2 — What is the cleanup discipline in your effects?

**中文** — 你的 effect 清理纪律是什么？

**Tests / 考察点:** React effect lifecycle — cleanup, StrictMode double-invoke · React effect 生命周期：清理、StrictMode 双调用

**A.** Every effect that acquires something releases it, and both hooks are written to
survive React 18 StrictMode's deliberate double-invoke in development
([main.tsx](apps/dashboard/src/main.tsx) mounts under `<StrictMode>`):

- `useNow` → `clearInterval` in cleanup.
- `usePriceStream` → `connection.stop()`, guarded by a state check so a connection that
  never opened is not stopped twice.

The effect's dependency array is `[]` — one connection per mount, deliberately, not one per
render.

**答.** **凡是获取了资源的 effect 都要释放**，而且两个 hook 都写成能扛住 React 18 StrictMode 在
开发环境下**故意的双次调用**（`main.tsx` 挂在 `<StrictMode>` 下）：`useNow` 在清理里
`clearInterval`；`usePriceStream` 调 `connection.stop()`，并用状态检查守住，避免对一个从未打开的
连接停两次。

依赖数组是 `[]`——**每次挂载一个连接**，这是刻意的，不是每次渲染一个。

#### Q5.3 — After adding a ticker, how does the list update? Is that optimistic?

**中文** — 添加一个 ticker 之后列表怎么更新？是乐观更新吗？

**Tests / 考察点:** Server-state management — cache writes, optimistic-update trade-offs · 服务端状态管理：缓存写入、乐观更新取舍

**A.** It is **not** optimistic, and the docs were corrected when they claimed otherwise.
The mutation's `onSuccess` writes the server's authoritative response straight into the
cache with `queryClient.setQueryData(watchlistKey, watchlist)`
([useWatchlist.ts](apps/dashboard/src/features/watchlist/useWatchlist.ts)).

That is one round trip and zero refetch — the API returns the full new watchlist, so
`invalidateQueries` would be a wasted GET. The trade-off is that the input clears only after
the server confirms.

**答.** **不是**乐观更新——文档里曾经这么写过，后来被改正了。mutation 的 `onSuccess` 用
`queryClient.setQueryData(...)` 把服务端的权威响应直接写进缓存。

结果是**一次往返、零次重新拉取**——API 返回的是完整的新 watchlist，所以 `invalidateQueries` 只会
浪费一次 GET。代价是：输入框要等服务端确认后才清空。

**Follow-up: when would you go optimistic? / 什么情况下你会上乐观更新？**

When the server response is predictable and latency is user-visible. Here the server can
*reject* the add (unknown ticker, duplicate, list full), so an optimistic insert would show
a row that then vanishes. Truly optimistic UI needs `onMutate` + snapshot + rollback in
`onError`; for a rejection-prone operation the added complexity buys very little.

> 当服务端响应**可预测**、而且延迟**用户可感知**的时候。这里服务端是可能**拒绝**这次添加的
> （未知 ticker、重复、列表满），乐观插入会先显示一行然后又消失。真正的乐观 UI 需要
> `onMutate` + 快照 + `onError` 里回滚；对一个"容易被拒"的操作，这份复杂度买到的东西很少。

#### Q5.4 — How do you communicate connection state to the user?

**中文** — 你怎么把连接状态传达给用户？

**Tests / 考察点:** UI state modelling — communicating failure modes to users · UI 状态建模：向用户传达失败模式

**A.** A three-state discriminated union — `connecting | connected | reconnecting` — drives
two distinct signals in [WatchlistScreen.tsx](apps/dashboard/src/features/watchlist/WatchlistScreen.tsx):
a `role="status"` banner while reconnecting, and per-cell dimming
(`opacity: 0.4`) when a price is stale *or* the connection is down. The distinction matters:
"the connection dropped" and "this one ticker stopped updating" are different problems and
get different affordances.

**答.** 一个三态可辨识联合（`connecting | connected | reconnecting`）驱动两种**不同的**信号：
重连时显示 `role="status"` 横幅；价格陈旧**或**连接断开时该格子变暗（`opacity: 0.4`）。
这个区分很重要：**"连接断了"和"这一个 ticker 不更新了"是两个不同的问题**，应该有不同的表现。

#### Q5.5 — Where does "am I signed in?" live in React state?

**中文** — "我登录了吗"这个状态住在 React 的哪里？

**Tests / 考察点:** State architecture — client state vs server state, auth as a query · 状态架构：客户端 vs 服务端状态、认证即查询

**A.** **Nowhere.** That is the whole answer, and it is the most interesting consequence of
the cookie decision (ADR-003). There is no token in JavaScript to store, so there is no auth
context, no auth reducer, and no `localStorage` mirror. The session lives in `httpOnly`
cookies the browser manages, which makes "am I signed in?" a **server** question — and a
server question is a *query*, not client state.

So [useSession.ts](apps/dashboard/src/features/auth/useSession.ts) is a plain TanStack Query
over `GET /api/v1/auth/me`, with `retry: false` because a 401 is the **expected answer for a
signed-out visitor**, not a transient failure to retry through.
[ProtectedRoute](apps/dashboard/src/features/auth/ProtectedRoute.tsx) is twelve lines: pending
→ a message, error → `<Navigate to="/login" replace />`, otherwise render the children.

The `replace` matters: without it the back button walks the user straight back onto a route
they were just bounced off, producing a redirect loop in the history stack.

**答.** **哪里都不住。** 这就是完整答案，也是 cookie 决定（ADR-003）最有意思的后果。JavaScript
里根本没有 token 可存，所以**没有 auth context、没有 auth reducer、没有 `localStorage` 镜像**。
会话住在浏览器托管的 `httpOnly` cookie 里，于是"我登录了吗"变成一个**服务端问题**——
而服务端问题是**查询**，不是客户端状态。

所以 `useSession.ts` 就是对 `GET /api/v1/auth/me` 的一个普通 TanStack Query，配 `retry: false`
——因为对一个未登录访客来说，**401 就是预期的答案**，不是值得重试的瞬时故障。
`ProtectedRoute` 只有十二行：pending 显示提示，error 就 `<Navigate to="/login" replace />`，
否则渲染 children。

`replace` 很重要：不加它，用户按返回键会**直接走回刚被弹开的那个路由**，在历史栈里形成重定向循环。

**Follow-up: sign-out — what has to be invalidated? / 登出时要失效掉什么？**

`queryClient.clear()`, not `invalidateQueries(sessionKey)`. Clearing only the session key
leaves **the previous user's watchlist sitting in the cache**, so the next person to sign in
on that browser sees it flash on screen before the refetch lands. Cross-user data leakage
through a client cache is still cross-user data leakage. The server-side counterpart is in
`LogoutHandler`, which revokes the whole refresh-token family — see Q13.4.

> 用 `queryClient.clear()`，**不是** `invalidateQueries(sessionKey)`。只清会话那个 key，
> **上一个用户的 watchlist 还留在缓存里**，于是下一个在这台浏览器上登录的人，会在重新拉取到达前
> **先看到它闪一下**。**通过客户端缓存泄漏跨用户数据，依然是跨用户数据泄漏。**
> 服务端那一半在 `LogoutHandler` 里，它撤销整条刷新令牌家族链——见 Q13.4。

### 6. HTML/CSS & layout

*HTML/CSS 与布局*

#### Q6.2 — Your prices dim via inline `opacity`. What's wrong with that?

**中文** — 价格是用内联 `opacity` 变暗的。这有什么问题？

**Tests / 考察点:** CSS & accessibility judgement — non-visual signals, contrast, theming · CSS 与无障碍判断：非视觉信号、对比度、主题化

**A.** Three things, and I would fix all three before shipping to users:

1. **Opacity alone is not an accessible signal.** A screen-reader user gets nothing.
   `aria-live` on the cell, or a textual "stale" indicator, is the real fix — the `title`
   attribute currently there is hover-only and unreliable on touch.
2. **`opacity: 0.4` on already-grey text** can drop below the WCAG 4.5:1 contrast floor.
   Untested.
3. **Inline styles** don't participate in theming, can't respond to
   `prefers-reduced-motion`, and cost a style recalculation per render.

**答.** 三个问题，上线给真实用户前我三个都会修：

1. **只靠 opacity 不是无障碍信号。** 屏幕阅读器用户什么都得不到。真正的修法是给格子加
   `aria-live`，或者一个文字化的"stale"指示；现在挂的 `title` 属性只在悬停时出现，触屏上不可靠。
2. **本来就是灰字再叠 `opacity: 0.4`**，可能跌破 WCAG 4.5:1 的对比度底线。**没测过。**
3. **内联样式**不参与主题化、无法响应 `prefers-reduced-motion`，而且每次渲染都要重算样式。

### 7. Web API design

*Web API 设计*

#### Q7.3 — Your POST and DELETE both return 200 with a body. Isn't that wrong?

**中文** — 你的 POST 和 DELETE 都返回 200 带响应体，这不对吧？

**Tests / 考察点:** REST semantics — pragmatism vs strict convention · REST 语义：务实 vs 严格约定

**A.** It is a defensible deviation, and I would justify it rather than defend it as
canonical. Strict REST says POST → **201 Created** with a `Location` header, DELETE →
**204 No Content**.

I return the full updated watchlist from all three verbs so the client never needs a
follow-up GET — which is exactly what makes `setQueryData` (Q5.3) a single round trip. The
cost: it is not RESTful-by-the-book, and a `Location` header would be more discoverable.
At a larger API I would return 201 + Location and let the client's cache handle the rest.

**答.** 这是一个**站得住的偏离**，我会为它给理由，而不是假装它是标准做法。严格 REST 是：
POST → **201 Created** 带 `Location` 头，DELETE → **204 No Content**。

我三个动词都返回**完整的更新后 watchlist**，这样客户端永远不需要再补一次 GET——这正是 Q5.3 里
`setQueryData` 能做到"一次往返"的原因。代价是：不符合教科书 REST，而且 `Location` 头更利于发现性。
在更大的 API 上我会返回 201 + Location，剩下交给客户端缓存。

#### Q7.4 — Versioning?

**中文** — 版本策略呢？

**Tests / 考察点:** API versioning & lifecycle policy · API 版本与生命周期策略

**A.** URL path versioning — `/api/v1/watchlist`
([WatchlistController.cs](src/MarketPulse.Api/Controllers/WatchlistController.cs)). It is
the most visible, the most cacheable, and the easiest to route at a proxy. Header-based
versioning is purer but invisible in logs and browser dev tools, which matters more in
practice than purity.

What is missing is the *policy*: no documented deprecation window, no sunset headers, and
no strategy for what constitutes a breaking change. A version number without a policy is
decoration.

**答.** URL 路径版本 —— `/api/v1/watchlist`。它最显眼、最好缓存、在代理层最好路由。基于请求头的
版本更"纯粹"，但在日志和浏览器开发者工具里**看不见**，而实践中这一点比纯粹性更重要。

**缺的是策略本身**：没有写明的废弃窗口、没有 sunset 响应头、也没有"什么算破坏性变更"的定义。
**没有策略的版本号只是装饰。**

### 8. Data access & SQL Server

*数据访问与 SQL Server*

#### Q8.2 — Your invariants live in C#. What stops a bad row getting in another way?

**中文** — 不变量写在 C# 里。那从别的路径写进来的脏数据靠什么拦？

**Tests / 考察点:** Data integrity — defence in depth, constraints, concurrency races · 数据完整性：纵深防御、约束、并发竞争

**A.** Defence in depth — the domain enforces intent, the database enforces truth:

| Rule / 规则 | Domain / 领域层 | Database / 数据库 |
|---|---|---|
| No duplicate ticker per watchlist / 同一 watchlist 内不重复 | `DuplicateTickerException` | Unique index `(WatchlistId, Ticker)` |
| One watchlist per user / 每用户一个 watchlist | — | Unique index on `Watchlists.UserId` |
| Max 20 items / 最多 20 条 | `WatchlistFullException` | ❌ not enforced / 未强制 |
| Orphaned watchlists / 孤儿 watchlist | — | FK with `ON DELETE CASCADE` |

**答.** **纵深防御——领域层强制意图，数据库强制事实。** 见上表。

**Follow-up: so two concurrent adds could exceed 20? / 那两个并发添加能超过 20 条？**

Yes — and that is the honest answer. Both requests read a 19-item list, both pass the count
check, both save, and you have 21. The duplicate case is caught by the unique index (the
second write fails), but capacity is not. The fix is a concurrency token (`rowversion`) on
`Watchlist` so the second `SaveChangesAsync` throws `DbUpdateConcurrencyException`. With one
user it has never fired; that is not the same as being correct.

> **能——这是诚实答案。** 两个请求都读到 19 条，都通过了数量检查，都保存，于是变成 21 条。
> 重复的情况有唯一索引兜底（第二次写入失败），但**容量没有**。修法是给 `Watchlist` 加并发令牌
> （`rowversion`），让第二次 `SaveChangesAsync` 抛 `DbUpdateConcurrencyException`。
> 单用户下它从来没触发过——**但"没触发过"不等于"是对的"。**

#### Q8.3 — Why is `Ticker.Code` the primary key rather than a surrogate ID?

**中文** — 为什么用 `Ticker.Code` 当主键，而不是代理键？

**Tests / 考察点:** Schema design — natural vs surrogate keys, numeric precision · 表设计：自然键 vs 代理键、数值精度

**A.** It is a natural key on immutable reference data: `nvarchar(8)`, never renamed,
externally meaningful. `WatchlistItems.Ticker` stores the code directly, so listing a
watchlist requires no join to `Tickers` at all — the read path is a single-table query.

The trade-off is the standard natural-key one: if ASX ever renamed a code, that is a
cascading update across every watchlist rather than a single row change. For ETF codes I
took that bet.

Note also `SeedPrice` is `decimal(18,4)`, explicitly configured. EF's default for `decimal`
on SQL Server is `decimal(18,2)`, which silently truncates — money columns should never be
left to a convention.

**答.** 这是**不可变参考数据上的自然键**：`nvarchar(8)`，从不改名，对外有意义。
`WatchlistItems.Ticker` 直接存代码，所以列出一个 watchlist **完全不需要 join `Tickers` 表**——
读路径是单表查询。

代价是自然键的经典代价：万一 ASX 改了某个代码，那就是**跨所有 watchlist 的级联更新**，而不是改一行。
对 ETF 代码我赌这个不会发生。

另外 `SeedPrice` 显式配成 `decimal(18,4)`。EF 在 SQL Server 上对 `decimal` 的默认是
`decimal(18,2)`，会**静默截断**——**钱的列永远不要交给约定去决定。**

### 9. Browser, network & web performance

*浏览器、网络与 Web 性能*

#### Q9.1 — What's the network profile of this app?

**中文** — 这个应用的网络画像是什么样的？

**Tests / 考察点:** Network architecture — push vs poll, CORS with credentials · 网络架构：推 vs 拉、带凭据的 CORS

**A.** Honest answer: unmeasured, and deliberately so — slice 1 banks nothing in this
category. What I can describe is the *shape*:

- One REST GET on load, then no polling — updates arrive over a persistent SignalR
  connection (WebSocket, negotiating down to SSE or long-polling).
- ~25 messages/second, each a small JSON object, all broadcast to every client.
- CORS is explicitly configured with `AllowCredentials()` over an enumerated origin list
  (`http://localhost:5173` for the Vite dev server, `:4173` for the Playwright preview build)
  ([Program.cs](src/MarketPulse.Api/Program.cs), `Cors:AllowedOrigins` in appsettings) — note
  that `AllowCredentials` is **incompatible** with `AllowAnyOrigin`, which is why the origins
  are enumerated rather than wildcarded. Since slice 2 that is not a stylistic choice: the
  session cookies *are* the credentials, so `AllowAnyOrigin` would not merely be sloppy, it
  would fail to compile the policy at runtime.

**答.** 诚实回答：**没测量过，而且是刻意的**——slice 1 在这个类别上什么都没存下。我能描述的是**形状**：

- 加载时一次 REST GET，之后**不轮询**——更新走一条持久 SignalR 连接（WebSocket，协商失败会
  降级到 SSE 或 long-polling）。
- 约每秒 25 条消息，每条是一个小 JSON 对象，全部广播给每个客户端。
- CORS 显式配了 `http://localhost:5173` 并开了 `AllowCredentials()`——注意
  **`AllowCredentials` 和 `AllowAnyOrigin` 不能共存**，这就是为什么源必须逐个列出来。

#### Q9.2 — What's the obvious performance problem in the fan-out?

**中文** — 扇出这一块最明显的性能问题是什么？

**Tests / 考察点:** Performance & scalability reasoning — fan-out cost, batching · 性能与可扩展性推理：扇出成本、打包

**A.** `hub.Clients.All.SendAsync(...)` in
[TickBroadcaster](src/MarketPulse.Api/RealTime/TickBroadcaster.cs) sends **every** ticker to
**every** connected client, regardless of what is on their watchlist. A user watching 4
tickers receives 25/second and discards 21 — 84% waste, multiplied by every connected
client.

The fix is SignalR groups: one group per ticker, clients join the groups for their
watchlist, and the broadcaster sends to `Clients.Group(ticker)`. Second improvement: batch
the per-second sweep into one message of 25 prices instead of 25 messages, cutting frame
overhead by an order of magnitude.

I have not built it because with one user the waste is invisible — but I can state the
threshold at which it stops being invisible, which is the part that matters.

**答.** `hub.Clients.All.SendAsync(...)` 把**每一个** ticker 发给**每一个**连接的客户端，
完全不管人家的 watchlist 上有什么。一个只看 4 个 ticker 的用户每秒收 25 条、丢掉 21 条——
**84% 是浪费**，再乘以每个在线客户端。

修法是 **SignalR 分组**：每个 ticker 一个组，客户端按自己的 watchlist 加入对应组，广播端发给
`Clients.Group(ticker)`。第二个改进：把每秒那一轮扫描**打包成 1 条含 25 个价格的消息**，
而不是 25 条消息，帧开销降一个数量级。

我没有做，是因为单用户下这个浪费**看不见**——但我能说清楚**它从哪个规模开始就不再看不见**，
而这才是关键。

#### Gaps (category 9) / 类别 9 的缺口

All of it: caching strategy (no `ETag`, no `Cache-Control`), bundle analysis, code
splitting, lazy loading, Core Web Vitals or RUM, and the measurement log. This category is
explicitly deferred to a phase where there is something real to measure.

> **全欠**：缓存策略（没有 `ETag`、没有 `Cache-Control`）、打包分析、代码分割、懒加载、
> Core Web Vitals / RUM、以及测量日志。这个类别被明确推迟到"有真东西可测"的阶段。

### 10. Architecture & system design ⭐

*架构与系统设计（面试图谱里标注的最大 senior 区分点，也是本代码库最强的一类）*

#### Q10.1 — Describe the architecture and how the boundaries are actually enforced.

**中文** — 描述一下架构，以及边界到底是怎么被强制的。

**Tests / 考察点:** Architecture — layering, dependency inversion, enforced boundaries · 架构：分层、依赖倒置、强制边界

**A.** Clean Architecture, four projects, dependencies pointing inward — enforced by the
compiler, not by convention:

```
Api  →  Infrastructure  →  Application  →  Domain
                                              ↑ (references nothing)
```

`Domain` has zero project references and zero package references.
`Application` defines the interfaces it needs — `IWatchlistRepository`, `ICurrentUser` — and
`Infrastructure` implements them. That is dependency inversion doing real work: the
Application layer knows *nothing* about EF Core or HTTP.

And it is verified, not asserted
([DependencyRuleTests.cs](tests/MarketPulse.UnitTests/Architecture/DependencyRuleTests.cs)):

```csharp
var forbidden = typeof(Watchlist).Assembly.GetReferencedAssemblies()
    .Select(a => a.Name!)
    .Where(name => !name.StartsWith("System") && name != "netstandard" && name != "mscorlib");

Assert.Empty(forbidden);
```

Reflection over the compiled assembly. If anyone adds an EF Core reference to Domain, CI
goes red. Architecture that isn't tested is architecture that decays.

**答.** Clean Architecture，四个项目，依赖**向内指**——由**编译器**强制，不是靠约定。

`Domain` 有**零个**项目引用、**零个**包引用。`Application` 定义它需要的接口
（`IWatchlistRepository`、`ICurrentUser`），由 `Infrastructure` 实现。这是依赖倒置在干真活：
**Application 层对 EF Core 和 HTTP 一无所知。**

而且它是被**验证**的，不是被**声称**的：用反射检查编译后的程序集引用。谁要是给 Domain 加了 EF Core
引用，**CI 直接红**。
**没有被测试的架构，就是会腐化的架构。**

#### Q10.2 — You used CQRS with MediatR. At this size, isn't that over-engineering?

**中文** — 这个规模就上 MediatR + CQRS，这不是过度设计吗？

**Tests / 考察点:** Architecture judgement — pattern cost/benefit, reversibility · 架构判断：模式成本收益、可逆性

**A.** At slice 1's three endpoints it was at the edge, and I said so rather than defend it
reflexively. Slice 2 is the first evidence in its favour, and I'd present it as evidence
rather than as vindication.

What it buys concretely:

- **The pipeline behaviour.** `ValidationBehaviour<TRequest, TResponse>`
  ([Behaviours/ValidationBehaviour.cs](src/MarketPulse.Application/Behaviours/ValidationBehaviour.cs))
  runs FluentValidation for *every* request without a single line in any handler or
  controller. Slice 2 added four commands, two of them with validators
  (`RegisterUserCommand`, `LoginCommand`), and **not one line of registration** — handlers and
  validators are both discovered by assembly scan in `AddApplication()`. Logging, caching and
  transactions plug in the same way, at one place each rather than N.
- **Controllers stay dispatch.** Adding a whole authentication subsystem did not put one line
  of orchestration in the HTTP layer, because there is nowhere for it to sit.
- **Handlers substitute cleanly in tests.**
  [LoginHandlerTests](tests/MarketPulse.UnitTests/Application/LoginHandlerTests.cs) drives
  lockout and timing behaviour through NSubstitute doubles with no host, no HTTP and no
  database — possible because the handler's dependencies are all interfaces the Application
  layer defines.

What it costs, unchanged: indirection. A reader chasing "what happens on POST /login" goes
controller → `ISender` → runtime-resolved handler, with **no compile-time link to follow**.

**答.** 在 slice 1 的三个端点上**它确实在边缘上**，我当时也是直说的，而不是条件反射地辩护。
**slice 2 是第一份对它有利的证据**——而我会把它当作**证据**来讲，不是当作**平反**。

它买到的具体东西：

- **管道行为。** `ValidationBehaviour<TRequest, TResponse>` 让 FluentValidation 对**每一个**请求
  生效，而 handler 和控制器里一行代码都不用写。slice 2 新增了四个命令，其中两个带校验器
  （`RegisterUserCommand`、`LoginCommand`），**注册代码一行都没写**——handler 和校验器都由
  `AddApplication()` 里的程序集扫描自动发现。日志、缓存、事务将来都以同样方式接入：
  **各自一处，而不是 N 处。**
- **控制器保持为转发。** 加进来一整套认证子系统，**没有在 HTTP 层留下一行编排逻辑**，
  因为根本没有位置给它。
- **handler 在测试里能干净地替换。** `LoginHandlerTests` 用 NSubstitute 替身驱动锁定和时间等价
  行为，**不启动宿主、不走 HTTP、不碰数据库**——之所以可能，是因为 handler 的依赖全是 Application
  层自己定义的接口。

代价没变：**间接性**。一个读代码的人想搞清"POST /login 发生了什么"，要走 控制器 → `ISender` →
运行时解析的 handler，中间**没有编译期链接可以跟**。

**Follow-up: when would you rip it out? / 什么时候你会把它拆掉？**

If the app had stayed at slice 1's size and the pipeline behaviours had stayed at one, I would
have. Slice 2 moved the threshold rather than crossed it: the endpoint count tripled, but
`ValidationBehaviour` is **still the only behaviour**, which is the number that actually
justifies the indirection. So the honest position is "the case got stronger, and it is still
not proven". The interview map lists a planned ADR-004 — *"CQRS was overkill, here's the
rollback"* — and I have not withdrawn it, because being able to reverse an architectural
decision is a stronger signal than being able to make one, and because deciding you were right
on the strength of one favourable slice is exactly how architectures ossify.

> 如果应用一直停在 slice 1 的规模、管道行为一直只有一个，**我会拆**。
> **slice 2 是把门槛推远了，不是跨过了它**：端点数翻了三倍，但 `ValidationBehaviour`
> **仍然是唯一的一个行为**——**而这才是真正能为那层间接性买单的数字**。
> 所以诚实的立场是"**论据变强了，但仍然没有被证成**"。面试图谱里那份计划中的
> ADR-004——"CQRS 是过度设计，这是回滚方案"——**我没有撤回**，
> 因为**能推翻一个架构决定，比能做出一个架构决定是更强的信号**；
> 也因为**凭一个对自己有利的 slice 就断定自己当初是对的，正是架构僵化的标准路径。**

#### Q10.3 — What are the seams that make later slices cheap?

**中文** — 哪些接缝让后续 slice 变便宜？

**Tests / 考察点:** Evolutionary design — seams, bounding rework · 演进式设计：接缝、框定返工

**A.** Every known stub is isolated behind exactly one boundary, so replacing it touches one
file:

| Stub / 桩件 | Seam / 接缝 | Replacement / 替换物 | Status / 状态 |
|---|---|---|---|
| `DevAuthMiddleware` | `ICurrentUser` | JWT claims — no handler, query, or schema change / 不动 handler、查询、schema | ✅ **done, slice 2** |
| `FakeTickService` | `PriceTickChannel` | Real market feed — no consumer change / 真实行情源，消费端不动 | pending |
| In-process `Channel<T>` | `PriceTickChannel` | RabbitMQ (ADR-001) — same interface / 同一接口 | pending |

This is the explicit consequence recorded in
[ADR-002](docs/adr/002-walking-skeleton-first.md): some rework is *accepted*, and its cost is
bounded up front by where the seam sits. Saying "I'll fix it later" is a wish; putting an
interface at the boundary is a plan.

**答.** 每个已知的桩件都**被隔离在恰好一个边界后面**，所以替换它只动一个文件（见上表）。

这是 [ADR-002](docs/adr/002-walking-skeleton-first.md) 里明确记下的**后果**：**返工是被接受的**，
而它的成本由接缝的位置**事先框定**。
**说"以后再修"是许愿；在边界上放一个接口才是计划。**

**Follow-up: has any of it paid off yet? / 有哪一条已经兑现了吗？**

Yes — row one, and it is now measurable rather than asserted. Slice 2 replaced the dev-auth
stub with real cookie-based JWT authentication. The diff on the watchlist path is **empty**:
`ICurrentUser`'s implementation, `IWatchlistRepository`, `WatchlistRepository`, every
watchlist handler and validator, `GetWatchlistQuery`, and the watchlist tables are all
unchanged. `WatchlistController` gained exactly one line — `[Authorize]`. The seam held.

What it did *not* protect against, stated honestly: the **tests** all had to change. Every
integration test now has to register a user and carry a cookie jar
([AuthenticatedClient.cs](tests/MarketPulse.IntegrationTests/AuthenticatedClient.cs)), and the
frontend gained a router that every screen test now mounts inside. A seam bounds the *code*
that changes; it does not bound the *harness* that exercises it. Nobody predicts that in the
ADR, and it was the larger share of the work.

> **兑现了——第一行，而且现在是可测量的，不是断言的。** slice 2 用真实的 cookie-based JWT 认证
> 替换了 dev-auth 桩件。watchlist 那条路径上的 diff 是**空的**：`ICurrentUser` 的实现、
> `IWatchlistRepository`、`WatchlistRepository`、所有 watchlist 的 handler 和校验器、
> `GetWatchlistQuery`、以及 watchlist 相关的表，**全部未改**。`WatchlistController` 只多了一行
> `[Authorize]`。**接缝扛住了。**
>
> 但要老实说它**没有**保护到什么：**测试全都得改**。每个集成测试现在都要先注册用户、带着 cookie jar；
> 前端多了一个 router，每个界面测试都得挂在它里面。
> **接缝框定的是会变的"代码"，框不住跑这些代码的"脚手架"。** ADR 里没人预测到这一点，
> 而它恰恰是工作量里更大的那一半。

#### Q10.4 — Why a walking skeleton instead of building the backend first?

**中文** — 为什么先做 walking skeleton，而不是先把后端建完？

**Tests / 考察点:** Delivery strategy — integration risk, walking skeleton · 交付策略：集成风险、walking skeleton

**A.** [ADR-002](docs/adr/002-walking-skeleton-first.md). The original plan built the backend
completely over six weeks before any frontend existed. That leaves the highest-risk seam in
the system — ingestion → `Channel<T>` → SignalR → React re-render — unproven until week
three, and produces nothing demoable until week seven.

Integration risk concentrates at seams, not inside layers. A skeleton pays the integration
cost while the codebase is still small enough to change cheaply. The rejected alternatives
are written down too, including "scaffolding-only first slice" — green CI over an empty test
suite is not evidence that anything works.

**答.** 见 ADR-002。原计划是先花六周把后端完整建好，然后才有前端。那意味着系统里**风险最高的接缝**
——采集 → `Channel<T>` → SignalR → React 重渲染——**到第三周都还没被验证过**，而且**七周之内没有
任何能演示的东西**。

**集成风险集中在接缝上，不在层内部。** 骨架让你在代码库还小、改起来还便宜的时候就把集成成本付掉。
被否决的方案也写下来了，包括"第一个 slice 只做脚手架"——
**在空测试套件上得到绿色 CI，不构成任何东西能工作的证据。**

#### Q10.5 — Modular monolith or microservices?

**中文** — 模块化单体还是微服务？

**Tests / 考察点:** System design — service boundaries, scaling profiles · 系统设计：服务边界、扩展画像

**A.** [ADR-001](docs/adr/001-modular-monolith-plus-one-service.md): modular monolith plus
**one** extracted service. Portfolio and Market Data ship inside `MarketPulse.Api`; alert
evaluation extracts to its own worker over RabbitMQ from Phase 3.

The reasoning is a scaling-profile test, not a fashion test. Alert evaluation is the only
component that is CPU-bound, bursty on price movement, and tolerant of eventual
consistency. Everything else shares the same request-scoped lifetime and the same
transaction boundary — splitting those buys distributed-transaction problems and no
independent scaling.

Rejected: pure monolith (forfeits any messaging demonstration) and microservices throughout
(four or five pipelines, databases and observability stacks for a system with one user —
the cost is real, the benefit imaginary).

**答.** 见 ADR-001：**模块化单体 + 抽出一个服务**。Portfolio 和 Market Data 在 `MarketPulse.Api`
里；告警评估从 Phase 3 起抽成独立 worker，通过 RabbitMQ 通信。

判断依据是**扩展画像**，不是流行度。**告警评估是唯一一个 CPU 密集、随价格波动而突发、且能容忍最终
一致性的组件。** 其余所有东西共享同一个请求作用域生命周期和同一个事务边界——把它们拆开，买到的是
分布式事务问题，买不到任何独立伸缩。

被否决的：纯单体（放弃了任何消息相关的展示）；全面微服务（为一个只有一个用户的系统建四五套流水线、
数据库和可观测性栈——**成本是真的，收益是想象的**）。

#### Gaps (category 10) / 类别 10 的缺口

ADR-001's decision is documented but not yet *realised* — there is no second deployable and
no RabbitMQ. No micro-frontends, no design system, and ADR-004 (the CQRS rollback) is
unwritten. ADR-003 records a deployment constraint the codebase cannot yet honour: cookie auth
requires the SPA and API to be same-site, and there is no deployment topology to check that
against (Q12.5).

> ADR-001 的决定**只写在纸上、还没落地**——没有第二个可部署物，也没有 RabbitMQ。
> 没有微前端、没有设计系统，ADR-004（CQRS 回滚）还没写。
> ADR-003 记下了一条**代码库目前还兑现不了的部署约束**：cookie 认证要求 SPA 和 API 同站，
> 而现在**根本没有一个部署拓扑可以拿来验证它**（见 Q12.5）。

### 11. Distributed systems & messaging

*分布式系统与消息*

#### Q11.1 — You have a `Channel<T>`. Is that a message queue?

**中文** — 你用了 `Channel<T>`，那算消息队列吗？

**Tests / 考察点:** Distributed-systems fundamentals — in-process buffers vs durable queues · 分布式基础：进程内缓冲 vs 持久队列

**A.** No, and conflating them would be the mistake worth catching. `Channel<T>` is an
**in-process** buffer: same memory, same lifetime, no durability, no delivery guarantee, no
consumer outside the process. If the API restarts, everything in it is gone.

That is why the interview map credits it to category 1 (runtime internals) and explicitly
credits category 11 with **nothing**. It does earn one architectural point — the *seam* is
in the right place, so swapping the in-process channel for RabbitMQ is an implementation
change behind an existing boundary rather than a redesign.

**答.** **不算**，而且把两者混为一谈正是该被抓住的错误。`Channel<T>` 是**进程内**缓冲：
同一块内存、同一个生命周期，**没有持久化、没有投递保证、进程外没有消费者**。API 一重启，里面的
东西全没。

所以面试图谱把它记在类别 1（运行时底层）名下，而类别 11 **明确记为零**。它只挣到一个架构上的分：
**接缝的位置是对的**，所以把进程内通道换成 RabbitMQ 是"既有边界后面的实现替换"，不是重新设计。

#### Q11.2 — What breaks first if you run two API instances behind a load balancer?

**中文** — 如果在负载均衡后面跑两个 API 实例，最先坏的是什么？

**Tests / 考察点:** Distributed systems — horizontal scaling, shared state, backplanes · 分布式系统：横向扩展、共享状态、backplane

**A.** Three things, in order:

1. **SignalR has no backplane.** Each instance broadcasts only to clients connected to
   *itself*. Half your users see half the ticks. Fix: Redis backplane, or Azure SignalR
   Service.
2. **Two independent `FakeTickService` instances** both walk prices from the same seed, so
   the two halves of your userbase see *different prices for the same ticker*. Fix: the tick
   source must be a single producer publishing to a shared broker — which is exactly what
   ADR-001's RabbitMQ move implies.
3. **No distributed concurrency control.** The capacity race in Q8.2 goes from unlikely to
   routine.

The instructive part: two of those three are invisible at one instance and immediately fatal
at two. Horizontal scaling is a design property, not a deployment setting.

**答.** 三件事，按顺序：

1. **SignalR 没有 backplane。** 每个实例只广播给连到**它自己**的客户端。一半用户看到一半的 tick。
   修法：Redis backplane，或 Azure SignalR Service。
2. **两个独立的 `FakeTickService`** 各自从同一个种子开始游走，于是**两半用户看到同一个 ticker 的
   不同价格**。修法：tick 源必须是**单一生产者**发布到共享 broker——这正是 ADR-001 里 RabbitMQ
   那一步的含义。
3. **没有分布式并发控制。** Q8.2 里的容量竞争从"不太可能"变成"家常便饭"。

**最有教育意义的一点：这三件里有两件在单实例下完全看不见，在双实例下立刻致命。**
**横向扩展是设计属性，不是部署开关。**

#### Gaps (category 11) / 类别 11 的缺口

Everything: RabbitMQ topology, the outbox pattern, idempotent consumers, eventual-consistency
handling, Polly resilience policies, and the chaos test. This is the largest single gap in
the codebase and it is scheduled for Phase 3.

> **全欠**：RabbitMQ 拓扑、outbox 模式、幂等消费者、最终一致性处理、Polly 弹性策略、混沌测试。
> 这是整个代码库**最大的单一缺口**，排期在 Phase 3。

### 12. Cloud & DevOps

*云与 DevOps*

#### Q12.2 — Explain the Dockerfile's layer strategy.

**中文** — 讲讲 Dockerfile 的分层策略。

**Tests / 考察点:** Docker build optimisation — layer caching, runtime vs SDK images · Docker 构建优化：层缓存、运行时镜像

**A.** Multi-stage, ordered for cache hits
([src/MarketPulse.Api/Dockerfile](src/MarketPulse.Api/Dockerfile)):

1. Copy **only** `Directory.Build.props`, `Directory.Packages.props` and the four `.csproj`
   files → `dotnet restore`.
2. *Then* copy `src/` and publish.

Source changes on every commit; the dependency graph changes rarely. Splitting them means
the expensive `restore` layer is cached across virtually every build. Final stage is
`aspnet:10.0`, not `sdk:10.0` — the runtime image, without a compiler in production.

**答.** 多阶段构建，顺序是为**缓存命中**排的：

1. **只**复制 `Directory.Build.props`、`Directory.Packages.props` 和四个 `.csproj` → `restore`。
2. **然后**才复制 `src/` 并 publish。

源码每次提交都变，依赖图很少变。拆开的结果是：**昂贵的 restore 层几乎每次构建都命中缓存。**
最终阶段用 `aspnet:10.0` 而不是 `sdk:10.0`——运行时镜像，**生产环境里不带编译器**。

#### Q12.5 — Slice 2 put a constraint on your deployment topology. What is it?

**中文** — slice 2 给你的部署拓扑加了一条约束。是什么？

**Tests / 考察点:** Deployment architecture & the browser security model — same-site, BFF · 部署架构与浏览器安全模型：same-site、BFF

**A.** **Cookie auth requires the SPA and API to be same-site**, and it is recorded as a
consequence in [ADR-003](docs/adr/003-cookie-based-sessions.md) rather than left to be
discovered during a deploy.

`localhost:5173` and `localhost:5100` differ only by port, and ports are **not part of a
"site"** — so local development and the E2E suite work unchanged. But a production topology of
`app.example.com` + `api.example.com` **would silently stop sending `mp_refresh`**, which is
`SameSite=Strict`. Nothing would throw. Login would appear to work, and then every session
would die at the 15-minute mark with no error anyone could search for.

The deployment slice therefore has only two options: serve both from one origin, or add a
backend-for-frontend proxy hop. ADR-003 frames the second as the intended direction rather
than a workaround — putting the token behind a server you control is what BFF *is*.

The related one: **SignalR authenticates off the same cookie**, so the hub inherits the same
constraint. There is no separate token negotiation to relax it with.

**答.** **cookie 认证要求 SPA 和 API 同站（same-site）**，这条作为"后果"写进了 ADR-003，
而不是留到部署时才被发现。

`localhost:5173` 和 `localhost:5100` 只差端口，而**端口不属于"站点"的一部分**——所以本地开发和
E2E 套件都不受影响。但生产上如果是 `app.example.com` + `api.example.com`，
**`mp_refresh`（`SameSite=Strict`）会静默地不再发送**。**什么都不会抛异常**：登录看起来是成功的，
然后每个会话都在第 15 分钟死掉，而且没有任何人能搜到的错误信息。

所以部署 slice 只有两条路：**两者由同一个源提供**，或者**加一层 BFF 代理**。ADR-003 把第二条
写成**本来就要去的方向**，而不是一个绕路——**把 token 藏在你自己控制的服务器后面，这正是 BFF 的定义**。

配套的一条：**SignalR 用同一个 cookie 认证**，所以 hub 继承同一条约束，
**没有独立的 token 协商机制可以拿来放宽它**。

**Follow-up: is the E2E job worth what it costs? / 那个 E2E job 值这个代价吗？**

It is materially slower than the rest of the pipeline — SQL Server, a `dotnet run`, a Vite
preview server and a Chromium download, versus three jobs that need none of that. Accepted as
worth it because it is the **only** test in the suite that proves a cookie survives a real
browser round-trip. Every other layer stubs the browser: `WebApplicationFactory` uses .NET's
`CookieContainer`, MSW intercepts before a cookie jar is involved. `SameSite`, `httpOnly` and
`Path` are browser behaviours, and only a browser can falsify them.

> 它**明显比流水线其余部分慢**——SQL Server、一次 `dotnet run`、一个 Vite preview server、
> 外加下载 Chromium，而另外三个 job 一个都不需要。判定为值得，因为它是整个套件里**唯一**能证明
> **cookie 扛得住真实浏览器往返**的测试。其它每一层都在打桩：`WebApplicationFactory` 用的是 .NET
> 的 `CookieContainer`，MSW 在 cookie jar 介入之前就拦截了。**`SameSite`、`httpOnly`、`Path`
> 是浏览器行为，而只有浏览器能证伪它们。**

### 13. Security (full-stack)

*全栈安全*

#### Q13.2 — How is user input validated, and where?

**中文** — 用户输入在哪里、怎么校验？

**Tests / 考察点:** Input validation — layered defence, allowlists, purity trade-offs · 输入校验：分层防御、白名单、纯度取舍

**A.** Three layers, each catching what the one before it can't:

1. **Type/shape** — `[ApiController]` model binding rejects malformed JSON before any code runs.
2. **Format and existence** — FluentValidation via the MediatR pipeline: non-empty, ≤ 8
   characters, and must exist in the `Tickers` reference table
   ([AddWatchlistItemCommand.cs](src/MarketPulse.Application/Watchlists/AddWatchlistItemCommand.cs)).
3. **Business invariants** — the domain aggregate: no duplicates, max 20
   ([Watchlist.cs](src/MarketPulse.Domain/Entities/Watchlist.cs)).

The ticker allowlist is the strongest control: input is checked against a fixed set of 25
known codes, not merely sanitised. Allowlists beat denylists.

**答.** 三层，每一层拦住上一层拦不住的：

1. **类型/形状**——`[ApiController]` 的模型绑定在任何代码运行前就拒掉畸形 JSON。
2. **格式与存在性**——MediatR 管道里的 FluentValidation：非空、≤ 8 字符、且必须存在于 `Tickers`
   参考表里。
3. **业务不变量**——领域聚合：不重复、最多 20 条。

其中**ticker 白名单是最强的控制**：输入是**对照 25 个已知代码的固定集合校验**的，而不只是被"清洗"。
**白名单永远优于黑名单。**

**Follow-up: doing a database lookup inside a validator — is that a good idea? / 在校验器里查数据库，这样好吗？**

It is the sharpest design criticism available in this codebase, and the honest answer is
"it's a trade-off I'd revisit". `AddWatchlistItemValidator` takes an `IWatchlistRepository`
and calls `TickerExistsAsync`. Upside: unknown tickers get a clean 400 with a precise slug
rather than a 500 from a foreign-key violation. Downsides: validation now performs I/O (so
"validation" is no longer cheap or pure), it is a time-of-check/time-of-use race in
principle, and the Application layer's validation is coupled to persistence. The alternative
— let the handler check and throw a domain exception — keeps validators pure at the cost of
one more exception type. On reference data that changes never, I took the coupling.

> **这是整个代码库里最尖锐的一处设计批评**，诚实答案是"这是一个我会重新考虑的取舍"。
> `AddWatchlistItemValidator` 注入了 `IWatchlistRepository` 并调用 `TickerExistsAsync`。
> **好处**：未知 ticker 得到一个干净的 400 和精确的 slug，而不是外键冲突导致的 500。
> **坏处**：校验现在要做 I/O（于是"校验"不再是廉价的、纯的）；原理上存在 TOCTOU 竞争；
> Application 层的校验被耦合到了持久化上。
> **另一条路**——让 handler 去查并抛领域异常——能保持校验器纯净，代价是多一个异常类型。
> 在一份**永远不变**的参考数据上，我选择了接受这个耦合。

#### Q13.5 — Why CSRF protection if you have `SameSite` cookies?

**中文** — 都用了 `SameSite` cookie，为什么还要防 CSRF？

**Tests / 考察点:** Web security — CSRF, defence in depth, exemption reasoning · Web 安全：CSRF、纵深防御、豁免论证

**A.** Because it is defence in depth, and I'd say that plainly rather than overclaim it.
`SameSite` already blocks the common cross-site cases; the double-submit token covers what
`SameSite=Lax` does not, and degrades safely if a browser or a future same-site-adjacent
topology weakens the assumption. The asymmetry it exploits: a cross-site attacker can cause
the browser to *send* the `mp_csrf` cookie, but cannot *read* it to set the matching header.

[CsrfMiddleware.cs](src/MarketPulse.Api/Middleware/CsrfMiddleware.cs), and three details in it
are worth more than the concept:

- **`CryptographicOperations.FixedTimeEquals`**, not `==`. A short-circuiting string compare
  leaks the prefix length through timing. It is a nonce rather than a long-lived secret so the
  practical risk is small — but constant-time comparison of a security token costs nothing and
  arguing about whether you need it costs more than doing it.
- **Safe methods and two paths are exempt.** `GET`/`HEAD`/`OPTIONS`/`TRACE` by definition;
  `login` and `register` because **a client cannot hold a CSRF cookie before its first
  successful authentication** — those are rate limited instead.
- **`/hubs/prices` is exempt for a completely different reason**, and the source spells it out
  so the exemption is not read as a blanket "anything under /hubs". SignalR negotiate is a
  POST, but `PriceHub` only pushes server-to-client — there is **no client-invokable method
  for a forged request to trigger**, so there is nothing for CSRF to protect. Also, the
  browser's WebSocket API cannot attach a custom header to an upgrade request even if we
  wanted one. A future hub with client-invokable methods needs that argument re-checked
  against it, not inherited.

Double-submit is chosen over the framework's `IAntiforgery`, which is oriented around form
posts and MVC views rather than a JSON API consumed by a SPA.

**答.** 因为这是**纵深防御**，而且我会**直说，不夸大它**。`SameSite` 已经挡住了常见的跨站场景；
double-submit 令牌覆盖的是 `SameSite=Lax` 覆盖不到的部分，并且在浏览器行为变化、或未来出现某种
"准同站"拓扑削弱这个假设时能安全降级。它利用的**不对称性**是：跨站攻击者能让浏览器**发出**
`mp_csrf` cookie，但**读不到**它，因而设不出匹配的请求头。

`CsrfMiddleware.cs` 里有三个细节，比这个概念本身更值钱：

- **用 `CryptographicOperations.FixedTimeEquals`，不是 `==`。** 会短路的字符串比较会通过时间
  泄漏前缀长度。它是个 nonce 而不是长期密钥，所以实际风险很小——但**对安全令牌做常量时间比较
  不花任何成本，而争论"要不要"比直接做的成本更高**。
- **安全方法和两条路径豁免。** `GET`/`HEAD`/`OPTIONS`/`TRACE` 按定义豁免；`login` 和 `register`
  豁免是因为**客户端在第一次认证成功之前不可能持有 CSRF cookie**——它们改用限流来防。
- **`/hubs/prices` 的豁免理由完全不同**，源码里把它写清楚了，免得被读成"凡是 /hubs 下面的都放行"。
  SignalR 的 negotiate 是 POST，但 `PriceHub` **只做服务端到客户端的推送**——**没有任何可被客户端
  调用的方法**能让伪造请求触发，所以这里根本没有东西需要 CSRF 去保护。而且浏览器的 WebSocket API
  **本来就没法给升级请求加自定义头**。将来若出现带可调用方法的 hub，**必须重新对它检验这套论证，
  而不是继承这个豁免**。

选 double-submit 而不是框架的 `IAntiforgery`，是因为后者面向的是表单提交和 MVC 视图，
而不是被 SPA 消费的 JSON API。

**Follow-up: the CSRF cookie outlives the access token. Is that a mistake? / CSRF cookie 比 access token 活得久，这是失误吗？**

No, it is forced. `mp_csrf` is set to the **refresh** lifetime, not the access lifetime
([AuthController.cs](src/MarketPulse.Api/Controllers/AuthController.cs)). If it expired with
the access token, a client whose access token had just expired would have no valid CSRF token
with which to authenticate its own `POST /auth/refresh` — the refresh call is a mutation like
any other and has to pass the same check. Tie the nonce to the short lifetime and you build a
session that **cannot renew itself**.

> **不是失误，是被逼的。** `mp_csrf` 的有效期跟的是**刷新**令牌，不是 access 令牌。如果它和
> access token 一起过期，那么一个 access token 刚刚过期的客户端，**就没有有效的 CSRF 令牌去认证
> 它自己那次 `POST /auth/refresh`**——刷新和其它变更请求一样，必须过同一道检查。
> **把 nonce 绑到短生命周期上，你造出来的会话将无法自我续期。**

### 14. Testing (full-stack)

*全栈测试*

#### Q14.1 — What is your testing strategy, and why that shape?

**中文** — 你的测试策略是什么？为什么是这个形状？

**Tests / 考察点:** Test strategy — trophy vs pyramid, where bugs live · 测试策略：奖杯形 vs 金字塔形、bug 住在哪里

**A.** Trophy-shaped, not pyramid-shaped: heaviest at integration, thin but present at unit
and end-to-end. Documented in [docs/TESTING.md](docs/TESTING.md).

| Level / 层级 | Tooling / 工具 | Scope / 范围 |
|---|---|---|
| Domain unit | xUnit | `Watchlist` invariants, `RandomWalk` bounds, `User` lockout, `RefreshToken` rotation |
| Application unit | xUnit + NSubstitute | Handler orchestration over substituted repositories, password policy |
| Architecture | xUnit + reflection | Domain references nothing outside the BCL |
| Backend integration | WebApplicationFactory + Testcontainers | Real SQL Server, real migrations, real HTTP; auth journeys, CSRF, rate limiting, cross-user isolation |
| Frontend unit | Vitest | Reducer, schemas, `useNow`/`usePriceStream`/`PriceCell`, api-client CSRF + single-flight refresh |
| Frontend integration | Vitest + RTL + MSW | Screen render, 409-duplicate path, login error paths, protected-route redirect |
| End-to-end | Playwright + Chromium | Three journeys through the real API and the production dashboard build |

**98 .NET tests** (58 unit + 40 integration) **+ 32 frontend tests** (20 dashboard + 12
api-client) **+ 3 Playwright journeys** — up from 43 total at the end of slice 1. Slice 2 more
than doubled the suite, and the growth is concentrated at integration, which is the shape the
trophy predicts.

The rationale for the trophy: in a layered app most bugs live *between* layers, so tests that
cross a boundary find more per unit of maintenance cost. The clean-clone FK bug in Q8.4 is the
proof, and the logout-revocation bug in Q15.2 is a second one — both lived exactly in the gap
that unit tests cannot see.

**答.** **奖杯形，不是金字塔形**：集成层最重，单元和端到端两头薄但都有。

**98 个 .NET 测试**（58 单元 + 40 集成）**+ 32 个前端测试**（20 dashboard + 12 api-client）
**+ 3 条 Playwright 旅程**——slice 1 结束时总共 43 个。**slice 2 把套件规模翻了一倍多，而增量集中在
集成层**，这正是奖杯形所预测的形状。

奖杯形的理由是：**在分层应用里，大多数 bug 住在层与层之间**，所以跨边界的测试在**每单位维护成本**上
能发现更多问题。Q8.4 那个干净克隆外键 bug 是一个证据，Q15.2 那个登出撤销 bug 是第二个——
**两个都恰好住在单元测试看不见的那道缝里**。

#### Q14.2 — Testcontainers instead of the in-memory provider. Why pay the runtime cost?

**中文** — 为什么用 Testcontainers 而不是内存提供程序？值得付这个运行时代价吗？

**Tests / 考察点:** Test fidelity — real infrastructure vs in-memory fakes · 测试保真度：真实基础设施 vs 内存假件

**A.** Because the in-memory provider is not SQL Server. It doesn't enforce unique indexes,
it doesn't enforce foreign keys, it doesn't run your migrations, and it translates LINQ
differently. Every single database-level guarantee in Q8.2's table is invisible to it —
which means the tests would pass on a schema that cannot exist.

[SqlServerFixture](tests/MarketPulse.IntegrationTests/SqlServerFixture.cs) starts a real
`mssql/server:2022-latest` container and runs `Database.MigrateAsync()` — so the tests
exercise the same migrations that will run in production. It is shared across test classes
via `ICollectionFixture` so the (expensive) container starts once per run, not once per
class.

**答.** 因为**内存提供程序不是 SQL Server**。它不强制唯一索引、不强制外键、不跑你的迁移，
而且 LINQ 翻译方式也不同。**Q8.2 那张表里每一条数据库级保证，它统统看不见**——
意味着测试可能在一个**根本不可能存在的 schema** 上全绿。

`SqlServerFixture` 启动真实的 `mssql/server:2022-latest` 容器并执行 `Database.MigrateAsync()`
——所以测试跑的**就是将来在生产上跑的那套迁移**。它通过 `ICollectionFixture` 在测试类之间共享，
所以这个（昂贵的）容器**每次运行只启动一次，而不是每个类一次**。

#### Q14.6 — What did you deliberately *not* test, and why?

**中文** — 你刻意**不**测什么？为什么？

**Tests / 考察点:** Test judgement — deliberate non-coverage, testability-driven design · 测试判断：刻意不测、可测试性驱动设计

**A.** Four gaps, recorded rather than hidden ([docs/TESTING.md](docs/TESTING.md)):

- **No coverage threshold.** On a codebase this small a threshold drives noise, not quality.
  It arrives when there is a codebase to threshold.
- **No SignalR transport unit test.** That would be testing Microsoft's library. One
  integration test proves our chain; the reducer tests cover our logic.
- **No load or brute-force simulation.** The rate limiter is tested for *behaviour at its
  threshold* — the tenth request passes, the eleventh is rejected — not under real concurrent
  load. Proving a fixed-window limiter holds up under contention needs a load harness, which
  is a different exercise.
- **No test that `Secure` cookies work over HTTPS**, and this one is a genuine trade-off
  rather than laziness. The whole suite runs in Development, where `Secure` is off *by
  design*: .NET's `CookieContainer` **refuses to send `Secure` cookies over plain HTTP**, so
  turning it on would break every integration test rather than strengthen it. The attribute is
  instead covered by [AuthCookiesTests](tests/MarketPulse.UnitTests/Api/AuthCookiesTests.cs) as
  a pure function over the environment — which is exactly why `AuthCookies.Build` was written
  as a pure function in the first place. **Testability shaped the design, not the reverse.**

Writing the gaps down is the point. An untested area you can name is a decision; one you
can't is a surprise.

**答.** 四个缺口，**记下来而不是藏起来**：

- **没有覆盖率门槛。** 在这么小的代码库上，门槛驱动的是噪音，不是质量。等有值得设门槛的代码量再说。
- **没有 SignalR 传输层单元测试。** 那是在测微软的库。一个集成测试证明我们这条链；
  reducer 的测试覆盖我们的逻辑。
- **没有压测或暴力破解模拟。** 限流器只测了**阈值处的行为**——第 10 个请求通过、第 11 个被拒——
  **没有在真实并发下测**。要证明一个固定窗口限流器在争用下站得住，需要一套压测装置，那是另一件事。
- **没有测 `Secure` cookie 在 HTTPS 下的行为**，而这一条是**真实的取舍，不是偷懒**。整个套件跑在
  Development 下，那里 `Secure` **是刻意关掉的**：.NET 的 `CookieContainer` **拒绝在明文 HTTP 上
  发送 `Secure` cookie**，所以把它打开只会**让每个集成测试挂掉**，而不是让它们更强。这个属性改由
  `AuthCookiesTests` 作为**环境名的纯函数**来覆盖——而这恰恰是 `AuthCookies.Build` 当初就被写成纯
  函数的原因。**是可测试性塑造了设计，而不是反过来。**

**把缺口写下来本身就是重点。一个你叫得出名字的未测区域是决定；叫不出名字的是意外。**

### 15. Engineering practice & behavioural

*工程实践与行为面试*

#### Q15.1 — How do changes get from your machine to `main`?

**中文** — 改动是怎么从你的机器走到 `main` 的？

**Tests / 考察点:** Engineering process — branching discipline, CI, commit hygiene · 工程流程：分支纪律、CI、提交卫生

**A.** Feature branches merge into `test`; `main` advances only after verification on
`test`. CI runs on push to both branches and on every PR
([.github/workflows/ci.yml](.github/workflows/ci.yml)).

Commits are conventional (`feat:`, `fix:`, `docs:`, `ci:`, `chore:`, `test:`) and scoped to
one change each — the history reads as a narrative of the build, and `fix:` commits after
`feat:` commits are review findings being addressed, visibly.

**答.** 特性分支合进 `test`；**`main` 只在 `test` 上验证通过后才推进**。CI 在这两个分支的推送
和每个 PR 上运行。

提交遵循 conventional commits，每个提交**只做一件事**——历史读起来是一条建造过程的叙事，
而 `feat:` 后面跟的 `fix:` 提交，**就是评审发现被处理掉的可见证据**。

#### Q15.2 — Tell me about a bug you shipped and how you caught it.

**中文** — 讲一个你交付出去的 bug，以及你是怎么抓到它的。

**Tests / 考察点:** Verification discipline & debugging (behavioural / STAR) · 验证纪律与调试（行为面试 / STAR）

**A (STAR).**

- **Situation.** Twelve implementation tasks were complete, every per-task review was clean,
  43 tests were green, and the branch looked ready to merge.
- **Task.** Run a whole-branch review before merging rather than trusting the accumulated
  per-task ones.
- **Action.** That review found two **Critical** defects no per-task review could have seen,
  because each lived *between* two tasks. (1) The dev `User` row was seeded only by the test
  fixture, never by the application — so from a clean clone the very first
  `GET /api/v1/watchlist` violated a foreign key and 500'd, permanently. (2) The dashboard
  defaulted to `http://localhost:5100` while `launchSettings.json` bound port 5151 — REST and
  SignalR both hitting a dead port. Both were fixed, then verified end to end against a
  **wiped database**: 25 tickers, 1 dev user, 1 watchlist, 4 items, a real 200 with a
  correlation ID, a real 409, a real 400, and a live SignalR negotiate.
- **Result.** Both DoD criteria that had been quietly failing now genuinely pass. The
  transferable lesson: green tests measure the paths you thought of. A first-run-from-clean
  check measures the one your user actually takes — and no amount of unit testing
  substitutes for it.

**答（STAR）.**

- **情境（S）.** 12 个实现任务全部完成，每个任务的评审都是干净的，43 个测试全绿，分支看起来可以合了。
- **任务（T）.** 在合并前跑一次**整分支评审**，而不是信任那些累积起来的单任务评审。
- **行动（A）.** 那次评审发现了**两个 Critical 缺陷，是任何单任务评审都不可能看到的**，
  因为它们各自住在**两个任务之间**。
  （1）dev 的 `User` 行**只由测试 fixture 种入，应用自己从不种**——所以从干净克隆开始，
  第一次 `GET /api/v1/watchlist` 就违反外键、**永久 500**。
  （2）dashboard 默认指向 `http://localhost:5100`，而 `launchSettings.json` 绑的是 5151——
  **REST 和 SignalR 打的都是一个死端口**。
  两个都修掉后，对着一个**被清空的数据库**做端到端验证：25 个 ticker、1 个 dev 用户、
  1 个 watchlist、4 个条目，真实的 200 + 相关性 ID、真实的 409、真实的 400、以及一次活的
  SignalR negotiate。
- **结果（R）.** 两条一直在**静默失败**的完成标准，现在真正达成了。
  可迁移的教训：**绿色测试衡量的是你想到的那些路径；"从干净状态首次运行"衡量的是你的用户
  真正走的那条路——而这一条，多少单元测试都替代不了。**

**Follow-up: give me one where the test itself was the problem. / 再给一个"测试本身就是问题"的例子。**

Slice 2's logout. There was a passing test asserting that logging out cleared the session, and
it passed for **entirely the wrong reason**.

The bug was a chain of three things, none wrong on its own. The refresh cookie was path-scoped
to `/api/v1/auth/refresh`, so it **never rode the logout request**. `LogoutHandler` starts with
a null guard — logging out with no token is deliberately a success, not an error — so with no
cookie present, `RevokeAllForUserAsync` was **unreachable code**. And the test asserted on *the
caller's own cookie jar*, which logout does empty. So: green test, cookies visibly cleared,
and **every refresh token stayed valid for its full 14 days after sign-out**. Sign out on a
shared machine and the session was still live.

The fix was two lines of behaviour and a much better test: widen the cookie path to
`/api/v1/auth` (Q7.5), then assert with a **token captured before logout and replayed from a
clean client** that holds nothing from the jar logout just emptied. Plus a control test proving
that same replay *does* mint a session when logout has not happened — otherwise the revocation
test could pass simply because a request carrying no cookie fails too.

The transferable lesson: **assert on the thing you actually care about, from outside the thing
you are testing.** The original test asserted on state the system under test controls. The
question was never "were the caller's cookies cleared" — it was "is that token dead", and only
an independent client can ask it.

> slice 2 的登出。当时有一个**通过的**测试，断言登出会清掉会话——而它**完全是因为错误的理由通过的**。
>
> bug 是三件事串起来的，**每一件单独看都没错**。刷新 cookie 的路径被限定在
> `/api/v1/auth/refresh`，所以它**根本不会随登出请求发出**。`LogoutHandler` 开头有个 null 守卫
> ——没带令牌的登出被**刻意**定义为成功而非错误——于是在没有 cookie 的情况下，
> `RevokeAllForUserAsync` 是**永远走不到的代码**。而测试断言的是**调用方自己的 cookie jar**，
> 那个登出确实清空了。结果就是：**测试绿的、cookie 肉眼可见地清了，而每个刷新令牌在登出之后
> 仍然完整有效 14 天。** 在共用电脑上登出，会话其实还活着。
>
> 修法是两行行为改动 + 一个好得多的测试：把 cookie 路径放宽到 `/api/v1/auth`（见 Q7.5），
> 然后用一个**在登出前捕获、再由干净客户端重放**的令牌来断言——那个客户端不持有任何来自刚被清空的
> jar 的东西。另外补一个**对照测试**，证明同样的重放在**没有登出**时确实能换到会话——
> 否则那个撤销测试可能仅仅因为"不带 cookie 的请求本来也会失败"而通过。
>
> 可迁移的教训：**要在被测系统之外，去断言你真正在意的那件事。**
> 原来那个测试断言的是**被测系统自己控制的状态**。问题从来不是"调用方的 cookie 清了没"，
> 而是"**那个令牌死了没**"——**而这个问题只有一个独立的客户端能问出口。**

#### Q15.3 — Give me an example of pushing back on feedback.

**中文** — 举一个你反驳评审意见的例子。

**Tests / 考察点:** Code-review judgement — verify claims, principled pushback (behavioural) · 评审判断：核实主张、有原则的反驳（行为面试）

**A.** A reviewer flagged that an implementation report "misattributed decisions to the
brief" — two package-version resolutions that appeared unauthorised. I checked the source
rather than complying: both had been explicitly authorised in the dispatch prompt, under an
"Expected friction" heading the reviewer could not see. I ruled it a false positive, recorded
*why* in the ledger, and made no code change.

The reverse also happened. Two findings were plan-mandated fragilities in the streaming
hooks — the code matched the written plan exactly, so "the plan says so" was available as a
defence. I fixed them anyway and recorded the deliberate deviation, because the plan being
wrong is not a reason to ship the wrong thing.

Both are the same principle: verify the claim, then decide on the merits. Neither
performative agreement nor reflexive defence.

**答.** 有一次评审者指出某份实现报告"把决定错误地归给了任务简报"——两处包版本的处理看起来未经授权。
我**没有照单全收，而是去查了源头**：两处都在派发提示词里一个叫"预期摩擦"的小节中被**明确授权**过，
而评审者看不到那部分。我判定这是**误报**，把**理由**记进台账，代码一行没改。

反过来的情况也发生了。有两处发现是**计划本身规定的脆弱点**——代码和书面计划**完全一致**，
所以"计划就是这么写的"是可以拿来当挡箭牌的。**我还是修了**，并记录下这是**刻意偏离计划**，
因为**计划错了，不构成交付错误东西的理由**。

两件事是同一个原则：**先核实主张，再就事论事地决定。既不表演性地同意，也不条件反射地防御。**

#### Q15.4 — How do you decide when to add a dependency?

**中文** — 你怎么决定要不要加一个依赖？

**Tests / 考察点:** Dependency & risk management — licences, version pinning · 依赖与风险管理：许可证、版本钉死

**A.** Every dependency must earn its place at more than one call site, and the licence gets
checked. Recorded in [ADR-002](docs/adr/002-walking-skeleton-first.md):

- **MediatR pinned to 12.x** — the last MIT-licensed major version; 13.x moved to a
  commercial licence. A transitive licence change is a legal problem, not a technical one,
  and it belongs in an ADR.
- **FluentValidation, zod, TanStack Query** each used at multiple call sites.
- `Microsoft.OpenApi` pinned to 2.7.5 because an earlier version carried a known
  vulnerability (NU1903) that `TreatWarningsAsErrors` escalated into a build failure.

That last one is worth its own note: `TreatWarningsAsErrors` is on solution-wide
([Directory.Build.props](Directory.Build.props)), and central package management
([Directory.Packages.props](Directory.Packages.props)) pins every version in one file. Both
are choices to make problems loud and early rather than quiet and late.

**答.** **每个依赖必须在多于一个调用点上挣到自己的位置**，而且许可证要查。写在 ADR-002 里：

- **MediatR 钉在 12.x**——那是最后一个 MIT 许可的大版本；13.x 转成了商业许可。
  **传递性的许可证变更是法律问题，不是技术问题**，它应该进 ADR。
- **FluentValidation、zod、TanStack Query** 各自都在多个调用点被用到。
- `Microsoft.OpenApi` 钉在 2.7.5，因为更早的版本带一个已知漏洞（NU1903），
  而 `TreatWarningsAsErrors` 把它升级成了构建失败。

最后这条值得单独说：`TreatWarningsAsErrors` 是**全解决方案开启**的，中央包管理把每个版本
**钉在同一个文件里**。这两个都是同一种选择：**让问题吵闹地早点暴露，而不是安静地晚点爆炸。**

#### Q15.5 — What would you do differently next time?

**中文** — 下次你会怎么做得不一样？

**Tests / 考察点:** Retrospection & engineering judgement (behavioural) · 复盘与工程判断（行为面试）

**A.** Four things, in order of how much they cost:

1. **Verify from a clean clone earlier.** The two Critical defects in Q15.2 were both
   "works on my machine" bugs. A scripted first-run check on day one would have caught both
   for a fraction of the cost of finding them at merge time.
2. **Write the security test from the attacker's side first.** The logout bug in Q15.2 existed
   because the test asked "did *we* clear the cookies" instead of "is that token *dead*". For
   anything revocation-shaped, the assertion has to come from a client that holds nothing the
   system under test controls. I now treat that as the default shape for a security test, not
   a refinement of one.
3. **Design the concurrency story with the schema, not after it.** The capacity race in
   Q8.2 is trivial to prevent with a `rowversion` column at migration time and awkward to
   retrofit once data exists. Slice 2 added two tables and repeated the omission.
4. **Not doing database I/O inside a validator** (Q13.2). It reads as convenient and it
   couples two layers that were otherwise cleanly separated. Notably, slice 2's auth
   validators are shape-only and push existence checks (`EmailExistsAsync`) into the handler
   — so the better pattern is already in the codebase, sitting next to the worse one.

None of those are exotic. All four are the kind of thing that is obvious in review and
invisible while typing — which is the argument for review.

**答.** 四件事，按代价排序：

1. **更早地从干净克隆做验证。** Q15.2 里那两个 Critical 都是"在我机器上能跑"型 bug。
   **第一天就写一个首次运行检查脚本**，能以合并时才发现的一小部分成本把它们都抓到。
2. **安全测试要先从攻击者那一侧写。** Q15.2 里那个登出 bug 之所以存在，是因为测试问的是
   "**我们**把 cookie 清了吗"，而不是"那个令牌**死了吗**"。凡是**撤销**形状的东西，
   断言都必须来自一个**不持有被测系统所控制的任何东西**的客户端。
   我现在把这当成安全测试的**默认形状**，而不是对它的一次改良。
3. **并发方案要和 schema 一起设计，而不是事后补。** Q8.2 里的容量竞争，在做迁移时加一列
   `rowversion` 就能轻松防住；等有了数据再回填就很别扭。**slice 2 又加了两张表，又漏了同一件事。**
4. **不要在校验器里做数据库 I/O**（Q13.2）。它读起来很方便，但把两个本来干净分离的层耦合了起来。
   值得一提的是：slice 2 的认证校验器**只做形状校验**，把存在性检查（`EmailExistsAsync`）推进了
   handler——所以**更好的那个模式已经在代码库里了，就摆在更差的那个旁边。**

这四条都不新奇。**它们都属于"评审时一眼看见、敲代码时完全看不见"的那类问题——而这正是评审存在的理由。**

#### Gaps (category 15) / 类别 15 的缺口

Five of the six planned STAR stories depend on incidents that haven't happened yet — they
need later phases with real production-shaped failures. Mentoring, cross-team collaboration,
and stakeholder communication are not demonstrable from a solo codebase and have to come from
employment history.

> 计划中的六个 STAR 故事有五个依赖**还没发生的事故**——它们需要后面几个阶段里真实的、
> 生产形态的失败。带人、跨团队协作、干系人沟通**没法从一个单人代码库里展示**，
> 只能来自工作经历。

---

## Part II — Hard · code-focused deep dives / 第二部分（Hard）· 代码级深挖

Implementation questions. Each expects a detailed walk through the actual code — files,
mechanisms, and the failure modes the implementation defends against.

> 面向实现的问题。每一题都要求对真实代码做详细讲解——文件、机制、以及实现所防御的失败模式。

### 1. C# & .NET runtime internals

*C# 与 .NET 运行时底层*

#### Q1.1 — Walk me through the producer/consumer pipeline in this app.

**中文** — 讲一下这个应用里的生产者/消费者管道。

**Tests / 考察点:** Concurrency & producer/consumer design — bounded channels, backpressure policy · 并发与生产者/消费者设计：有界通道、背压策略

**A.** A price tick is produced once per second per ticker, and delivered to browsers over
SignalR. Those two concerns are decoupled by a bounded `Channel<PriceTick>`.

- Producer: [FakeTickService.cs](src/MarketPulse.Infrastructure/RealTime/FakeTickService.cs) —
  a `BackgroundService` driving a `PeriodicTimer` at 1 Hz over 25 tickers.
- Buffer: [PriceTickChannel.cs](src/MarketPulse.Infrastructure/RealTime/PriceTickChannel.cs) —
  `Channel.CreateBounded<PriceTick>(1000)`, `SingleReader = true`, `SingleWriter = false`.
- Consumer: [TickBroadcaster.cs](src/MarketPulse.Api/RealTime/TickBroadcaster.cs) —
  `await foreach (var tick in channel.Reader.ReadAllAsync(ct))`, fanning out to
  `hub.Clients.All`.

The channel is the seam that makes the tick *source* replaceable: Phase 2 swaps
`FakeTickService` for a real market feed and no consumer changes.

**答.** 每个 ticker 每秒产生一个价格 tick，通过 SignalR 推给浏览器。这两件事由一个**有界**
`Channel<PriceTick>` 解耦：生产者是 `FakeTickService`（`BackgroundService` + `PeriodicTimer`，
1 Hz，25 个 ticker）；缓冲是 `PriceTickChannel`（容量 1000）；消费者是 `TickBroadcaster`
（`await foreach` 读通道，扇出到 `hub.Clients.All`）。

这个 channel 就是让 tick **来源**可替换的接缝：Phase 2 把 `FakeTickService` 换成真实行情源，
消费端一行都不用改。

**Follow-up: why bounded, and why `DropOldest`? / 为什么用有界通道，为什么选 `DropOldest`？**

An unbounded channel converts a slow consumer into a memory leak. Bounded forces a policy
decision, and for a price ticker the correct policy is obvious: a stale price has no value,
so `BoundedChannelFullMode.DropOldest` sheds the oldest tick rather than blocking the
producer or throwing. If this were an order feed the answer would invert — you would block
or persist, because dropping an order is a correctness bug.

> 无界通道会把"消费慢"变成"内存泄漏"。有界强迫你做一个策略决定，而对价格流来说这个策略是显然的：
> 过期价格没有价值，所以用 `DropOldest` 丢最旧的，而不是阻塞生产者或抛异常。
> 如果这是**订单流**，答案就完全相反——必须阻塞或落盘，因为丢一个订单是正确性 bug。

**Follow-up: what do `SingleReader`/`SingleWriter` actually buy? / 这两个标志到底买到了什么？**

They are optimisation hints, not enforcement. `SingleReader = true` lets the channel skip
some interlocked bookkeeping on the read path. It is true here because exactly one
`BackgroundService` reads. `SingleWriter = false` because writers are not guaranteed to be
one — and getting these *wrong* is a silent data race, not an exception.

> 它们是**优化提示，不是强制约束**。`SingleReader = true` 让通道在读路径上省掉一些 interlocked
> 记账开销——这里成立，因为确实只有一个 `BackgroundService` 在读。`SingleWriter = false` 是因为
> 写方不保证只有一个。填错了的后果是**静默的数据竞争，不是抛异常**，这是最需要强调的一点。

#### Q1.3 — `record`, `readonly record struct`, `sealed class` — you use all three. Why each?

**中文** — 这三种类型你都用了，各自为什么这么选？

**Tests / 考察点:** C# type design & memory — value vs reference semantics, immutability, allocation · C# 类型设计与内存：值/引用语义、不可变性、分配

**A.** The choice is about identity, mutability, and allocation:

- [`PriceTick`](src/MarketPulse.Domain/ValueObjects/PriceTick.cs) is a
  `readonly record struct`. It is a small, immutable value with no identity, created ~25
  times a second and immediately consumed. A struct keeps it off the heap; `readonly`
  guarantees no defensive copies; `record` gives structural equality for free.
- [`Watchlist`](src/MarketPulse.Domain/Entities/Watchlist.cs) is a `sealed class` with a
  private constructor. It is an entity — it has identity (`Id`) that survives changes to
  its state — so reference semantics are correct and value equality would be *wrong*.
- Commands like [`AddWatchlistItemCommand`](src/MarketPulse.Application/Watchlists/AddWatchlistItemCommand.cs)
  are `record` classes: immutable messages where structural equality is convenient and the
  allocation is one per request, not one per tick.

`sealed` everywhere it can be is deliberate — it enables devirtualisation by the JIT and
signals "not an extension point".

**答.** 判断依据是**身份、可变性、分配**三件事：

- `PriceTick` 用 `readonly record struct`：小的、不可变的、**没有身份**的值，每秒创建约 25 个
  且立刻被消费。struct 让它不上堆；`readonly` 保证没有防御性拷贝；`record` 白送结构化相等。
- `Watchlist` 用 `sealed class` + 私有构造：它是**实体**，有跨状态变化仍然存续的身份（`Id`），
  所以引用语义才是对的，值相等在这里反而**是错的**。
- 命令对象用 `record` class：不可变消息，结构化相等好用，而且分配是每请求一个，不是每 tick 一个。

能 `sealed` 的地方全部 `sealed`：既让 JIT 能去虚化，也是在表态"这里不是扩展点"。

**Follow-up: when would a struct be the wrong call for `PriceTick`? / 什么时候 struct 就错了？**

If it grew past ~16 bytes of payload and were passed around a lot by value, or if it were
ever boxed into an `object`/interface on a hot path. It is 3 fields today; if it grew to
carry bid/ask/volume/exchange I would re-measure before keeping it a struct.

> 如果它超过约 16 字节还到处按值传，或者在热路径上被装箱成 `object`/接口。现在是 3 个字段；
> 如果以后要带 bid/ask/volume/exchange，我会**重新测**再决定要不要继续当 struct。

#### Q1.4 — Where do `CancellationToken`s flow, and what happens on shutdown?

**中文** — `CancellationToken` 贯穿到哪里？关闭时发生什么？

**Tests / 考察点:** Async cancellation & graceful shutdown · 异步取消与优雅停机

**A.** Every async path takes one, threaded end to end: HTTP request →
[controller](src/MarketPulse.Api/Controllers/WatchlistController.cs) → MediatR handler →
`IWatchlistRepository` → `SaveChangesAsync(ct)`. On the streaming side, the host's stopping
token flows into `PeriodicTimer.WaitForNextTickAsync` and `Reader.ReadAllAsync`.

Both hosted services catch `OperationCanceledException` and log an orderly stop rather than
letting a cancellation surface as a crash —
[FakeTickService.cs:41](src/MarketPulse.Infrastructure/RealTime/FakeTickService.cs#L41),
[TickBroadcaster.cs:26](src/MarketPulse.Api/RealTime/TickBroadcaster.cs#L26). That is the
distinction worth stating: cancellation is expected control flow, not an error.

**答.** 每条异步路径都带，端到端串起来：HTTP 请求 → 控制器 → MediatR handler → 仓储 →
`SaveChangesAsync(ct)`。流那一侧，宿主的 stopping token 流进 `WaitForNextTickAsync` 和
`ReadAllAsync`。

两个 hosted service 都捕获 `OperationCanceledException` 并记一条有序停止的日志，而不是让取消
变成崩溃。这里值得说透的一句话是：**取消是预期内的控制流，不是错误**。

#### Q1.5 — There's a subtle decimal/rounding bug class in `RandomWalk`. Talk me through it.

**中文** — `RandomWalk` 里有一类很隐蔽的 decimal/取整 bug，讲讲。

**Tests / 考察点:** Numeric correctness — decimal precision, rounding, invariant testing · 数值正确性：decimal 精度、取整、不变量测试

**A.** [RandomWalk.Next](src/MarketPulse.Infrastructure/RealTime/RandomWalk.cs) must move a
price by at most ±1%, rounded to cents. The naive implementation rounds to the nearest cent
*and then* clamps — but rounding-to-nearest can push the clamped value **back outside** the
bound it was just clamped to. The fix is direction-aware: `Math.Truncate` on the upper
clamp, `Math.Ceiling` on the lower, so the clamp can only ever move the value *inward*.

`decimal` (not `double`) throughout, because these are money values and base-2 floating
point cannot represent 0.01 exactly.

**答.** `RandomWalk.Next` 要让价格最多波动 ±1%，并取整到分。天真的写法是先四舍五入到分、**再**做
上下界钳制——但四舍五入会把刚刚钳进去的值又**推回界外**。修法是让钳制**带方向**：上界用
`Math.Truncate`，下界用 `Math.Ceiling`，这样钳制只可能把值往**内**推。

全程用 `decimal` 而不是 `double`，因为这是钱，二进制浮点表示不了精确的 0.01。

**Known limitation, stated honestly / 已知局限（要主动讲）:** below roughly $1, cent-rounding
absorbs the entire ±1% drift and the price freezes. No reference ticker is near that today
(cheapest is $11.85). The correct fix is to walk in integer cents rather than decimal dollars.

> 价格低于约 $1 时，分位取整会把整个 ±1% 的漂移吃掉，价格就冻住了。目前没有参考 ticker 接近这个
> 区间（最便宜的是 $11.85）。正确修法是**按整数分**游走，而不是按 decimal 元。

**Follow-up: how did you find it? / 怎么发现的？**

Seeded invariant tests that iterate rather than assert a single case — 1000 chained walks
asserting the ±1% band holds at every step, 500 walks from $0.02 asserting positivity
([RandomWalkTests.cs](tests/MarketPulse.UnitTests/RealTime/RandomWalkTests.cs)) — plus a
brute-force sweep across ~50k prices × thousands of drifts during review.

> 用**固定种子的不变量测试**，而不是断言单个用例：1000 次链式游走，每一步都断言 ±1% 区间成立；
> 从 $0.02 起 500 次游走断言恒为正。评审阶段另外做了约 5 万个价格 × 数千种漂移的暴力扫描。

#### Gaps (category 1) / 类别 1 的缺口

`Span<T>` / allocation-free parsing, BenchmarkDotNet numbers, a GC/Gen0–Gen2 investigation,
a LINQ-vs-loop benchmark, and a delegate/event-based bus. Nothing here is hot enough yet to
justify them — which is itself the answer: *measure before optimising*.

> 欠：`Span<T>` / 零分配解析、BenchmarkDotNet 实测数据、GC 分代调查、LINQ vs 循环的基准、
> 委托/事件总线。目前没有任何一段代码热到需要它们——**这本身就是答案：先测量，再优化。**

### 2. JavaScript fundamentals & language internals

*JavaScript 语言底层*

#### Q2.1 — Explain the reconnect logic you wrote, and why the library default wasn't enough.

**中文** — 讲讲你写的重连逻辑，以及为什么库的默认行为不够用。

**Tests / 考察点:** Async programming & resilience — retry policies, reconnection design · 异步编程与弹性：重试策略、重连设计

**A.** `withAutomaticReconnect([0, 2000, 5000, 10000, 30000])` retries five times and then
**gives up permanently**, leaving the UI stuck showing "Reconnecting…" forever after ~47
seconds. For a dashboard someone leaves open on a second monitor, that is the wrong
behaviour: the common case is a laptop that slept, or an API being redeployed.

So [usePriceStream.ts](apps/dashboard/src/features/prices/usePriceStream.ts) implements
`IRetryPolicy` directly: ramp through the array for the first attempts, then retry forever
at the final capped delay of 30s.

```ts
nextRetryDelayInMilliseconds(retryContext) {
  const index = retryContext.previousRetryCount;
  return index < RECONNECT_DELAYS_MS.length
    ? (RECONNECT_DELAYS_MS[index] ?? FINAL_RECONNECT_DELAY_MS)
    : FINAL_RECONNECT_DELAY_MS;
}
```

**答.** `withAutomaticReconnect([...])` 传数组时只重试五次，然后**永久放弃**——约 47 秒后 UI 就
永远卡在"Reconnecting…"。对一个被人开在副屏上的看板来说这是错的行为：最常见的情况恰恰是笔记本
休眠过、或者 API 正在重新部署。

所以我直接实现了 `IRetryPolicy`：前几次按数组爬坡，之后**以 30 秒为上限无限重试**。

**Follow-up: 30 seconds fixed — no jitter? / 固定 30 秒，不加抖动？**

Correct, and that is a real weakness at scale. With many clients, a synchronised 30s retry
is a thundering herd against an API that just came back up. Production wants
`delay * (0.5 + Math.random())`. At one user it is invisible; I would add it before this
served real traffic.

> 对，这在规模上是真实缺陷。客户端一多，同步的 30 秒重试就是对刚恢复的 API 的一次**惊群**。
> 生产上应该是 `delay * (0.5 + Math.random())`。单用户看不出来；上真实流量前我会先补上。

#### Q2.2 — Where are closures load-bearing in this code?

**中文** — 这份代码里，闭包在哪些地方是"承重"的？

**Tests / 考察点:** JavaScript closures & effect lifecycle — resource cleanup · JS 闭包与 effect 生命周期：资源清理

**A.** [useNow.ts](apps/dashboard/src/features/prices/useNow.ts) — the `setInterval`
callback closes over `setNow`, and the effect's cleanup closes over the interval `id`. Get
the dependency array wrong and you leak an interval per render. The cleanup returning
`clearInterval(id)` is the closure doing its job.

The same pattern in [usePriceStream.ts](apps/dashboard/src/features/prices/usePriceStream.ts):
the teardown closes over `connection` and only calls `stop()` when the state is not already
`Disconnected` — calling `stop()` on an already-stopped connection is a needless promise
rejection.

**答.** `useNow.ts`：`setInterval` 的回调闭包捕获 `setNow`，effect 的清理函数闭包捕获定时器 `id`。
依赖数组写错，就会**每次渲染泄漏一个定时器**；清理函数里的 `clearInterval(id)` 就是闭包在干活。

`usePriceStream.ts` 同一个模式：拆卸函数闭包捕获 `connection`，并且只在状态不是 `Disconnected`
时才 `stop()`——对一个已经停掉的连接再 `stop()` 会白白产生一次 promise rejection。

#### Q2.5 — Show me the single most load-bearing closure in the codebase.

**中文** — 给我看整个代码库里最"承重"的那个闭包。

**Tests / 考察点:** Async concurrency control — promise memoisation, the single-flight pattern · 异步并发控制：promise 记忆化、单飞模式

**A.** Single-flight refresh in
[client.ts](packages/api-client/src/client.ts). The access token expires every 15 minutes,
so the steady state is that *several* in-flight requests get a 401 at the same moment. Each
one naively retrying would fire its own `POST /auth/refresh` — and because every refresh
**rotates** the token and revokes its predecessor (Q7.5), the second concurrent refresh would
present a token the first one just revoked, trip reuse detection, and **log the user out**.
The obvious retry loop doesn't just waste a request; it destroys the session.

The fix is one closed-over variable:

```ts
let refreshInFlight: Promise<boolean> | null = null;

function refreshOnce(): Promise<boolean> {
  refreshInFlight ??= fetch(`${root}/api/v1/auth/refresh`, { … })
    .then((response) => response.ok)
    .catch(() => false)
    .finally(() => { refreshInFlight = null; });

  return refreshInFlight;
}
```

Three details carry the weight. `??=` means the *first* caller creates the promise and the
next nine get the **same** one, so nine callers await a result they did not initiate.
`.finally` clears the slot so the next expiry gets a fresh attempt rather than a cached
stale `false`. And the retry passes `allowRefresh = false`, so a request can retry at most
once — a refresh that fails cannot recurse.

**答.** `client.ts` 里的**单飞刷新**。access token 每 15 分钟过期一次，所以稳态就是**好几个**在途
请求在同一时刻拿到 401。如果各自天真地重试，就会各自发一个 `POST /auth/refresh`——而由于每次刷新都
**轮换**令牌并撤销前一个（见 Q7.5），第二个并发刷新会拿着刚被第一个撤销的令牌去换，**触发重用检测，
把用户直接踢下线**。所以那个显而易见的重试循环不只是浪费一个请求，**它会摧毁会话**。

修法就是一个被闭包捕获的变量：`??=` 让**第一个**调用者创建 promise，后面九个拿到的是**同一个**，
于是九个调用者在 await 一个不是自己发起的结果；`.finally` 把槽位清空，让下一次过期能重新尝试，
而不是拿到一个缓存下来的陈旧 `false`；重试时传 `allowRefresh = false`，所以一个请求最多只重试一次
——**刷新失败不可能递归**。

**Follow-up: how is that tested without timing luck? / 这个怎么在不靠运气的前提下测？**

[client.test.ts](packages/api-client/src/client.test.ts) fires concurrent requests through
MSW against a handler that counts refresh hits, and asserts the counter reads **1**. Two more
tests pin the boundaries: a request gives up after one failed refresh rather than looping, and
a 401 from *login* never triggers a refresh at all — wrong credentials are not an expired
session.

> `client.test.ts` 通过 MSW 并发发请求，打到一个**给刷新次数计数**的 handler 上，断言计数器读到
> **1**。另外两个测试钉住边界：刷新失败后请求**放弃而不是循环**；以及**登录**返回的 401 **绝不**
> 触发刷新——凭据错误不是会话过期。

#### Gaps (category 2) / 类别 2 的缺口

`AbortController` is threaded through the API client but only exercised on the GET path via
TanStack Query's `signal`. No `Promise.allSettled` batching, no prototype-chain work, and
the standalone ES-module emitter package is not built.

> `AbortController` 虽然在 API 客户端里贯通了，但只有 GET 路径通过 TanStack Query 的 `signal`
> 真正跑到。没有 `Promise.allSettled` 批处理、没有原型链相关的东西、独立的 ES module emitter
> 包还没建。

### 3. TypeScript

*TypeScript*

#### Q3.2 — Show me a discriminated union you narrowed properly.

**中文** — 给我看一个你正确收窄了的可辨识联合。

**Tests / 考察点:** TypeScript narrowing — discriminated unions, exhaustiveness checking · TS 类型收窄：可辨识联合、穷尽性检查

**A.** `StreamAction` in
[streamReducer.ts](apps/dashboard/src/features/prices/streamReducer.ts) is discriminated on
`type`, and the reducer's `default` branch is:

```ts
default: {
  const exhaustive: never = action;
  return exhaustive;
}
```

If someone adds a fourth action variant and forgets a case, that assignment fails to compile.
The compiler enforces exhaustiveness — no runtime test required, and no silent fall-through.

**答.** `StreamAction` 以 `type` 作为判别式，reducer 的 `default` 分支把 `action` 赋给 `never`。
如果有人加了第四种 action 却忘了写 case，**这行赋值编译不过**。穷尽性由编译器强制，不需要运行时
测试，也不会有静默的 fall-through。

#### Q3.3 — What does `noUncheckedIndexedAccess` change, and where did it bite?

**中文** — `noUncheckedIndexedAccess` 改变了什么？在哪里咬到你了？

**Tests / 考察点:** TypeScript strictness — null safety, empty-state design · TS 严格模式：空值安全、空状态设计

**A.** It makes `arr[i]` yield `T | undefined` instead of `T`, which is the truth. Both
tsconfigs enable it
([apps/dashboard/tsconfig.json](apps/dashboard/tsconfig.json),
[packages/api-client/tsconfig.json](packages/api-client/tsconfig.json)).

It shows up in two places: `RECONNECT_DELAYS_MS[index] ?? FINAL_RECONNECT_DELAY_MS` in
usePriceStream, and `stream.prices[item.ticker]?.price` in
[WatchlistScreen.tsx](apps/dashboard/src/features/watchlist/WatchlistScreen.tsx) — where
`undefined` is a genuine state (a ticker on the watchlist that has not received its first
tick yet), rendered as `—` by
[PriceCell](apps/dashboard/src/features/prices/PriceCell.tsx). The flag turned an invisible
`undefined.toFixed()` crash into a compile-time prompt to design the empty state.

**答.** 它让 `arr[i]` 的类型变成 `T | undefined`——**而这才是事实**。两个 tsconfig 都开了。

具体咬到两处：`usePriceStream` 里的 `RECONNECT_DELAYS_MS[index] ?? FINAL_RECONNECT_DELAY_MS`，
以及 `WatchlistScreen.tsx` 里的 `stream.prices[item.ticker]?.price`——后者的 `undefined` 是**真实
状态**（watchlist 上有这个 ticker，但还没收到第一个 tick），由 `PriceCell` 渲染成 `—`。
这个开关把一次看不见的 `undefined.toFixed()` 崩溃，变成了编译期"请设计一下空状态"的提示。

#### Gaps (category 3) / 类别 3 的缺口

DTOs are hand-written rather than generated from the .NET OpenAPI document — the drift risk
is caught at runtime by zod, but generation would catch it at build time. No `Result<T, E>`
type; errors use exceptions (`ApiError`). The message union is only `Tick`; `AlertTriggered`
and `OrderEvent` arrive with later slices.

> DTO 是手写的，不是从 .NET 的 OpenAPI 文档生成的——漂移风险由 zod 在运行时兜住，但生成能在
> **构建时**就兜住。没有 `Result<T, E>`，错误走异常（`ApiError`）。消息联合目前只有 `Tick`，
> `AlertTriggered` 和 `OrderEvent` 要等后面的 slice。

**Correction worth making out loud.** The approved design specified a hand-written session
union (`loading | authenticated | anonymous`). The implementation does **not** have one:
[useSession.ts](apps/dashboard/src/features/auth/useSession.ts) is a plain TanStack Query
call, and `ProtectedRoute` narrows on `isPending` / `isError`. That is TanStack Query's own
discriminated union rather than mine — the narrowing discipline is genuinely there, but I
didn't write it. Writing a parallel union over the top of a library that already exposes one
would have been duplicated state. Claim the outcome, not authorship of the union.

> **这里有一处要主动说清的更正。** 批准的设计里写的是**手写**的会话可辨识联合
> （`loading | authenticated | anonymous`）。实现里**没有**：`useSession.ts` 就是一个普通的
> TanStack Query 调用，`ProtectedRoute` 靠 `isPending` / `isError` 收窄。那是 **TanStack Query
> 自带的**可辨识联合，不是我写的——收窄纪律确实在，但作者不是我。在一个已经暴露了联合类型的库上
> 再套一层平行的联合，只会造成**状态重复**。**要认领结果，但不要认领那个联合的作者身份。**

### 4. ASP.NET Core framework depth

*ASP.NET Core 框架深度*

#### Q4.1 — Walk me through the middleware pipeline, in order, and defend the order.

**中文** — 按顺序讲一遍中间件管道，并为这个顺序辩护。

**Tests / 考察点:** Middleware architecture — ordering, cross-cutting concerns, cheap-rejection security · 中间件架构：顺序、横切关注点、低成本拒绝

**A.** From [Program.cs](src/MarketPulse.Api/Program.cs):

```
CorrelationId → ExceptionHandling → CORS → RateLimiter → Csrf → Authentication → Authorization → MapControllers / MapHub
```

- **Correlation ID first**, because everything downstream — including the exception
  handler — needs to *stamp* it. It reads an inbound `X-Correlation-Id` or mints one,
  stores it in `HttpContext.Items`, and echoes it on the response
  ([CorrelationIdMiddleware.cs](src/MarketPulse.Api/Middleware/CorrelationIdMiddleware.cs)).
- **Exception handling second**, so it wraps everything after it. It sits *inside* the
  correlation middleware precisely so the ProblemDetails body can carry
  `correlationId` — the user sees a reference, the logs carry the same one
  ([ExceptionHandlingMiddleware.cs](src/MarketPulse.Api/Middleware/ExceptionHandlingMiddleware.cs)).
- **CORS before anything that can reject**, or preflight `OPTIONS` never gets its headers —
  and a CORS failure in a browser reports as an opaque network error with no status code,
  which is the worst thing to debug.
- **Rate limiter before CSRF and auth**, deliberately. A brute-force attempt should be
  rejected on the *cheapest* possible path: rejecting at the limiter costs no database read
  and no PBKDF2 verification. Put it after authentication and the attacker gets you to do
  100,000 hash iterations before you turn them away.
- **CSRF before authentication**, because the check is a stateless cookie/header comparison
  that needs no principal. It throws `CsrfValidationException`, which the exception
  middleware above it renders into the same ProblemDetails shape as everything else
  ([CsrfMiddleware.cs](src/MarketPulse.Api/Middleware/CsrfMiddleware.cs)).
- **Authentication then authorization**, in that order and never reversed — authorization
  evaluates a principal that authentication has to have already established.

**答.**

- **相关性 ID 放第一个**，因为后面所有东西——包括异常处理器——都要**盖这个戳**。
- **异常处理放第二个**，这样它能包住它后面的一切；而它又**在**相关性中间件**里面**，正是为了让
  ProblemDetails 响应体能带上 `correlationId`——用户看到一个编号，日志里是同一个。
- **CORS 放在任何会拒绝请求的东西之前**，否则预检 `OPTIONS` 拿不到头——而浏览器里的 CORS 失败
  报出来是一个**没有状态码的不透明网络错误**，最难调。
- **限流器放在 CSRF 和认证之前**，这是刻意的。暴力破解尝试应该在**最便宜的路径**上被拒掉：
  在限流器这里拒绝，不用读数据库、不用跑 PBKDF2 验证。**放到认证之后，攻击者就能让你先做十万次
  哈希迭代再把他打发走。**
- **CSRF 放在认证之前**，因为这个检查是**无状态的 cookie/header 比对**，根本不需要 principal。
  它抛 `CsrfValidationException`，由上面的异常中间件渲染成和其它错误一样的 ProblemDetails。
- **先认证再授权**，顺序绝不能反——授权评估的 principal 必须由认证先建立起来。

**Follow-up: what breaks if exception handling goes first? / 如果异常处理放最前面会怎样？**

The 500 response would still be produced, but with no correlation ID — the one field that
makes a production incident traceable.

> 500 响应照样会产生，但**没有相关性 ID**——而那恰恰是让线上事故可追踪的唯一字段。

**Follow-up: slice 1 had a `DevAuthMiddleware` here. Where did it go? / slice 1 那个 `DevAuthMiddleware` 呢？**

Deleted in slice 2 — it was a stub that stamped a fixed user id onto every request, fenced
twice (registered only under `IsDevelopment()`, and throwing if it ever executed outside
Development). Its replacement is `AddAuthentication().AddJwtBearer(...)` with an
`OnMessageReceived` event that pulls the token out of the `mp_access` cookie instead of the
`Authorization` header. **Nothing below the API layer changed** — see Q10.3.

> slice 2 里**删掉了**。它原本是一个往每个请求上盖固定用户 id 的桩件，被两道栅栏围着（只在
> `IsDevelopment()` 下注册；而且一旦在非 Development 下执行就抛异常）。替代它的是
> `AddAuthentication().AddJwtBearer(...)`，用 `OnMessageReceived` 事件从 `mp_access` cookie
> 里取 token，而不是从 `Authorization` 头。**API 层以下一行没改**——见 Q10.3。

#### Q4.2 — `HasStarted` in the exception handler. What is that guard, and what's wrong with it?

**中文** — 异常处理器里的 `HasStarted` 是什么守卫？它有什么问题？

**Tests / 考察点:** HTTP response lifecycle — defensive middleware, refactoring judgement · HTTP 响应生命周期：防御式中间件、重构判断

**A.** Once the first byte of a response is on the wire, the status code and headers are
immutable — writing a ProblemDetails body over a partially-sent response corrupts it. So the
handler checks `context.Response.HasStarted` and bails.

Slice 2 refactored it: the guard used to be repeated per catch-branch, and now lives once at
the top of a single private `WriteAsync(context, status, errorCode, detail)` helper that every
branch funnels through
([ExceptionHandlingMiddleware.cs](src/MarketPulse.Api/Middleware/ExceptionHandlingMiddleware.cs)).
That is what made adding three new mappings — `UnauthorizedAccessException` → 401, and
`DomainException.StatusCode` now driving 401/403/409/429 instead of a hardcoded 409 — a
one-line change each rather than a copy-paste of the guard.

**The flaw, stated plainly:** the bail is still *silent* — no log. A domain error raised after
a response has started vanishes entirely. It is unreachable today, because every response is a
single-shot JSON write, but it is a genuine latent fragility and the first thing I would fix
when streaming responses arrive. The same guard, with the same silence, is duplicated in the
JWT bearer `OnChallenge` handler in [Program.cs](src/MarketPulse.Api/Program.cs) — that
duplication is itself a smell I would collapse.

**答.** 响应的第一个字节一旦上线，状态码和响应头就不可变了——往一个已经发出一半的响应上再写
ProblemDetails 会把它写坏。所以处理器先检查 `Response.HasStarted` 然后直接返回。

slice 2 把它重构了：这个守卫原本在**每个 catch 分支里各写一遍**，现在**只写一次**，放在所有分支共同
汇入的私有 `WriteAsync(...)` 辅助方法顶部。正因为如此，新增三条映射——
`UnauthorizedAccessException` → 401，以及让 `DomainException.StatusCode` 去驱动
401/403/409/429（而不是写死 409）——**每条都只是一行改动**，而不是把守卫复制粘贴一遍。

**缺陷要直说：** 这个提前返回**仍然是静默的——不记日志**。发生在"响应已开始"之后的领域错误会
**彻底消失**。今天这条路走不到，因为所有响应都是一次性 JSON 写入；但这是真实存在的潜伏脆弱点，
等引入流式响应时我第一个修它。而且**同一个守卫、同样的静默，在 `Program.cs` 的 JWT bearer
`OnChallenge` 处理器里又重复了一遍**——这份重复本身就是个应该被收拢掉的坏味道。

#### Q4.3 — Explain your DI lifetimes. Where's the captive-dependency risk?

**中文** — 讲讲你的 DI 生命周期。俘获依赖（captive dependency）的风险在哪？

**Tests / 考察点:** Dependency injection — lifetimes, captive dependencies · 依赖注入：生命周期、俘获依赖

**A.** Three lifetimes, chosen by state ownership
([Program.cs](src/MarketPulse.Api/Program.cs),
[Infrastructure/DependencyInjection.cs](src/MarketPulse.Infrastructure/DependencyInjection.cs)):

| Service | Lifetime | Why / 原因 |
|---|---|---|
| `MarketPulseDbContext` | Scoped | Change tracker is per-request state / 变更跟踪器是每请求状态，跨请求共享是正确性 bug |
| `IWatchlistRepository`, `IUserRepository`, `IRefreshTokenRepository` | Scoped | Follow the DbContext they wrap / 跟随它们包装的 DbContext |
| `ICurrentUser` | Scoped | Reads `HttpContext` / 读 `HttpContext`，按定义就是每请求 |
| `IPasswordHasher`, `ITokenService` | Singleton | Stateless; both hold only immutable config / 无状态，只持有不可变配置 |
| `PriceTickChannel` | Singleton | It *is* the shared buffer / 它**就是**那个共享缓冲区 |
| `FakeTickService`, `TickBroadcaster` | Hosted (singleton) | Application-lifetime background work / 应用生命周期的后台任务 |

The captive-dependency trap is right there in the last two rows: those hosted services are
effectively singletons, so if either ever needed the `DbContext` it could **not** take it in
the constructor — it would have to inject `IServiceScopeFactory` and create a scope per unit
of work. Today neither touches the database, which is why the design holds.

The two singletons added in slice 2 are worth defending individually, because "auth service"
sounds like it should be scoped. `PasswordHasherAdapter` wraps a `PasswordHasher<User>` that
salts randomly per call and keeps no state between calls; `JwtTokenService` holds an
`IOptions<JwtOptions>` snapshot and nothing else. Neither reads `HttpContext`, neither touches
the `DbContext` — so scoping them would allocate one per request for no reason. **The
lifetime follows the state, not the layer name.**

**答.** 三种生命周期，按**谁拥有状态**来选（见上表）。

俘获依赖的陷阱就在最后两行：那两个 hosted service 实际上是单例，所以一旦它们中的任何一个需要
`DbContext`，就**不能**在构造函数里拿——必须注入 `IServiceScopeFactory`，每个工作单元创建一个作用域。
现在两个都不碰数据库，所以这个设计成立。

slice 2 加的那两个单例值得单独辩护，因为"认证服务"听起来像是该 scoped 的。
`PasswordHasherAdapter` 包着的 `PasswordHasher<User>` **每次调用都随机加盐、调用之间不留状态**；
`JwtTokenService` 只持有一份 `IOptions<JwtOptions>` 快照。两者都不读 `HttpContext`、都不碰
`DbContext`——scoped 化只会毫无理由地每请求分配一个。**生命周期跟的是状态，不是层的名字。**

#### Q4.4 — `Program.cs` ends with `public partial class Program;`. Why?

**中文** — `Program.cs` 结尾那行 `public partial class Program;` 是干什么的？

**Tests / 考察点:** .NET testing infrastructure — WebApplicationFactory internals · .NET 测试基础设施：WebApplicationFactory 细节

**A.** So `WebApplicationFactory<Program>` in the integration tests can reference the
implicitly-generated entry-point class, which is otherwise internal. That one line is what
makes [WatchlistApiTests](tests/MarketPulse.IntegrationTests/WatchlistApiTests.cs) able to
boot the *real* pipeline — real middleware, real DI, real routing — instead of a mock of it.

**答.** 为了让集成测试里的 `WebApplicationFactory<Program>` 能引用到那个隐式生成的入口类——它默认
是 internal 的。就这一行，让 `WatchlistApiTests` 能启动**真实的管道**：真中间件、真 DI、真路由，
而不是它的 mock。

#### Q4.6 — You use the options pattern in one place and raw config in another. Why the split?

**中文** — 你一处用 options 模式，另一处裸读配置。为什么不统一？

**Tests / 考察点:** Configuration & fail-fast design — options pattern, startup validation · 配置与快速失败：options 模式、启动校验

**A.** Because they fail differently, and the failure mode is what should drive the choice.

[`JwtOptions`](src/MarketPulse.Application/Configuration/JwtOptions.cs) and
[`AuthOptions`](src/MarketPulse.Application/Configuration/AuthOptions.cs) are bound with
`.ValidateDataAnnotations().ValidateOnStart()`. A signing key that is missing or shorter than
32 characters **fails the process at boot**, not at the first login — and a 20-character HMAC
key is exactly the kind of thing that works fine in dev and quietly weakens production.
`[Required, MinLength(32)]` on the property is the whole enforcement.

The connection string is still read raw, with a `?? throw`, because it is needed to build the
`DbContext` registration before any options machinery is available. That is not elegance, it
is ordering — and it fails at boot too, which is the property that actually matters.

**One deliberate exception:** the rate limiter reads `IOptionsMonitor<AuthOptions>` **per
request** rather than capturing a value at startup
([Program.cs](src/MarketPulse.Api/Program.cs)). That is not defensive coding — it is required.
Integration tests override `LoginRequestsPerMinute` through `WebApplicationFactory`
configuration, which lands *after* the top-level statements have run, so a snapshot taken at
startup would never see the override and the rate-limit test would silently exercise the
default. The comment in the source says exactly that, because it looks like an unnecessary
indirection otherwise.

**答.** 因为**它们的失败方式不同**，而失败方式才应该决定选哪个。

`JwtOptions` 和 `AuthOptions` 用 `.ValidateDataAnnotations().ValidateOnStart()` 绑定。签名密钥
缺失、或短于 32 字符，**进程在启动时就挂**，而不是等到第一次登录——而一个 20 字符的 HMAC 密钥
恰恰是那种"开发环境跑得好好的、却悄悄削弱了生产"的东西。属性上的 `[Required, MinLength(32)]`
就是全部的强制手段。

连接字符串仍然是裸读 + `?? throw`，因为它要在任何 options 机制可用之前就用来注册 `DbContext`。
这不是优雅，是**顺序**——而且它同样在启动时失败，**这才是真正要紧的性质**。

**一处刻意的例外：** 限流器是**每请求**读 `IOptionsMonitor<AuthOptions>`，而不是在启动时抓一个
快照。这不是防御式编程，是**必须**：集成测试通过 `WebApplicationFactory` 的配置覆盖
`LoginRequestsPerMinute`，而那发生在顶层语句跑完**之后**，所以启动时取的快照永远看不到这个覆盖，
限流测试就会静默地在测默认值。源码里写了这条注释，否则它看起来就是一层没必要的间接。

#### Gaps (category 4) / 类别 4 的缺口

No action filters and no Minimal APIs side by side for comparison. The options pattern is now
present but only over the two auth sections — `Cors:AllowedOrigins` and the connection string
are still read raw.

> 欠：action filter、以及 Minimal API 并排对照。options 模式现在有了，但只覆盖两个认证配置节
> ——`Cors:AllowedOrigins` 和连接字符串仍然是裸读的。

### 5. React depth

*React 深度*

#### Q5.1 — Twenty-five tickers updating every second. How do you stop the whole tree re-rendering?

**中文** — 25 个 ticker 每秒都在更新，你怎么避免整棵树重渲染？

**Tests / 考察点:** React rendering performance — memoisation, state placement, reconciliation · React 渲染性能：memo、状态放置、协调

**A.** Two-layer split. Server state lives in TanStack Query
([useWatchlist.ts](apps/dashboard/src/features/watchlist/useWatchlist.ts)); ephemeral stream
state lives in the `useReducer` inside
[usePriceStream](apps/dashboard/src/features/prices/usePriceStream.ts). They change at
completely different rates — the watchlist changes when a human types; prices change 25
times a second — so they must not share a store.

Then [PriceCell](apps/dashboard/src/features/prices/PriceCell.tsx) is wrapped in `memo` and
takes only primitives (`ticker`, `price`, `stale`, `disconnected`). When IVV ticks, only the
IVV cell's props change identity, so React bails out of re-rendering the other 24.

**答.** **两层拆分**。服务端状态放 TanStack Query；短暂的流状态放 `usePriceStream` 内部的
`useReducer`。两者**变化频率完全不同**——watchlist 是人打字时才变，价格是每秒变 25 次——所以它们
绝不能共用一个 store。

然后 `PriceCell` 用 `memo` 包起来，只接收原始值（`ticker`/`price`/`stale`/`disconnected`）。
IVV 跳动时只有 IVV 那个格子的 props 引用变了，React 会跳过另外 24 个的重渲染。

**Follow-up — the honest one: does that actually work today? / 老实说：这套现在真的生效吗？**

Partially. The parent `WatchlistScreen` still re-renders on every tick, because the reducer
state lives in it. `memo` stops the *children* from re-rendering, which is the expensive
part, but the parent's reconciliation still runs 25×/second. The real fix is a
subscription-based store (`useSyncExternalStore`, or Zustand with a per-ticker selector) so
each cell subscribes to its own ticker and the parent never re-renders at all. That is the
right next step, and I would want a profiler trace before and after to prove it.

> **只生效了一半。** 父组件 `WatchlistScreen` 仍然每个 tick 都重渲染，因为 reducer 状态就住在它
> 里面。`memo` 挡住的是**子组件**的重渲染（那是贵的部分），但父组件的协调过程仍然每秒跑 25 次。
> 真正的修法是**订阅式 store**（`useSyncExternalStore`，或 Zustand 配按 ticker 的 selector），
> 让每个格子只订阅自己那个 ticker，父组件根本不重渲染。这是下一步该做的，而且我会要**前后各一份
> profiler trace** 来证明它确实有效。

#### Gaps (category 5) / 类别 5 的缺口

No list virtualisation (25 rows does not need it; 5,000 would). No profiler-driven
before/after measurement. The subscription-store fix in Q5.1 is still designed-for, not built.
The auth screens use uncontrolled-ish local `useState` per field with no form library and no
field-level validation feedback — fine at two fields, not a pattern to scale.

> 没有列表虚拟化（25 行不需要，5000 行就需要）。没有 profiler 驱动的前后对比测量。
> Q5.1 里那个订阅式 store 的修法仍然只是设计，没建。认证表单是每个字段一个本地 `useState`，
> 没有表单库、没有字段级校验反馈——两个字段够用，但**不是能往上扩的模式**。

### 6. HTML/CSS & layout

*HTML/CSS 与布局*

#### Q6.1 — What accessibility work is actually in here?

**中文** — 这里面实际做了哪些无障碍工作？

**Tests / 考察点:** Web accessibility — semantic HTML, ARIA, testing through the a11y tree · 无障碍：语义化 HTML、ARIA、经无障碍树测试

**A.** Structure and labelling, deliberately, and nothing beyond that:

- Landmark structure — `<header>`, `<main>`, `<section aria-labelledby>` linked to a real
  `<h2>` ([App.tsx](apps/dashboard/src/App.tsx),
  [WatchlistScreen.tsx](apps/dashboard/src/features/watchlist/WatchlistScreen.tsx)).
- Every control labelled: `<label htmlFor="add-ticker">`, and
  `aria-label={`Remove ${item.ticker}`}` so "Remove" buttons are distinguishable to a screen
  reader.
- Errors as `role="alert"` (assertive — the user must know the add failed), reconnection as
  `role="status"` (polite — informational, must not interrupt).
- `aria-label={`${ticker} price`}` on each price cell.
- The auth forms follow the same rules
  ([LoginScreen.tsx](apps/dashboard/src/features/auth/LoginScreen.tsx)): every input has a
  real `<label htmlFor>`, failures render as `role="alert"`, and the password fields carry
  `autoComplete="current-password"` / `"new-password"` so password managers behave — an
  accessibility affordance people forget is one, because fighting a password manager pushes
  users toward weaker passwords.

That the tests query by `getByLabelText` and `getByRole`
([WatchlistScreen.test.tsx](apps/dashboard/src/features/watchlist/WatchlistScreen.test.tsx)),
and that the **Playwright journeys do too**
([authentication.spec.ts](tests/e2e/specs/authentication.spec.ts)), is not incidental —
testing through the accessibility tree at every level means a11y regressions break the test
suite. The E2E suite needed **no source changes** to find its selectors, because the labels
were already there.

**答.** 只做了**结构和标注**，刻意到此为止：

- 地标结构：`<header>`、`<main>`、`<section aria-labelledby>` 关联到真实的 `<h2>`。
- 每个控件都有标注：`<label htmlFor="add-ticker">`；每个"Remove"按钮带
  `aria-label="Remove IVV"`，这样屏幕阅读器能区分它们。
- 错误用 `role="alert"`（assertive——用户**必须**知道添加失败了），重连用 `role="status"`
  （polite——只是信息，不该打断）。
- 每个价格格子带 `aria-label="IVV price"`。
- 认证表单遵守同一套规则：每个输入框都有真实的 `<label htmlFor>`，失败渲染成 `role="alert"`，
  密码框带 `autoComplete="current-password"` / `"new-password"` 让密码管理器正常工作——
  **这是一个常被忘记其属于无障碍范畴的可用性设施**，因为**跟密码管理器对着干，会把用户推向更弱的
  密码**。

测试用 `getByLabelText` 和 `getByRole` 查询**不是顺手为之**，**Playwright 的旅程也是这么写的**
——**在每一层都通过无障碍树来测试**，意味着 a11y 退化会直接让测试挂掉。
E2E 套件为了找选择器**一行源码都不用改**，因为那些标注本来就在。

#### Gaps (category 6) / 类别 6 的缺口

This is the least-developed category, by design. No stylesheet at all — the app is unstyled
HTML. No responsive design, no container queries, no CSS Grid shell, no theming, no axe run,
no focus-visible states, no command palette. Everything above the landmark layer is owed.

> 这是**最欠缺的一个类别，而且是刻意的**。整个应用没有任何样式表，就是无样式的 HTML。
> 没有响应式、没有容器查询、没有 CSS Grid 外壳、没有主题、没跑过 axe、没有 focus-visible 状态、
> 没有命令面板。地标层以上的全部欠着。

### 7. Web API design

*Web API 设计*

#### Q7.1 — Describe your error contract.

**中文** — 描述一下你的错误契约。

**Tests / 考察点:** API error-contract design — RFC 7807, error taxonomy, status mapping · API 错误契约设计：RFC 7807、错误分类、状态码映射

**A.** RFC 7807 ProblemDetails, mapped from a domain error taxonomy — never from stringly-typed
guesses.

Domain exceptions carry a stable machine-readable slug **and the status they map to**
([DomainException.cs](src/MarketPulse.Domain/Exceptions/DomainException.cs)):

```csharp
public abstract class DomainException(string message) : Exception(message)
{
    public abstract string ErrorCode { get; }   // "duplicate-ticker", "invalid-credentials", …
    public virtual int StatusCode => 409;       // watchlist rule violations are conflicts
}
```

That `virtual` is slice 2's doing, and it is the more interesting half. Slice 1 hardcoded
`DomainException` → 409 in the middleware, which was fine while every domain error was a
conflict. Authentication broke that: `InvalidCredentialsException` is a **401**,
`CsrfValidationException` a **403**, `AccountLockedException` a **429**
([AuthExceptions.cs](src/MarketPulse.Domain/Exceptions/AuthExceptions.cs)). The alternative
was a growing `switch` in the middleware over concrete exception types — a second place that
has to be edited every time the domain gains an error, and one the compiler cannot check.
Putting the status on the exception keeps **one** thing to write per error, in the layer that
knows what the error means.

[ExceptionHandlingMiddleware](src/MarketPulse.Api/Middleware/ExceptionHandlingMiddleware.cs)
now maps: `DomainException` → **its own `StatusCode`**, `UnauthorizedAccessException` →
**401**, `ValidationException` → **400**, anything else → **500** with a generic detail and a
logged correlation ID. `AccountLockedException` gets one special case — a `Retry-After` header
computed from the remaining lockout, because a 429 without it tells the client to back off
without saying how far.

Every response carries `type: https://marketpulse.local/errors/{code}`, `title: {code}`, and a
`correlationId` extension, with content type `application/problem+json`.

The point of the slug: clients branch on `errorCode`, never on the human-readable message.
The message is free to change; the slug is a contract.

**答.** RFC 7807 ProblemDetails，**从一套领域错误分类映射过来**，而不是靠字符串猜。

领域异常带一个稳定的、机器可读的 slug（`ErrorCode`）**以及它对应的状态码**（`StatusCode`）。

那个 `virtual` 是 slice 2 加的，而且它是更有意思的一半。slice 1 在中间件里把 `DomainException`
写死成 409——在所有领域错误都是冲突的时候这没问题。**认证把这个前提打破了**：
`InvalidCredentialsException` 是 **401**、`CsrfValidationException` 是 **403**、
`AccountLockedException` 是 **429**。另一条路是在中间件里写一个**越长越大的、按具体异常类型分支的
`switch`**——那就多出**第二个每次领域新增错误都必须改的地方**，而且**编译器管不到它**。
把状态码放在异常上，**每个错误只需要写一处，而且写在那个知道这个错误意味着什么的层里**。

中间件现在的映射是：`DomainException` → **它自己的 `StatusCode`**、
`UnauthorizedAccessException` → **401**、`ValidationException` → **400**、其它 → **500**
（对外通用文案，日志里带相关性 ID）。`AccountLockedException` 有一处特判——按剩余锁定时间算出
`Retry-After` 响应头，因为**一个不带它的 429 等于告诉客户端"退避"却不说退多远**。

每个响应都带 `type`、`title`、`correlationId` 扩展字段，content type 是
`application/problem+json`。

slug 的意义在于：**客户端分支判断只依赖 `errorCode`，永远不依赖给人看的文案。**
文案可以随便改，slug 是契约。

**Follow-up: how do you know the content type is right? / 你怎么知道 content type 是对的？**

Because getting it wrong is easy — `WriteAsJsonAsync(problem)` silently overwrites the
content type with `application/json`. The explicit
`contentType: "application/problem+json"` overload is required, and
[WatchlistApiTests](tests/MarketPulse.IntegrationTests/WatchlistApiTests.cs) asserts the
media type, not just the status code.

> 因为**写错太容易了**——`WriteAsJsonAsync(problem)` 会静默地把 content type 覆盖成
> `application/json`。必须用显式传 `contentType: "application/problem+json"` 的那个重载。
> 集成测试断言的是**媒体类型**，不只是状态码。

#### Q7.2 — There's a validation-error-code trap you hit. What was it?

**中文** — 你踩过一个校验错误码的坑，是什么？

**Tests / 考察点:** API contract hygiene — library defaults leaking into public contracts · API 契约卫生：库默认值泄漏进公开契约

**A.** FluentValidation's default `ErrorCode` is the *validator class name* —
`NotEmptyValidator`, `MaximumLengthValidator`. The middleware puts the first error code into
the ProblemDetails `title`, so internal class names were leaking into the public API
contract as error slugs — and would silently change if I swapped a rule.

Fixed by declaring the slug explicitly on every rule
([AddWatchlistItemCommand.cs](src/MarketPulse.Application/Watchlists/AddWatchlistItemCommand.cs)):

```csharp
RuleFor(x => x.Ticker)
    .NotEmpty().WithErrorCode("invalid-ticker")
    .MaximumLength(8).WithErrorCode("invalid-ticker")
    .MustAsync(...).WithErrorCode("unknown-ticker");
```

**答.** FluentValidation 的默认 `ErrorCode` 是**校验器的类名**——`NotEmptyValidator`、
`MaximumLengthValidator`。而中间件会把第一个错误码放进 ProblemDetails 的 `title`，于是
**内部类名作为错误 slug 泄漏到了公开 API 契约里**——而且我一换规则，它就会静默改变。

修法是在每条规则上**显式声明 slug**。

#### Q7.5 — How does authentication work on this API?

**中文** — 这个 API 的认证是怎么工作的？

**Tests / 考察点:** Authentication architecture — cookies vs bearer, token rotation, reuse detection · 认证架构：cookie vs bearer、令牌轮换、重用检测

**A.** Cookie-delivered JWTs with rotating refresh tokens —
[ADR-003](docs/adr/003-cookie-based-sessions.md),
[AuthController.cs](src/MarketPulse.Api/Controllers/AuthController.cs). Five endpoints under
`/api/v1/auth`: `register`, `login`, `refresh`, `logout`, `me`.

- **Local accounts** (email + password) only. OIDC is deliberately a *later* slice, because
  adding an external provider in the same slice brings a second identity-linking path in
  `User` and E2E tests that cannot run offline.
- **httpOnly cookies, not bearer-in-JS-memory.** This is the contrarian choice and the one
  worth defending: the IETF *OAuth 2.0 for Browser-Based Applications* BCP discourages
  holding tokens in the browser at all. `httpOnly` doesn't stop an XSS from *making*
  authenticated requests while the page is open, but it stops the credential being
  exfiltrated for use later from somewhere else — a materially smaller blast radius.
- Three cookies, minted in one place —
  [AuthCookies.Build](src/MarketPulse.Api/Authentication/AuthCookies.cs), a pure function so
  the attributes are unit-testable without booting a host: `mp_access` (15-minute JWT,
  `SameSite=Lax`), `mp_refresh` (14 days, `SameSite=Strict`, path-scoped), and `mp_csrf`
  (readable by JS by design — that is what "double submit" means).
- **Refresh rotation with reuse detection.** Each refresh mints a new token and records the
  successor's id on its predecessor. Presenting an *already-revoked* token means it leaked —
  so the user's entire token family is revoked and every session ends
  ([RefreshSessionCommand.cs](src/MarketPulse.Application/Authentication/RefreshSessionCommand.cs)).
  That is the security core of the slice.
- Only the **SHA-256 hash** of a refresh token is persisted, never the token. SHA-256 rather
  than PBKDF2 is correct here: the token is 256 bits of CSPRNG output, not a low-entropy
  human secret, so there is no dictionary attack for a slow hash to defend against — and this
  runs on every authenticated refresh.

**答.** **用 cookie 投递的 JWT + 轮换刷新令牌**。`/api/v1/auth` 下五个端点：`register`、`login`、
`refresh`、`logout`、`me`。

- **只做本地账号**（邮箱 + 密码）。OIDC 刻意放到**后面的 slice**——同一个 slice 里引入外部提供方
  会在 `User` 上多出第二条身份关联路径，还会让 E2E 测试无法离线运行。
- **用 httpOnly cookie，而不是把 bearer token 放在 JS 内存里。** 这是反直觉的那个选择，也是最值得
  辩护的：IETF 的 *OAuth 2.0 for Browser-Based Applications* BCP **不建议在浏览器里持有 token**。
  `httpOnly` 拦不住 XSS 在页面打开期间**发起**认证请求，但它拦住了**把凭据偷出去、之后在别处用**
  ——**爆炸半径小一个量级**。
- 三个 cookie，全部由一个地方签发——`AuthCookies.Build`，写成**纯函数**，所以不启动宿主就能单测它的
  属性：`mp_access`（15 分钟 JWT，`SameSite=Lax`）、`mp_refresh`（14 天，`SameSite=Strict`，
  按路径限定）、`mp_csrf`（**刻意设计成 JS 可读**——这正是 "double submit" 的含义）。
- **刷新令牌轮换 + 重用检测。** 每次刷新签发新令牌，并把继任者的 id 记在前一个上。拿一个
  **已撤销**的令牌来换，说明它泄漏了——于是**该用户整条令牌家族链全部撤销，所有会话终止**。
- 数据库里只存刷新令牌的 **SHA-256 哈希**，绝不存令牌本身。这里用 SHA-256 而不是 PBKDF2 是**对的**：
  令牌是 256 位 CSPRNG 输出，不是低熵的人类密码，**没有字典攻击需要慢哈希去防**——而且这段代码在
  每次认证刷新时都要跑。

**Follow-up: what changed below the API layer? / API 层以下改了什么？**

**Nothing in the watchlist path.** `CurrentUser` reads `ClaimTypes.NameIdentifier` off
`HttpContext.User` and has no opinion about who put it there
([CurrentUser.cs](src/MarketPulse.Api/CurrentUser.cs)). Slice 2 deleted one middleware, added
a JWT bearer handler that pulls the token out of the cookie, and populated the same claim.
`ICurrentUser`, `IWatchlistRepository`, every watchlist handler, every query, and the entire
watchlist schema are **byte-for-byte unchanged**. New tables (`Users`, `RefreshTokens`) and new
repositories were added alongside; nothing existing was modified to accommodate them. See
Q10.3 — this is the seam paying off, and now it is a measurement rather than a prediction.

> **watchlist 那条路径上什么都没改。** `CurrentUser` 只是从 `HttpContext.User` 上读
> `ClaimTypes.NameIdentifier`，它对"是谁把这个 claim 放上去的"没有任何意见。slice 2 删掉一个
> 中间件，加一个从 cookie 取 token 的 JWT bearer handler，填同一个 claim。
> `ICurrentUser`、`IWatchlistRepository`、所有 watchlist handler、所有查询、整个 watchlist schema
> **一个字节都没动**。新表（`Users`、`RefreshTokens`）和新仓储是**并排加上去的**，没有为了容纳它们
> 而修改任何既有代码。见 Q10.3——**这是接缝在回本，而且现在是实测，不再是预测。**

**Follow-up: where did the implementation diverge from the approved design? / 实现和批准的设计在哪里出现了偏离？**

Two places, and both are better answers than the design was.

1. **The refresh cookie's path.** The design said `/api/v1/auth/refresh` — scope it to exactly
   the one endpoint that needs it. That is wrong, and implementation proved it: RFC 6265
   path-matching would then withhold the cookie from `/api/v1/auth/logout`, so **logout could
   never see the token it has to revoke**, and every issued refresh token would stay live for
   its full 14 days after signing out. The shipped path is `/api/v1/auth` — one segment wider,
   still off every watchlist, hub and health request. The reasoning is written into
   [AuthCookies.cs](src/MarketPulse.Api/Authentication/AuthCookies.cs) so nobody "tightens" it
   back.
2. **The CSRF nonce encoding.** Standard base64 produces `+`, `/` and `=`, which `Set-Cookie`
   percent-encodes. A frontend reading the cookie off `document.cookie` and echoing it
   verbatim as a header would never match what `Request.Cookies` decodes on the way back in.
   base64url has no characters needing encoding, so cookie and header are always byte-for-byte
   identical and neither side needs a decode step.

> 两处，而且**两处的实现都比设计更好**。
>
> 1. **刷新 cookie 的路径。** 设计里写的是 `/api/v1/auth/refresh`——限定到唯一需要它的那个端点。
>    **这是错的，实现把它证伪了**：按 RFC 6265 的路径匹配，这样一来 cookie 就不会随
>    `/api/v1/auth/logout` 发出，于是**登出永远看不到它必须撤销的那个令牌**，每个已签发的刷新令牌
>    在登出后仍然完整存活 14 天。实际交付的路径是 `/api/v1/auth`——**宽了一段**，但仍然不会随任何
>    watchlist、hub、health 请求发出。理由写进了 `AuthCookies.cs`，免得有人再把它"收紧"回去。
> 2. **CSRF nonce 的编码。** 标准 base64 会产生 `+`、`/`、`=`，而 `Set-Cookie` 会把它们百分号编码。
>    前端从 `document.cookie` 读出来原样回显成请求头，就永远匹配不上服务端 `Request.Cookies`
>    解码后的值。base64url 没有需要编码的字符，所以 cookie 和 header **永远逐字节相同**，
>    两边都不需要解码步骤。

#### Gaps (category 7) / 类别 7 的缺口

Authentication is shipped; **authorisation is only authentication**. Every endpoint is either
`[Authorize]` or `[AllowAnonymous]` — there are no roles, no policies, no resource-based
checks, because there is exactly one kind of user and each owns exactly one watchlist. Beyond
that: no OIDC, no idempotency keys, no pagination, no documented versioning policy (Q7.4), and
the auth endpoints are not versioned differently from the rest despite having a very different
compatibility profile.

> 认证已交付，但**授权其实只有认证**。每个端点非 `[Authorize]` 即 `[AllowAnonymous]`——
> **没有角色、没有策略、没有基于资源的检查**，因为目前只有一种用户、每人恰好一个 watchlist。
> 除此之外还欠：OIDC、幂等键、分页、成文的版本策略（Q7.4）；而且认证端点的兼容性画像和其余端点
> 差别很大，却没有做任何区分的版本处理。

### 8. Data access & SQL Server

*数据访问与 SQL Server*

#### Q8.1 — Explain how the `Watchlist` aggregate is mapped. Why `OwnsMany` on a private field?

**中文** — 讲讲 `Watchlist` 聚合是怎么映射的。为什么对私有字段用 `OwnsMany`？

**Tests / 考察点:** EF Core mapping & DDD — owned types, aggregate boundaries · EF Core 映射与 DDD：owned 类型、聚合边界

**A.** [MarketPulseDbContext.OnModelCreating](src/MarketPulse.Infrastructure/Persistence/MarketPulseDbContext.cs)
maps items as an **owned collection over the backing field**, not over a public navigation:

```csharp
e.Ignore(x => x.Items);
e.OwnsMany<WatchlistItem>("_items", items => { … });
```

Two things fall out of that:

- **The aggregate boundary is real.** `WatchlistItem` has no `DbSet`, so there is no way to
  query or mutate an item except through its `Watchlist` — which is where the invariants
  live ([Watchlist.cs](src/MarketPulse.Domain/Entities/Watchlist.cs)). The persistence model
  enforces the domain model instead of quietly undermining it.
- **`e.Ignore(x => x.Items)` is required**, not decorative. `Items` is a computed property
  (`_items.AsReadOnly()`); without the `Ignore`, EF's conventions discover it as a *second*
  ambiguous navigation alongside the field-mapped one and throw at model-build time.

**答.** 映射是把 items 做成**基于backing field 的 owned collection**，而不是基于公开导航属性。
由此得到两件事：

- **聚合边界是真的。** `WatchlistItem` 没有 `DbSet`，所以除了通过它的 `Watchlist`，**没有任何
  途径**能查询或修改一个 item——而不变量正是住在 `Watchlist` 里的。**持久化模型在强化领域模型，
  而不是悄悄拆它的台。**
- **`e.Ignore(x => x.Items)` 是必需的，不是装饰。** `Items` 是一个计算属性
  （`_items.AsReadOnly()`）；不加 `Ignore`，EF 的约定会把它发现成**第二个**有歧义的导航属性，
  在建模型时直接抛异常。

**Follow-up: no `Include` in the repository — how do items load? / 仓储里没有 `Include`，items 怎么加载的？**

[WatchlistRepository](src/MarketPulse.Infrastructure/Persistence/WatchlistRepository.cs) is
just `db.Watchlists.FirstOrDefaultAsync(...)`. Owned types are **auto-included** by EF Core —
they are part of the owner by definition, so an explicit `Include` is unnecessary (and
`Ignore`d navigations couldn't be `Include`d anyway). That is a genuine EF subtlety: owned
collections behave differently from ordinary navigations, which are lazy/explicit.

> 仓储里就是一句 `FirstOrDefaultAsync`。**owned 类型是 EF Core 自动 include 的**——按定义它们就是
> owner 的一部分，所以不需要显式 `Include`（而且被 `Ignore` 的导航本来也没法 `Include`）。
> 这是一个真实的 EF 细节：**owned 集合的行为和普通导航属性不一样**，后者是惰性/显式的。

#### Q8.4 — How does seeding work, and what was hard about it?

**中文** — 种子数据是怎么做的？难点在哪？

**Tests / 考察点:** EF Core migrations & seeding — determinism, clean-clone verification · EF 迁移与种子数据：确定性、干净克隆验证

**A.** `HasData` in the model, materialised into the migrations — 25 reference tickers, one
dev user, one watchlist, four items
([SeedData.cs](src/MarketPulse.Infrastructure/Persistence/SeedData.cs)).

Two real constraints made it awkward:

1. **`HasData` needs deterministic values.** `Guid.NewGuid()` or `DateTimeOffset.UtcNow`
   would produce a fresh migration diff on every scaffold. Hence the fixed GUIDs and the
   fixed `DevWatchlistItemsAddedUtc`.
2. **`HasData` can't call your constructors.** `Watchlist.Create` is a static factory that
   mints a random Id, and `WatchlistItem`'s constructor is `internal`. So the seed passes
   anonymous types carrying just the mapped properties — EF reads property values by name to
   build the `InsertData`, it never constructs a real entity.

**答.** 用模型里的 `HasData`，落成迁移——25 个参考 ticker、1 个 dev 用户、1 个 watchlist、4 个条目。

两个真实的约束让它变得别扭：

1. **`HasData` 需要确定性的值。** `Guid.NewGuid()` 或 `DateTimeOffset.UtcNow` 会让**每次生成迁移
   都产生新的 diff**。所以用了固定 GUID 和固定的 `DevWatchlistItemsAddedUtc`。
2. **`HasData` 不能调你的构造函数。** `Watchlist.Create` 是会随机生成 Id 的静态工厂，
   `WatchlistItem` 的构造函数是 `internal`。所以种子数据传的是**只带映射属性的匿名类型**——
   EF 只按名字读属性值来生成 `InsertData`，它从不真的构造实体。

**Follow-up: why did this matter? / 这为什么重要？**

Because it was a *bug found in final review*, not a design choice. The dev user was
originally seeded only by the test fixture, never by the app — so from a clean clone the
first `GET /api/v1/watchlist` violated `FK_Watchlists_Users_UserId` and 500'd forever. Every
test passed. The lesson: "works on my machine" and "works from a clean clone" are different
claims, and only one of them was being tested.

> 因为这**是最终评审时发现的 bug，不是一开始的设计**。dev 用户原本只由测试 fixture 种进去、
> 应用自己从不种——所以**从干净克隆开始，第一次 `GET /api/v1/watchlist` 就违反外键、永久 500**。
> 而所有测试都是绿的。
> 教训：**"在我机器上能跑"和"从干净克隆能跑"是两个不同的断言，而当时只测了其中一个。**

#### Q8.5 — Justify every index on the `RefreshTokens` table.

**中文** — 把 `RefreshTokens` 表上的每个索引都说出理由。

**Tests / 考察点:** Indexing — query-driven index design, storage growth · 索引：查询驱动的索引设计、存储增长

**A.** Two, each tied to one query, from
[MarketPulseDbContext.OnModelCreating](src/MarketPulse.Infrastructure/Persistence/MarketPulseDbContext.cs):

- **Unique index on `TokenHash`.** Every refresh looks a token up by hash — that is the *only*
  way a refresh token is ever found, since the plaintext is never stored. Without the index
  that is a table scan on the hottest authenticated path in the app, growing linearly with
  every session ever issued. Unique rather than plain, because two rows sharing a hash would
  mean either a SHA-256 collision or a bug, and both should fail loudly at the database.
- **Non-unique index on `UserId`.** Reuse detection and logout both call
  `RevokeAllForUserAsync`, which queries `WHERE UserId = @id AND RevokedUtc IS NULL`.

`TokenHash` is `nvarchar(64)` — a base64-encoded SHA-256 digest is 44 characters, so the
column is sized for the encoding rather than left at EF's `nvarchar(max)`, which cannot be
indexed at all.

**答.** 两个，每个对应一条查询：

- **`TokenHash` 上的唯一索引。** 每次刷新都按哈希查令牌——而且这是**唯一**能找到刷新令牌的方式，
  因为明文从不落库。没有这个索引，就是在**应用里最热的认证路径上做全表扫描**，而且随着历史签发的
  每个会话线性增长。用唯一而不是普通索引，是因为两行共享同一个哈希意味着要么 SHA-256 碰撞、
  要么有 bug——**两种情况都应该在数据库层大声失败**。
- **`UserId` 上的非唯一索引。** 重用检测和登出都会调 `RevokeAllForUserAsync`，
  查询是 `WHERE UserId = @id AND RevokedUtc IS NULL`。

`TokenHash` 是 `nvarchar(64)`——base64 编码的 SHA-256 摘要是 44 个字符，所以这一列是**按编码长度
定尺寸**的，而不是留给 EF 默认的 `nvarchar(max)`——后者**根本没法建索引**。

**Follow-up: what's wrong with this table? / 这张表有什么问题？**

**Nothing ever deletes from it.** Every refresh writes a row and revokes the previous one, so
a single active user accumulates a row every 15 minutes — roughly 35,000 rows a year, forever,
of which all but one are dead. There is no cleanup job and no retention policy. The
`RevokedUtc IS NULL` filter keeps queries correct but the index keeps growing. A background
sweep deleting rows past `ExpiresUtc` is the obvious fix and it is not written.

> **它从来不删数据。** 每次刷新写一行、撤销前一行，所以**一个活跃用户每 15 分钟积累一行**——
> 大约每年 3.5 万行，永久留存，其中除了一行全是死数据。**没有清理任务，也没有保留策略。**
> `RevokedUtc IS NULL` 这个过滤条件保证了查询正确，但索引会一直长。
> 明显的修法是加一个后台清扫任务删掉过了 `ExpiresUtc` 的行——**而它没有写。**

#### Gaps (category 8) / 类别 8 的缺口

No Dapper read path or EF-vs-Dapper write-up, no deliberate N+1 postmortem, no execution
plans or index tuning under load, no explicit transactions or isolation-level work, no
concurrency token (Q8.2), and no retention policy on `RefreshTokens` (Q8.5).

> 欠：Dapper 读路径与 EF-vs-Dapper 的对比文章、刻意的 N+1 复盘、负载下的执行计划与索引调优、
> 显式事务与隔离级别、并发令牌（Q8.2），以及 `RefreshTokens` 的保留策略（Q8.5）。

### 12. Cloud & DevOps

*云与 DevOps*

#### Q12.1 — Walk me through the CI pipeline.

**中文** — 讲一遍 CI 流水线。

**Tests / 考察点:** CI/CD design — job gating, real-infrastructure tests in CI · CI/CD 设计：job 门控、CI 中的真实基础设施

**A.** [.github/workflows/ci.yml](.github/workflows/ci.yml) — four jobs on push to
`main`/`test` and on every pull request:

- **backend** — `dotnet restore` → `build -c Release` → `test`. Note the integration tests
  spin up a real SQL Server via Testcontainers *on the runner*, so CI green means the
  database path genuinely works.
- **frontend** — `pnpm install --frozen-lockfile` → `typecheck` → `test` → `build`.
- **docker** — gated on `needs: [backend, frontend]`, builds the API image.
- **e2e** *(new in slice 2)* — also gated on `needs: [backend, frontend]`. It runs SQL Server
  as a **service container** rather than through Testcontainers, because Playwright starts the
  API itself and the API needs a database at a known address before the browser opens. The
  service has a `sqlcmd` healthcheck with a 30-second start period, for the same reason
  docker-compose does (Q12.4). A `playwright-report` artifact uploads **only on failure**,
  with a 7-day retention — a passing run has nothing to look at, and artifacts cost storage.

`--frozen-lockfile` is the important flag: it fails the build if the lockfile and
`package.json` disagree, rather than silently resolving something the developer never tested.

**答.** 四个 job，在推送到 `main`/`test` 以及每个 PR 上触发：

- **backend**：`restore` → `build -c Release` → `test`。注意集成测试会**在 runner 上**通过
  Testcontainers 拉起一个真实 SQL Server，所以 **CI 绿了就意味着数据库这条路真的通**。
- **frontend**：`pnpm install --frozen-lockfile` → `typecheck` → `test` → `build`。
- **docker**：`needs: [backend, frontend]` 门控，构建 API 镜像。
- **e2e**（slice 2 新增）：同样由 `needs: [backend, frontend]` 门控。它用 **service container**
  跑 SQL Server，而不是 Testcontainers——因为 API 是 Playwright 自己启动的，而 API 在浏览器打开
  之前就需要一个**地址已知**的数据库。这个 service 配了 `sqlcmd` healthcheck 和 30 秒
  `start-period`，理由和 docker-compose 那边一样（见 Q12.4）。`playwright-report` 制品
  **只在失败时**上传、保留 7 天——**跑绿了没什么好看的，而制品是要占存储的**。

`--frozen-lockfile` 是关键开关：**lockfile 和 `package.json` 不一致时直接让构建失败**，
而不是静默解析出一个开发者从没测过的版本组合。

#### Q12.3 — What's wrong with your Docker setup right now?

**中文** — 你现在的 Docker 配置有什么问题？

**Tests / 考察点:** Production readiness — registries, credential hygiene, build context · 生产就绪：registry、凭据卫生、构建上下文

**A.** Three things I would fix before this ran anywhere real:

1. **The CI docker job uses `push: false`.** It proves the image *builds*; it publishes
   nothing. The `publishes a Docker image` line in the definition of done is not yet met —
   it needs a registry decision (GHCR being the obvious default).
2. **`appsettings.Development.json` ships in the Release image**, containing the dev SA
   password. It is inert — `ASPNETCORE_ENVIRONMENT` defaults to `Production` so the file is
   never loaded, and the image is never published — but shipping a credential you don't
   intend to ship is exactly how credentials leak. It should be excluded at publish time.
3. **`.dockerignore` doesn't trim `tests/`, `docs/`, `apps/`, `packages/`** from the build
   context — build-time cost only, but wasteful.

**答.** 三件事，上真实环境前我都会修：

1. **CI 的 docker job 用的是 `push: false`。** 它只证明镜像**能构建**，什么都没发布。
   完成定义里"发布 Docker 镜像"这一条**还没达成**——需要先定 registry（GHCR 是最自然的默认）。
2. **`appsettings.Development.json` 被打进了 Release 镜像**，里面有 dev 的 SA 密码。它是**惰性的**
   ——`ASPNETCORE_ENVIRONMENT` 默认是 `Production`，这个文件永远不会被加载，镜像也从不发布——
   但**发出去一个你并不打算发出去的凭据，正是凭据泄漏的标准路径**。应该在 publish 时排除掉。
3. **`.dockerignore` 没有裁掉 `tests/`、`docs/`、`apps/`、`packages/`**——只是构建时开销，但是浪费。

#### Q12.4 — Why `platform: linux/amd64` pinned in docker-compose?

**中文** — docker-compose 里为什么钉死 `platform: linux/amd64`？

**Tests / 考察点:** Environment reproducibility — platform pinning, healthchecks · 环境可复现性：平台钉死、健康检查

**A.** [docker-compose.yml](docker-compose.yml) — SQL Server 2022 has no native arm64 image,
so on Apple Silicon it runs under emulation. Pinning the platform makes that explicit and
reproducible rather than leaving it to a confusing runtime failure. It also means local dev
runs the *same* SQL Server as CI, which is the whole point.

The healthcheck matters too: `sqlcmd -Q 'SELECT 1'` with a 30s `start_period`, because
"container started" and "SQL Server accepting connections" are 20+ seconds apart, and
racing that gap is a classic flaky-test source.

**答.** SQL Server 2022 **没有原生 arm64 镜像**，所以在 Apple Silicon 上是跑在模拟层里的。
把平台钉死让这件事**显式且可复现**，而不是留给一个莫名其妙的运行时失败。而且这样本地开发跑的
SQL Server 和 CI **是同一个**——这正是重点。

healthcheck 同样重要：`sqlcmd -Q 'SELECT 1'`，`start_period` 给 30 秒，因为
**"容器已启动"和"SQL Server 开始接受连接"之间差 20 多秒**，抢这个空档是经典的 flaky 测试来源。

#### Gaps (category 12) / 类别 12 的缺口

Every AWS resource, Terraform/IaC, IAM, blue-green or canary deployment, and — the one that
would matter most in production — **observability**. There is no OpenTelemetry, no structured
logging sink, no metrics, no tracing. The correlation ID is threaded through the code but
lands nowhere it can be searched. Slice 2 made that worse, not better: failed logins, lockouts
and refresh-token reuse detections are exactly the events a real system alerts on, and none of
them emit anything.

> 欠：所有 AWS 资源、Terraform/IaC、IAM、蓝绿或金丝雀部署，以及**生产上最要命的那一项——可观测性**。
> 没有 OpenTelemetry、没有结构化日志落库、没有指标、没有链路追踪。
> **相关性 ID 在代码里贯通了，却落不到任何可以搜索的地方。**
> 而且 **slice 2 让这件事变得更糟而不是更好**：登录失败、账号锁定、刷新令牌重用检测——
> 这些恰恰是真实系统要**告警**的事件，而它们**一条日志都不发**。

### 13. Security (full-stack)

*全栈安全*

#### Q13.1 — Slice 1 shipped with a dev-auth stub. Defend that, and tell me what replaced it.

**中文** — slice 1 是带着一个 dev-auth 桩件上线的。为它辩护，然后讲讲替换它的是什么。

**Tests / 考察点:** Security engineering — fail-closed design, staged hardening · 安全工程：fail-closed 设计、分阶段加固

**A.** I won't defend the stub as *sufficient* — I'll defend the sequencing and the
containment, and then show the bill being paid.

Slice 1 is a walking skeleton whose purpose is proving the ingestion → SignalR → React seam.
`DevAuthMiddleware` stamped a fixed user id onto every request so the watchlist could be
scoped to *someone*. What mattered was that the stub was **fenced in two independent ways**:
it was only registered inside `if (app.Environment.IsDevelopment())`, and the middleware
*itself* threw `InvalidOperationException` if it ever executed outside Development. Belt and
braces, because a registration guard is one careless refactor away from being removed and a
dev-auth stub reaching production is a total-compromise bug. **It failed closed.**

Slice 2 deleted it. It is now a JWT bearer handler reading a cookie, five auth endpoints, a
CSRF middleware, per-IP rate limiting and per-account lockout — Q7.5 and Q13.4 cover the
shape. `[Authorize]` on `WatchlistController` and on `PriceHub`; `[AllowAnonymous]` on
`/health` and the four pre-session auth endpoints, each for a stated reason.

**答.** 我不会辩护那个桩件"足够"——我辩护的是**排序和围堵**，然后把账还上。

slice 1 是 walking skeleton，目的是验证采集 → SignalR → React 这条接缝。`DevAuthMiddleware`
往每个请求上盖一个固定用户 id，好让 watchlist 能限定到**某个人**身上。关键在于这个桩件被
**两道独立的围栏**围着：只在 `if (app.Environment.IsDevelopment())` 里注册；而且**中间件自己**
在非 Development 下执行时就抛 `InvalidOperationException`。**双保险**——注册那道守卫距离"被一次
粗心重构删掉"只有一步，而 dev-auth 桩件进生产是**彻底沦陷级别**的 bug。**它是 fail closed 的。**

slice 2 把它删了。现在是：读 cookie 的 JWT bearer handler、五个认证端点、一个 CSRF 中间件、
按 IP 限流 + 按账号锁定——形状见 Q7.5 和 Q13.4。`WatchlistController` 和 `PriceHub` 上是
`[Authorize]`；`/health` 和四个会话建立前的认证端点是 `[AllowAnonymous]`，**每一个都有明说的理由**。

**Follow-up: the 500-instead-of-401 defect you recorded — is it fixed? / 你记录过的"该 401 却 500"那个缺陷，修了吗？**

Yes. `CurrentUser` throws `UnauthorizedAccessException` when no authenticated principal is on
the request ([CurrentUser.cs](src/MarketPulse.Api/CurrentUser.cs)); slice 1 let that fall
through to the generic handler and return a **500**. The exception middleware now catches it
explicitly and returns **401 `unauthenticated`**.

The same problem existed in a second place nobody would have predicted: the JWT bearer
handler's default challenge writes an **empty-bodied 401**, which would have been the one
error response in the API that didn't follow the ProblemDetails contract. `OnChallenge` calls
`context.HandleResponse()` to suppress it and writes the same shape by hand, correlation ID
included ([Program.cs](src/MarketPulse.Api/Program.cs)). An error contract is only a contract
if the framework's own error paths honour it too.

> **修了。** 没有认证 principal 时 `CurrentUser` 会抛 `UnauthorizedAccessException`；slice 1
> 让它掉进通用分支返回 **500**。现在异常中间件**显式捕获它并返回 401 `unauthenticated`**。
>
> 同样的问题还在**第二个没人预料到的地方**存在：JWT bearer handler 默认的 challenge 会写一个
> **空响应体的 401**——那将是整个 API 里**唯一一个不遵守 ProblemDetails 契约的错误响应**。
> 所以 `OnChallenge` 调 `context.HandleResponse()` 把它压掉，手写同样的结构，**带上相关性 ID**。
> **一个错误契约，只有在框架自己的错误路径也遵守它的时候，才算契约。**

#### Q13.3 — SQL injection, XSS, secrets — cover each.

**中文** — SQL 注入、XSS、密钥管理，一个个说。

**Tests / 考察点:** Application security — injection, XSS, secrets management · 应用安全：注入、XSS、密钥管理

**A.**

- **SQL injection** — every query goes through EF Core LINQ, which parameterises. There is
  no raw SQL anywhere in the codebase, and no string concatenation into a query.
- **XSS** — React escapes interpolated content by default, and there is no
  `dangerouslySetInnerHTML` anywhere. Server-supplied strings (error `detail`, tickers) land
  as text nodes.
- **Secrets** — this one is a *known deviation*, and slice 2 added two more of them. The dev
  SA password is literally in
  [appsettings.Development.json](src/MarketPulse.Api/appsettings.Development.json), alongside
  the JWT signing key; and the seeded dev account's PBKDF2 hash is a committed constant in
  [SeedData.cs](src/MarketPulse.Infrastructure/Persistence/SeedData.cs). All three are
  explicit, recorded decisions for local-only credentials — they buy a clean-clone dev
  experience, and ADR-003 schedules the seed account for removal in the hardening slice.

  The containment that makes this defensible: **`appsettings.json` has no `Jwt` section at
  all.** So in Production, `JwtOptions`' `ValidateOnStart()` finds a missing `SigningKey` and
  **the process refuses to boot**. There is no path where the dev signing key silently becomes
  the production one — the deployment must supply a real key through the environment or a
  secret store, or it does not start. That is the difference between a documented weakness and
  a latent compromise.

  What is still owed: nothing in the codebase *demonstrates* an actual secret store, and
  `appsettings.Development.json` still ships inside the Release image (Q12.3).

**答.**

- **SQL 注入**——所有查询都走 EF Core 的 LINQ，会参数化。代码库里**没有任何裸 SQL**，
  也没有任何字符串拼接进查询。
- **XSS**——React 默认转义插值内容，全库**没有一处 `dangerouslySetInnerHTML`**。
  服务端来的字符串（错误 `detail`、ticker）都落成文本节点。
- **密钥**——这一条是**已知偏离**，而且 slice 2 又加了两条。dev 的 SA 密码明文写在
  `appsettings.Development.json` 里，JWT 签名密钥也在同一个文件；种子 dev 账号的 PBKDF2 哈希
  是提交进 `SeedData.cs` 的常量。三者都是针对**纯本地凭据**的**显式、有记录的决定**——换来的是
  干净克隆即可开发的体验，而 ADR-003 已经把种子账号的移除排进了加固 slice。

  让这件事站得住的**围堵**是：**`appsettings.json` 里根本没有 `Jwt` 这一节。** 所以在生产环境下，
  `JwtOptions` 的 `ValidateOnStart()` 会发现 `SigningKey` 缺失，**进程直接拒绝启动**。
  **不存在任何一条路径能让 dev 的签名密钥静默变成生产密钥**——部署方必须通过环境变量或密钥存储
  提供真实密钥，否则它根本起不来。**这就是"有记录的弱点"和"潜伏的沦陷"之间的区别。**

  仍然欠着的：代码库里**没有任何东西在展示真正的密钥存储**，而且
  `appsettings.Development.json` 依然被打进了 Release 镜像（见 Q12.3）。

#### Q13.4 — Walk me through your password and session security.

**中文** — 讲一遍你的密码与会话安全。

**Tests / 考察点:** Credential & session security — password hashing, lockout, timing side-channels · 凭据与会话安全：密码哈希、锁定、时序侧信道

**A.** Four decisions, each with a rejected alternative
([ADR-003](docs/adr/003-cookie-based-sessions.md)):

- **Password hashing: `PasswordHasher<T>` from ASP.NET Core Identity, and nothing else from
  Identity** ([PasswordHasherAdapter.cs](src/MarketPulse.Infrastructure/Authentication/PasswordHasherAdapter.cs)) —
  PBKDF2-HMAC-SHA512, 100,000 iterations, 128-bit random salt per hash. Full Identity forces
  `IdentityUser<Guid>` — an Infrastructure type — into the model, which means either a second
  parallel user model kept in sync with `Domain.User`, or a `Domain` → Identity reference that
  **breaks the dependency-rule test from Q10.1**. The generic is constrained to `class`, not to
  `IdentityUser`, so the audited implementation is reused with no Identity type crossing an
  architectural boundary. Hand-rolling Argon2id is a better algorithm but means owning crypto
  parameter tuning for no benefit here.
- **Password policy: NIST SP 800-63B**
  ([PasswordPolicy.cs](src/MarketPulse.Application/Authentication/PasswordPolicy.cs)). 12
  character minimum, **no composition rules**, no forced rotation, plus a blocklist of the
  10,000 most common breached passwords compiled in as an embedded resource. This is
  deliberately the opposite of the "one uppercase, one number, one symbol" reflex — NIST now
  advises *against* composition rules, on the evidence that they push users toward
  `Password1!`. The stronger check is the Have I Been Pwned range API, but that puts an
  outbound network call on the registration path and a third-party availability dependency in
  the middle of the test suite.
- **Refresh rotation with reuse detection** — see Q7.5. Non-rotating refresh tokens are a
  long-lived bearer credential; rotation *without* reuse detection silently tolerates a
  stolen token being replayed once.
- **Two independent brute-force controls, because they defend different things.** Per-IP
  fixed-window rate limiting on `/auth/login` and `/auth/register` (10 requests/minute)
  defends *the endpoint*. Per-account lockout (5 consecutive failures → 15 minutes, tracked
  on [`User`](src/MarketPulse.Domain/Entities/User.cs), reset on success) defends *one account*
  against an attempt distributed across many IPs. Both surface as `429` with `Retry-After`, and
  both thresholds live in `AuthOptions` so integration tests can drive them without a
  15-minute wait.

**答.** 四个决定，每个都有被否方案：

- **密码哈希：只用 ASP.NET Core Identity 里的 `PasswordHasher<T>`，Identity 的其它部分一律不用。**
  完整 Identity 会把 `IdentityUser<Guid>`（一个 Infrastructure 类型）**塞进模型**，那就只有两条路：
  要么维护第二套和 `Domain.User` 同步的用户模型，要么让 `Domain` 引用 Identity——而后者
  **会让 Q10.1 那个依赖规则测试直接挂掉**。而这个泛型的约束是 `class`，不是 `IdentityUser`，
  所以能复用经过审计的 PBKDF2 实现，**而没有任何 Identity 类型跨越架构边界**。
  手写 Argon2id 算法更好，但意味着要自己负责密码学参数调优，在这里换不到任何收益。
- **密码策略：NIST SP 800-63B。** 最少 12 字符、**不要组合规则**、不强制轮换，外加一份作为嵌入
  资源编译进程序集的、**一万条**最常见泄露密码的黑名单。这**刻意与"一个大写、一个数字、一个符号"
  的条件反射相反**——NIST 现在**明确反对**组合规则，证据是它们把用户推向 `Password1!`。
  更强的做法是 Have I Been Pwned 的 range API，但那会在注册路径上加一次外部网络调用，
  并在测试套件中间引入第三方可用性依赖。
- **刷新令牌轮换 + 重用检测**——见 Q7.5。不轮换的刷新令牌就是一个长期有效的 bearer 凭据；
  **只轮换而不做重用检测，等于默默容忍被盗令牌被重放一次。**
- **两道独立的暴力破解防线，因为它们防的是不同的东西。** 对 `/auth/login` 和 `/auth/register`
  的**按 IP** 固定窗口限流（10 次/分钟）防的是**端点**；**按账号**锁定（连续 5 次失败锁 15 分钟，
  记在 `User` 上，成功即清零）防的是**单个账号被分布在多个 IP 上的尝试打穿**。
  两者都以 `429` + `Retry-After` 呈现，阈值都放在配置里，好让集成测试不必真等 15 分钟。

**Follow-up: how do you make "no such user" and "wrong password" indistinguishable? / 你怎么让"用户不存在"和"密码错误"无法区分？**

Two things, and the second is the one people miss. Both paths throw the **same**
`InvalidCredentialsException` with the same message — that handles the *response*
([AuthExceptions.cs](src/MarketPulse.Domain/Exceptions/AuthExceptions.cs)). But the response
is not the only channel: an unknown email returns in microseconds because no hash was
verified, while a wrong password costs 100,000 PBKDF2 iterations. **The timing tells the
attacker what the message would not.**

So the unknown-email path in
[LoginHandler](src/MarketPulse.Application/Authentication/LoginCommand.cs) verifies the
supplied password against a **dummy hash** before throwing, purely to spend the same time.
And it has to be a genuinely well-formed PBKDF2 hash — a made-up string would fail format
parsing and return immediately, which is the exact signal the dummy was added to remove.

> 两件事，而**第二件是大多数人会漏掉的**。两条路径抛的是**同一个** `InvalidCredentialsException`、
> 同样的文案——这处理的是**响应**。但**响应不是唯一的信道**：邮箱不存在时因为压根没做哈希验证，
> 微秒级就返回了；而密码错误要花掉十万次 PBKDF2 迭代。**时间会告诉攻击者文案不肯告诉他的事。**
>
> 所以 `LoginHandler` 在"邮箱不存在"这条路径上，会**先拿用户提交的密码去验证一个假哈希**再抛异常，
> 纯粹为了花掉同样的时间。而且那必须是一个**格式完全合法**的 PBKDF2 哈希——随手编一个字符串会在
> 格式解析阶段就失败并立刻返回，**那正是引入假哈希本来要消掉的信号**。

**Follow-up: what does this NOT solve? / 这套东西解决不了什么？**

Three limitations, recorded in ADR-003 rather than hoped over:

1. **Account enumeration on registration.** Login is indistinguishable both ways, per above.
   Registration cannot hide the same fact without an email round trip — it has to return
   `409 email-taken`. Accepted for a showcase; the fix is confirmation-email registration.
2. **The seeded dev account.** `dev@marketpulse.local` needs a password, which means a PBKDF2
   hash committed as a constant, because `HasData` requires determinism and `PasswordHasher<T>`
   salts randomly. Credentials are documented in the README; removal is scheduled for the
   hardening slice. Registration creates an *empty* watchlist, so real users are unaffected.
3. **The password blocklist is very nearly inert** — and this only became visible once it was
   built. Only **10 of the 10,000** entries are 12 characters or longer, so the length minimum
   already rejects 9,990 of them before the blocklist is ever consulted. It is retained because
   it is real breach data at no runtime cost and is immediately correct if the minimum ever
   drops. But claiming it as a meaningful control would be overclaiming, so I don't.

> 三条局限，写在 ADR-003 里，不是蒙混过去的：
>
> 1. **注册的账号枚举。** 登录两个方向都不可区分（见上）。注册没法在不加邮件往返的前提下藏住同样的
>    事实——它必须返回 `409 email-taken`。作为展示项目接受；真正的修法是邮箱确认注册。
> 2. **种子 dev 账号。** `dev@marketpulse.local` 需要密码，也就意味着**一个 PBKDF2 哈希常量被提交
>    进仓库**，因为 `HasData` 要求确定性而 `PasswordHasher<T>` 是随机加盐的。凭据写在 README 里，
>    移除排在加固 slice。注册创建的是**空** watchlist，所以真实用户不受影响。
> 3. **密码黑名单几乎是惰性的**——而这一点**是把它建出来之后才看见的**。一万条里只有 **10 条**
>    长度 ≥ 12，所以**长度下限已经先把其中 9990 条拒掉了**，黑名单根本轮不到被查。保留它是因为
>    它是**零运行时成本的真实泄露数据**，而且一旦长度下限下调它立刻就是正确的。
>    但**把它宣称成一项有意义的控制就是夸大，所以我不这么讲。**

#### Gaps (category 13) / 类别 13 的缺口

Authentication is shipped, but the perimeter around it is not. **No security headers are set
at all** — no HSTS, no `X-Content-Type-Options`, no CSP, no `Referrer-Policy`. That is the
cheapest remaining win in this category and the most obviously missing. Beyond it: no secrets
management (Q13.3), no least-privilege IAM, no written threat model, no audit log of security
events (Q12 gaps), no account recovery or email verification flow, and no retention policy on
`RefreshTokens` (Q8.5).

> 认证交付了，但**它外面那圈周界没有**。**现在一个安全响应头都没设**——没有 HSTS、没有
> `X-Content-Type-Options`、没有 CSP、没有 `Referrer-Policy`。这是这个类别里**剩下最便宜的一分**，
> 也是最明显缺的一块。除此之外还欠：密钥管理（Q13.3）、最小权限 IAM、成文的威胁模型、
> 安全事件审计日志（见类别 12 的缺口）、账号找回与邮箱验证流程，以及 `RefreshTokens`
> 的保留策略（Q8.5）。

### 14. Testing (full-stack)

*全栈测试*

#### Q14.3 — How do you test a SignalR stream?

**中文** — SignalR 的流怎么测？

**Tests / 考察点:** Testing real-time systems — bounded waits, authenticated connections · 实时系统测试：有界等待、带认证的连接

**A.** [PriceStreamTests](tests/MarketPulse.IntegrationTests/PriceStreamTests.cs) boots the
real app in `WebApplicationFactory`, connects a real `HubConnection` through the in-memory
test server, and asserts a tick arrives within five seconds:

- `o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler()` routes the SignalR
  client through the in-memory server — no real socket, no port.
- `o.Transports = HttpTransportType.LongPolling`, because `TestServer` does not support
  WebSockets. That is a deliberate fidelity trade: the transport differs from production,
  the *pipeline* does not.
- `TaskCompletionSource` with `RunContinuationsAsynchronously` + `Task.WhenAny(received,
  Task.Delay(5s))` gives a bounded wait — a test that can hang is worse than a test that
  fails.

What this proves is the full chain: `FakeTickService` → `Channel<T>` → `TickBroadcaster` →
hub → client.

Slice 2 added two things to it. The connected test now registers a real user first and sets
`o.Headers["Cookie"] = accessCookie` by hand, because a `HubConnectionBuilder` has no cookie
jar — that is what
[`RegisterAndGetAccessCookieAsync`](tests/MarketPulse.IntegrationTests/AuthenticatedClient.cs)
exists for. And a second test asserts an **unauthenticated** connection is refused, since
`PriceHub` is now `[Authorize]`. Proving the door opens for the right client is only half a
test; the other half is proving it stays shut for the wrong one.

> slice 2 给它加了两样东西。已连接的那个测试现在要先注册一个真实用户，并**手工**设置
> `o.Headers["Cookie"] = accessCookie`——因为 `HubConnectionBuilder` **没有 cookie jar**，
> 这正是 `RegisterAndGetAccessCookieAsync` 存在的理由。另外新增一个测试断言
> **未认证的连接会被拒绝**，因为 `PriceHub` 现在带 `[Authorize]`。
> **证明门对正确的客户端会开，只是半个测试；另外半个是证明它对错误的客户端仍然关着。**

**答.** 用 `WebApplicationFactory` 启动真实应用，通过内存测试服务器连一个真实的 `HubConnection`，
断言 5 秒内收到 tick：

- `HttpMessageHandlerFactory` 指向 `factory.Server.CreateHandler()`，把 SignalR 客户端路由到内存
  服务器——**没有真实 socket，没有端口**。
- 传输方式**强制 LongPolling**，因为 `TestServer` 不支持 WebSocket。这是**刻意的保真度取舍：
  传输层和生产不同，但管道是同一个。**
- `TaskCompletionSource`（配 `RunContinuationsAsynchronously`）+ `Task.WhenAny(..., Delay(5s))`
  给出一个**有界等待**——**一个会挂住的测试，比一个会失败的测试更糟。**

它证明的是完整链路：`FakeTickService` → `Channel<T>` → `TickBroadcaster` → hub → 客户端。

#### Q14.4 — Why MSW rather than mocking `fetch`?

**中文** — 为什么用 MSW 而不是 mock 掉 `fetch`？

**Tests / 考察点:** Frontend test design — network-layer interception vs mocks · 前端测试设计：网络层拦截 vs mock

**A.** Mocking `fetch` tests that you called a mock. MSW intercepts at the network layer, so
the *real* `api-client` runs — real URL construction, real headers, real zod parsing of the
response. The test in
[WatchlistScreen.test.tsx](apps/dashboard/src/features/watchlist/WatchlistScreen.test.tsx)
returns a genuine 409 ProblemDetails body and asserts the user sees the server's message,
which exercises `ApiError`, the zod problem-details schema, TanStack Query's error state, and
the `role="alert"` render — one test, the whole error path.

`server.listen({ onUnhandledRequest: 'error' })` is the discipline that makes it trustworthy:
any request the test didn't explicitly stub fails the test, so a silently-changed URL cannot
pass.

**答.** **mock `fetch` 测的是"你调用了一个 mock"。** MSW 在**网络层**拦截，所以跑的是**真实的**
`api-client`——真实的 URL 拼接、真实的请求头、真实的 zod 响应解析。

那个测试返回一个货真价实的 409 ProblemDetails 响应体，然后断言用户看到了服务端的文案——
一次性走通 `ApiError`、zod 的 problem-details schema、TanStack Query 的错误状态、
以及 `role="alert"` 的渲染。**一个测试，整条错误路径。**

`server.listen({ onUnhandledRequest: 'error' })` 是让它可信的那条纪律：
**任何测试没有显式打桩的请求都会让测试失败**，所以一个被悄悄改掉的 URL 不可能蒙混过关。

#### Q14.5 — Tell me about a test-environment problem you had to debug.

**中文** — 讲一个你不得不去调试的测试环境问题。

**Tests / 考察点:** Toolchain debugging — root-cause discipline, safe workarounds · 工具链调试：根因定位、安全的 workaround

**A.** The dashboard tests needed a custom Vitest environment
([jsdomNodeFetchEnvironment.ts](apps/dashboard/src/test/jsdomNodeFetchEnvironment.ts)).
jsdom installs its own `AbortSignal`, but MSW/undici's `fetch` is Node's native one — and
Node's `fetch` rejects a jsdom `AbortSignal` as a foreign object, so TanStack Query's
`{ signal }` broke every GET.

The fix restores the native `AbortSignal` inside the jsdom environment. Two things I insisted
on before accepting it: confirming it was a known toolchain incompatibility
(vitest-dev/vitest#8374) rather than an application defect, and confirming that MSW's
`onUnhandledRequest: 'error'` stayed in force so the workaround didn't quietly weaken the
suite. The recorded caveat is that restoring `AbortSignal` globally could theoretically
invert the mismatch for a future test that uses jsdom's `addEventListener({ signal })`.

**答.** dashboard 的测试需要一个**自定义 Vitest 环境**。jsdom 会装它自己的 `AbortSignal`，
但 MSW/undici 用的 `fetch` 是 Node 原生的——而 **Node 的 `fetch` 会把 jsdom 的 `AbortSignal`
当成外来对象拒绝掉**，于是 TanStack Query 传的 `{ signal }` 把每个 GET 都搞挂了。

修法是在 jsdom 环境内**恢复原生 `AbortSignal`**。接受这个 workaround 之前我坚持确认两件事：
一是确认它是**已知的工具链不兼容**（vitest-dev/vitest#8374）**而不是应用缺陷**；
二是确认 MSW 的 `onUnhandledRequest: 'error'` **仍然生效**，这样 workaround 不会悄悄削弱测试套件。
记录在案的隐患是：全局恢复 `AbortSignal` 理论上会**反向**影响将来某个用 jsdom
`addEventListener({ signal })` 的测试。

#### Q14.7 — What was the cheapest high-value test you were missing, and did you write it?

**中文** — 你之前缺的、性价比最高的那个测试是什么？写了吗？

**Tests / 考察点:** Coverage blind spots — multi-user isolation, what green tests cannot prove · 覆盖盲区：多用户隔离、绿测试证明不了什么

**A.** **Cross-user isolation**, and yes —
[CrossUserIsolationTests.cs](tests/MarketPulse.IntegrationTests/CrossUserIsolationTests.cs),
five tests.

Slice 1 scoped every query by `UserId` — `.Where(w => w.UserId == …)` — but the database had
only ever contained **one** user. So a query that lost its `.Where` clause would have passed
every single test in the suite. The tests could not distinguish **"correctly scoped"** from
**"there is only one row"**. That is the failure mode I care most about, because the test suite
looks fully green while the guarantee it appears to prove does not exist.

The fixture registers two real users, Alice and Bob, each with their own cookie jar, and
asserts Bob cannot read, add to, or delete from Alice's watchlist. It cost almost nothing once
real registration existed — which is the argument: **the test was cheap, it was just
impossible to write before slice 2.** Some tests are blocked on a feature, not on effort, and
naming which ones is part of knowing your own coverage.

**答.** **跨用户隔离**——写了，`CrossUserIsolationTests.cs`，**五个测试**。

slice 1 的每个查询都按 `UserId` 限定（`.Where(w => w.UserId == …)`），但数据库里**从头到尾只有
一个用户**。所以一个**丢掉了 `.Where` 子句的查询，会通过当时套件里的每一个测试**。
那些测试**根本无法区分"正确限定了作用域"和"本来就只有一行"**。这是我最在意的失败模式，
因为**测试套件全绿，而它看起来在证明的那个保证根本不存在**。

fixture 注册两个真实用户 Alice 和 Bob，各自持有独立的 cookie jar，断言 Bob 读不到、加不进、
删不掉 Alice 的 watchlist。在真注册存在之后它几乎不花成本——**而这正是重点：这个测试一直很便宜，
只是在 slice 2 之前根本写不出来。** 有些测试卡的是**功能**，不是**工作量**；
**能说清楚是哪些，本身就是了解自己覆盖率的一部分。**

#### Gaps (category 14) / 类别 14 的缺口

E2E and cross-user isolation are now built, so what remains is narrower: TDD demonstrated on a
genuinely test-driven component (the alert-evaluation engine is the planned one), coverage
philosophy applied rather than just stated, mutation testing, and any form of load or
performance testing. The E2E suite is three happy-ish paths on one browser — no cross-browser
matrix, no mobile viewport, no visual regression.

> E2E 和跨用户隔离都建好了，所以剩下的缺口更窄了：在一个真正测试驱动的组件上展示 TDD
> （计划中的是告警评估引擎）、把覆盖率理念**真正实践出来**而不只是写出来、变异测试、
> 以及任何形式的压测或性能测试。E2E 套件目前是**单一浏览器上的三条接近顺利路径**——
> 没有跨浏览器矩阵、没有移动视口、没有视觉回归。

---

## Appendix — the honest summary / 附录：诚实的总结

If asked "what's the weakest part of this?", the answer is short and should be given
without hedging:

> 如果被问"这里面最弱的是什么"，答案要短，而且**不要打太极**：

1. **No observability.** A correlation ID that lands nowhere searchable is a half-built
   feature — and slice 2 made it worse by adding events (failed logins, lockouts, refresh-token
   reuse) that a real system would *alert* on, none of which emit anything.
   > **没有可观测性。** 一个落不到任何可搜索位置的相关性 ID，是个半成品功能——
   > 而 slice 2 让它更糟：新增了登录失败、账号锁定、刷新令牌重用这些**真实系统要告警**的事件，
   > 而它们**一条都不发**。
2. **Nothing is measured.** No benchmarks, no profiler traces, no Core Web Vitals. Several
   answers above say "I'd measure before optimising" — none of them have.
   > **什么都没测量过。** 没有基准、没有 profiler trace、没有 Core Web Vitals。
   > 上面好几个答案都说"我会先测量再优化"——**一个都还没测。**
3. **Single-instance by construction.** SignalR has no backplane and the tick source is
   in-process (Q11.2).
   > **结构上就是单实例的。** SignalR 没有 backplane，tick 源在进程内（Q11.2）。
4. **No styling or real accessibility.** The UI is semantic, unstyled HTML — now including the
   login and registration screens.
   > **没有样式，也没有真正的无障碍。** UI 就是语义化的、无样式的 HTML——现在连登录和注册界面
   > 也是。
5. **Authentication is shipped, but the perimeter around it is not.** No security headers at
   all, secrets in config, and a seeded account with a committed password hash (Q13.3).
   > **认证交付了，但它外面那圈周界没有。** 一个安全响应头都没设、密钥在配置里、
   > 还有一个密码哈希被提交进仓库的种子账号（Q13.3）。

And what it does well:

> 而它做得好的地方：

1. **Boundaries that are enforced, not just documented** — the architecture test fails CI, and
   slice 2 proved the seam by swapping the auth stub for the real thing with an **empty diff**
   below the API layer (Q10.3).
   > **边界是被强制的，不只是被写下来的**——架构测试挂了 CI 就红；而 slice 2 用"把认证桩件换成
   > 真货、API 层以下 diff 为空"（Q10.3）**实测**了这条接缝。
2. **Errors as a designed contract** — a domain taxonomy mapped to ProblemDetails with
   stable slugs, verified by integration tests, and honoured even on the framework's own
   401 challenge path (Q13.1).
   > **错误是被设计出来的契约**——领域错误分类映射到 ProblemDetails，slug 稳定、有集成测试验证，
   > 而且**连框架自己的 401 challenge 路径也遵守它**（Q13.1）。
3. **Tests that touch real infrastructure** — real SQL Server, real migrations, real HTTP,
   real SignalR, and now a real browser.
   > **测试碰的是真实基础设施**——真 SQL Server、真迁移、真 HTTP、真 SignalR，现在还有**真浏览器**。
4. **Decisions written down with their rejected alternatives** — including the ones that
   turned out to be wrong, like the refresh cookie's path (Q7.5).
   > **决定连同被否方案一起写下来**——包括那些后来被证明是错的，比如刷新 cookie 的路径（Q7.5）。
5. **Known defects recorded rather than hidden** — every "Gaps" section above is in the repo,
   not invented for this document. So is the admission that the password blocklist is nearly
   inert (Q13.4).
   > **已知缺陷是记录在案而不是藏起来的**——上面每一节 Gaps 都在仓库里有据可查，
   > 不是为了这份文档临时编的。**"密码黑名单几乎是惰性的"这个承认，也一样**（Q13.4）。
