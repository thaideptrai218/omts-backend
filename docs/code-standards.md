# Code standards

## .NET conventions

Use the SDK in global.json, nullable reference types, explicit public contracts and asynchronous I/O with cancellation tokens. `Directory.Build.props` defines compiler/analyzer gates; `.editorconfig` is authoritative for formatting. Use standard .NET PascalCase types/member names, camelCase locals, descriptive namespaces and file names matching the owning type. Documentation and script names use kebab-case.

Keep transport concerns at the API boundary. Introduce feature folders when product capabilities exist. Extract domain/application/infrastructure projects only when dependency boundaries justify them. Avoid generic repositories, mediator pipelines or microservices without a demonstrated need.

## API contracts

Use HTTP status codes accurately and RFC 9457 ProblemDetails for errors. Never expose stack traces or connection details in production. Validate incoming bodies and identifiers at the boundary. Document pagination, idempotency, authorization and versioning when adding endpoints. Respect configured request size and timeout limits. Use bounded retries only for known transient failures, with cancellation and idempotency considered.

Use a managed identity provider rather than implementing password or token issuance ad hoc. Require explicit authorization policies on product endpoints. Configure CORS for approved origins only when a client needs it. Implement forwarded headers only for explicitly trusted proxies.

## Dependencies and configuration

Keep package versions centralized and lock files committed. Use `dotnet restore --locked-mode` in CI. Review upgrades and dependency audit results; document any accepted advisory with an expiry and owner. Validate mandatory configuration at startup. Keep environment overrides and secrets outside source control.

## Testing and logs

Integration tests use the real ASP.NET hosting pipeline. Test observable success/failure contracts and configuration failures. Add dependency integration tests against the real selected persistence/message technology when introduced. Keep tests independent and avoid timing assumptions.

Use structured logs with trace context. Do not log bodies or credentials by default. Metrics should have bounded cardinality; never label metrics with arbitrary user/resource IDs. Define retention and incident access for telemetry before production.
