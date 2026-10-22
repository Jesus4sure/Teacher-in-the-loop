# TITLAPE — Teacher-in-the-Loop Agentic Precision Education

A C# / .NET 8 implementation of the *TITLAPE Comprehensive Architectural, Ethical, and Evaluation
Framework for Resource-Constrained Sub-Saharan Africa*.

Here's how it works: the teacher teaches the lesson in class. After school, at the school's solar hub, an
agentic AI tutor gives each learner step-by-step Socratic practice. Every diagnosis, risk flag and
pathway change goes back to the teacher's dashboard, and the teacher stays in charge.

## Quick start

Requirements: the **.NET 8 SDK** (Windows, macOS or Linux). No NuGet packages are used, so the
solution builds fully offline.

```bash
dotnet build TITLAPE.sln
dotnet run --project src/Titlape.Web          # → http://localhost:5080
dotnet run --project tests/Titlape.Tests      # 35 tests, including the full evaluation matrix
```

Open http://localhost:5080 and choose one of these roles:

| Role | What to try |
|---|---|
| **Learner** (e.g. *Tunde Bakare*, Yoruba) | Answer wrongly on purpose and you get a graduated hint. Type *"just tell me the answer"* and the tutor refuses but still helps. Switch the language to Swahili or Hausa to see code-switching. |
| **Teacher** (*Mrs. Adebayo*, Lagos; *Mr. Otieno*, Kisumu) | Clustered misconception alerts and at-risk alerts with SHAP bars. Open the evidence traces, approve or modify a pathway proposal, rate how useful an explanation was, and toggle consent. |
| **Hub administrator** | Take the backhaul offline and watch writes cache locally. Drain the solar battery to change model routing. Run delta-sync, verify the audit chain, look at the bias audit, and **run the Section 7 evaluation matrix**. |

On start-up the demo seeds 18 fictional learners in two schools. They are simulated, and they go
through the *real* agent pipeline. A small amount of model fault injection is switched on during
seeding only, so some traces show drafts that the Safety Agent blocked.

## Architecture mapping (framework → code)

| Framework layer | Project | What is implemented |
|---|---|---|
| **L6 Autonomous** | `Titlape.Orchestration`, `Titlape.Web` | Master Orchestrator (sessions, consent gate, approved-pathway progression, signal → alert synthesis); `AlertService` (misconception clustering, Shapley risk alerts, digest + per-class cap against alert fatigue, trust ratings); Student PWA, Teacher Portal, Ops console; SSE token streaming and live alerts; offline service worker |
| **L5 Intelligence** | `Titlape.Agents` | Diagnostic, Socratic, Localization and Safety agents in a LangGraph-style `StateGraph` (conditional edges, step budget, per-node checkpoints) with a **multi-agent reflection loop** (revise ≤2×, then vetted fallback) |
| **L4 Context** | `Titlape.Context` | Curriculum pack (NERDC-aligned Primary 4 maths), parametric item bank with misconception signatures and leak-free hint ladders, hybrid BM25 + vector **Curriculum RAG** with skill-aware reranking, **Bayesian Knowledge Tracing** student memory, teacher-controlled pathways |
| **L3 XAI / RAI** | `Titlape.Governance` | Guardrails (prompt injection, answer seeking, toxicity, **safeguarding escalation**, answer leakage incl. equivalent fractions, **arithmetic-hallucination check**, off-curriculum drift, PII), JSON-configurable policy, plain-language CoT tracer (OpenTelemetry/OpenInference spans), HMAC **hash-chain audit log**, **exact Shapley** explainer, four-fifths-rule **bias auditor**, DSPy-style output contract for teacher digests |
| **L2 Model** | `Titlape.Models` | `ILanguageModel`; OpenAI-compatible adapter for **llama.cpp** (edge, 4-bit GGUF) and **vLLM** (cloud); offline rule-based fallback engine; **SHA-256 model signature verification**; edge-first, battery-aware router; tokens/sec stats |
| **L1 Data** | `Titlape.Data` | Presidio-style **PII gateway** (NDPA/POPIA identifiers: NG/KE/ZA/ET phones, NIN, Luhn-checked ZA ID, DOB, addresses, names incl. Swahili/Yoruba/Hausa "my name is"), keyed pseudonyms, **AES-256-GCM** at rest, **CRDTs** (HLC, LWW, PN-counter, OR-set) offline replica with **delta-sync**, vector store, anonymised interaction log |
| **Cross-cutting security** | `Titlape.CrossCutting/Security` | Signed tokens (JWT/HS256 shape), **ABAC with tenant isolation**, scope-bounded agent tool grants, purpose-specific guardian consent |
| **Cross-cutting communication** | `Titlape.CrossCutting/Communication` | Native **Protobuf wire format**, MQTT-semantics pub/sub (wildcards, QoS, retained), W3C trace-context propagation, Brotli framing |
| **Section 7 evaluation** | `Titlape.Evaluation` | Simulated learners with ground-truth misconceptions, cohort runner, PII corpus, multi-day CRDT outage, payload and Zero-Trust overhead benchmarks. Every RQ row reports its benchmark and whether its **design-refinement trigger** fired. |

