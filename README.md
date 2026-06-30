# JSONColdStore

JSONColdStore is an EF Core provider for local applications that want durable, file-based JSON storage without managing a database server. It stores data in a configured directory, supports encrypted and compressed data at rest, and is designed for cold or append-heavy records such as logs, history, messages, events, and imported user data. The goal is a small setup surface with practical storage behavior: indexed reads, bounded startup work, manifest-backed writes, and maintenance tools for verification, compaction, and repair.
