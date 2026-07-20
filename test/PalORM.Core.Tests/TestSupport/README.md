# TestSupport — 测试 helper 集中化目录

> **目的**：把散落在各测试文件中的桩代码（TestInterceptor / OrderedInterceptor / 测试用 RegistryFragment 构造器 / Mock 事务等）逐步集中到此处。
>
> **当前状态**：v3.0 引入目录占位，未强制迁移现有 helper（避免大规模改动破坏稳定代码）。
> 后续新加测试 helper 优先放到本目录，旧的 helper 在自然演进中迁移。
>
> **规划文件**（按需创建）：
> - `Interceptors.cs` — TestInterceptor / OrderedInterceptor / CountingInterceptor
> - `RegistryFragments.cs` — BuildRegistryFragment&lt;T&gt;(...) 测试夹具构造
> - `MockTransactions.cs` — FailingDbTransaction / CountingTransaction
> - `FakeConnections.cs` — TestDbConnection / FailureInjectionConnection
>
> **规范参考**：`.ai/lessons.md` 的「测试代码规范」章节。
