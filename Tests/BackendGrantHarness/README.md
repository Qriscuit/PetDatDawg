# Legacy backend grant receipt checks

This dependency-free .NET 8 harness exercises the production ASP.NET Steam client
with a fake HTTP handler. It makes no real Steam calls.

```powershell
dotnet restore Tests/BackendGrantHarness/BackendGrantHarness.csproj --configfile Tests/BackendGrantHarness/NuGet.Config
dotnet run --no-restore --project Tests/BackendGrantHarness/BackendGrantHarness.csproj
```

The checks cover empty and malformed receipts, missing Pets, upstream rejection,
safe error messages, exact 64-bit request IDs, optional response flags, and replay
receipts for a Pets stack that was subsequently consumed. A failed receipt never
claims one granted Pet or automatically sends another mutation.

HTTP 200 responses with failed, malformed, or conflicting `x-eresult` headers,
or a nonempty `x-error_message`, are rejected before any grant or balance is
confirmed. Both empty and valid-looking response bodies are covered. Raw header
text never enters an error, and successful or absent headers retain normal
receipt validation and valid empty-inventory behavior.
