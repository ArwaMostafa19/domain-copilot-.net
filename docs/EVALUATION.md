# .NET evaluation

`eval/golden.jsonl` remains the shared source of reference questions and expected evidence. The package-free .NET command is retrieval-only:

```powershell
.\scripts\run-cli.ps1 eval
```

It loads the configured embedder, verifies the registered embedding model, runs the existing .NET hybrid retrieval for each golden case, and reports hit@6 where expected evidence is present. It reads the fixture and database index; it does not write a report, alter the threshold, or call a chat completion provider. It does contact the configured embedding provider unless `EMBEDDING_PROVIDER=fake` is selected.

Prerequisites: apply the .NET migrations, ingest the shared Corpus, and make the matching embedding index available. Run it only against an authorized database. A fresh install can first select the fake embedder for offline smoke verification; meaningful retrieval requires the same embedding model/index as the ingestion run.

The Python evaluator's calibration, fact-support scoring, refusal-gate scoring, adversarial subset, answer generation, and JSON report format are not implemented by this CLI command. They are not represented as supported .NET features. Domain, Application, Infrastructure, API, and frontend test runners cover their respective deterministic behavior. No Python interpreter, Python service, or Python script is needed to build, start, initialize, ingest, or use the .NET application.
