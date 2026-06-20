---
name: dotnet-backend-rules
description: Architectural rules for .NET and C# development
applyTo: "**/*.cs"
---
# .NET & C# Implementation Standards
- **Framework**: Modern .NET (C# 12+ optimization features).
- **Patterns**: Emphasize Clean Architecture, explicit dependency injection, strongly-typed configurations, and asynchronous/await patterns with cancellation tokens.
- **Data Access**: Use Entity Framework Core optimized LINQ queries (explicitly tracking vs `AsNoTracking()` where performance demands).
- **Performance**: Minimize unnecessary heap allocations; prefer `ReadOnlySpan<T>` or pattern matching logic for hot-path formatting where applicable.