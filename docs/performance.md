# Performance test plan

No performance claims are made yet. The required comparison is direct local upstream versus the same request through TrafficGate under identical payload, concurrency, warmup, and duration. Record throughput, P50/P95/P99 from a real latency histogram, error rate, CPU, memory, connections, queue time, limiter overhead, and SSE first-byte latency. Store raw results under `artifacts/perf/` and record the OS, machine, .NET SDK, YARP version, tool, route, target, concurrency, duration, warmup, and body sizes.
