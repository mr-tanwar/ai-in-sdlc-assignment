# Architecture test

Rule: Domain must not depend on web or storage code (from AGENTS.md).
File: backend/tests/InvoiceApp.Tests/ArchitectureTests.cs
Run: dotnet test backend/InvoiceApp.sln --filter "FullyQualifiedName~ArchitectureTests"
Proof: added a field of type HttpContext → test FAILED, naming ApprovalService (screenshot).
Reverted both changes → test passes.
Why it's useful: the compiler already stops Domain using the Api project, but nothing
stops someone adding ASP.NET or EF to Domain. This test does.
