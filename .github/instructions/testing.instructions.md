---
description: Test placement, determinism, and evidence rules
applyTo: 'tests/**,eng/tests/**'
---

# Testing instructions

- Put assertions at the lowest layer that can observe the required behavior and use the test taxonomy in the specification.
- Test externally visible contracts and invariants rather than copying production algorithms into expected values.
- Keep tests deterministic by controlling clocks, random sources, provider responses, and shared state.
- Add cross-tenant, authorization, malformed-input, cancellation, concurrency, and recovery coverage where those risks apply.
- Use committed fixtures for validator behavior and create any test scratch data only beneath `eng/tests/`, cleaning it after each case.
- Run focused tests first, then the containing project or validator suite.
