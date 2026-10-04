# DB schema: SQLite (running app) ↔ MySQL (design file)

- `db/schema.sql` — **designed** schema in MySQL 8 dialect, opens in MySQL Workbench.
- The running application uses **SQLite** by default and creates the same logical schema
  through the committed EF Core migration (`src/Backend/CommentsApi/Data/Migrations/`).

## Type map

| Logical column        | MySQL (`db/schema.sql`)    | SQLite (EF Core)   | .NET type |
|-----------------------|----------------------------|--------------------|-----------|
| `id`                  | `BIGINT UNSIGNED AUTO_INCREMENT` | `INTEGER PRIMARY KEY AUTOINCREMENT` | `long` |
| `parent_id`           | `BIGINT UNSIGNED NULL`     | `INTEGER NULL`     | `long?` |
| `user_name`           | `VARCHAR(50)`              | `TEXT`             | `string` |
| `email`               | `VARCHAR(100)`             | `TEXT`             | `string` |
| `home_page`           | `VARCHAR(200) NULL`        | `TEXT NULL`        | `string?` |
| `text_html`           | `TEXT`                     | `TEXT`             | `string` |
| `text_plain`          | `TEXT`                     | `TEXT`             | `string` |
| `created_at`          | `DATETIME(6)`              | `TEXT` (ISO-8601 UTC) | `DateTimeOffset` / `DateTime` |
| `client_ip`           | `VARCHAR(64) NULL`         | `TEXT NULL`        | `string?` |
| `user_agent`          | `VARCHAR(512) NULL`        | `TEXT NULL`        | `string?` |
| `attachment_id`       | `BIGINT UNSIGNED NULL`     | `INTEGER NULL`     | `long?` |
| `kind`                | `ENUM('image','text')`     | `TEXT` + CHECK     | `string` / enum |
| `size_bytes`          | `BIGINT UNSIGNED`          | `INTEGER`          | `long` |
| `width`,`height`      | `INT NULL`                 | `INTEGER NULL`     | `int?` |
| `original_width/height` | `INT NULL`               | `INTEGER NULL`     | `int?` |
| `sha256`              | `CHAR(64)`                 | `TEXT`             | `string` |

## Indexes / constraints

| Name | MySQL | Purpose |
|------|-------|---------|
| `ix_comments_parent_id` | `(parent_id)` | fetch replies of a root; cascade delete |
| `ix_comments_created_at` | `(created_at)` | default LIFO sort + pagination |
| `ix_comments_user_name` | `(user_name)` | sortable column |
| `ix_comments_email` | `(email)` | sortable column |
| `fk_comments_parent` | self FK, `ON DELETE CASCADE` | tree integrity |
| `fk_comments_attachment` | FK → `attachments(id)`, `ON DELETE SET NULL` | optional attachment |

## CAPTCHA

Not persisted anywhere: stored in `IMemoryCache` with a 5-minute TTL, one-time use,
answer never returned to the client (except the Development-only peek endpoint).