### Tutoring graph (one learner turn)

```
input_guard ─┬─► escalate (safeguarding → urgent teacher alert) ─────────────┐
             ├─► refuse   (prompt injection)  ───────────────────────────────┤
             └─► diagnose ─► socratic_plan ─► localize_pre ─► generate        │
                                ▲                                 │          │
                                │                           localize_post    │
                                └──── blocked, retry ◄──── safety_check      │
                                       retries exhausted ─► safe_fallback ─► respond ─► END
```

Nothing reaches a learner until the Safety Agent has validated it, and that includes streamed tokens.

## Using a real small language model

By default the tutor's wording comes from the built-in rule-based engine, so the platform runs on any
hardware. To use an actual quantized SLM, set the following in `src/Titlape.Web/appsettings.json`:

```jsonc
"Titlape": {
  "EdgeSlmUrl": "http://127.0.0.1:8080",            // llama.cpp: llama-server -m phi-3-mini-4k-instruct-q4_k_m.gguf
  "EdgeSlmModel": "phi-3-mini-4k-instruct-q4_k_m",
  "EdgeSlmModelPath": "/models/phi-3-mini-4k-instruct-q4_k_m.gguf",
  "EdgeSlmSha256": "<sha256 of the .gguf from your signed manifest>",
  "CloudLlmUrl": "",                                 // optional vLLM endpoint (in-country)
  "AllowCloud": true
}
```

A model with no verified digest is never routed to. Every SLM draft still passes through the same
guardrails and reflection loop, and the evaluation matrix then reports real tokens/sec for RQ 4.1.

## Evaluation matrix (Section 7)

You can run the matrix from the Ops page, or with `dotnet run --project tests/Titlape.Tests -- Evaluation`.
A typical run (5 days × 14 turns × 18 learners, with 15% leaky / 5% drifting / 5% hallucinating model
drafts injected):

| RQ | Metric | Result | Benchmark |
|---|---|---|---|
| 1.1 | Alerts acted on / ignored | ~99% / ~1% | ignored ≤ 30% |
| 1.2 | Answer leaks delivered to learners | 0 of ~1,260 turns | 0% |
| 1.2 | Misconception diagnosis accuracy | 100% | ≥ 90% |
| 2.2 | Hallucinations delivered | 0% | < 1% |
| 2.2 | Dialect disparity (4/5ths) | ≥ 0.9 | ≥ 0.8 |
| 2.1 | Shapley exactness | Σφ error ≈ 1e-16 | — |
| 3.1 | RAG precision@3 | 94.4% | > 90% |
| 3.2 | PII leaks | 0 | 0 |
| 4.2 | CRDT convergence after 3-day outage | converged | 100% |
| 5.2 | Daily sync payload | ~25 KB (≈92% smaller than REST/JSON) | < 100 KB |
| 5.1 | Zero-Trust overhead | ≈0.01 ms/request; est. 3G RTT ≈1.1 s | RTT < 2 s |

## Limitations

- **Python stack equivalents.** NeMo Guardrails, Presidio, LangGraph, DSPy, SHAP/Captum, RxDB/Automerge
  and gRPC are Python or JavaScript tools. Here they are re-implemented natively in C# under the same
  design contracts, so the platform needs no external packages. The real tools can be put behind the
  same interfaces later.
- **Simulated measurements.** Teacher trust, teacher time saved and learner behaviour in the evaluation
  are *simulated*. The dashboard collects real 1–5 trust ratings for field studies. RQ 4.1 reports N/A
  until a real llama.cpp model is configured.
- **Production pieces still needed.** mTLS between services, an OIDC identity provider and a KMS/TPM for
  the root key are deployment concerns. Here they are represented by HMAC-signed tokens and HKDF-derived
  keys (`MasterSecret`).
- **Language content needs review.** Local-language glossary terms and metaphors should be checked by
  in-country language teachers before use. They live in `CurriculumPack.cs` as data.
- **Demo data is fictional.** All demo schools and learners are fictional. State is in-memory unless
  `PersistState` is true. When it is, the edge replica is saved AES-256-GCM encrypted and the audit log
  is append-only.
