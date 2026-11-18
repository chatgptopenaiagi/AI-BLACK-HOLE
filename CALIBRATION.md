# Calibration

AUTO CALIBRATE performs a bounded sequence: inspect compatibility and memory, load a conservative CPU baseline, try a second CPU configuration, and include a CUDA candidate when the runtime and available VRAM support it. The current ABH model is unloaded before free-resource measurement. Other applications are not stopped.

The baseline context is capped at 2,048 tokens; the GPU candidate may use up to 4,096 within declared context. Threads, GPU layers, batch and microbatch differ between candidates. One inference slot is used. Custom profiles expose supported settings with finite validation limits. Other parameter combinations are not exhaustively searched.

Each chat candidate generates a paragraph capped at 64 tokens, followed by a separate warm arithmetic request capped at 24 tokens, with temperature 0 and seed 42. Prompt and output rates come from runtime timings. Startup time is measured. RAM is engine working set. VRAM is sampled total device usage, including unrelated applications; it is not claimed as exclusive model VRAM. OS disk caching means startup is not a controlled cold-disk benchmark.

SAFE favors conservative CPU operation. BALANCED selects a successful interactive candidate. FASTEST TESTED selects the highest measured output rate in this bounded set. GPU is not assumed faster. Tiny quality checks are recorded independently of transport compatibility; SmolLM2's strict arithmetic responses failed even when generation succeeded.

Reports bind the model SHA-256, complete engine fingerprint and hardware/driver fingerprint. A mismatch requires recalibration. Failed candidates are stopped and the last verified candidate is restored after exploration. Cancellation saves partial evidence and leaves the engine stopped. Memory estimates retain system headroom; they cannot guarantee that other applications will not consume resources later.

See `reports/ABH-CALIBRATION-RESULTS.json` and recovery acceptance for measured configurations, failures, rollback and limitations.
