# Standalone TX POC

Diagnostic-only. Reproduces `NntpConnection.SendAsync` consume shape without NNTPD, SPEEDTEST, Channel, or TCS.

Does **not** send NNTP. `198.18.0.66:1199` is bound by this tool's raw TCP drain (stop `VectorNNTP.NNTPD` first). Sending raw bytes to NNTPD is unsafe and is not done.

```powershell
# In-process drain + 5 runs of raw / pipe / production-loop
dotnet run -c Release --project tools\VectorNNTP.StandaloneTxPoc -- --host 198.18.0.66 --port 1199 --runs 5

# Optional two-process
dotnet run -c Release --project tools\VectorNNTP.StandaloneTxPoc -- --listen --host 198.18.0.66 --port 1199
dotnet run -c Release --project tools\VectorNNTP.StandaloneTxPoc -- --client-only --mode raw --host 198.18.0.66 --port 1199
```

Modes: `raw`, `pipe`, `production-loop`, `all` (default).
