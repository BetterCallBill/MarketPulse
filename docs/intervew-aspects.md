**Your question in casual English:** "Take my frontend and .NET interview categories and reorganize them into one clean list."

**Super casual text version:** "can u merge these 2 lists (FE + .NET interview topics) into 1 tidy list? theres overlap, dedupe it"

---

**English**

**Conclusion first:** the two lists merge naturally into **fourteen categories** because five areas overlap (testing, security, architecture, CI/CD, behavioural) — the cleanest structure is a full-stack master list grouped into four tiers: language & runtime, framework & platform, cross-cutting engineering, and professional practice, with the overlapping categories consolidated so each contains both the frontend and backend sub-topics.

**Tier 1 — Language & runtime fundamentals**

1. **C# language & .NET runtime internals** — async/await and the task model, LINQ internals, delegates/events, generics, memory management & GC, `Span<T>`/performance features, records & pattern matching
2. **JavaScript fundamentals & language internals** — closures, event loop, async, prototypes, ES modules
3. **TypeScript** — type system, generics, narrowing

**Tier 2 — Framework & platform depth**

4. **.NET 8 / ASP.NET Core framework depth** — middleware pipeline, dependency injection & service lifetimes, configuration & options pattern, minimal APIs vs controllers, filters, hosted/background services
5. **Frontend framework depth (React or Angular)** — rendering model, state management, hooks/lifecycle, performance patterns
6. **HTML/CSS & layout** — semantics, accessibility, responsive design, flexbox/grid
7. **Web API design** — REST maturity, versioning, authentication/authorization (JWT, OAuth2, OpenID Connect), rate limiting, idempotency, error handling standards (ProblemDetails)
8. **Data access & SQL Server** — EF Core (change tracking, migrations, query translation, N+1), raw SQL/Dapper trade-offs, indexing & execution plans, transactions & isolation levels, query tuning

**Tier 3 — Cross-cutting engineering**

9. **Browser, network & web performance** — rendering pipeline, caching, HTTP, CORS, cookies vs storage; Core Web Vitals, bundle optimisation, lazy loading, measurement-first mindset
10. **Architecture & system design** ⭐ *(biggest senior differentiator, both stacks)* — backend: Clean/Onion architecture, CQRS & MediatR, DDD basics, SOLID, dependency inversion, when *not* to use patterns; frontend: component design, micro-frontends, design systems, API layer, state architecture
11. **Distributed systems & messaging** — microservices vs modular monolith trade-offs, RabbitMQ/Azure Service Bus/SNS-SQS, eventual consistency, outbox pattern, idempotent consumers, resilience (Polly, retries, circuit breakers)
12. **Cloud & DevOps** — AWS/Azure compute options (ECS/EKS/Lambda, App Service/Functions), storage, IAM/managed identity, networking, cost awareness; GitHub Actions/Azure DevOps pipelines, Docker & Kubernetes, IaC (Terraform/Bicep), deployment strategies (blue-green, canary), observability (logging, metrics, tracing, Datadog/App Insights)
13. **Security (full-stack)** — backend: OWASP Top 10 for APIs, secrets management, input validation, SQL injection, secure auth flows, least privilege; frontend: XSS, CSRF, CSP, auth patterns
14. **Testing (full-stack)** — backend: xUnit, Moq/NSubstitute, WebApplicationFactory, Testcontainers, TDD familiarity; frontend: unit, integration, E2E strategy and tooling; shared: test strategy & coverage philosophy

**Tier 4 — Professional practice**

15. **Engineering practice & behavioural** — CI/CD workflow discipline, code review, mentoring, cross-team collaboration, stakeholder communication, agile ceremonies, incident handling ("tell me about a production issue"), STAR stories, Australian culture-fit and plain-spoken communication

Note: "Frontend & full-stack breadth" from the .NET list dissolves into categories 2, 3, 5, 6 since your master list already covers frontend at full depth; and CI/CD appears in both Tier 3 (technical: pipelines, IaC) and Tier 4 (practice: workflow discipline) intentionally, matching how interviews actually probe it.

**中文**

**先说结论:** 两份清单合并后共 **十四(实际编号十五)个类别**,因为有五个领域重叠(测试、安全、架构、CI/CD、行为面试)——最清晰的结构是按四个层级组织的全栈总清单:语言与运行时、框架与平台、横切工程能力、职业实践,重叠类别做了合并,前后端子考点收在同一条目下。

**第一层——语言与运行时基础:** 1. C# 与 .NET 运行时底层;2. JavaScript 语言底层;3. TypeScript。**第二层——框架与平台深度:** 4. .NET 8 / ASP.NET Core 框架深度;5. 前端框架深度(React/Angular);6. HTML/CSS 与布局;7. Web API 设计;8. 数据访问与 SQL Server。**第三层——横切工程能力:** 9. 浏览器、网络与 Web 性能(合并了原来的两个前端类别);10. 架构与系统设计 ⭐(前后端 senior 最大区分点,后端含 Clean 架构/CQRS/DDD,前端含组件设计/微前端/设计系统/状态架构);11. 分布式系统与消息;12. 云与 DevOps(合并 AWS/Azure 与 CI/CD 技术面);13. 全栈安全(后端 OWASP/密钥管理/注入,前端 XSS/CSRF/CSP);14. 全栈测试(后端 xUnit/Testcontainers,前端单元/集成/E2E,共享测试策略理念)。**第四层——职业实践:** 15. 工程实践与行为面试(代码评审、带人、跨团队协作、故障处理、STAR 故事、澳洲文化契合)。

补充说明:.NET 清单里的"前端与全栈广度"已并入第 2、3、5、6 类,因为你的前端清单本身就是全深度覆盖;CI/CD 刻意同时出现在第三层(技术面:流水线、IaC)和第四层(实践面:工作流纪律),这符合面试中实际的考察方式。


**Q1: 帮我按这十五个类别做一个自评打分表(1–5 分),并给出每个类别 senior 级别的达标标准是什么?**


**Q2: 如果我只有四周备战时间,这十五个类别的优先级和时间分配应该怎么排?**


**Q3: MarketPulse 和 OzAuction 两个项目加起来,这十五个类别还有哪些覆盖不到,需要第三个小项目或专项练习补齐?**