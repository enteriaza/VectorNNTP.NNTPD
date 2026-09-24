```

BenchmarkDotNet v0.15.4, Windows 11 (10.0.26200.9457)
12th Gen Intel Core i9-12900KF 3.20GHz, 1 CPU, 24 logical and 16 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3
  DefaultJob : .NET 10.0.12 (10.0.12, 10.0.1226.42308), X64 RyuJIT x86-64-v3


```
| Method                     | Mean       | Ratio | Gen0   | Allocated | Alloc Ratio |
|--------------------------- |-----------:|------:|-------:|----------:|------------:|
| CurrentStream_Check        | 118.819 ns |  1.00 | 0.0529 |     832 B |        1.00 |
| CurrentStream_TakeThis     | 114.307 ns |  0.96 | 0.0525 |     824 B |        0.99 |
| ReferenceClassify_Check    |   7.172 ns |  0.06 |      - |         - |        0.00 |
| ReferenceClassify_TakeThis |   9.731 ns |  0.08 |      - |         - |        0.00 |
| ReferenceDispatch_Check    |  26.868 ns |  0.23 | 0.0184 |     288 B |        0.35 |
| ReferenceDispatch_TakeThis |  30.923 ns |  0.26 | 0.0184 |     288 B |        0.35 |
