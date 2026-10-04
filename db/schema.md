# DB schema: PostgreSQL (running app) ↔ MySQL (design file)

- `db/schema.sql` — **designed** schema in MySQL 8 dialect; opens in **MySQL Workbench**
  (File → Open SQL Script), as required by the assignment.
- `db/schema-postgres.sql` — idempotent, human-readable PostgreSQL DDL (review / manual bootstrap).
- The running application uses **PostgreSQL 16** through EF Core / Npgsql. The physical schema is
  created by the committed migrations in
  `src/Backend/Comments.Infrastructure/Persistence/Migrations/`, applied at startup under a
  `pg_advisory_lock` (idempotent, multi-instance safe). See `docs/API-v2.md` §8.

## Type map

| Logical column        | MySQL (`db/schema.sql`)    | PostgreSQL (EF Core)   | .NET type |
|-----------------------|----------------------------|------------------------|-----------|
| `id`                  | `BIGINT UNSIGNED AUTO_INCREMENT` | `bigint IDENTITY` | `int` in CLR, `bigint` in DB |
| `parent_id`           | `BIGINT UNSIGNED NULL`     | `bigint NULL`      | `int?` |
| `user_name`           | `VARCHAR(50)`              | `varchar(50)`      | `string` |
| `email`               | `VARCHAR(100)`             | `varchar(100)`     | `string` |
| `home_page`           | `VARCHAR(200) NULL`        | `varchar(200) NULL`| `string?` |
| `text_html`           | `TEXT`                     | `text`             | `string` |
| `text_plain`          | `TEXT`                     | `text`             | `string` |
| `quoted_text`         | `TEXT NULL`                | `text NULL`        | `string?` |
| `created_at`          | `DATETIME(6)`              | `timestamptz` (UTC) | `DateTime` |
| `client_ip`           | `VARCHAR(64) NULL`         | `varchar(64) NULL` | `string?` |
| `user_agent`          | `VARCHAR(512) NULL`        | `varchar(512) NULL`| `string?` |
| `attachment_id`       | `BIGINT UNSIGNED NULL`     | `bigint NULL`      | `int?` |
| `file_name`           | `VARCHAR(255)`             | `varchar(260)`     | `string` |
| `stored_name`         | `VARCHAR(255)`             | `varchar(260)`     | `string` |
| `storage_path`        | `VARCHAR(512)`             | `varchar(512)`     | `string` |
| `content_type`        | `VARCHAR(100)`             | `varchar(100)`     | `string` |
| `kind`                | `ENUM('image','text')`     | `varchar(10)`      | `string` |
| `size_bytes`          | `BIGINT UNSIGNED`          | `bigint`           | `long` |
| `width`,`height`      | `INT NULL`                 | `integer NULL`     | `int?` |
| `original_width/height` | `INT NULL`               | `integer NULL`     | `int?` |
| `sha256`              | `CHAR(64)`                 | `varchar(64)`      | `string` |

## Indexes / constraints

| Logical name | MySQL | PostgreSQL | Purpose |
|--------------|-------|------------|---------|
| `ix_comments_parent_id` | `(parent_id)` | `(parent_id)` | fetch replies of a root |
| `ix_comments_created_at_id` | `(created_at)` | `(created_at DESC, id DESC)` | default LIFO sort + keyset pagination |
| `ix_comments_parent_created` | — | `(parent_id, created_at, id)` | tree assembly (BFS) |
| `ix_comments_user_name_lower` | `(user_name)` | `(lower(user_name))` | case-insensitive sort by User Name |
| `ix_comments_email_lower` | `(email)` | `(lower(email))` | case-insensitive sort by E-mail |
| `ix_attachments_kind` | `(kind)` | `(kind)` | attachment filtering |
| `ix_attachments_sha256` | `(sha256)` | `(sha256)` | integrity / dedup |
| `fk_comments_parent` | self FK, `ON DELETE CASCADE` | self FK, `ON DELETE RESTRICT` | tree integrity (v2 keeps root delete restrictions) |
| `fk_comments_attachment` | FK → `attachments(id)`, `ON DELETE SET NULL` | same | optional attachment |

Case-insensitive sorting uses `lower(value)` in PostgreSQL (functional index), **not** the SQLite
`NOCASE` collation of the previous stage (docs/API-v2.md §4.2).

## Functional quote (`quoted_text`)

`comments.quoted_text` is an additive, nullable column (EF Core migration
`20261004161433_AddCommentQuotedText`, `AddColumn quoted_text text NULL`):

- written only for replies (`parent_id IS NOT NULL`) — a snapshot of the parent's `text_plain`
  taken at creation time (docs/DESIGN-v2.1-decisions.md §1);
- whitespace-collapsed, trimmed, truncated to 160 characters on a word boundary and suffixed with
  `…` (U+2026), so the stored value is at most 161 characters;
- plain text, never HTML: the parent `text_plain` is already sanitised, so XSS safety is the
  renderer's escaping duty (Angular interpolation);
- `NULL` for root comments, for legacy rows and when the parent text is empty.

## CAPTCHA

Not persisted in the relational schema: stored through the `ICaptchaStore` port — **Redis** by
default (`captcha:{id}`, TTL 300 s, one-time `GETDEL`), memory fallback when Redis is unavailable.
The answer is never returned to the client except through the Development-only peek endpoint
(`docs/API-v2.md` §2.8).
