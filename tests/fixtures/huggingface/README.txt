Captured Hugging Face tree listings
===================================

Verbatim responses of

    GET https://huggingface.co/api/models/{repositoryId}/tree/main?recursive=1

captured on 2026-08-02, one file per repository, with "/" in the repository id written as "__".

Why they are checked in
-----------------------
The model catalog used to hardcode a file list per model and nobody found out it was wrong until a
user pressed "다운로드" and got a 404 (`vocabulary.txt` on `Systran/faster-whisper-large-v3`, which
publishes `vocabulary.json`). The downloader now derives the file list from this listing, and these
captures let both test suites exercise that logic - selection, exclusion, split-shard handling,
measured sizes - with no network at all:

  * tests/KSubMaker.UnitTests/Domain/ModelFileSelectorTests.cs
  * tests/KSubMaker.UnitTests/Domain/ModelCatalogFixtureTests.cs
  * worker/tests/test_model_manager.py

tests/KSubMaker.IntegrationTests/Models/ModelCatalogHubTests.cs re-runs the same assertions against
the live API and skips when there is no network, so drift between these captures and reality shows
up as a failing online test rather than a failing user download.

Refreshing a capture
--------------------
    curl -s "https://huggingface.co/api/models/<repo>/tree/main?recursive=1" \
      | python3 -m json.tool > "tests/fixtures/huggingface/<repo with __>.json"

If a refresh changes the selected file set or the totals, update ModelCatalog.BuiltIn() and
docs/MODEL_MANAGEMENT.md in the same commit - the tests will tell you exactly which numbers moved.
