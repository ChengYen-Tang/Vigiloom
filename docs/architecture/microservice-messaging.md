# Microservice messaging

Each service configures `UseVigiloomMessaging` with its own SQL Server connection and the shared RabbitMQ connection. The SQL database stores that service's Wolverine inbox, outbox, deduplication claims, and failed messages. RabbitMQ transfers messages between services; each service configures only its own routes and listeners.

Use separate queues for reliable and replaceable messages. `PublishReliablyTo<T>` and `ListenReliablyOn` enable durable outbox and inbox processing. `PublishBestEffortTo<T>` and `ListenBestEffortOn` buffer in memory; use them only when a later update can replace a lost message. They do not provide delivery guarantees during a process failure.

For a business change that must be delivered, enroll the same EF Core context in `IDbContextOutbox` before publishing, then call `SaveChangesAndFlushMessagesAsync`. Do not commit the business update separately from the outbox. Preserve a stable logical message identifier and pass it in `DeliveryOptions.DeduplicationId`. On the receiving side use `[Deduplicated]` and `[Transactional]` with the service's business context. Each service owns its handler and decides how to ignore stale state versions.

The shared logical deduplication window is seven days. It is not a permanent exactly-once guarantee and does not compare payloads. Where an existing business contract requires a longer window, payload conflict detection, or durable uniqueness, the owning service must implement and retain those rules separately.

Handling failures retry after one, five, and fifteen seconds. Exhausted messages are stored in the receiver's Wolverine SQL dead-letter storage; the platform management service can query Wolverine's `IMessageStore.DeadLetters` without exposing message payloads or secrets to unauthorized users. The management UI and any replay operation belong to REQ-50.

For local integration tests start an isolated RabbitMQ broker and set `VIGILOOM_TEST_RABBITMQ` to its AMQP URI. Tests use the isolated SQL test fixture and delete their own queues. Ubuntu CI provisions disposable SQL Server and RabbitMQ containers. Windows tests without an explicitly configured RabbitMQ report these integration cases as inconclusive.
