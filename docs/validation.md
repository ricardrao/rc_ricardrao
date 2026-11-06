# Verification record

Local verification used .NET SDK 10.0.403, PostgreSQL 17.6, and a local Redis-compatible 5.0.14 server. The deployment and CI configuration targets Redis 7. Docker was not installed on the execution host, so the Compose stack has not been executed here.

- Release build: zero warnings and errors.
- Backend automated tests: 130 passed, zero failed, zero skipped, with TEST_POSTGRES and TEST_REDIS enabled.
- Frontend tests: 7 passed with TEST_UI_URL set to the running local console. The integration workflow uses JSDOM and real HTTP APIs; it is not a browser screenshot/layout test.
- The console workflow registers a supplier, previews and publishes a mapped capability, creates a connection, submits a notification twice with the same key, verifies the original receipt, checks successful delivery, navigates records/tickets/settings, and switches languages while retaining draft form values.
- New GET /v1/notifications tests cover controller delegation, authentication, pagination, status filtering, and tenant isolation.
- Capability descriptions are verified for persistence, business visibility, the 2,000-character limit, legacy-version compatibility, and no effect on outbound payloads. The console workflow verifies description display in both version lists and recipient selection and ensures markup is displayed as plain text.
- Real PostgreSQL tests use a randomly named isolated database and clean it up after completion.
- Shared Redis token-bucket and concurrency-slot behavior was exercised against a real server.
- Every controller action is covered by unit tests; every protected route is covered by an HTTP authentication test.
- Language switching was tested in the ASP.NET Core pipeline using both Accept-Language and query-string overrides.
- Source scan found no literal Chinese characters in C#, JSON, SQL, Python, or build/configuration files; the Chinese catalog uses Unicode escapes.
- Python examples parse successfully and Compose YAML loads with the expected five services. Container execution remains unverified on this host.
- Local end-to-end demonstration against a running Kestrel service, real PostgreSQL/Redis, and an HTTP supplier simulator:
  - Registration, version publication, connection creation, and duplicate submission succeeded.
  - A supplier failing twice completed successfully on the third attempt.
  - A persistently failing supplier stopped after three attempts and produced one internal ticket and one successfully delivered external ticket event.
  - A supplier committing before a response timeout received two attempts with the same delivery ID, performed one business operation, and eventually returned success. The task retained its historical uncertainty flag.
  - Business health event webhooks and the ticket event were delivered; all observed Outbox entries reached succeeded.

TRX reports are generated in the ignored TestResults directory. Run the documented test command to regenerate them; GitHub Actions uploads them as a workflow artifact.
